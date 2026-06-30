using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;

namespace FPSLimiter
{
    public enum FpsSyncMode
    {
        Async = 0,
        FrontEdgeSync = 1,
        BackEdgeSync = 2
    }

    public static class FpsSyncModeNames
    {
        public static string GetDisplayName(FpsSyncMode mode)
        {
            switch (mode)
            {
                case FpsSyncMode.FrontEdgeSync:
                    return "Front Edge Sync";
                case FpsSyncMode.BackEdgeSync:
                    return "Back Edge Sync";
                default:
                    return "Async";
            }
        }
    }

    public enum PlayniteUiMode
    {
        Desktop = 0,
        Fullscreen = 1
    }

    public class ModeLimitSettings : ObservableObject
    {
        private bool enabled = false;
        private double frameLimit = 0;
        private FpsSyncMode syncMode = FpsSyncMode.Async;

        public bool Enabled
        {
            get => enabled;
            set => SetValue(ref enabled, value);
        }

        public double FrameLimit
        {
            get => frameLimit;
            set => SetValue(ref frameLimit, value);
        }

        public FpsSyncMode SyncMode
        {
            get => syncMode;
            set => SetValue(ref syncMode, value);
        }
    }

    public class FPSLimiterSettings : ObservableObject
    {
        private string rtssPath = string.Empty;
        private bool autoStartRtss = true;
        private bool useGlobalProfileDuringLaunch = true;
        private string presetsText = "30, 60, 120";
        private ModeLimitSettings globalDesktop = new ModeLimitSettings();
        private ModeLimitSettings globalFullscreen = new ModeLimitSettings();
        private bool rtssStartedByExtension = false;
        private List<GameLimitProfile> gameLimits = new List<GameLimitProfile>();
        private List<LimitSessionSnapshot> activeSessions = new List<LimitSessionSnapshot>();

        // VRR refresh-rate switching
        private bool vrrRefreshRateEnabled = false;
        private int vrrTargetHz = 48;
        private double vrrFpsThreshold = 40;

        // Non-VRR refresh-rate matching (refresh rate set to a multiple of the FPS cap)
        private bool matchRefreshRateEnabled = false;
        private int matchRefreshRateMaxMultiplier = 4;

        public string RtssPath
        {
            get => rtssPath;
            set => SetValue(ref rtssPath, value);
        }

        public bool AutoStartRtss
        {
            get => autoStartRtss;
            set => SetValue(ref autoStartRtss, value);
        }

        public bool UseGlobalProfileDuringLaunch
        {
            get => useGlobalProfileDuringLaunch;
            set => SetValue(ref useGlobalProfileDuringLaunch, value);
        }

        public string PresetsText
        {
            get => presetsText;
            set => SetValue(ref presetsText, value);
        }

        public ModeLimitSettings GlobalDesktop
        {
            get => globalDesktop ?? (globalDesktop = new ModeLimitSettings());
            set => SetValue(ref globalDesktop, value ?? new ModeLimitSettings());
        }

        public ModeLimitSettings GlobalFullscreen
        {
            get => globalFullscreen ?? (globalFullscreen = new ModeLimitSettings());
            set => SetValue(ref globalFullscreen, value ?? new ModeLimitSettings());
        }

        public ModeLimitSettings GetGlobal(PlayniteUiMode mode)
        {
            return mode == PlayniteUiMode.Fullscreen ? GlobalFullscreen : GlobalDesktop;
        }

        public bool RtssStartedByExtension
        {
            get => rtssStartedByExtension;
            set => SetValue(ref rtssStartedByExtension, value);
        }

        public List<GameLimitProfile> GameLimits
        {
            get => gameLimits ?? (gameLimits = new List<GameLimitProfile>());
            set => SetValue(ref gameLimits, value ?? new List<GameLimitProfile>());
        }

        public List<LimitSessionSnapshot> ActiveSessions
        {
            get => activeSessions ?? (activeSessions = new List<LimitSessionSnapshot>());
            set => SetValue(ref activeSessions, value ?? new List<LimitSessionSnapshot>());
        }

        /// <summary>
        /// When true, the display refresh rate is switched to <see cref="VrrTargetHz"/> whenever
        /// an FPS cap at or below <see cref="VrrFpsThreshold"/> is applied, and restored on game stop.
        /// </summary>
        public bool VrrRefreshRateEnabled
        {
            get => vrrRefreshRateEnabled;
            set => SetValue(ref vrrRefreshRateEnabled, value);
        }

        /// <summary>Refresh rate (Hz) to switch to when the VRR low-fps cap is active.</summary>
        public int VrrTargetHz
        {
            get => vrrTargetHz;
            set => SetValue(ref vrrTargetHz, value);
        }

        /// <summary>FPS caps at or below this value trigger the refresh-rate switch.</summary>
        public double VrrFpsThreshold
        {
            get => vrrFpsThreshold;
            set => SetValue(ref vrrFpsThreshold, value);
        }

