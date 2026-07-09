using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace FPSLimiter
{
    public class FPSLimiter : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string ActiveMenuPrefix = "\u2713 ";
        // Used for the Global sync mode menu, which has no parent to fall back to.
        private static readonly FpsSyncMode[] SyncModes =
        {
            FpsSyncMode.Async,
            FpsSyncMode.FrontEdgeSync,
            FpsSyncMode.BackEdgeSync,
            FpsSyncMode.ReflexSync
        };

        // Used for per-game sync mode menus (game context menu, top bar button), which can also
        // track whichever Global sync mode is currently active instead of a fixed choice.
        private static readonly FpsSyncMode[] GameSyncModes =
        {
            FpsSyncMode.UseGlobal,
            FpsSyncMode.Async,
            FpsSyncMode.FrontEdgeSync,
            FpsSyncMode.BackEdgeSync,
            FpsSyncMode.ReflexSync
        };

        // Used for the Global Reflex marker injection menu, which has no parent to fall back to.
        private static readonly ReflexMarkerMode[] ReflexMarkerModes =
        {
            ReflexMarkerMode.Enabled,
            ReflexMarkerMode.Disabled
        };

        // Used for per-game Reflex marker injection menus, which can also track whichever Global
        // setting is currently active instead of a fixed choice.
        private static readonly ReflexMarkerMode[] GameReflexMarkerModes =
        {
            ReflexMarkerMode.UseGlobal,
            ReflexMarkerMode.Enabled,
            ReflexMarkerMode.Disabled
        };

        private FPSLimiterSettingsViewModel settings;
        private LimiterService limiterService;
        private HotkeyManager hotkeyManager;

        private const string UpdateRepoOwner = "pahan3000";
        private const string UpdateRepoName = "playnite-fps-limiter";
        private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

        public override Guid Id { get; } = Guid.Parse("cd5ffd73-3b1a-45d7-86b4-9183f1d858f5");

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

            Task.Run(() => CheckForUpdatesAsync());
        }

        /// <summary>
        /// Checks the GitHub releases page for a newer published version and, if found, shows a
        /// Playnite notification linking to the release. Automatic (startup) calls are throttled
        /// to once per <see cref="UpdateCheckInterval"/>; pass <paramref name="manual"/> to bypass
        /// the throttle and surface the result (found/up to date/error) directly to the user,
        /// e.g. from the "Check for updates now" menu item.
        /// </summary>
        private async Task CheckForUpdatesAsync(bool manual = false)
        {
            try
            {
                if (!manual && DateTime.UtcNow - settings.Settings.LastUpdateCheckUtc < UpdateCheckInterval)
                {
                    return;
                }

                settings.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
                settings.PersistUpdateCheckState();

                string json;
                try
                {
                    using (var client = new HttpClient())
                    {
                        client.Timeout = TimeSpan.FromSeconds(10);
                        client.DefaultRequestHeaders.UserAgent.ParseAdd("PlayniteFPSLimiter-UpdateCheck");
                        var url = $"https://api.github.com/repos/{UpdateRepoOwner}/{UpdateRepoName}/releases/latest";
                        json = await client.GetStringAsync(url).ConfigureAwait(false);
                    }
                }
                catch (Exception e)
                {
                    logger.Warn(e, "FPS Limiter update check failed.");
                    if (manual)
                    {
                        PlayniteApi.Dialogs.ShowErrorMessage(
                            $"FPS Limiter could not reach GitHub to check for updates.\n\n{e.Message}",
                            "FPS Limiter");
                    }

                    return;
                }

                var tagMatch = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                var urlMatch = Regex.Match(json, "\"html_url\"\\s*:\\s*\"([^\"]+)\"");

                if (!tagMatch.Success)
                {
                    if (manual)
                    {
                        PlayniteApi.Dialogs.ShowErrorMessage(
                            "FPS Limiter could not read release information from GitHub. The latest release may be missing, or marked as a draft/pre-release (those are excluded from the check).",
                            "FPS Limiter");
                    }

                    return;
                }

                var latestTag = tagMatch.Groups[1].Value.TrimStart('v', 'V');
                var releaseUrl = urlMatch.Success
                    ? urlMatch.Groups[1].Value
                    : $"https://github.com/{UpdateRepoOwner}/{UpdateRepoName}/releases/latest";

                var currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

                if (!Version.TryParse(latestTag, out var latestVersion))
                {
                    if (manual)
                    {
                        PlayniteApi.Dialogs.ShowErrorMessage(
                            $"FPS Limiter could not parse the latest release tag ('{tagMatch.Groups[1].Value}'). Release tags should look like 'v1.0.3'.",
                            "FPS Limiter");
                    }

                    return;
                }

                if (latestVersion <= currentVersion)
                {
                    if (manual)
                    {
                        PlayniteApi.Dialogs.ShowMessage(
                            $"FPS Limiter is up to date (v{currentVersion.ToString(3)}).\n\nLatest GitHub release: v{latestVersion.ToString(3)}.",
                            "FPS Limiter");
                    }

                    return;
                }

                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "FPSLimiter_UpdateAvailable_" + latestTag,
                    $"FPS Limiter v{latestTag} is available (you have v{currentVersion.ToString(3)}). Click to view the release.",
                    NotificationType.Info,
                    () => OpenUrl(releaseUrl)));

                if (manual)
                {
                    var response = PlayniteApi.Dialogs.ShowMessage(
                        $"FPS Limiter v{latestTag} is available (you have v{currentVersion.ToString(3)}).\n\nOpen the release page now?",
                        "FPS Limiter",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);

                    if (response == MessageBoxResult.Yes)
                    {
                        OpenUrl(releaseUrl);
                    }
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "FPS Limiter update check failed.");
                if (manual)
                {
                    PlayniteApi.Dialogs.ShowErrorMessage(
                        $"FPS Limiter could not check for updates.\n\n{e.Message}",
                        "FPS Limiter");
                }
            }
        }

        private static void OpenUrl(string url)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
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

            foreach (var mode in GameSyncModes)
            {
                yield return new GameMenuItem
                {
                    MenuSection = $"{menuRoot}|Sync mode",
                    Description = GetSyncModeMenuText(games, mode),
                    Action = _ => limiterService.SetGameSyncMode(games, mode)
                };
            }

            foreach (var mode in GameReflexMarkerModes)
            {
                yield return new GameMenuItem
                {
                    MenuSection = $"{menuRoot}|Reflex markers",
                    Description = GetReflexMarkerMenuText(games, mode),
                    Action = _ => limiterService.SetGameReflexMarkers(games, mode)
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

            foreach (var mode in ReflexMarkerModes)
            {
                yield return new MainMenuItem
                {
                    MenuSection = $"{menuRoot}|Global Reflex markers",
                    Description = GetGlobalReflexMarkerMenuText(mode),
                    Action = _ => limiterService.SetGlobalReflexMarkers(mode)
                };
            }

            yield return new MainMenuItem
            {
                MenuSection = menuRoot,
                Description = "Check for updates now",
                Action = _ => Task.Run(() => CheckForUpdatesAsync(true))
            };
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
            var options = GameSyncModes
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
            if (index >= 0 && index < GameSyncModes.Length)
            {
                limiterService.SetGameSyncMode(games, GameSyncModes[index]);
            }
        }

        private string GetTopPanelSyncModeMenuText(List<Game> games)
        {
            if (games.Count == 1)
            {
                return $"Sync mode: {GetSyncModeDisplayName(GetSelectedSyncMode(games[0]))}";
            }

            return "Sync mode...";
        }

        /// <summary>The sync mode currently stored on the game's profile (UseGlobal if none set yet).</summary>
        private FpsSyncMode GetSelectedSyncMode(Game game)
        {
            var profile = limiterService.GetGameProfile(game);
            return profile?.GetMode(CurrentMode).SyncMode ?? FpsSyncMode.UseGlobal;
        }

        /// <summary>Display text for a sync mode option, spelling out what "Use Global" currently resolves to.</summary>
        private string GetSyncModeDisplayName(FpsSyncMode mode)
        {
            if (mode == FpsSyncMode.UseGlobal)
            {
                return $"Use Global Sync Mode ({FpsSyncModeNames.GetDisplayName(limiterService.GlobalSyncMode)})";
            }

            return FpsSyncModeNames.GetDisplayName(mode);
        }

        /// <summary>The Reflex marker setting currently stored on the game's profile (UseGlobal if none set yet).</summary>
        private ReflexMarkerMode GetSelectedReflexMarkers(Game game)
        {
            var profile = limiterService.GetGameProfile(game);
            return profile?.GetMode(CurrentMode).ReflexMarkers ?? ReflexMarkerMode.UseGlobal;
        }

        /// <summary>Display text for a Reflex marker option, spelling out what "Use Global" currently resolves to.</summary>
        private string GetReflexMarkerDisplayName(ReflexMarkerMode mode)
        {
            if (mode == ReflexMarkerMode.UseGlobal)
            {
                return $"Use Global Setting ({ReflexMarkerModeNames.GetDisplayName(limiterService.GlobalReflexMarkers)})";
            }

            return ReflexMarkerModeNames.GetDisplayName(mode);
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
            var name = GetSyncModeDisplayName(mode);

            if (games.Count == 1 && GetSelectedSyncMode(games[0]) == mode)
            {
                return $"{ActiveMenuPrefix}{name}";
            }

            return name;
        }

        private string GetReflexMarkerMenuText(List<Game> games, ReflexMarkerMode mode)
        {
            var name = GetReflexMarkerDisplayName(mode);

            if (games.Count == 1 && GetSelectedReflexMarkers(games[0]) == mode)
            {
                return $"{ActiveMenuPrefix}{name}";
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

        private string GetGlobalReflexMarkerMenuText(ReflexMarkerMode mode)
        {
            var name = ReflexMarkerModeNames.GetDisplayName(mode);
            return limiterService.GlobalReflexMarkers == mode ? $"{ActiveMenuPrefix}{name}" : name;
        }

    }
}
