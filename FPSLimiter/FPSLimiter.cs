using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace FPSLimiter
{
    public class FPSLimiter : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string ActiveMenuPrefix = "\u2713 ";
        private static readonly FpsSyncMode[] SyncModes =
        {
            FpsSyncMode.Async,
            FpsSyncMode.FrontEdgeSync,
            FpsSyncMode.BackEdgeSync
        };

        private FPSLimiterSettingsViewModel settings;
        private LimiterService limiterService;

        public override Guid Id { get; } = Guid.Parse("4b308964-9a0d-4775-b7c2-78b92af4d7b6");

        public FPSLimiter(IPlayniteAPI api) : base(api)
        {
            settings = new FPSLimiterSettingsViewModel(this, api);
            limiterService = new LimiterService(api, settings);

            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            if (settings.Settings.ActiveSessions.Any())
            {
                limiterService.RestoreAllActiveSessions(true);
            }
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            limiterService.RestoreAllActiveSessions(false);
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            limiterService.TryApplyForGame(args.Game, args.SourceAction, null, true);
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            limiterService.TryApplyForGame(args.Game, args.SourceAction, args.StartedProcessId, false);
            Task.Run(() => limiterService.RetargetAfterLaunch(args.Game, args.SourceAction, args.StartedProcessId));
        }

        public override void OnGameStartupCancelled(OnGameStartupCancelledEventArgs args)
        {
            limiterService.RestoreActiveSession(args.Game, false);
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            limiterService.RestoreActiveSession(args.Game, true);
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var games = args.Games ?? new List<Game>();
            if (!games.Any())
            {
                yield break;
            }

            var modeLabel = CurrentMode == PlayniteUiMode.Fullscreen ? "Fullscreen" : "Desktop";
            var menuRoot = $"FPS Limiter ({modeLabel})";

            foreach (var preset in limiterService.GetPresets())
            {
                yield return new GameMenuItem
                {
                    MenuSection = menuRoot,
                    Description = GetPresetMenuText(games, preset),
                    Action = _ => limiterService.SetGameLimit(games, preset)
                };
            }

            yield return new GameMenuItem
            {
                MenuSection = menuRoot,
                Description = GetCustomMenuText(games),
                Action = _ => SetCustomCap(games)
            };

            yield return new GameMenuItem
            {
                MenuSection = menuRoot,
                Description = "Disable FPS cap",
                Action = _ => limiterService.DisableGameLimit(games)
            };

            foreach (var mode in SyncModes)
            {
                yield return new GameMenuItem
                {
                    MenuSection = $"{menuRoot}|Sync mode",
                    Description = GetSyncModeMenuText(games, mode),
                    Action = _ => limiterService.SetGameSyncMode(games, mode)
                };
            }

            if (PlayniteApi.ApplicationInfo.Mode != ApplicationMode.Desktop)
            {
                yield break;
            }

            yield return new GameMenuItem
            {
                MenuSection = $"{menuRoot}|Target executable",
                Description = "Choose target executable...",
                Action = _ => ChooseManualExecutable(games)
            };

            yield return new GameMenuItem
            {
                MenuSection = $"{menuRoot}|Target executable",
                Description = "Clear manual target executable",
                Action = _ => ClearManualExecutable(games)
            };
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            var modeLabel = CurrentMode == PlayniteUiMode.Fullscreen ? "Fullscreen" : "Desktop";
            var menuRoot = $"FPS Limiter ({modeLabel})";

            foreach (var preset in limiterService.GetPresets())
            {
                yield return new MainMenuItem
                {
                    MenuSection = $"{menuRoot}|Global FPS limit",
                    Description = GetGlobalPresetMenuText(preset),
                    Action = _ => limiterService.SetGlobalLimit(preset)
                };
            }

            yield return new MainMenuItem
            {
                MenuSection = $"{menuRoot}|Global FPS limit",
                Description = GetGlobalCustomMenuText(),
                Action = _ => SetGlobalCustomCap()
            };

            yield return new MainMenuItem
            {
                MenuSection = $"{menuRoot}|Global FPS limit",
                Description = "Disable global FPS cap",
                Action = _ => limiterService.DisableGlobalLimit()
            };

            foreach (var mode in SyncModes)
            {
                yield return new MainMenuItem
                {
                    MenuSection = $"{menuRoot}|Global sync mode",
                    Description = GetGlobalSyncModeMenuText(mode),
                    Action = _ => limiterService.SetGlobalSyncMode(mode)
                };
            }
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        private PlayniteUiMode CurrentMode =>
            PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen
                ? PlayniteUiMode.Fullscreen
                : PlayniteUiMode.Desktop;

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new FPSLimiterSettingsView();
        }

        private string GetPresetMenuText(List<Game> games, double preset)
        {
            var presetText = FormatFps(preset);
            if (games.Count == 1)
            {
                var profile = limiterService.GetGameProfile(games[0]);
                var mode = profile?.GetMode(CurrentMode);
                if (mode != null && mode.Enabled && mode.FrameLimit == preset)
                {
                    return $"{ActiveMenuPrefix}{presetText} FPS";
                }
            }

            return $"{presetText} FPS";
        }

        private string GetCustomMenuText(List<Game> games)
        {
            if (games.Count == 1)
            {
                var profile = limiterService.GetGameProfile(games[0]);
                var mode = profile?.GetMode(CurrentMode);
                if (mode != null &&
                    mode.Enabled &&
                    mode.FrameLimit > 0 &&
                    !limiterService.GetPresets().Contains(mode.FrameLimit))
                {
                    return $"{ActiveMenuPrefix}Custom FPS cap...";
                }
            }

            return "Custom FPS cap...";
        }

        private void SetCustomCap(List<Game> games)
        {
            var defaultValue = "60";
            if (games.Count == 1)
            {
                var profile = limiterService.GetGameProfile(games[0]);
                var mode = profile?.GetMode(CurrentMode);
                if (mode != null && mode.Enabled && mode.FrameLimit > 0)
                {
                    defaultValue = FormatFps(mode.FrameLimit);
                }
            }

            var result = PlayniteApi.Dialogs.SelectString("Enter an FPS cap between 1 and 1000. Decimals are allowed, e.g. 59.9.", "FPS Limiter", defaultValue);
            if (!result.Result)
            {
                return;
            }

            double cap;
            if (!double.TryParse(result.SelectedString, NumberStyles.Float, CultureInfo.InvariantCulture, out cap) || cap <= 0 || cap > 1000)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Enter a number between 1 and 1000, e.g. 60 or 59.9.", "FPS Limiter");
                return;
            }

            limiterService.SetGameLimit(games, Math.Round(cap, 3));
        }

        private static string FormatFps(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private void ChooseManualExecutable(List<Game> games)
        {
            if (games.Count != 1)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Choose exactly one game before setting a manual target executable.", "FPS Limiter");
                return;
            }

            var executable = PlayniteApi.Dialogs.SelectFile("Executable files (*.exe)|*.exe|All files (*.*)|*.*");
            if (string.IsNullOrWhiteSpace(executable))
            {
                return;
            }

            if (!File.Exists(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                PlayniteApi.Dialogs.ShowErrorMessage("The target executable must be an .exe file.", "FPS Limiter");
                return;
            }

            limiterService.SetManualExecutable(games[0], executable);
        }

        private void ClearManualExecutable(List<Game> games)
        {
            if (games.Count != 1)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Choose exactly one game before clearing a manual target executable.", "FPS Limiter");
                return;
            }

            limiterService.ClearManualExecutable(games[0]);
        }

        private string GetSyncModeMenuText(List<Game> games, FpsSyncMode mode)
        {
            var name = FpsSyncModeNames.GetDisplayName(mode);
            if (games.Count == 1)
            {
                var profile = limiterService.GetGameProfile(games[0]);
                var defaultSyncMode = CurrentMode == PlayniteUiMode.Desktop ? FpsSyncMode.FrontEdgeSync : FpsSyncMode.Async;
                var current = profile?.GetMode(CurrentMode).SyncMode ?? defaultSyncMode;
                if (current == mode)
                {
                    return $"{ActiveMenuPrefix}{name}";
                }
            }

            return name;
        }

        private string GetGlobalPresetMenuText(double preset)
        {
            var presetText = FormatFps(preset);
            if (limiterService.IsGlobalLimitEnabled && limiterService.GlobalFrameLimit == preset)
            {
                return $"{ActiveMenuPrefix}{presetText} FPS";
            }

            return $"{presetText} FPS";
        }

        private string GetGlobalCustomMenuText()
        {
            if (limiterService.IsGlobalLimitEnabled &&
                limiterService.GlobalFrameLimit > 0 &&
                !limiterService.GetPresets().Contains(limiterService.GlobalFrameLimit))
            {
                return $"{ActiveMenuPrefix}Custom FPS cap...";
            }

            return "Custom FPS cap...";
        }

        private void SetGlobalCustomCap()
        {
            var defaultValue = limiterService.GlobalFrameLimit > 0 ? FormatFps(limiterService.GlobalFrameLimit) : "60";
            var result = PlayniteApi.Dialogs.SelectString("Enter a global FPS cap between 1 and 1000. Decimals are allowed, e.g. 59.9.", "FPS Limiter", defaultValue);
            if (!result.Result)
            {
                return;
            }

            double cap;
            if (!double.TryParse(result.SelectedString, NumberStyles.Float, CultureInfo.InvariantCulture, out cap) || cap <= 0 || cap > 1000)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Enter a number between 1 and 1000, e.g. 60 or 59.9.", "FPS Limiter");
                return;
            }

            limiterService.SetGlobalLimit(Math.Round(cap, 3));
        }

        private string GetGlobalSyncModeMenuText(FpsSyncMode mode)
        {
            var name = FpsSyncModeNames.GetDisplayName(mode);
            return limiterService.GlobalSyncMode == mode ? $"{ActiveMenuPrefix}{name}" : name;
        }

    }
}