        /// <summary>
        /// When true (and <see cref="VrrRefreshRateEnabled"/> is false), the display refresh rate
        /// is automatically switched to a whole multiple of the active FPS cap whenever a cap is
        /// applied (e.g. 30 FPS -> 60 Hz, 36 FPS -> 72 Hz), and restored on game stop. This keeps
        /// frame pacing even on displays without VRR.
        /// </summary>
        public bool MatchRefreshRateEnabled
        {
            get => matchRefreshRateEnabled;
            set => SetValue(ref matchRefreshRateEnabled, value);
        }

        /// <summary>
        /// Highest multiplier of the FPS cap to try when looking for a supported refresh rate
        /// (2x is tried first, then 3x, 4x, ... up to this value).
        /// </summary>
        public int MatchRefreshRateMaxMultiplier
        {
            get => matchRefreshRateMaxMultiplier;
            set => SetValue(ref matchRefreshRateMaxMultiplier, value);
        }

        public List<double> GetPresetValues()
        {
            return ParsePresetValues(PresetsText);
        }

        public GameLimitProfile GetOrCreateGameProfile(Guid gameId)
        {
            var profile = GameLimits.FirstOrDefault(a => a.GameId == gameId);
            if (profile == null)
            {
                profile = new GameLimitProfile { GameId = gameId };
                GameLimits.Add(profile);
            }

            return profile;
        }

        public GameLimitProfile GetGameProfile(Guid gameId)
        {
            return GameLimits.FirstOrDefault(a => a.GameId == gameId);
        }

        public void RemoveEmptyGameProfile(Guid gameId)
        {
            var profile = GetGameProfile(gameId);
            if (profile != null &&
                !profile.HasAnyLimit() &&
                string.IsNullOrWhiteSpace(profile.ManualExecutablePath) &&
                string.IsNullOrWhiteSpace(profile.LastResolvedExecutable))
            {
                GameLimits.Remove(profile);
            }
        }

        public static List<double> ParsePresetValues(string text)
        {
            var result = new List<double>();

            if (string.IsNullOrWhiteSpace(text))
            {
                return result;
            }

            var parts = text.Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                double value;
                if (double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                    value > 0 &&
                    value <= 1000)
                {
                    result.Add(Math.Round(value, 3));
                }
            }

