using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace FPSLimiter
{
    public class RtssLimiterBackend
    {
        private const string AppDetectionLevelProperty = "AppDetectionLevel";
        private const string FramerateLimitProperty = "FramerateLimit";
        private const string GlobalProfileDisplayName = "Global";
        private const string GlobalProfileFileName = "Global";
        private const int ActiveAppDetectionLevel = 3;
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

        public void EnsureRtssRunning(string rtssPath)
        {
            if (Process.GetProcessesByName("RTSS").Any())
            {
                return;
            }

            if (!settings.AutoStartRtss)
            {
                return;
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
        }

        public LimitSessionSnapshot ApplyLimit(Guid gameId, string gameName, string executablePath, int frameLimit)
        {
            var rtssPath = ResolveRtssPath();
            if (string.IsNullOrWhiteSpace(rtssPath))
            {
                throw new InvalidOperationException("RTSS.exe was not found. Set the RTSS path in FPS Limiter settings.");
            }

            EnsureRtssRunning(rtssPath);

            var usesGlobalProfile = settings.UseGlobalProfileDuringLaunch;
            var profileName = usesGlobalProfile ? GlobalProfileDisplayName : Path.GetFileName(executablePath);
            var apiProfileName = usesGlobalProfile ? string.Empty : profileName;
            if (!usesGlobalProfile && string.IsNullOrWhiteSpace(profileName))
            {
                throw new InvalidOperationException("Could not resolve an RTSS profile name for the target executable.");
            }

            using (var api = new RtssProfileApi(rtssPath))
            {
                api.Initialize();

                var profileExisted = usesGlobalProfile || api.ProfileExists(profileName);
                var sourceProfileName = profileExisted ? apiProfileName : string.Empty;
                var fileSnapshot = CaptureProfileFile(rtssPath, apiProfileName);

                int originalLimit;
                var originalLimitAvailable = api.TryGetIntegerProperty(sourceProfileName, FramerateLimitProperty, out originalLimit);

                int originalAppDetectionLevel;
                var originalAppDetectionLevelAvailable = api.TryGetIntegerProperty(sourceProfileName, AppDetectionLevelProperty, out originalAppDetectionLevel);

                var properties = new Dictionary<string, int>
                {
                    { FramerateLimitProperty, frameLimit }
                };

                if (!usesGlobalProfile && (!profileExisted || originalAppDetectionLevelAvailable))
                {
                    properties[AppDetectionLevelProperty] = ActiveAppDetectionLevel;
                }
                else if (!usesGlobalProfile)
                {
                    logger.Warn($"RTSS did not return {AppDetectionLevelProperty} for existing profile {profileName}; leaving detection level unchanged.");
                }

                api.SetIntegerProperties(sourceProfileName, apiProfileName, properties);
                api.UpdateProfiles();
                var readback = LogProfileReadback(api, apiProfileName, profileName, "after SDK apply");
                var fileFallbackUsed = false;

                if (!readback.LimitAvailable || readback.Limit != frameLimit)
                {
                    if (!settings.EnableProfileFileFallback)
                    {
                        throw CreateProfilePersistenceException(rtssPath, profileName, frameLimit, readback, null);
                    }

                    try
                    {
                        ApplyProfileFileLimit(rtssPath, apiProfileName, frameLimit);
                        api.UpdateProfiles();
                        var fileLimit = ReadProfileFileLimit(rtssPath, apiProfileName);
                        logger.Info($"RTSS profile file {profileName} after fallback apply: Limit={FormatReadbackValue(fileLimit.HasValue, fileLimit.GetValueOrDefault())}.");

                        if (!fileLimit.HasValue || fileLimit.Value != frameLimit)
                        {
                            throw new InvalidOperationException($"RTSS profile file read-back did not match the requested {frameLimit} FPS limit.");
                        }

                        fileFallbackUsed = true;
                    }
                    catch (Exception e)
                    {
                        throw CreateProfilePersistenceException(rtssPath, profileName, frameLimit, readback, e);
                    }
                }

                return new LimitSessionSnapshot
                {
                    GameId = gameId,
                    GameName = gameName,
                    ExecutablePath = executablePath,
                    ProfileName = profileName,
                    AppliedLimit = frameLimit,
                    UsesGlobalProfile = usesGlobalProfile,
                    ProfileExisted = profileExisted,
                    OriginalLimitAvailable = originalLimitAvailable,
                    OriginalLimit = originalLimitAvailable ? originalLimit : 0,
                    OriginalAppDetectionLevelAvailable = originalAppDetectionLevelAvailable,
                    OriginalAppDetectionLevel = originalAppDetectionLevelAvailable ? originalAppDetectionLevel : 0,
                    FileFallbackUsed = fileFallbackUsed,
                    ProfileFilePath = fileSnapshot.Path,
                    OriginalProfileFileExisted = fileSnapshot.Exists,
                    OriginalProfileFileContent = fileSnapshot.Content,
                    StartedAt = DateTime.UtcNow
                };
            }
        }

        public void RestoreLimit(LimitSessionSnapshot snapshot)
        {
            var rtssPath = ResolveRtssPath();
            if (string.IsNullOrWhiteSpace(rtssPath))
            {
                throw new InvalidOperationException("RTSS.exe was not found. FPS Limiter cannot restore the previous RTSS profile yet.");
            }

            EnsureRtssRunning(rtssPath);

            using (var api = new RtssProfileApi(rtssPath))
            {
                api.Initialize();

                if (snapshot.FileFallbackUsed)
                {
                    RestoreProfileFile(snapshot);
                    api.UpdateProfiles();
                    logger.Info($"Restored RTSS profile file {snapshot.ProfileName} after {snapshot.GameName}.");
                    return;
                }

                var apiProfileName = snapshot.UsesGlobalProfile ? string.Empty : snapshot.ProfileName;
                if (snapshot.ProfileExisted)
                {
                    var limit = snapshot.OriginalLimitAvailable ? snapshot.OriginalLimit : 0;
                    var properties = new Dictionary<string, int>
                    {
                        { FramerateLimitProperty, limit }
                    };

                    if (snapshot.OriginalAppDetectionLevelAvailable)
                    {
                        properties[AppDetectionLevelProperty] = snapshot.OriginalAppDetectionLevel;
                    }

                    api.SetIntegerProperties(apiProfileName, apiProfileName, properties);
                }
                else
                {
                    api.DeleteProfile(snapshot.ProfileName);
                }

                api.UpdateProfiles();
                if (snapshot.ProfileExisted)
                {
                    LogProfileReadback(api, apiProfileName, snapshot.ProfileName, "after restore");
                }
            }
        }

        private static ProfileReadback LogProfileReadback(RtssProfileApi api, string apiProfileName, string displayProfileName, string action)
        {
            int limit;
            var limitAvailable = api.TryGetIntegerProperty(apiProfileName, FramerateLimitProperty, out limit);

            int appDetectionLevel;
            var appDetectionLevelAvailable = api.TryGetIntegerProperty(apiProfileName, AppDetectionLevelProperty, out appDetectionLevel);

            logger.Info(
                $"RTSS profile {displayProfileName} {action}: " +
                $"{FramerateLimitProperty}={FormatReadbackValue(limitAvailable, limit)}, " +
                $"{AppDetectionLevelProperty}={FormatReadbackValue(appDetectionLevelAvailable, appDetectionLevel)}.");

            return new ProfileReadback
            {
                LimitAvailable = limitAvailable,
                Limit = limit,
                AppDetectionLevelAvailable = appDetectionLevelAvailable,
                AppDetectionLevel = appDetectionLevel
            };
        }

        private static string FormatReadbackValue(bool available, int value)
        {
            return available ? value.ToString() : "unavailable";
        }

        private static InvalidOperationException CreateProfilePersistenceException(
            string rtssPath,
            string profileName,
            int requestedLimit,
            ProfileReadback readback,
            Exception innerException)
        {
            var profilesDirectory = GetProfilesDirectory(rtssPath);
            var message =
                $"RTSS did not persist the requested {requestedLimit} FPS limit for profile {profileName}. " +
                $"RTSS read-back stayed at {FormatReadbackValue(readback.LimitAvailable, readback.Limit)}. " +
                $"This usually means Playnite cannot write to the RTSS Profiles folder: {profilesDirectory}. " +
                "Run Playnite as administrator, grant Modify permission to that folder, or install RTSS in a writable location.";

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

        private static void ApplyProfileFileLimit(string rtssPath, string apiProfileName, int frameLimit)
        {
            var path = GetProfileFilePath(rtssPath, apiProfileName);
            var profileText = File.Exists(path)
                ? File.ReadAllText(path, Encoding.Default)
                : ReadDefaultProfileText(rtssPath);

            profileText = SetProfileValue(profileText, "Framerate", "Limit", frameLimit.ToString());
            profileText = SetProfileValue(profileText, "Framerate", "LimitDenominator", "1");
            profileText = SetProfileValue(profileText, "Hooking", "EnableHooking", "1");

            File.WriteAllText(path, profileText, Encoding.Default);
        }

        private static int? ReadProfileFileLimit(string rtssPath, string apiProfileName)
        {
            var path = GetProfileFilePath(rtssPath, apiProfileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var profileText = File.ReadAllText(path, Encoding.Default);
            var value = GetProfileValue(profileText, "Framerate", "Limit");
            int limit;
            return int.TryParse(value, out limit) ? (int?)limit : null;
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

        private class ProfileReadback
        {
            public bool LimitAvailable { get; set; }
            public int Limit { get; set; }
            public bool AppDetectionLevelAvailable { get; set; }
            public int AppDetectionLevel { get; set; }
        }

        private class ProfileFileSnapshot
        {
            public string Path { get; set; }
            public bool Exists { get; set; }
            public string Content { get; set; }
        }

        private class RtssProfileApi : IDisposable
        {
            private readonly string rtssPath;
            private IntPtr libraryHandle;
            private EnumProfilesDelegate enumProfiles;
            private LoadProfileDelegate loadProfile;
            private SaveProfileDelegate saveProfile;
            private GetProfilePropertyDelegate getProfileProperty;
            private SetProfilePropertyDelegate setProfileProperty;
            private DeleteProfileDelegate deleteProfile;
            private UpdateProfilesDelegate updateProfiles;

            public RtssProfileApi(string rtssPath)
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

                enumProfiles = GetExport<EnumProfilesDelegate>("EnumProfiles");
                loadProfile = GetExport<LoadProfileDelegate>("LoadProfile");
                saveProfile = GetExport<SaveProfileDelegate>("SaveProfile");
                getProfileProperty = GetExport<GetProfilePropertyDelegate>("GetProfileProperty");
                setProfileProperty = GetExport<SetProfilePropertyDelegate>("SetProfileProperty");
                deleteProfile = GetExport<DeleteProfileDelegate>("DeleteProfile");
                updateProfiles = GetExport<UpdateProfilesDelegate>("UpdateProfiles");
            }

            public bool ProfileExists(string profileName)
            {
                return EnumerateProfiles()
                    .Any(a => IsSameProfileName(a, profileName));
            }

            public bool TryGetFramerateLimit(string sourceProfileName, out int limit)
            {
                return TryGetIntegerProperty(sourceProfileName, FramerateLimitProperty, out limit);
            }

            public bool TryGetIntegerProperty(string sourceProfileName, string propertyName, out int value)
            {
                loadProfile(sourceProfileName ?? string.Empty);

                var buffer = new byte[4];
                var success = getProfileProperty(propertyName, buffer, (uint)buffer.Length);
                value = success ? BitConverter.ToInt32(buffer, 0) : 0;
                return success;
            }

            public void SetFramerateLimit(string sourceProfileName, string saveProfileName, int limit)
            {
                SetIntegerProperties(sourceProfileName, saveProfileName, new Dictionary<string, int>
                {
                    { FramerateLimitProperty, limit }
                });
            }

            public void SetIntegerProperties(string sourceProfileName, string saveProfileName, IDictionary<string, int> properties)
            {
                loadProfile(sourceProfileName ?? string.Empty);

                foreach (var property in properties)
                {
                    var bytes = BitConverter.GetBytes(property.Value);
                    if (!setProfileProperty(property.Key, bytes, (uint)bytes.Length))
                    {
                        throw new InvalidOperationException($"RTSS rejected the {property.Key} profile update.");
                    }
                }

                saveProfile(saveProfileName ?? string.Empty);
            }

            public void DeleteProfile(string profileName)
            {
                deleteProfile(profileName);
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

            private IEnumerable<string> EnumerateProfiles()
            {
                var buffer = new byte[32768];
                var size = enumProfiles(buffer, (uint)buffer.Length);
                var length = Array.IndexOf(buffer, (byte)0);
                if (length < 0)
                {
                    length = size > 0 && size < buffer.Length ? (int)size : buffer.Length;
                }

                var text = Encoding.ASCII.GetString(buffer, 0, length);
                return text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(a => a.Trim())
                    .Where(a => !string.IsNullOrWhiteSpace(a));
            }

            private static bool IsSameProfileName(string candidate, string profileName)
            {
                if (string.Equals(candidate, profileName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var withoutConfig = candidate.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)
                    ? candidate.Substring(0, candidate.Length - 4)
                    : candidate;

                return string.Equals(withoutConfig, profileName, StringComparison.OrdinalIgnoreCase);
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
            private delegate uint EnumProfilesDelegate(byte[] profilesList, uint profilesListSize);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            private delegate void LoadProfileDelegate([MarshalAs(UnmanagedType.LPStr)] string profile);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            private delegate void SaveProfileDelegate([MarshalAs(UnmanagedType.LPStr)] string profile);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private delegate bool GetProfilePropertyDelegate([MarshalAs(UnmanagedType.LPStr)] string propertyName, byte[] propertyData, uint propertySize);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private delegate bool SetProfilePropertyDelegate([MarshalAs(UnmanagedType.LPStr)] string propertyName, byte[] propertyData, uint propertySize);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            private delegate void DeleteProfileDelegate([MarshalAs(UnmanagedType.LPStr)] string profile);

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
