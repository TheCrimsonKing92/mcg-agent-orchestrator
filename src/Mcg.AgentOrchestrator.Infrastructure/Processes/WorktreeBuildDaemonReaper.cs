using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorktreeBuildDaemonReaper
{
    private static readonly string[] BuildServerCandidates = ["VBCSCompiler", "MSBuild"];

    internal static string? Reap(
        string workingDirectory,
        Func<string, IReadOnlyList<ProcessInspectionRecord>> findBuildDaemons,
        Func<ProcessInspectionRecord, bool> tryKillBuildDaemon)
    {
        try
        {
            var daemons = findBuildDaemons(workingDirectory);
            if (daemons.Count == 0)
            {
                return null;
            }

            var reaped = new List<string>();
            var failed = new List<string>();

            foreach (var daemon in daemons)
            {
                bool killed;
                try
                {
                    killed = tryKillBuildDaemon(daemon);
                }
                catch
                {
                    killed = false;
                }

                if (killed)
                {
                    reaped.Add($"{daemon.Name} PID {daemon.ProcessId}");
                }
                else
                {
                    failed.Add($"PID {daemon.ProcessId}");
                }
            }

            var parts = new List<string>();
            if (reaped.Count > 0)
            {
                parts.Add($"Reaped worktree build daemon(s): {string.Join(", ", reaped)}.");
            }

            if (failed.Count > 0)
            {
                parts.Add($"Note: failed to stop {string.Join(", ", failed)}.");
            }

            return parts.Count > 0 ? string.Join(" ", parts) : null;
        }
        catch
        {
            return null;
        }
    }

    internal static List<ProcessInspectionRecord> Find(string workingDirectory) =>
        Find(workingDirectory, ProcessCommandLines.SnapshotByNames);

    internal static List<ProcessInspectionRecord> Find(
        string workingDirectory,
        Func<IReadOnlyCollection<string>, ProcessCommandLineSnapshot> createSnapshot)
    {
        var snapshot = createSnapshot(BuildServerCandidates);
        var result = new List<ProcessInspectionRecord>();

        foreach (var record in snapshot.Records.Values)
        {
            if (record.Status == ProcessInspectionStatus.Available &&
                ShouldReap(workingDirectory, record.CommandLine))
            {
                result.Add(record);
            }
        }

        return result;
    }

    internal static bool ShouldReap(string workingDirectory, string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(workingDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return commandLine.Contains(normalizedPath, StringComparison.OrdinalIgnoreCase) ||
               commandLine.Contains(workingDirectory, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryKill(ProcessInspectionRecord discovered)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsNativeProcessInspection.TryTerminateIfMatches(discovered);
            }

            var currentSnapshot = ProcessCommandLines.Snapshot([discovered.ProcessId]);
            if (!currentSnapshot.Records.TryGetValue(discovered.ProcessId, out var current))
            {
                return false;
            }

            return TryKillRevalidated(discovered, current, KillProcess);
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryKillRevalidated(
        ProcessInspectionRecord discovered,
        ProcessInspectionRecord current,
        Func<int, bool> kill)
    {
        if (!WindowsNativeProcessInspection.MatchesIdentity(discovered, current))
        {
            return false;
        }

        return kill(discovered.ProcessId);
    }

    private static bool KillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            process.Kill(entireProcessTree: false);
            process.WaitForExit(3000);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
