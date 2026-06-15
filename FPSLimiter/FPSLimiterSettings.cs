using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FPSLimiter
{
    public class FPSLimiterSettings : ObservableObject
    {
        private string rtssPath = string.Empty;
        private bool autoStartRtss = true;
        private bool useGlobalProfileDuringLaunch = true;
        private bool enableProfileFileFallback = true;
        private string presetsText = "30, 60, 120";
        private List<GameLimitProfile> gameLimits = new List<GameLimitProfile>();
        private List<LimitSessionSnapshot> activeSessions = new List<LimitSessionSnapshot>();

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

        public bool EnableProfileFileFallback
        {
            get => enableProfileFileFallback;
            set => SetValue(ref enableProfileFileFallback, value);
        }

        public string PresetsText
        {
            get => presetsText;
            set => SetValue(ref presetsText, value);
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

        public List<int> GetPresetValues()
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
                !profile.Enabled &&
                string.IsNullOrWhiteSpace(profile.ManualExecutablePath) &&
                string.IsNullOrWhiteSpace(profile.LastResolvedExecutable))
            {
                GameLimits.Remove(profile);
            }
        }

        public static List<int> ParsePresetValues(string text)
        {
            var result = new List<int>();

            if (string.IsNullOrWhiteSpace(text))
            {
                return result;
            }

            var parts = text.Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                int value;
                if (int.TryParse(part.Trim(), out value) && value > 0 && value <= 1000)
                {
                    result.Add(value);
                }
            }

            return result.Distinct().OrderBy(a => a).ToList();
        }
    }

    public class GameLimitProfile
    {
        public Guid GameId { get; set; }
        public bool Enabled { get; set; }
        public int FrameLimit { get; set; }
        public string ManualExecutablePath { get; set; }
        public string LastResolvedExecutable { get; set; }
    }

    public class LimitSessionSnapshot
    {
        public Guid GameId { get; set; }
        public string GameName { get; set; }
        public string ExecutablePath { get; set; }
        public string ProfileName { get; set; }
        public int AppliedLimit { get; set; }
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
        public DateTime StartedAt { get; set; }
    }

    public class FPSLimiterSettingsViewModel : ObservableObject, ISettings
    {
        private readonly FPSLimiter plugin;
        private FPSLimiterSettings editingClone;
        private FPSLimiterSettings settings;

        public FPSLimiterSettings Settings
        {
            get => settings;
            set
            {
                settings = value;
                OnPropertyChanged();
            }
        }

        public FPSLimiterSettingsViewModel(FPSLimiter plugin)
        {
            this.plugin = plugin;

            var savedSettings = plugin.LoadPluginSettings<FPSLimiterSettings>();
            Settings = savedSettings ?? new FPSLimiterSettings();
        }

        public void SaveSettings()
        {
            plugin.SavePluginSettings(Settings);
        }

        public void BeginEdit()
        {
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
            Settings.PresetsText = string.Join(", ", Settings.GetPresetValues());
            Settings.RtssPath = Settings.RtssPath?.Trim() ?? string.Empty;
        }
    }
}
