using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace FPSLimiter
{
    public class RtssLimiterBackend
    {
        private const string GlobalProfileDisplayName = "Global";
        private const string GlobalProfileFileName = "Global";
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly FPSLimiterSettings settings;

        public RtssLimiterBackend(FPSLimiterSettings settings)
        {
            this.settings = settings;
        }

        public string ResolveRtssPath()
        {
            if (!string.IsNullOrWhiteSpace(settings.RtssPath) && File.Exists(settings.RtssPath))
            {
                return settings.RtssPath;
            }

            foreach (var process in Process.GetProcessesByName("RTSS"))
            {
                try
                {
                    var path = process.MainModule.FileName;
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
                catch (Exception e)
                {
                    logger.Debug(e, "Could not read RTSS process path.");
                }
            }

            var defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "RivaTuner Statistics Server",
                "RTSS.exe");

            if (File.Exists(defaultPath))
            {
                return defaultPath;
            }

            return null;
        }

        public string ResolveProfilesDirectory()
        {
            var rtssPath = ResolveRequiredRtssPath();
            var profilesDirectory = GetProfilesDirectory(rtssPath);
            if (!Directory.Exists(profilesDirectory))
            {
                throw new DirectoryNotFoundException($"RTSS Profiles folder was not found: {profilesDirectory}");
            }

            return profilesDirectory;
        }

        public RtssProfilePermissionTestResult TestProfileWriteAccess()
        {
            string testPath = null;

            try
            {
                var rtssPath = ResolveRequiredRtssPath();
                var profilesDirectory = GetProfilesDirectory(rtssPath);
                if (!Directory.Exists(profilesDirectory))
                {
                    return RtssProfilePermissionTestResult.Fail(profilesDirectory, $"RTSS Profiles folder was not found: {profilesDirectory}");
                }

                testPath = Path.Combine(profilesDirectory, $"FPSLimiterPermissionTest_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(testPath, "fps-limiter permission test", Encoding.ASCII);
                var readback = File.ReadAllText(testPath, Encoding.ASCII);
                File.Delete(testPath);
                testPath = null;

                if (!string.Equals(readback, "fps-limiter permission test", StringComparison.Ordinal))
                {
                    return RtssProfilePermissionTestResult.Fail(profilesDirectory, "Temporary file read-back did not match.");
                }

                var globalPath = GetProfileFilePath(rtssPath, string.Empty);
                var globalText = File.ReadAllText(globalPath, Encoding.Default);
                File.WriteAllText(globalPath, globalText, Encoding.Default);

                return RtssProfilePermissionTestResult.Ok(profilesDirectory);
            }
            catch (Exception e)
            {
                var profilesDirectory = SafeGetProfilesDirectory();
                return RtssProfilePermissionTestResult.Fail(profilesDirectory, e.Message);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(testPath) && File.Exists(testPath))
                {
                    try
                    {
                        File.Delete(testPath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        public void GrantProfilesModifyPermissionToCurrentUser()
        {
            var profilesDirectory = ResolveProfilesDirectory();
            var identity = WindowsIdentity.GetCurrent();
            var sid = identity?.User?.Value;
            if (string.IsNullOrWhiteSpace(sid))
            {
                throw new InvalidOperationException("Could not resolve the current Windows user SID.");
            }

            var arguments = $"\"{profilesDirectory}\" /grant *{sid}:(OI)(CI)M /T /C";
            var icaclsPath = Path.Combine(Environment.SystemDirectory, "icacls.exe");
            try
            {
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = icaclsPath,
                    Arguments = arguments,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (process == null)
                    {
                        throw new InvalidOperationException("Windows did not start the elevated permission command.");
                    }

                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        throw new InvalidOperationException($"The elevated permission command exited with code {process.ExitCode}.");
                    }
                }
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                throw new InvalidOperationException("Administrator approval was cancelled.", e);
            }
        }

        public bool EnsureRtssRunning(string rtssPath)
        {
            if (Process.GetProcessesByName("RTSS").Any())
            {
                return false;
            }

            if (!settings.AutoStartRtss)
            {
                return false;
            }

            if (!File.Exists(rtssPath))
            {
                throw new FileNotFoundException("RTSS.exe was not found.", rtssPath);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = rtssPath,
                WorkingDirectory = Path.GetDirectoryName(rtssPath),
                UseShellExecute = true
            });

            Thread.Sleep(1000);
            return true;
        }

        public void CloseRtssProcess()
        {
            foreach (var process in Process.GetProcessesByName("RTSS"))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(3000);
                    logger.Info("Closed RTSS because no FPS Limiter caps are active anymore.");
                }
                catch (Exception e)
                {
                    logger.Debug(e, "Could not close RTSS process.");
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        public LimitSessionSnapshot ApplyLimit(Guid gameId, string gameName, string executablePath, double frameLimit, FpsSyncMode syncMode, bool forceGlobalProfile, bool disableReflexMarkers = false)
        {
            var rtssPath = ResolveRtssPath();
            if (string.IsNullOrWhiteSpace(rtssPath))
            {
                throw new InvalidOperationException("RTSS.exe was not found. Set the RTSS path in FPS Limiter settings.");
            }

            var startedRtss = EnsureRtssRunning(rtssPath);

            var usesGlobalProfile = forceGlobalProfile || settings.UseGlobalProfileDuringLaunch;
            var profileName = usesGlobalProfile ? GlobalProfileDisplayName : Path.GetFileName(executablePath);
            var apiProfileName = usesGlobalProfile ? string.Empty : profileName;
            if (!usesGlobalProfile && string.IsNullOrWhiteSpace(profileName))
            {
                throw new InvalidOperationException("Could not resolve an RTSS profile name for the target executable.");
            }

            var profileExisted = File.Exists(GetProfileFilePath(rtssPath, apiProfileName));
            var fileSnapshot = CaptureProfileFile(rtssPath, apiProfileName);

            try
            {
                ApplyProfileFileLimit(rtssPath, apiProfileName, frameLimit, syncMode, disableReflexMarkers);
                RefreshRtssProfiles(rtssPath);
                var fileLimit = ReadProfileFileLimit(rtssPath, apiProfileName);
                logger.Info($"RTSS profile file {profileName} after apply: Limit={FormatFileReadback(fileLimit)}, Sync={syncMode}, ReflexMarkersDisabled={disableReflexMarkers}.");

                if (!fileLimit.HasValue || Math.Abs(fileLimit.Value - frameLimit) > 0.01)
                {
                    throw CreateProfileFilePersistenceException(rtssPath, profileName, frameLimit, null);
                }
            }
            catch (Exception e) when (!(e is InvalidOperationException))
            {
                throw CreateProfileFilePersistenceException(rtssPath, profileName, frameLimit, e);
            }

            return new LimitSessionSnapshot
            {
                GameId = gameId,
                GameName = gameName,
                ExecutablePath = executablePath,
                ProfileName = profileName,
                AppliedLimit = frameLimit,
                AppliedSyncMode = syncMode,
                AppliedReflexMarkersDisabled = disableReflexMarkers,
                UsesGlobalProfile = usesGlobalProfile,
                ProfileExisted = profileExisted,
                FileFallbackUsed = true,
                ProfileFilePath = fileSnapshot.Path,
                OriginalProfileFileExisted = fileSnapshot.Exists,
                OriginalProfileFileContent = fileSnapshot.Content,
                StartedRtssProcess = startedRtss,
                StartedAt = DateTime.UtcNow
            };
        }

        public void RestoreLimit(LimitSessionSnapshot snapshot)
        {
            var rtssPath = ResolveRtssPath();
            if (string.IsNullOrWhiteSpace(rtssPath))
            {
                throw new InvalidOperationException("RTSS.exe was not found. FPS Limiter cannot restore the previous RTSS profile yet.");
            }

            RestoreProfileFile(snapshot);

            if (Process.GetProcessesByName("RTSS").Any())
            {
                RefreshRtssProfiles(rtssPath);
            }

            logger.Info($"Restored RTSS profile file {snapshot.ProfileName} after {snapshot.GameName}.");
        }

        private static string FormatFileReadback(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "unavailable";
        }

        private static void RefreshRtssProfiles(string rtssPath)
        {
            using (var refresher = new RtssProfileRefresher(rtssPath))
            {
                refresher.Initialize();
                refresher.UpdateProfiles();
            }
        }

        private string ResolveRequiredRtssPath()
        {
            var rtssPath = ResolveRtssPath();
            if (string.IsNullOrWhiteSpace(rtssPath))
            {
                throw new InvalidOperationException("RTSS.exe was not found. Set the RTSS path in FPS Limiter settings.");
            }

            return rtssPath;
        }

        private string SafeGetProfilesDirectory()
        {
            try
            {
                var rtssPath = ResolveRtssPath();
                return string.IsNullOrWhiteSpace(rtssPath) ? null : GetProfilesDirectory(rtssPath);
            }
            catch
            {
                return null;
            }
        }

        private static InvalidOperationException CreateProfileFilePersistenceException(
            string rtssPath,
            string profileName,
            double requestedLimit,
            Exception innerException)
        {
            var profilesDirectory = GetProfilesDirectory(rtssPath);
            var message =
                $"FPS Limiter could not write the requested {requestedLimit.ToString("0.###", CultureInfo.InvariantCulture)} FPS limit to RTSS profile {profileName}. " +
                $"This usually means Playnite cannot write to the RTSS Profiles folder: {profilesDirectory}. " +
                "Use FPS Limiter's RTSS access setup, grant Modify permission to that folder, or install RTSS in a writable location.";

            return innerException == null
                ? new InvalidOperationException(message)
                : new InvalidOperationException(message, innerException);
        }

        private static ProfileFileSnapshot CaptureProfileFile(string rtssPath, string apiProfileName)
        {
            var path = GetProfileFilePath(rtssPath, apiProfileName);
            if (!File.Exists(path))
            {
                return new ProfileFileSnapshot { Path = path };
            }

            return new ProfileFileSnapshot
            {
                Path = path,
                Exists = true,
                Content = File.ReadAllText(path, Encoding.Default)
            };
        }

        // RTSS profile key used for the "Inject NVIDIA Reflex latency markers" checkbox in RTSS's
        // own Setup dialog (Injection properties). Confirmed by diffing a real RTSS profile .cfg
        // file: it's ReflexSetLatencyMarker under [Framerate], 1 = enabled (RTSS's default), 0 =
        // disabled.
        private const string ReflexMarkersSection = "Framerate";
        private const string ReflexMarkersKey = "ReflexSetLatencyMarker";

        private static void ApplyProfileFileLimit(string rtssPath, string apiProfileName, double frameLimit, FpsSyncMode syncMode, bool disableReflexMarkers)
        {
            var path = GetProfileFilePath(rtssPath, apiProfileName);
            var profileText = File.Exists(path)
                ? File.ReadAllText(path, Encoding.Default)
                : ReadDefaultProfileText(rtssPath);

            int numerator;
            int denominator;
            ToFraction(frameLimit, out numerator, out denominator);

            profileText = SetProfileValue(profileText, "Framerate", "Limit", numerator.ToString(CultureInfo.InvariantCulture));
            profileText = SetProfileValue(profileText, "Framerate", "LimitDenominator", denominator.ToString(CultureInfo.InvariantCulture));
            profileText = SetProfileValue(profileText, "Framerate", "SyncLimiter", ((int)syncMode).ToString());
            profileText = SetProfileValue(profileText, "Hooking", "EnableHooking", "1");
            profileText = SetProfileValue(profileText, ReflexMarkersSection, ReflexMarkersKey, disableReflexMarkers ? "0" : "1");

            File.WriteAllText(path, profileText, Encoding.Default);
        }

        // RTSS stores the limiter target as Limit / LimitDenominator, which lets it represent
        // fractional caps (e.g. 59.9, 23.976) precisely instead of only whole numbers.
        private static void ToFraction(double frameLimit, out int numerator, out int denominator)
        {
            denominator = 1000;
            numerator = (int)Math.Round(frameLimit * denominator, MidpointRounding.AwayFromZero);

            var divisor = Gcd(numerator, denominator);
            if (divisor > 1)
            {
                numerator /= divisor;
                denominator /= divisor;
            }

            if (denominator <= 0)
            {
                denominator = 1;
            }
        }

        private static int Gcd(int a, int b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0)
            {
                var t = b;
                b = a % b;
                a = t;
            }

            return a == 0 ? 1 : a;
        }

        private static double? ReadProfileFileLimit(string rtssPath, string apiProfileName)
        {
            var path = GetProfileFilePath(rtssPath, apiProfileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var profileText = File.ReadAllText(path, Encoding.Default);
            var limitValue = GetProfileValue(profileText, "Framerate", "Limit");
            var denominatorValue = GetProfileValue(profileText, "Framerate", "LimitDenominator");

            int limit;
            if (!int.TryParse(limitValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit))
            {
                return null;
            }

            int denominator;
            if (string.IsNullOrWhiteSpace(denominatorValue) ||
                !int.TryParse(denominatorValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out denominator) ||
                denominator <= 0)
            {
                denominator = 1;
            }

            return (double)limit / denominator;
        }

        private static void RestoreProfileFile(LimitSessionSnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(snapshot.ProfileFilePath))
            {
                throw new InvalidOperationException($"RTSS profile file path was not captured for {snapshot.ProfileName}.");
            }

            if (snapshot.OriginalProfileFileExisted)
            {
                File.WriteAllText(snapshot.ProfileFilePath, snapshot.OriginalProfileFileContent ?? string.Empty, Encoding.Default);
            }
            else if (File.Exists(snapshot.ProfileFilePath))
            {
                File.Delete(snapshot.ProfileFilePath);
            }
        }

        private static string ReadDefaultProfileText(string rtssPath)
        {
            var globalPath = GetProfileFilePath(rtssPath, string.Empty);
            if (!File.Exists(globalPath))
            {
                throw new FileNotFoundException("RTSS Global profile file was not found.", globalPath);
            }

            return File.ReadAllText(globalPath, Encoding.Default);
        }

        private static string GetProfilesDirectory(string rtssPath)
        {
            return Path.Combine(Path.GetDirectoryName(rtssPath), "Profiles");
        }

        private static string GetProfileFilePath(string rtssPath, string apiProfileName)
        {
            var fileName = string.IsNullOrWhiteSpace(apiProfileName)
                ? GlobalProfileFileName
                : apiProfileName.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)
                    ? apiProfileName
                    : apiProfileName + ".cfg";

            return Path.Combine(GetProfilesDirectory(rtssPath), fileName);
        }

        private static string SetProfileValue(string text, string section, string key, string value)
        {
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var normalized = text.Replace("\r\n", "\n");
            var lines = normalized.Split('\n').ToList();
            var sectionHeader = "[" + section + "]";
            var inSection = false;
            var sectionFound = false;

            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    if (inSection)
                    {
                        lines.Insert(i, key + "=" + value);
                        return string.Join(newline, lines);
                    }

                    inSection = string.Equals(trimmed, sectionHeader, StringComparison.OrdinalIgnoreCase);
                    sectionFound = sectionFound || inSection;
                    continue;
                }

                if (inSection && trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = key + "=" + value;
                    return string.Join(newline, lines);
                }
            }

            if (sectionFound)
            {
                lines.Add(key + "=" + value);
            }
            else
            {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines.Last()))
                {
                    lines.Add(string.Empty);
                }

                lines.Add(sectionHeader);
                lines.Add(key + "=" + value);
            }

            return string.Join(newline, lines);
        }

        private static string GetProfileValue(string text, string section, string key)
        {
            var normalized = text.Replace("\r\n", "\n");
            var lines = normalized.Split('\n');
            var sectionHeader = "[" + section + "]";
            var inSection = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    inSection = string.Equals(trimmed, sectionHeader, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (inSection && trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed.Substring(key.Length + 1).Trim();
                }
            }

            return null;
        }

        private class ProfileFileSnapshot
        {
            public string Path { get; set; }
            public bool Exists { get; set; }
            public string Content { get; set; }
        }

        public class RtssProfilePermissionTestResult
        {
            public bool Success { get; private set; }
            public string ProfilesDirectory { get; private set; }
            public string Message { get; private set; }

            public static RtssProfilePermissionTestResult Ok(string profilesDirectory)
            {
                return new RtssProfilePermissionTestResult
                {
                    Success = true,
                    ProfilesDirectory = profilesDirectory,
                    Message = "OK"
                };
            }

            public static RtssProfilePermissionTestResult Fail(string profilesDirectory, string message)
            {
                return new RtssProfilePermissionTestResult
                {
                    Success = false,
                    ProfilesDirectory = profilesDirectory,
                    Message = message
                };
            }
        }

        private class RtssProfileRefresher : IDisposable
        {
            private readonly string rtssPath;
            private IntPtr libraryHandle;
            private UpdateProfilesDelegate updateProfiles;

            public RtssProfileRefresher(string rtssPath)
            {
                this.rtssPath = rtssPath;
            }

            public void Initialize()
            {
                var rtssDirectory = Path.GetDirectoryName(rtssPath);
                var hooksName = Environment.Is64BitProcess ? "RTSSHooks64.dll" : "RTSSHooks.dll";
                var hooksPath = Path.Combine(rtssDirectory, hooksName);

                if (!File.Exists(hooksPath) && Environment.Is64BitProcess)
                {
                    hooksPath = Path.Combine(rtssDirectory, "RTSSHooks.dll");
                }

                if (!File.Exists(hooksPath))
                {
                    throw new FileNotFoundException("RTSS hooks DLL was not found.", hooksPath);
                }

                libraryHandle = LoadLibrary(hooksPath);
                if (libraryHandle == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"Could not load {hooksPath}.");
                }

                updateProfiles = GetExport<UpdateProfilesDelegate>("UpdateProfiles");
            }

            public void UpdateProfiles()
            {
                updateProfiles();
            }

            public void Dispose()
            {
                if (libraryHandle != IntPtr.Zero)
                {
                    FreeLibrary(libraryHandle);
                    libraryHandle = IntPtr.Zero;
                }
            }

            private T GetExport<T>(string name) where T : class
            {
                var pointer = GetProcAddress(libraryHandle, name);
                if (pointer == IntPtr.Zero)
                {
                    throw new MissingMethodException($"RTSS hooks DLL does not export {name}.");
                }

                return Marshal.GetDelegateForFunctionPointer(pointer, typeof(T)) as T;
            }

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void UpdateProfilesDelegate();

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern IntPtr LoadLibrary(string lpFileName);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
            private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool FreeLibrary(IntPtr hModule);
        }
    }
}
