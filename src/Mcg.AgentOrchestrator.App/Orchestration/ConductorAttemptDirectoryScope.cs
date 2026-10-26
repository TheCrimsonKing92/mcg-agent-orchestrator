using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorAttemptDirectoryScope
{
    internal static IReadOnlyList<string> HeartbeatDirectories(string root,
        IReadOnlyList<(GoalId Id, GoalStatus Status)> goals, DateTimeOffset now, TimeSpan window,
        Func<string, DateTime>? directoryWriteTimeUtc = null)
    {
        var live = LiveGoals(goals);
        var earliest = (now - window).UtcDateTime;
        directoryWriteTimeUtc ??= Directory.GetLastWriteTimeUtc;
        return Directories(root).Where(directory =>
        {
            var name = Path.GetFileName(directory);
            if (!GoalShaped(name) || live.Contains(name)) return true;
            try { return directoryWriteTimeUtc(directory) >= earliest; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }).ToArray();
    }

    internal static IReadOnlyList<string> FinishedAttemptDirectories(string root,
        IReadOnlyList<(GoalId Id, GoalStatus Status)> goals)
    {
        var live = LiveGoals(goals);
        return Directories(root).Where(directory =>
            !GoalShaped(Path.GetFileName(directory)) || live.Contains(Path.GetFileName(directory))).ToArray();
    }

    private static HashSet<string> LiveGoals(IReadOnlyList<(GoalId Id, GoalStatus Status)> goals) =>
        goals.Where(goal => goal.Status is not (GoalStatus.Parked or GoalStatus.Completed or GoalStatus.Failed or
            GoalStatus.Cancelled or GoalStatus.Superseded)).Select(goal => goal.Id.Value).ToHashSet(StringComparer.Ordinal);

    private static bool GoalShaped(string name) => name.Length == 32 && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string[] Directories(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    // Scoped heartbeat walks retain files directly in the root and never recurse below supplied folders.
    internal static IEnumerable<FileInfo> HeartbeatFiles(string root, IReadOnlyList<string> directories, string pattern)
    {
        foreach (var directory in new[] { root }.Concat(directories))
        {
            FileInfo[] files;
            try
            {
                files = new DirectoryInfo(directory).GetFiles(pattern,
                    new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 });
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
        }
    }
}
