using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace FPSLimiter
{
    public enum FpsSyncMode
    {
        /// <summary>
        /// Per-game only: no sync mode of its own has been chosen, so the currently active
        /// Global sync mode (Desktop or Fullscreen, whichever applies) is used instead. This is
        /// the default for every new per-game profile, so a game's sync mode automatically
        /// tracks the Global sync mode until the user explicitly overrides it for that game.
        /// </summary>
        UseGlobal = -1,
        Async = 0,
        FrontEdgeSync = 1,
        BackEdgeSync = 2,
        ReflexSync = 3
    }

    public static class FpsSyncModeNames
    {
        public static string GetDisplayName(FpsSyncMode mode)
        {
            switch (mode)
            {
                case FpsSyncMode.UseGlobal:
                    return "Use Global Sync Mode";
                case FpsSyncMode.FrontEdgeSync:
                    return "Front Edge Sync";
                case FpsSyncMode.BackEdgeSync:
                    return "Back Edge Sync";
                case FpsSyncMode.ReflexSync:
                    return "NVIDIA Reflex";
                default:
                    return "Async";
            }
        }
    }

    /// <summary>
    /// Controls RTSS's "Inject NVIDIA Reflex latency markers" profile option, which RTSS enables
    /// by default on every profile. This is independent of <see cref="FpsSyncMode.ReflexSync"/>
    /// (the framerate limiter mode); it's the separate marker-injection option used for latency
    /// measurement/overlay purposes.
    /// </summary>
    public enum ReflexMarkerMode
    {
        /// <summary>
        /// Per-game only: no explicit choice has been made for this game, so the currently active
        /// Global Reflex marker setting (Desktop or Fullscreen, whichever applies) is used instead.
        /// </summary>
        UseGlobal = -1,
        Enabled = 0,
        Disabled = 1
    }

    public static class ReflexMarkerModeNames
    {
        public static string GetDisplayName(ReflexMarkerMode mode)
        {
            switch (mode)
            {
                case ReflexMarkerMode.UseGlobal:
                    return "Use Global Setting";
                case ReflexMarkerMode.Disabled:
                    return "Disabled";
                default:
                    return "Enabled";
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
        private string frameLimitText = FormatFrameLimit(0);
        private FpsSyncMode syncMode = FpsSyncMode.Async;
        private ReflexMarkerMode reflexMarkers = ReflexMarkerMode.Enabled;

        public bool Enabled
        {
            get => enabled;
            set => SetValue(ref enabled, value);
        }

        public double FrameLimit
        {
            get => frameLimit;
            set
            {
                if (frameLimit.Equals(value))
                {
                    return;
                }

                frameLimit = value;
                OnPropertyChanged(nameof(FrameLimit));

                var formatted = FormatFrameLimit(value);
                if (!string.Equals(frameLimitText, formatted, StringComparison.Ordinal))
                {
                    frameLimitText = formatted;
                    OnPropertyChanged(nameof(FrameLimitText));
                }
            }
        }

        /// <summary>
        /// Text-editable proxy for <see cref="FrameLimit"/>, same reasoning as
        /// <see cref="HotkeyBinding.FrameLimitText"/>: lets the settings page's Global FPS limit
        /// fields accept fractional values like "59.9" without WPF's default numeric converter
        /// fighting the user mid-keystroke.
        /// </summary>
        public string FrameLimitText
        {
            get => frameLimitText;
            set
            {
                if (string.Equals(frameLimitText, value, StringComparison.Ordinal))
                {
                    return;
                }

                frameLimitText = value;
                OnPropertyChanged(nameof(FrameLimitText));

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                    parsed >= 0 && parsed <= 1000)
                {
                    frameLimit = parsed;
                    OnPropertyChanged(nameof(FrameLimit));
                }
            }
        }

        public FpsSyncMode SyncMode
        {
            get => syncMode;
            set => SetValue(ref syncMode, value);
        }

        /// <summary>
        /// Whether RTSS should inject NVIDIA Reflex latency markers for this profile. RTSS enables
        /// this by default; set to <see cref="ReflexMarkerMode.Disabled"/> to turn it off.
        /// </summary>
        public ReflexMarkerMode ReflexMarkers
        {
            get => reflexMarkers;
            set => SetValue(ref reflexMarkers, value);
        }

        private static string FormatFrameLimit(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
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
        private ObservableCollection<HotkeyBinding> hotkeys = new ObservableCollection<HotkeyBinding>();
        private DateTime lastUpdateCheckUtc = DateTime.MinValue;

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

        public DateTime LastUpdateCheckUtc
        {
            get => lastUpdateCheckUtc;
            set => SetValue(ref lastUpdateCheckUtc, value);
        }

        /// <summary>Global keyboard shortcuts that apply an FPS cap to the currently running (or selected) game.</summary>
        public ObservableCollection<HotkeyBinding> Hotkeys
        {
            get => hotkeys ?? (hotkeys = new ObservableCollection<HotkeyBinding>());
            set => SetValue(ref hotkeys, value ?? new ObservableCollection<HotkeyBinding>());
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
        public ModeLimitSettings Desktop { get; set; } = new ModeLimitSettings { SyncMode = FpsSyncMode.UseGlobal, ReflexMarkers = ReflexMarkerMode.UseGlobal };
        public ModeLimitSettings Fullscreen { get; set; } = new ModeLimitSettings { SyncMode = FpsSyncMode.UseGlobal, ReflexMarkers = ReflexMarkerMode.UseGlobal };
        public string ManualExecutablePath { get; set; }
        public string LastResolvedExecutable { get; set; }

        public ModeLimitSettings GetMode(PlayniteUiMode mode)
        {
            if (Desktop == null)
            {
                Desktop = new ModeLimitSettings { SyncMode = FpsSyncMode.UseGlobal, ReflexMarkers = ReflexMarkerMode.UseGlobal };
            }

            if (Fullscreen == null)
            {
                Fullscreen = new ModeLimitSettings { SyncMode = FpsSyncMode.UseGlobal, ReflexMarkers = ReflexMarkerMode.UseGlobal };
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
        public bool AppliedReflexMarkersDisabled { get; set; }
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
    }

    /// <summary>
    /// A global keyboard shortcut that applies (or disables) an FPS cap on whichever game
    /// currently has an active RTSS session, falling back to the game selected in the library.
    /// </summary>
    public class HotkeyBinding : ObservableObject
    {
        private bool enabled = true;
        private bool disableCap = false;
        private double frameLimit = 60;
        private string frameLimitText = FormatFrameLimit(60);
        private ModifierKeys modifiers = ModifierKeys.Control | ModifierKeys.Alt;
        private Key key = Key.None;

        public bool Enabled
        {
            get => enabled;
            set => SetValue(ref enabled, value);
        }

        /// <summary>When true, pressing the hotkey disables the FPS cap instead of applying FrameLimit.</summary>
        public bool DisableCap
        {
            get => disableCap;
            set
            {
                SetValue(ref disableCap, value);
                OnPropertyChanged(nameof(DisplayText));
            }
        }

        public double FrameLimit
        {
            get => frameLimit;
            set
            {
                if (frameLimit.Equals(value))
                {
                    return;
                }

                frameLimit = value;
                OnPropertyChanged(nameof(FrameLimit));

                var formatted = FormatFrameLimit(value);
                if (!string.Equals(frameLimitText, formatted, StringComparison.Ordinal))
                {
                    frameLimitText = formatted;
                    OnPropertyChanged(nameof(FrameLimitText));
                }
            }
        }

        /// <summary>
        /// Text-editable proxy for <see cref="FrameLimit"/>. The hotkey FPS textbox binds to this
        /// instead of FrameLimit directly, because WPF's default numeric value converter parses
        /// using the current culture and rejects intermediate keystrokes like "59." while typing a
        /// decimal such as "59.9", making it look like fractional caps can't be entered at all.
        /// Parsing here always uses invariant culture (so "." is the decimal separator regardless of
        /// Windows locale) and only pushes a value into FrameLimit once it's a complete, valid number,
        /// so partial input while typing is never rejected or reformatted out from under the user.
        /// </summary>
        public string FrameLimitText
        {
            get => frameLimitText;
            set
            {
                if (string.Equals(frameLimitText, value, StringComparison.Ordinal))
                {
                    return;
                }

                frameLimitText = value;
                OnPropertyChanged(nameof(FrameLimitText));

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                    parsed > 0 && parsed <= 1000)
                {
                    frameLimit = parsed;
                    OnPropertyChanged(nameof(FrameLimit));
                }
            }
        }

        private static string FormatFrameLimit(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public ModifierKeys Modifiers
        {
            get => modifiers;
            set
            {
                SetValue(ref modifiers, value);
                OnPropertyChanged(nameof(DisplayText));
            }
        }

        public Key Key
        {
            get => key;
            set
            {
                SetValue(ref key, value);
                OnPropertyChanged(nameof(DisplayText));
            }
        }

        /// <summary>Human-readable combo, e.g. "Ctrl + Alt + F1". Not persisted; derived from Modifiers/Key.</summary>
        public string DisplayText
        {
            get
            {
                if (Key == Key.None)
                {
                    return "Click, then press a key combo...";
                }

                var parts = new List<string>();
                if (Modifiers.HasFlag(ModifierKeys.Control))
                {
                    parts.Add("Ctrl");
                }

                if (Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    parts.Add("Alt");
                }

                if (Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    parts.Add("Shift");
                }

                if (Modifiers.HasFlag(ModifierKeys.Windows))
                {
                    parts.Add("Win");
                }

                parts.Add(KeyToDisplayString(Key));
                return string.Join(" + ", parts);
            }
        }

        private static string KeyToDisplayString(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9)
            {
                return ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture);
            }

            if (key >= Key.NumPad0 && key <= Key.NumPad9)
            {
                return "Num" + (int)(key - Key.NumPad0);
            }

            return key.ToString();
        }
    }

    /// <summary>Display-friendly wrapper for a FpsSyncMode value, used by settings-page combo boxes.</summary>
    public class SyncModeOption
    {
        public FpsSyncMode Value { get; set; }
        public string DisplayName { get; set; }
    }

    /// <summary>Display-friendly wrapper for a ReflexMarkerMode value, used by settings-page combo boxes.</summary>
    public class ReflexMarkerOption
    {
        public ReflexMarkerMode Value { get; set; }
        public string DisplayName { get; set; }
    }

    public class FPSLimiterSettingsViewModel : ObservableObject, ISettings
    {
        private readonly FPSLimiter plugin;
        private readonly IPlayniteAPI playniteApi;
        private FPSLimiterSettings editingClone;
        private FPSLimiterSettings settings;

        public RelayCommand TestRtssProfilePermissionsCommand { get; private set; }
        public RelayCommand FixRtssProfilePermissionsCommand { get; private set; }
        public RelayCommand AddHotkeyCommand { get; private set; }
        public RelayCommand<HotkeyBinding> RemoveHotkeyCommand { get; private set; }
        public RelayCommand RefreshVrrSafeCapCommand { get; private set; }
        public RelayCommand AddVrrSafeCapToPresetsCommand { get; private set; }
        public RelayCommand ApplyVrrSafeCapToGlobalDesktopCommand { get; private set; }
        public RelayCommand ApplyVrrSafeCapToGlobalFullscreenCommand { get; private set; }

        /// <summary>Options for the Global FPS limit sync-mode combo boxes (Async / Front Edge / Back Edge / Reflex).</summary>
        public List<SyncModeOption> SyncModeOptions { get; } = ((FpsSyncMode[])Enum.GetValues(typeof(FpsSyncMode)))
            .Where(mode => mode != FpsSyncMode.UseGlobal)
            .Select(mode => new SyncModeOption { Value = mode, DisplayName = FpsSyncModeNames.GetDisplayName(mode) })
            .ToList();

        /// <summary>Options for the Global Reflex marker injection combo boxes (Enabled / Disabled).</summary>
        public List<ReflexMarkerOption> ReflexMarkerOptions { get; } = ((ReflexMarkerMode[])Enum.GetValues(typeof(ReflexMarkerMode)))
            .Where(mode => mode != ReflexMarkerMode.UseGlobal)
            .Select(mode => new ReflexMarkerOption { Value = mode, DisplayName = ReflexMarkerModeNames.GetDisplayName(mode) })
            .ToList();

        private double vrrSafeCapValue;
        private string vrrSafeCapPreview;

        /// <summary>The last-computed Blur Busters VRR-safe cap (0 if the refresh rate couldn't be detected).</summary>
        public double VrrSafeCapValue
        {
            get => vrrSafeCapValue;
            private set => SetValue(ref vrrSafeCapValue, value);
        }

        /// <summary>Human-readable summary of the detected refresh rate and its VRR-safe cap.</summary>
        public string VrrSafeCapPreview
        {
            get => vrrSafeCapPreview;
            private set => SetValue(ref vrrSafeCapPreview, value);
        }

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
            AddHotkeyCommand = new RelayCommand(AddHotkey);
            RemoveHotkeyCommand = new RelayCommand<HotkeyBinding>(RemoveHotkey);
            RefreshVrrSafeCapCommand = new RelayCommand(RecalculateVrrSafeCap);
            AddVrrSafeCapToPresetsCommand = new RelayCommand(AddVrrSafeCapToPresets);
            ApplyVrrSafeCapToGlobalDesktopCommand = new RelayCommand(() => ApplyVrrSafeCapToGlobal(PlayniteUiMode.Desktop));
            ApplyVrrSafeCapToGlobalFullscreenCommand = new RelayCommand(() => ApplyVrrSafeCapToGlobal(PlayniteUiMode.Fullscreen));

            RecalculateVrrSafeCap();
        }

        private void RecalculateVrrSafeCap()
        {
            var hz = RefreshRateManager.GetCurrentRefreshRate();
            if (hz <= 0)
            {
                VrrSafeCapValue = 0;
                VrrSafeCapPreview = "Could not detect the current display refresh rate.";
                return;
            }

            VrrSafeCapValue = Math.Round(RefreshRateManager.GetVrrSafeCap(hz), 2, MidpointRounding.AwayFromZero);
            VrrSafeCapPreview = $"Detected {hz} Hz \u2192 recommended VRR-safe cap: {FormatFps(VrrSafeCapValue)} FPS";
        }

        private void AddVrrSafeCapToPresets()
        {
            if (VrrSafeCapValue <= 0)
            {
                return;
            }

            var values = Settings.GetPresetValues();
            if (!values.Any(v => Math.Abs(v - VrrSafeCapValue) < 0.001))
            {
                values.Add(VrrSafeCapValue);
            }

            Settings.PresetsText = string.Join(", ", values.OrderBy(v => v).Select(FormatFps));
        }

        /// <summary>
        /// Applies the last-computed VRR-safe cap as the Global FPS limit (and enables it) for the
        /// given Playnite UI mode, so it's used as the fallback cap for games without their own preset.
        /// </summary>
        private void ApplyVrrSafeCapToGlobal(PlayniteUiMode mode)
        {
            if (VrrSafeCapValue <= 0)
            {
                return;
            }

            var target = Settings.GetGlobal(mode);
            target.FrameLimit = VrrSafeCapValue;
            target.Enabled = true;
        }

        private void AddHotkey()
        {
            Settings.Hotkeys.Add(new HotkeyBinding { Key = Key.None });
        }

        private void RemoveHotkey(HotkeyBinding binding)
        {
            if (binding != null)
            {
                Settings.Hotkeys.Remove(binding);
            }
        }

        public void SaveSettings()
        {
            plugin.SavePluginSettings(Settings);
        }

        /// <summary>
        /// Persists just the update-check bookkeeping (last-checked time / dismissed version)
        /// without going through the settings dialog's edit/save flow.
        /// </summary>
        public void PersistUpdateCheckState()
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
            plugin.RefreshHotkeys();
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

            if (Settings.GlobalDesktop.Enabled &&
                (Settings.GlobalDesktop.FrameLimit <= 0 || Settings.GlobalDesktop.FrameLimit > 1000))
            {
                errors.Add("Global FPS limit (Desktop): enter an FPS value between 1 and 1000, or uncheck Enabled.");
            }

            if (Settings.GlobalFullscreen.Enabled &&
                (Settings.GlobalFullscreen.FrameLimit <= 0 || Settings.GlobalFullscreen.FrameLimit > 1000))
            {
                errors.Add("Global FPS limit (Fullscreen): enter an FPS value between 1 and 1000, or uncheck Enabled.");
            }

            foreach (var profile in Settings.GameLimits)
            {
                if (!string.IsNullOrWhiteSpace(profile.ManualExecutablePath) &&
                    !File.Exists(profile.ManualExecutablePath))
                {
                    errors.Add($"Manual executable path no longer exists: {profile.ManualExecutablePath}");
                }
            }

            foreach (var hotkey in Settings.Hotkeys)
            {
                if (hotkey.Enabled && hotkey.Key != Key.None && !hotkey.DisableCap &&
                    (hotkey.FrameLimit <= 0 || hotkey.FrameLimit > 1000))
                {
                    errors.Add($"Hotkey {hotkey.DisplayText}: enter an FPS value between 1 and 1000, or check \"Disable cap\".");
                }
            }

            var duplicateCombo = Settings.Hotkeys
                .Where(a => a.Enabled && a.Key != Key.None)
                .GroupBy(a => new { a.Modifiers, a.Key })
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicateCombo != null)
            {
                errors.Add($"The hotkey {duplicateCombo.First().DisplayText} is assigned more than once.");
            }

            return !errors.Any();
        }

        private void NormalizeSettings()
        {
            Settings.PresetsText = string.Join(", ", Settings.GetPresetValues().Select(FormatFps));
            Settings.RtssPath = Settings.RtssPath?.Trim() ?? string.Empty;
            Settings.GlobalDesktop.FrameLimitText = FormatFps(Settings.GlobalDesktop.FrameLimit);
            Settings.GlobalFullscreen.FrameLimitText = FormatFps(Settings.GlobalFullscreen.FrameLimit);

            foreach (var hotkey in Settings.Hotkeys)
            {
                hotkey.FrameLimitText = FormatFps(hotkey.FrameLimit);
            }
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
