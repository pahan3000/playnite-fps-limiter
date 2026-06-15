using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace FPSLimiter
{
    public class GameTargetResolver
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IPlayniteAPI playniteApi;

        public GameTargetResolver(IPlayniteAPI playniteApi)
        {
            this.playniteApi = playniteApi;
        }

        public string ResolveTarget(Game game, GameLimitProfile profile, GameAction sourceAction = null, int? startedProcessId = null)
        {
            var manualPath = NormalizeExecutablePath(profile?.ManualExecutablePath, game, sourceAction);
            if (IsValidExecutable(manualPath))
            {
                return manualPath;
            }

            var processPath = ResolveStartedProcessPath(startedProcessId);
            if (IsValidExecutable(processPath))
            {
                return processPath;
            }

            var actionPath = ResolveActionExecutable(game, sourceAction);
            if (IsValidExecutable(actionPath))
            {
                return actionPath;
            }

            var trackingPath = NormalizeExecutablePath(sourceAction?.TrackingPath, game, sourceAction);
            if (IsValidExecutable(trackingPath))
            {
                return trackingPath;
            }

            var lastPath = NormalizeExecutablePath(profile?.LastResolvedExecutable, game, sourceAction);
            if (IsValidExecutable(lastPath))
            {
                return lastPath;
            }

            return null;
        }

        public string ResolveStartedProcessPath(int? startedProcessId)
        {
            if (!startedProcessId.HasValue || startedProcessId.Value <= 0)
            {
                return null;
            }

            try
            {
                using (var process = Process.GetProcessById(startedProcessId.Value))
                {
                    return process.MainModule.FileName;
                }
            }
            catch (Exception e)
            {
                logger.Debug(e, $"Could not resolve process path for PID {startedProcessId.Value}.");
                return null;
            }
        }

        private string ResolveActionExecutable(Game game, GameAction sourceAction)
        {
            if (sourceAction == null)
            {
                return null;
            }

            return NormalizeExecutablePath(sourceAction.Path, game, sourceAction);
        }

        private string NormalizeExecutablePath(string rawPath, Game game, GameAction sourceAction)
        {
            return NormalizePath(rawPath, sourceAction?.WorkingDir, game?.InstallDirectory);
        }

        private string NormalizePath(string rawPath, params string[] baseDirectories)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return null;
            }

            var path = rawPath.Trim().Trim('"');
            path = Environment.ExpandEnvironmentVariables(path);

            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                return null;
            }

            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            foreach (var baseDirectory in baseDirectories.Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                try
                {
                    var candidate = Path.GetFullPath(Path.Combine(baseDirectory, path));
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception e)
                {
                    logger.Debug(e, $"Could not combine executable path '{path}' with '{baseDirectory}'.");
                }
            }

            return path;
        }

        private static bool IsValidExecutable(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   File.Exists(path) &&
                   string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
        }
    }
}