            return result.Distinct().OrderBy(a => a).ToList();
        }
    }

    public class GameLimitProfile
    {
        public Guid GameId { get; set; }
        public ModeLimitSettings Desktop { get; set; } = new ModeLimitSettings { SyncMode = FpsSyncMode.FrontEdgeSync };
        public ModeLimitSettings Fullscreen { get; set; } = new ModeLimitSettings();
        public string ManualExecutablePath { get; set; }
        public string LastResolvedExecutable { get; set; }

        public ModeLimitSettings GetMode(PlayniteUiMode mode)
        {
            if (Desktop == null)
            {
                Desktop = new ModeLimitSettings { SyncMode = FpsSyncMode.FrontEdgeSync };
            }

            if (Fullscreen == null)
            {
                Fullscreen = new ModeLimitSettings();
            }

            return mode == PlayniteUiMode.Fullscreen ? Fullscreen : Desktop;
        }

        public bool HasAnyLimit()
        {
            return (Desktop != null && Desktop.Enabled) || (Fullscreen != null && Fullscreen.Enabled);
        }
    }

    public class LimitSessionSnapshot
    {
        public Guid GameId { get; set; }
        public string GameName { get; set; }
        public string ExecutablePath { get; set; }
        public string ProfileName { get; set; }
        public double AppliedLimit { get; set; }
        public FpsSyncMode AppliedSyncMode { get; set; }
        public bool UsesGlobalProfile { get; set; }
        public bool ProfileExisted { get; set; }
        public bool OriginalLimitAvailable { get; set; }
        public int OriginalLimit { get; set; }
        public bool OriginalAppDetectionLevelAvailable { get; set; }
        public int OriginalAppDetectionLevel { get; set; }
        public bool FileFallbackUsed { get; set; }
        public string ProfileFilePath { get; set; }
        public bool OriginalProfileFileExisted { get; set; }
        public string OriginalProfileFileContent { get; set; }
        public bool StartedRtssProcess { get; set; }
        public DateTime StartedAt { get; set; }

        // VRR refresh rate tracking
        public bool RefreshRateChanged { get; set; }
        public int OriginalRefreshRate { get; set; }
    }

    public class FPSLimiterSettingsViewModel : ObservableObject, ISettings
    {
        private readonly FPSLimiter plugin;
        private readonly IPlayniteAPI playniteApi;
        private FPSLimiterSettings editingClone;
        private FPSLimiterSettings settings;

        public RelayCommand TestRtssProfilePermissionsCommand { get; private set; }
        public RelayCommand FixRtssProfilePermissionsCommand { get; private set; }

        public FPSLimiterSettings Settings
        {
            get => settings;
            set
            {
                settings = value;
                OnPropertyChanged();
            }
        }

        public FPSLimiterSettingsViewModel(FPSLimiter plugin, IPlayniteAPI playniteApi)
        {
            this.plugin = plugin;
            this.playniteApi = playniteApi;

            var savedSettings = plugin.LoadPluginSettings<FPSLimiterSettings>();
            Settings = savedSettings ?? new FPSLimiterSettings();
            PopulateDetectedRtssPathIfEmpty();

            TestRtssProfilePermissionsCommand = new RelayCommand(TestRtssProfilePermissions);
            FixRtssProfilePermissionsCommand = new RelayCommand(FixRtssProfilePermissions);
        }

        public void SaveSettings()
        {
            plugin.SavePluginSettings(Settings);
        }

        public void BeginEdit()
        {
            PopulateDetectedRtssPathIfEmpty();
            editingClone = Serialization.GetClone(Settings);
        }

        public void CancelEdit()
        {
            Settings = editingClone;
        }

        public void EndEdit()
        {
            NormalizeSettings();
            SaveSettings();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();

            if (!string.IsNullOrWhiteSpace(Settings.RtssPath) &&
                (!File.Exists(Settings.RtssPath) ||
                 !string.Equals(Path.GetFileName(Settings.RtssPath), "RTSS.exe", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("RTSS path must point to RTSS.exe, or be left blank for auto-detection.");
            }

            if (!Settings.GetPresetValues().Any())
            {
                errors.Add("Add at least one FPS preset between 1 and 1000.");
            }

            foreach (var profile in Settings.GameLimits)
            {
                if (!string.IsNullOrWhiteSpace(profile.ManualExecutablePath) &&
                    !File.Exists(profile.ManualExecutablePath))
                {
                    errors.Add($"Manual executable path no longer exists: {profile.ManualExecutablePath}");
                }
            }

            return !errors.Any();
        }

        private void NormalizeSettings()
        {
            Settings.PresetsText = string.Join(", ", Settings.GetPresetValues().Select(FormatFps));
            Settings.RtssPath = Settings.RtssPath?.Trim() ?? string.Empty;
        }

        private static string FormatFps(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private void PopulateDetectedRtssPathIfEmpty()
        {
            if (!string.IsNullOrWhiteSpace(Settings.RtssPath))
            {
                return;
            }

            try
            {
                var detectedPath = new RtssLimiterBackend(Settings).ResolveRtssPath();
                if (!string.IsNullOrWhiteSpace(detectedPath))
                {
                    Settings.RtssPath = detectedPath;
                }
            }
            catch
            {
            }
        }

        private void TestRtssProfilePermissions()
        {
            try
            {
                NormalizeSettings();
                var backend = new RtssLimiterBackend(Settings);
                var result = backend.TestProfileWriteAccess();

                if (result.Success)
                {
                    playniteApi.Dialogs.ShowMessage(
                        $"FPS Limiter can write to the RTSS Profiles folder.\n\n{result.ProfilesDirectory}",
                        "FPS Limiter");
                }
                else
                {
                    playniteApi.Dialogs.ShowErrorMessage(
                        $"FPS Limiter cannot write to the RTSS Profiles folder yet.\n\n{result.Message}",
                        "FPS Limiter");
                }
            }
            catch (Exception e)
            {
                playniteApi.Dialogs.ShowErrorMessage(
                    $"FPS Limiter could not test RTSS profile access.\n\n{e.Message}",
                    "FPS Limiter");
            }
        }

        private void FixRtssProfilePermissions()
        {
            try
            {
                NormalizeSettings();
                var backend = new RtssLimiterBackend(Settings);
                var profilesDirectory = backend.ResolveProfilesDirectory();

                var response = playniteApi.Dialogs.ShowMessage(
                    $"FPS Limiter will request administrator approval once, then grant your current Windows user Modify access to this RTSS folder only:\n\n{profilesDirectory}\n\nContinue?",
                    "FPS Limiter",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (response != MessageBoxResult.Yes)
                {
                    return;
                }

                backend.GrantProfilesModifyPermissionToCurrentUser();
                var result = backend.TestProfileWriteAccess();

                if (result.Success)
                {
                    playniteApi.Dialogs.ShowMessage(
                        $"RTSS profile permissions are ready.\n\n{result.ProfilesDirectory}",
                        "FPS Limiter");
                }
                else
                {
                    playniteApi.Dialogs.ShowErrorMessage(
                        $"The permission command completed, but the write test still failed.\n\n{result.Message}",
                        "FPS Limiter");
                }
            }
            catch (Exception e)
            {
                playniteApi.Dialogs.ShowErrorMessage(
                    $"FPS Limiter could not fix RTSS profile permissions.\n\n{e.Message}",
                    "FPS Limiter");
            }
        }
    }
}
