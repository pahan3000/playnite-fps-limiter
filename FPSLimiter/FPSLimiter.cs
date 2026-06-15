using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Controls;

namespace FPSLimiter
{
    public class FPSLimiter : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private FPSLimiterSettingsViewModel settings;
        private LimiterService limiterService;

        public override Guid Id { get; } = Guid.Parse("4b308964-9a0d-4775-b7c2-78b92af4d7b6");

        public FPSLimiter(IPlayniteAPI api) : base(api)
        {
            settings = new FPSLimiterSettingsViewModel(this);
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

            foreach (var preset in limiterService.GetPresets())
            {
                yield return new GameMenuItem
                {
                    MenuSection = "FPS Limiter",
                    Description = GetPresetMenuText(games, preset),
                    Action = _ => limiterService.SetGameLimit(games, preset)
                };
            }

            yield return new GameMenuItem
            {
                MenuSection = "FPS Limiter",
                Description = "Custom FPS cap...",
                Action = _ => SetCustomCap(games)
            };

            yield return new GameMenuItem
            {
                MenuSection = "FPS Limiter",
                Description = "Disable FPS cap",
                Action = _ => limiterService.DisableGameLimit(games)
            };

            yield return new GameMenuItem
            {
                MenuSection = "FPS Limiter|Target executable",
                Description = "Choose target executable...",
                Action = _ => ChooseManualExecutable(games)
            };

            yield return new GameMenuItem
            {
                MenuSection = "FPS Limiter|Target executable",
                Description = "Clear manual target executable",
                Action = _ => ClearManualExecutable(games)
            };
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            foreach (var preset in limiterService.GetPresets())
            {
                yield return new MainMenuItem
                {
                    MenuSection = "@FPS Limiter",
                    Description = $"Set selected game to {preset} FPS",
                    Action = _ => SetPresetForSelectedGame(preset)
                };
            }

            yield return new MainMenuItem
            {
                MenuSection = "@FPS Limiter",
                Description = "Disable cap for selected game",
                Action = _ => DisableSelectedGame()
            };
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new FPSLimiterSettingsView();
        }

        private string GetPresetMenuText(List<Game> games, int preset)
        {
            if (games.Count == 1)
            {
                var profile = limiterService.GetGameProfile(games[0]);
                if (profile != null && profile.Enabled && profile.FrameLimit == preset)
                {
                    return $"{preset} FPS [active]";
                }
            }

            return $"{preset} FPS";
        }

        private void SetCustomCap(List<Game> games)
        {
            var defaultValue = "60";
            if (games.Count == 1)
            {
                var profile = limiterService.GetGameProfile(games[0]);
                if (profile != null && profile.Enabled && profile.FrameLimit > 0)
                {
                    defaultValue = profile.FrameLimit.ToString();
                }
            }

            var result = PlayniteApi.Dialogs.SelectString("Enter an FPS cap between 1 and 1000.", "FPS Limiter", defaultValue);
            if (!result.Result)
            {
                return;
            }

            int cap;
            if (!int.TryParse(result.SelectedString, out cap) || cap <= 0 || cap > 1000)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Enter a whole number between 1 and 1000.", "FPS Limiter");
                return;
            }

            limiterService.SetGameLimit(games, cap);
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

        private void SetPresetForSelectedGame(int preset)
        {
            var selectedGame = GetSingleSelectedGame();
            if (selectedGame == null)
            {
                return;
            }

            limiterService.SetGameLimit(new[] { selectedGame }, preset);
        }

        private void DisableSelectedGame()
        {
            var selectedGame = GetSingleSelectedGame();
            if (selectedGame == null)
            {
                return;
            }

            limiterService.DisableGameLimit(new[] { selectedGame });
        }

        private Game GetSingleSelectedGame()
        {
            var selectedGames = PlayniteApi.MainView.SelectedGames?.ToList() ?? new List<Game>();
            if (selectedGames.Count == 1)
            {
                return selectedGames[0];
            }

            if (selectedGames.Count == 0)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Select a game before using FPS Limiter.", "FPS Limiter");
            }
            else
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Select only one game before using FPS Limiter from the fullscreen Extensions menu.", "FPS Limiter");
            }

            return null;
        }
    }
}
