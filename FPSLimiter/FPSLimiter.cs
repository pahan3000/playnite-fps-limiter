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
using System.Windows;
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
        private HotkeyManager hotkeyManager;

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

            InitializeHotkeys();
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            limiterService.RestoreAllActiveSessions(false);
            hotkeyManager?.Dispose();
        }

        private void InitializeHotkeys()
        {
            try
            {
                hotkeyManager = new HotkeyManager();
                hotkeyManager.HotkeyPressed += OnHotkeyPressed;

                var window = Application.Current?.MainWindow;
                if (window == null)
                {
                    if (Application.Current != null)
                    {
                        Application.Current.Activated += OnApplicationActivatedForHotkeys;
                    }

                    return;
                }

                if (window.IsLoaded)
                {
                    AttachHotkeys(window);
                }
                else
                {
                    window.SourceInitialized += (s, e) => AttachHotkeys(window);
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "FPS Limiter could not initialize hotkeys.");
            }
        }

        private void OnApplicationActivatedForHotkeys(object sender, EventArgs e)
        {
            if (Application.Current != null)
            {
                Application.Current.Activated -= OnApplicationActivatedForHotkeys;
            }

            var window = Application.Current?.MainWindow;
            if (window != null)
            {
                AttachHotkeys(window);
            }
        }

        private void AttachHotkeys(Window window)
        {
            if (hotkeyManager != null && hotkeyManager.Attach(window))
            {
                RefreshHotkeys();
            }
        }

        /// <summary>Re-registers all enabled hotkeys from current settings. Call after settings change.</summary>
        public void RefreshHotkeys()
        {
            hotkeyManager?.UpdateBindings(settings.Settings.Hotkeys);
        }

        private void OnHotkeyPressed(HotkeyBinding binding)
        {
            try
            {
                var games = ResolveHotkeyTargetGames();
                if (!games.Any())
                {
                    logger.Debug($"FPS Limiter hotkey {binding.DisplayText} pressed but no running or selected game to target.");
                    return;
                }

                if (binding.DisableCap)
                {
                    limiterService.DisableGameLimit(games);
                    logger.Info($"FPS Limiter hotkey {binding.DisplayText}: cap disabled for {DescribeGames(games)}.");
                }
                else
                {
                    limiterService.SetGameLimit(games, binding.FrameLimit);
                    logger.Info($"FPS Limiter hotkey {binding.DisplayText}: {FormatFps(binding.FrameLimit)} FPS applied to {DescribeGames(games)}.");
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "FPS Limiter failed to apply a hotkey.");
            }
        }

        /// <summary>
        /// Targets whichever game(s) are actually running right now; if none are running, falls
        /// back to the game(s) selected in the library.
        /// </summary>
        private List<Game> ResolveHotkeyTargetGames()
        {
            var runningGameIds = limiterService.GetRunningGameIds().Distinct().ToList();
            if (runningGameIds.Any())
            {
                return runningGameIds
                    .Select(id => PlayniteApi.Database.Games.Get(id))
                    .Where(g => g != null)
                    .ToList();
            }

            return PlayniteApi.MainView.SelectedGames?.ToList() ?? new List<Game>();
        }

        private static string DescribeGames(List<Game> games)
        {
            return games.Count == 1 ? games[0].Name : $"{games.Count} games";
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            limiterService.TryApplyForGame(args.Game, args.SourceAction, null, true);
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            limiterService.NotifyGameStarted(args.Game.Id);
            limiterService.TryApplyForGame(args.Game, args.SourceAction, args.StartedProcessId, false);
            Task.Run(() => limiterService.RetargetAfterLaunch(args.Game, args.SourceAction, args.StartedProcessId));
        }

        public override void OnGameStartupCancelled(OnGameStartupCancelledEventArgs args)
        {
            limiterService.NotifyGameStopped(args.Game.Id);
            limiterService.RestoreActiveSession(args.Game, false);
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            limiterService.NotifyGameStopped(args.Game.Id);
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

        public override IEnumerable<TopPanelItem> GetTopPanelItems()
        {
            yield return new TopPanelItem
            {
                Icon = GetIconPath(),
                Title = "FPS Limiter (apply to selected game)",
                Activated = () => ShowTopPanelMenu()
            };
        }

        private string GetIconPath()
        {
            var cachedIconPath = Path.Combine(Path.GetTempPath(), "FPSLimiter_RTSS_icon.png");

            try
            {
                var rtssPath = limiterService.ResolveRtssPath();
                if (!string.IsNullOrWhiteSpace(rtssPath) && File.Exists(rtssPath))
                {
                    var rtssWriteTime = File.GetLastWriteTimeUtc(rtssPath);
                    var needsExtract = !File.Exists(cachedIconPath) ||
                        File.GetLastWriteTimeUtc(cachedIconPath) < rtssWriteTime;

                    if (needsExtract)
                    {
                        using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(rtssPath))
                        {
                            if (icon != null)
                            {
                                using (var bitmap = icon.ToBitmap())
                                {
                                    bitmap.Save(cachedIconPath, System.Drawing.Imaging.ImageFormat.Png);
                                }
                            }
                        }
                    }

                    if (File.Exists(cachedIconPath))
                    {
                        return cachedIconPath;
                    }
                }
            }
            catch (Exception e)
            {
                logger.Debug(e, "Could not extract RTSS icon, falling back to bundled icon.");
            }

            var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var directory = Path.GetDirectoryName(assemblyLocation);
            return Path.Combine(directory ?? string.Empty, "icon.png");
        }

        private void ShowTopPanelMenu()
        {
            var games = PlayniteApi.MainView.SelectedGames?.ToList() ?? new List<Game>();
            if (!games.Any())
            {
                PlayniteApi.Dialogs.ShowMessage("Select a game in the library first.", "FPS Limiter");
                return;
            }

            var presetList = limiterService.GetPresets().ToList();
            var options = new List<GenericItemOption>();

            foreach (var preset in presetList)
            {
                options.Add(new GenericItemOption(GetPresetMenuText(games, preset), string.Empty));
            }

            var customOption = new GenericItemOption(GetCustomMenuText(games), "Custom...");
            options.Add(customOption);

            var disableOption = new GenericItemOption("Disable FPS cap", "Disable");
            options.Add(disableOption);

            var syncModeOption = new GenericItemOption(GetTopPanelSyncModeMenuText(games), "Sync mode");
            options.Add(syncModeOption);

            var caption = games.Count == 1
                ? $"FPS Limiter - {games[0].Name}"
                : $"FPS Limiter - {games.Count} games selected";

            var selected = PlayniteApi.Dialogs.ChooseItemWithSearch(
                options,
                _ => options,
                null,
                caption);

            if (selected == null)
            {
                return;
            }

            if (ReferenceEquals(selected, customOption))
            {
                SetCustomCap(games);
                return;
            }

            if (ReferenceEquals(selected, disableOption))
            {
                limiterService.DisableGameLimit(games);
                return;
            }

            if (ReferenceEquals(selected, syncModeOption))
            {
                ShowTopPanelSyncModeMenu(games);
                return;
            }

            var index = options.IndexOf(selected);
            if (index >= 0 && index < presetList.Count)
            {
                limiterService.SetGameLimit(games, presetList[index]);
            }
        }

        private void ShowTopPanelSyncModeMenu(List<Game> games)
        {
            var options = SyncModes
                .Select(mode => new GenericItemOption(GetSyncModeMenuText(games, mode), string.Empty))
                .ToList();

            var caption = games.Count == 1
                ? $"FPS Limiter Sync Mode - {games[0].Name}"
                : $"FPS Limiter Sync Mode - {games.Count} games selected";

            var selected = PlayniteApi.Dialogs.ChooseItemWithSearch(
                options,
                _ => options,
                null,
                caption);

            if (selected == null)
            {
                return;
            }

            var index = options.IndexOf(selected);
            if (index >= 0 && index < SyncModes.Length)
            {
                limiterService.SetGameSyncMode(games, SyncModes[index]);
            }
        }

        private string GetTopPanelSyncModeMenuText(List<Game> games)
        {
            if (games.Count == 1)
            {
                var profile = limiterService.GetGameProfile(games[0]);
                var defaultSyncMode = CurrentMode == PlayniteUiMode.Desktop ? FpsSyncMode.FrontEdgeSync : FpsSyncMode.Async;
                var current = profile?.GetMode(CurrentMode).SyncMode ?? defaultSyncMode;
                return $"Sync mode: {FpsSyncModeNames.GetDisplayName(current)}";
            }

            return "Sync mode...";
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
