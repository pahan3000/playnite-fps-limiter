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
        private const string FramerateLimitProperty = "FramerateLimit";
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

            var profileName = Path.GetFileName(executablePath);
            if (string.IsNullOrWhiteSpace(profileName))
            {
                throw new InvalidOperationException("Could not resolve an RTSS profile name for the target executable.");
            }

            using (var api = new RtssProfileApi(rtssPath))
            {
                api.Initialize();

                var profileExisted = api.ProfileExists(profileName);
                int originalLimit;
                var originalLimitAvailable = api.TryGetFramerateLimit(profileExisted ? profileName : string.Empty, out originalLimit);

                api.SetFramerateLimit(profileExisted ? profileName : string.Empty, profileName, frameLimit);
                api.UpdateProfiles();

                return new LimitSessionSnapshot
                {
                    GameId = gameId,
                    GameName = gameName,
                    ExecutablePath = executablePath,
                    ProfileName = profileName,
                    AppliedLimit = frameLimit,
                    ProfileExisted = profileExisted,
                    OriginalLimitAvailable = originalLimitAvailable,
                    OriginalLimit = originalLimitAvailable ? originalLimit : 0,
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

                if (snapshot.ProfileExisted)
                {
                    var limit = snapshot.OriginalLimitAvailable ? snapshot.OriginalLimit : 0;
                    api.SetFramerateLimit(snapshot.ProfileName, snapshot.ProfileName, limit);
                }
                else
                {
                    api.DeleteProfile(snapshot.ProfileName);
                }

                api.UpdateProfiles();
            }
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
                loadProfile(sourceProfileName ?? string.Empty);

                var buffer = new byte[4];
                var success = getProfileProperty(FramerateLimitProperty, buffer, (uint)buffer.Length);
                limit = success ? BitConverter.ToInt32(buffer, 0) : 0;
                return success;
            }

            public void SetFramerateLimit(string sourceProfileName, string saveProfileName, int limit)
            {
                loadProfile(sourceProfileName ?? string.Empty);

                var bytes = BitConverter.GetBytes(limit);
                if (!setProfileProperty(FramerateLimitProperty, bytes, (uint)bytes.Length))
                {
                    throw new InvalidOperationException("RTSS rejected the FramerateLimit profile update.");
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
