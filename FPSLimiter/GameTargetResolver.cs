using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;

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

        public string ResolveProcessTreeTarget(int? startedProcessId, string currentExecutablePath, string gameName, int waitMilliseconds = 12000)
        {
            if (!startedProcessId.HasValue || startedProcessId.Value <= 0)
            {
                return null;
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(waitMilliseconds);
            while (DateTime.UtcNow <= deadline)
            {
                var candidate = GetBestDescendantExecutable(startedProcessId.Value, currentExecutablePath, gameName);
                if (IsValidExecutable(candidate))
                {
                    return candidate;
                }

                Thread.Sleep(500);
            }

            return null;
        }

        private string ResolveActionExecutable(Game game, GameAction sourceAction)
        {
            if (sourceAction == null)
            {
                return null;
            }

            return NormalizeExecutablePath(sourceAction.Path, game, sourceAction);
        }

        private string GetBestDescendantExecutable(int rootProcessId, string currentExecutablePath, string gameName)
        {
            try
            {
                var processes = QueryProcesses();
                var descendants = GetDescendants(processes, rootProcessId)
                    .Where(a => IsValidExecutable(a.ExecutablePath))
                    .ToList();

                if (!descendants.Any())
                {
                    return null;
                }

                var currentFile = string.IsNullOrWhiteSpace(currentExecutablePath)
                    ? null
                    : Path.GetFileName(currentExecutablePath);

                return descendants
                    .OrderByDescending(a => ScoreProcess(a, currentFile, gameName))
                    .ThenByDescending(a => a.ProcessId)
                    .Select(a => a.ExecutablePath)
                    .FirstOrDefault();
            }
            catch (Exception e)
            {
                logger.Debug(e, $"Could not inspect child processes for PID {rootProcessId}.");
                return null;
            }
        }

        private static List<ProcessInfo> QueryProcesses()
        {
            var result = new List<ProcessInfo>();
            using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, ExecutablePath, Name FROM Win32_Process"))
            using (var collection = searcher.Get())
            {
                foreach (ManagementObject process in collection)
                {
                    var executablePath = process["ExecutablePath"] as string;
                    if (string.IsNullOrWhiteSpace(executablePath))
                    {
                        continue;
                    }

                    result.Add(new ProcessInfo
                    {
                        ProcessId = Convert.ToInt32((uint)process["ProcessId"]),
                        ParentProcessId = Convert.ToInt32((uint)process["ParentProcessId"]),
                        ExecutablePath = executablePath,
                        Name = process["Name"] as string
                    });
                }
            }

            return result;
        }

        private static IEnumerable<ProcessInfo> GetDescendants(List<ProcessInfo> processes, int rootProcessId)
        {
            var childrenByParent = processes
                .GroupBy(a => a.ParentProcessId)
                .ToDictionary(a => a.Key, a => a.ToList());

            var seen = new HashSet<int>();
            var queue = new Queue<int>();
            queue.Enqueue(rootProcessId);

            while (queue.Any())
            {
                var parentId = queue.Dequeue();
                List<ProcessInfo> children;
                if (!childrenByParent.TryGetValue(parentId, out children))
                {
                    continue;
                }

                foreach (var child in children)
                {
                    if (!seen.Add(child.ProcessId))
                    {
                        continue;
                    }

                    yield return child;
                    queue.Enqueue(child.ProcessId);
                }
            }
        }

        private static int ScoreProcess(ProcessInfo process, string currentFile, string gameName)
        {
            var score = 0;
            var fileName = Path.GetFileName(process.ExecutablePath);

            if (!string.Equals(fileName, currentFile, StringComparison.OrdinalIgnoreCase))
            {
                score += 50;
            }

            if (HasMainWindow(process.ProcessId))
            {
                score += 100;
            }

            if (fileName.IndexOf("shipping", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 25;
            }

            foreach (var word in GetSignificantWords(gameName))
            {
                if (fileName.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    process.ExecutablePath.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 10;
                }
            }

            return score;
        }

        private static bool HasMainWindow(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return process.MainWindowHandle != IntPtr.Zero;
                }
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> GetSignificantWords(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield break;
            }

            var chars = value.Select(a => char.IsLetterOrDigit(a) ? a : ' ').ToArray();
            foreach (var word in new string(chars).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length >= 4)
                {
                    yield return word;
                }
            }
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

        private class ProcessInfo
        {
            public int ProcessId { get; set; }
            public int ParentProcessId { get; set; }
            public string ExecutablePath { get; set; }
            public string Name { get; set; }
        }
    }
}
