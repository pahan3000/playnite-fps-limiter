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

        public LimiterService(IPlayniteAPI playniteApi, FPSLimiterSettingsViewModel settingsViewModel)
        {
            this.playniteApi = playniteApi;
            this.settingsViewModel = settingsViewModel;
            rtssBackend = new RtssLimiterBackend(settingsViewModel.Settings);
            targetResolver = new GameTargetResolver(playniteApi);
        }

        private FPSLimiterSettings Settings => settingsViewModel.Settings;

        public IEnumerable<int> GetPresets()
        {
            return Settings.GetPresetValues();
        }

        public GameLimitProfile GetGameProfile(Game game)
        {
            return Settings.GetGameProfile(game.Id);
        }

        public void SetGameLimit(IEnumerable<Game> games, int frameLimit)
        {
            foreach (var game in games)
            {
                var profile = Settings.GetOrCreateGameProfile(game.Id);
                profile.Enabled = true;
                profile.FrameLimit = frameLimit;
            }

            settingsViewModel.SaveSettings();
        }

        public void DisableGameLimit(IEnumerable<Game> games)
        {
            foreach (var game in games)
            {
                RestoreActiveSession(game, true);

                var profile = Settings.GetOrCreateGameProfile(game.Id);
                profile.Enabled = false;
                profile.FrameLimit = 0;
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
            if (profile == null || !profile.Enabled || profile.FrameLimit <= 0)
            {
                return false;
            }

            var target = targetResolver.ResolveTarget(game, profile, sourceAction, startedProcessId);
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

            var existingSession = Settings.ActiveSessions.FirstOrDefault(a => a.GameId == game.Id);
            var profileName = Settings.UseGlobalProfileDuringLaunch ? "Global" : Path.GetFileName(target);
            if (existingSession != null)
            {
                if (string.Equals(existingSession.ProfileName, profileName, StringComparison.OrdinalIgnoreCase) &&
                    existingSession.AppliedLimit == profile.FrameLimit)
                {
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        profile.LastResolvedExecutable = target;
                    }

                    settingsViewModel.SaveSettings();
                    return true;
                }

                RestoreSession(existingSession, false);
            }

            try
            {
                var session = rtssBackend.ApplyLimit(game.Id, game.Name, target, profile.FrameLimit);
                Settings.ActiveSessions.RemoveAll(a => a.GameId == game.Id);
                Settings.ActiveSessions.Add(session);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    profile.LastResolvedExecutable = target;
                }

                settingsViewModel.SaveSettings();
                logger.Info($"Applied {profile.FrameLimit} FPS limit to {game.Name} via RTSS profile {session.ProfileName}.");
                return true;
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to apply FPS limit to {game.Name}.");

                if (showError)
                {
                    if (allowPermissionSetup && ShouldOfferRtssAccessSetup(e))
                    {
                        if (OfferRtssAccessSetup(game, profile.FrameLimit, e))
                        {
                            return TryApplyForGame(game, sourceAction, startedProcessId, showError, false);
                        }
                    }
                    else
                    {
                        playniteApi.Dialogs.ShowErrorMessage(
                            $"FPS Limiter could not apply the {profile.FrameLimit} FPS cap to {game.Name}.\n\n{e.Message}",
                            "FPS Limiter");
                    }
                }

                return false;
            }
        }

        private bool OfferRtssAccessSetup(Game game, int frameLimit, Exception originalError)
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
            if (profile == null || !profile.Enabled || profile.FrameLimit <= 0)
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
                    RestoreSession(currentSession, false);
                }

                var session = rtssBackend.ApplyLimit(game.Id, game.Name, actualTarget, profile.FrameLimit);
                Settings.ActiveSessions.RemoveAll(a => a.GameId == game.Id);
                Settings.ActiveSessions.Add(session);
                profile.LastResolvedExecutable = actualTarget;
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
            try
            {
                rtssBackend.RestoreLimit(snapshot);
                Settings.ActiveSessions.RemoveAll(a => a.GameId == snapshot.GameId && string.Equals(a.ProfileName, snapshot.ProfileName, StringComparison.OrdinalIgnoreCase));
                settingsViewModel.SaveSettings();
                logger.Info($"Restored RTSS profile {snapshot.ProfileName} after {snapshot.GameName}.");
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
