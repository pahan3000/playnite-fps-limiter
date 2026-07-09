using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace FPSLimiter
{
    public class LimiterService
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly IPlayniteAPI playniteApi;
        private readonly FPSLimiterSettingsViewModel settingsViewModel;
        private readonly RtssLimiterBackend rtssBackend;
        private readonly GameTargetResolver targetResolver;

        // Tracks which games are actually running right now, independent of whether they currently
        // have an active RTSS cap session. ActiveSessions is *not* a reliable "is this game running"
        // signal on its own: disabling the cap (or any restore) removes the game's entry from
        // ActiveSessions even though the game keeps running, which used to make later attempts to
        // re-apply a cap silently no-op because ReapplyIfRunning/hotkey targeting thought nothing
        // was running anymore.
        private readonly HashSet<Guid> runningGameIds = new HashSet<Guid>();

        public LimiterService(IPlayniteAPI playniteApi, FPSLimiterSettingsViewModel settingsViewModel)
        {
            this.playniteApi = playniteApi;
            this.settingsViewModel = settingsViewModel;
            rtssBackend = new RtssLimiterBackend(settingsViewModel.Settings);
            targetResolver = new GameTargetResolver(playniteApi);
        }

        private FPSLimiterSettings Settings => settingsViewModel.Settings;

        private PlayniteUiMode CurrentMode =>
            playniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen
                ? PlayniteUiMode.Fullscreen
                : PlayniteUiMode.Desktop;

        public IEnumerable<double> GetPresets()
        {
            return Settings.GetPresetValues();
        }

        public string ResolveRtssPath()
        {
            return rtssBackend.ResolveRtssPath();
        }

        public bool IsGlobalLimitEnabled => Settings.GetGlobal(CurrentMode).Enabled;

        public double GlobalFrameLimit => Settings.GetGlobal(CurrentMode).FrameLimit;

        public FpsSyncMode GlobalSyncMode => Settings.GetGlobal(CurrentMode).SyncMode;

        public ReflexMarkerMode GlobalReflexMarkers => Settings.GetGlobal(CurrentMode).ReflexMarkers;

        public void SetGlobalLimit(double frameLimit)
        {
            var global = Settings.GetGlobal(CurrentMode);
            global.Enabled = true;
            global.FrameLimit = frameLimit;
            settingsViewModel.SaveSettings();
        }

        public void DisableGlobalLimit()
        {
            var global = Settings.GetGlobal(CurrentMode);
            global.Enabled = false;
            global.FrameLimit = 0;
            settingsViewModel.SaveSettings();
        }

        public void SetGlobalSyncMode(FpsSyncMode syncMode)
        {
            Settings.GetGlobal(CurrentMode).SyncMode = syncMode;
            settingsViewModel.SaveSettings();
        }

        public void SetGlobalReflexMarkers(ReflexMarkerMode mode)
        {
            Settings.GetGlobal(CurrentMode).ReflexMarkers = mode;
            settingsViewModel.SaveSettings();
        }

        public GameLimitProfile GetGameProfile(Game game)
        {
            return Settings.GetGameProfile(game.Id);
        }

        /// <summary>
        /// Resolves the sync mode a per-game ModeLimitSettings actually applies: its own explicit
        /// choice, or the current Global sync mode (for the same Playnite UI mode) when the game
        /// is left on <see cref="FpsSyncMode.UseGlobal"/>.
        /// </summary>
        private FpsSyncMode ResolveEffectiveSyncMode(ModeLimitSettings modeLimit, PlayniteUiMode mode)
        {
            if (modeLimit != null && modeLimit.SyncMode != FpsSyncMode.UseGlobal)
            {
                return modeLimit.SyncMode;
            }

            return Settings.GetGlobal(mode).SyncMode;
        }

        /// <summary>The sync mode a single game will actually apply right now, resolving "Use Global" if set.</summary>
        public FpsSyncMode GetEffectiveGameSyncMode(Game game)
        {
            var profile = GetGameProfile(game);
            return ResolveEffectiveSyncMode(profile?.GetMode(CurrentMode), CurrentMode);
        }

        /// <summary>
        /// Resolves the Reflex marker setting a per-game ModeLimitSettings actually applies: its own
        /// explicit choice, or the current Global Reflex marker setting (for the same Playnite UI
        /// mode) when the game is left on <see cref="ReflexMarkerMode.UseGlobal"/>.
        /// </summary>
        private ReflexMarkerMode ResolveEffectiveReflexMarkers(ModeLimitSettings modeLimit, PlayniteUiMode mode)
        {
            if (modeLimit != null && modeLimit.ReflexMarkers != ReflexMarkerMode.UseGlobal)
            {
                return modeLimit.ReflexMarkers;
            }

            return Settings.GetGlobal(mode).ReflexMarkers;
        }

        /// <summary>The Reflex marker setting a single game will actually apply right now, resolving "Use Global" if set.</summary>
        public ReflexMarkerMode GetEffectiveGameReflexMarkers(Game game)
        {
            var profile = GetGameProfile(game);
            return ResolveEffectiveReflexMarkers(profile?.GetMode(CurrentMode), CurrentMode);
        }

        /// <summary>Marks a game as currently running. Call once the game process has actually started.</summary>
        public void NotifyGameStarted(Guid gameId)
        {
            runningGameIds.Add(gameId);
        }

        /// <summary>Marks a game as no longer running (stopped, or the launch was cancelled).</summary>
        public void NotifyGameStopped(Guid gameId)
        {
            runningGameIds.Remove(gameId);
        }

        /// <summary>Games currently known to be running, used to target hotkeys at the right game(s).</summary>
        public IEnumerable<Guid> GetRunningGameIds()
        {
            return runningGameIds.ToList();
        }

        public void SetGameLimit(IEnumerable<Game> games, double frameLimit)
        {
            var gameList = games.ToList();
            foreach (var game in gameList)
            {
                var profile = Settings.GetOrCreateGameProfile(game.Id);
                var mode = profile.GetMode(CurrentMode);
                mode.Enabled = true;
                mode.FrameLimit = frameLimit;
            }

            settingsViewModel.SaveSettings();

            foreach (var game in gameList)
            {
                ReapplyIfRunning(game);
            }
        }

        public void SetGameSyncMode(IEnumerable<Game> games, FpsSyncMode syncMode)
        {
            var gameList = games.ToList();
            foreach (var game in gameList)
            {
                var profile = Settings.GetOrCreateGameProfile(game.Id);
                profile.GetMode(CurrentMode).SyncMode = syncMode;
            }

            settingsViewModel.SaveSettings();

            foreach (var game in gameList)
            {
                ReapplyIfRunning(game);
            }
        }

        public void SetGameReflexMarkers(IEnumerable<Game> games, ReflexMarkerMode mode)
        {
            var gameList = games.ToList();
            foreach (var game in gameList)
            {
                var profile = Settings.GetOrCreateGameProfile(game.Id);
                profile.GetMode(CurrentMode).ReflexMarkers = mode;
            }

            settingsViewModel.SaveSettings();

            foreach (var game in gameList)
            {
                ReapplyIfRunning(game);
            }
        }

        /// <summary>
        /// If the game is currently running, immediately re-applies the limiter with the latest
        /// settings so the change takes effect live instead of waiting for the next launch. This is
        /// keyed off actual running state, not off whether a cap session is currently tracked, so it
        /// keeps working even right after the cap was disabled while the game is still running.
        /// </summary>
        private void ReapplyIfRunning(Game game)
        {
            if (!runningGameIds.Contains(game.Id))
            {
                return;
            }

            try
            {
                TryApplyForGame(game, null, null, false);
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to live-update FPS limit for {game.Name}.");
            }
        }

        public void DisableGameLimit(IEnumerable<Game> games)
        {
            foreach (var game in games)
            {
                RestoreActiveSession(game, true);

                var profile = Settings.GetOrCreateGameProfile(game.Id);
                var mode = profile.GetMode(CurrentMode);
                mode.Enabled = false;
                mode.FrameLimit = 0;
                Settings.RemoveEmptyGameProfile(game.Id);
            }

            settingsViewModel.SaveSettings();
        }

        public void SetManualExecutable(Game game, string executablePath)
        {
            var profile = Settings.GetOrCreateGameProfile(game.Id);
            profile.ManualExecutablePath = executablePath;
            profile.LastResolvedExecutable = executablePath;
            settingsViewModel.SaveSettings();
        }

        public void ClearManualExecutable(Game game)
        {
            var profile = Settings.GetOrCreateGameProfile(game.Id);
            profile.ManualExecutablePath = null;
            Settings.RemoveEmptyGameProfile(game.Id);
            settingsViewModel.SaveSettings();
        }

        public bool TryApplyForGame(Game game, GameAction sourceAction, int? startedProcessId, bool showError)
        {
            return TryApplyForGame(game, sourceAction, startedProcessId, showError, true);
        }

        private bool TryApplyForGame(Game game, GameAction sourceAction, int? startedProcessId, bool showError, bool allowPermissionSetup)
        {
            var profile = Settings.GetGameProfile(game.Id);
            var modeLimit = profile?.GetMode(CurrentMode);
            var hasGameProfile = modeLimit != null && modeLimit.Enabled && modeLimit.FrameLimit > 0;
            var globalForMode = Settings.GetGlobal(CurrentMode);
            var useGlobalFallback = !hasGameProfile && globalForMode.Enabled && globalForMode.FrameLimit > 0;

            if (!hasGameProfile && !useGlobalFallback)
            {
                return false;
            }

            var frameLimit = hasGameProfile ? modeLimit.FrameLimit : globalForMode.FrameLimit;
            var syncMode = hasGameProfile ? ResolveEffectiveSyncMode(modeLimit, CurrentMode) : globalForMode.SyncMode;
            var reflexMarkers = hasGameProfile ? ResolveEffectiveReflexMarkers(modeLimit, CurrentMode) : globalForMode.ReflexMarkers;
            var disableReflexMarkers = reflexMarkers == ReflexMarkerMode.Disabled;
            var forceGlobalProfile = useGlobalFallback || Settings.UseGlobalProfileDuringLaunch;

            string target = null;
            if (hasGameProfile)
            {
                target = targetResolver.ResolveTarget(game, profile, sourceAction, startedProcessId);
                if (string.IsNullOrWhiteSpace(target) && !Settings.UseGlobalProfileDuringLaunch)
                {
                    if (showError)
                    {
                        playniteApi.Dialogs.ShowErrorMessage(
                            $"FPS Limiter could not find a target executable for {game.Name}. Set a manual target executable from the game context menu.",
                            "FPS Limiter");
                    }

                    return false;
                }
            }

            var existingSession = Settings.ActiveSessions.FirstOrDefault(a => a.GameId == game.Id);
            var profileName = forceGlobalProfile ? "Global" : Path.GetFileName(target);
            if (existingSession != null)
            {
                if (string.Equals(existingSession.ProfileName, profileName, StringComparison.OrdinalIgnoreCase) &&
                    existingSession.AppliedLimit == frameLimit &&
                    existingSession.AppliedSyncMode == syncMode &&
                    existingSession.AppliedReflexMarkersDisabled == disableReflexMarkers)
                {
                    if (hasGameProfile && !string.IsNullOrWhiteSpace(target))
                    {
                        profile.LastResolvedExecutable = target;
                    }

                    settingsViewModel.SaveSettings();
                    return true;
                }

                RestoreSession(existingSession, false, true);
            }

            try
            {
                var session = rtssBackend.ApplyLimit(game.Id, game.Name, target, frameLimit, syncMode, forceGlobalProfile, disableReflexMarkers);
                Settings.ActiveSessions.RemoveAll(a => a.GameId == game.Id);

                Settings.ActiveSessions.Add(session);
                if (hasGameProfile && !string.IsNullOrWhiteSpace(target))
                {
                    profile.LastResolvedExecutable = target;
                }

                if (session.StartedRtssProcess)
                {
                    Settings.RtssStartedByExtension = true;
                }

                settingsViewModel.SaveSettings();
                logger.Info($"Applied {frameLimit} FPS limit ({syncMode}) to {game.Name} via RTSS profile {session.ProfileName}.");
                return true;
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to apply FPS limit to {game.Name}.");

                if (showError)
                {
                    if (allowPermissionSetup && ShouldOfferRtssAccessSetup(e))
                    {
                        if (OfferRtssAccessSetup(game, frameLimit, e))
                        {
                            return TryApplyForGame(game, sourceAction, startedProcessId, showError, false);
                        }
                    }
                    else
                    {
                        playniteApi.Dialogs.ShowErrorMessage(
                            $"FPS Limiter could not apply the {frameLimit} FPS cap to {game.Name}.\n\n{e.Message}",
                            "FPS Limiter");
                    }
                }

                return false;
            }
        }

        private bool OfferRtssAccessSetup(Game game, double frameLimit, Exception originalError)
        {
            var setupOption = new MessageBoxOption("Set up RTSS access", true, false);
            var continueOption = new MessageBoxOption("Continue without cap", false, true);
            var selected = playniteApi.Dialogs.ShowMessage(
                $"FPS Limiter could not apply the {frameLimit} FPS cap to {game.Name}.\n\n" +
                "Windows is blocking write access to RTSS profile files, so RTSS kept its previous settings.\n\n" +
                "Set up RTSS access to allow this Windows user to edit RTSS profiles, or continue launching the game without a cap.\n\n" +
                $"Details:\n{originalError.Message}",
                "FPS Limiter",
                MessageBoxImage.Warning,
                new List<MessageBoxOption> { setupOption, continueOption });

            if (selected != setupOption)
            {
                return false;
            }

            try
            {
                rtssBackend.GrantProfilesModifyPermissionToCurrentUser();
                var result = rtssBackend.TestProfileWriteAccess();

                if (result.Success)
                {
                    playniteApi.Dialogs.ShowMessage(
                        "RTSS access is ready. FPS Limiter will try the cap again.",
                        "FPS Limiter");
                    return true;
                }

                playniteApi.Dialogs.ShowErrorMessage(
                    $"FPS Limiter ran the RTSS access setup, but the write test still failed.\n\n{result.Message}",
                    "FPS Limiter");
                return false;
            }
            catch (Exception e)
            {
                playniteApi.Dialogs.ShowErrorMessage(
                    $"FPS Limiter could not set up RTSS access.\n\n{e.Message}",
                    "FPS Limiter");
                return false;
            }
        }

        private static bool ShouldOfferRtssAccessSetup(Exception error)
        {
            var current = error;
            while (current != null)
            {
                if (current is UnauthorizedAccessException)
                {
                    return true;
                }

                var message = current.Message ?? string.Empty;
                if (message.IndexOf("RTSS Profiles folder", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("cannot write", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("access", StringComparison.OrdinalIgnoreCase) >= 0 && message.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                current = current.InnerException;
            }

            return false;
        }

        public void RetargetAfterLaunch(Game game, GameAction sourceAction, int? startedProcessId)
        {
            var profile = Settings.GetGameProfile(game.Id);
            var modeLimit = profile?.GetMode(CurrentMode);
            if (modeLimit == null || !modeLimit.Enabled || modeLimit.FrameLimit <= 0)
            {
                return;
            }

            if (Settings.UseGlobalProfileDuringLaunch)
            {
                logger.Debug($"FPS Limiter is using the RTSS Global profile for {game.Name}; launch retargeting is skipped.");
                return;
            }

            var currentSession = Settings.ActiveSessions.FirstOrDefault(a => a.GameId == game.Id);
            var currentExecutable = currentSession?.ExecutablePath ?? profile.LastResolvedExecutable;
            var actualTarget = targetResolver.ResolveLaunchedGameProcessTarget(game, startedProcessId, currentExecutable);

            if (string.IsNullOrWhiteSpace(actualTarget))
            {
                logger.Debug($"FPS Limiter did not find a better running process target for {game.Name} from PID {startedProcessId} or install folder.");
                return;
            }

            if (string.Equals(
                Path.GetFileName(actualTarget),
                Path.GetFileName(currentExecutable),
                StringComparison.OrdinalIgnoreCase))
            {
                logger.Debug($"FPS Limiter child process target for {game.Name} still resolves to {Path.GetFileName(actualTarget)}.");
                return;
            }

            try
            {
                if (currentSession != null)
                {
                    RestoreSession(currentSession, false, true);
                }

                var syncMode = ResolveEffectiveSyncMode(modeLimit, CurrentMode);
                var disableReflexMarkers = ResolveEffectiveReflexMarkers(modeLimit, CurrentMode) == ReflexMarkerMode.Disabled;
                var session = rtssBackend.ApplyLimit(game.Id, game.Name, actualTarget, modeLimit.FrameLimit, syncMode, false, disableReflexMarkers);
                Settings.ActiveSessions.RemoveAll(a => a.GameId == game.Id);

                Settings.ActiveSessions.Add(session);
                profile.LastResolvedExecutable = actualTarget;

                if (session.StartedRtssProcess)
                {
                    Settings.RtssStartedByExtension = true;
                }

                settingsViewModel.SaveSettings();

                logger.Info($"Retargeted {game.Name} FPS limit from {Path.GetFileName(currentExecutable)} to {session.ProfileName}.");
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to retarget FPS limit for {game.Name} to {actualTarget}.");
            }
        }

        public void RestoreActiveSession(Game game, bool showError)
        {
            var snapshot = Settings.ActiveSessions.FirstOrDefault(a => a.GameId == game.Id);
            if (snapshot != null)
            {
                RestoreSession(snapshot, showError);
            }
        }

        public void RestoreAllActiveSessions(bool showError)
        {
            foreach (var snapshot in Settings.ActiveSessions.ToList())
            {
                RestoreSession(snapshot, showError);
            }
        }

        private void RestoreSession(LimitSessionSnapshot snapshot, bool showError)
        {
            RestoreSession(snapshot, showError, false);
        }

        private void RestoreSession(LimitSessionSnapshot snapshot, bool showError, bool suppressRtssClose)
        {
            try
            {
                rtssBackend.RestoreLimit(snapshot);
                Settings.ActiveSessions.RemoveAll(a => a.GameId == snapshot.GameId && string.Equals(a.ProfileName, snapshot.ProfileName, StringComparison.OrdinalIgnoreCase));
                logger.Info($"Restored RTSS profile {snapshot.ProfileName} after {snapshot.GameName}.");

                // When this restore is immediately followed by re-applying a new limit (e.g. a live
                // cap change while the game is still running), skip the "no caps left, close RTSS"
                // check entirely so RTSS doesn't flash closed/reopened mid-session.
                if (!suppressRtssClose && Settings.RtssStartedByExtension && !Settings.ActiveSessions.Any())
                {
                    rtssBackend.CloseRtssProcess();
                    Settings.RtssStartedByExtension = false;
                }

                settingsViewModel.SaveSettings();
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to restore RTSS profile {snapshot.ProfileName}.");

                if (showError)
                {
                    playniteApi.Dialogs.ShowErrorMessage(
                        $"FPS Limiter could not restore the previous RTSS profile for {snapshot.GameName}.\n\n{e.Message}",
                        "FPS Limiter");
                }
            }
        }
    }
}
