using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class AssemblyRealWorkerProcessGuard
{
    private static RealWorkerProcessSnapshot _assemblyBaseline = RealWorkerProcessGuard.CaptureSnapshot();

    [ModuleInitializer]
    internal static void Install()
    {
        Environment.SetEnvironmentVariable(
            BackgroundDispatchRunner.TestRewriteRealWorkerCommandsVariable,
            "1",
            EnvironmentVariableTarget.Process);

        _assemblyBaseline = RealWorkerProcessGuard.CaptureSnapshot();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (!_assemblyBaseline.CommandLineEnumerationAvailable)
            {
                Console.Error.WriteLine(RealWorkerProcessGuard.CommandLineEnumerationUnavailableMessage);
                return;
            }

            var matches = RealWorkerProcessGuard.FindNewMatches(_assemblyBaseline);
            if (matches.Count == 0)
            {
                return;
            }

            Console.Error.WriteLine(RealWorkerProcessGuard.FormatFailure(matches));
            Environment.ExitCode = 1;
        };
    }
}

internal sealed record RealWorkerProcessSnapshot(
    bool CommandLineEnumerationAvailable,
    IReadOnlySet<int> MatchingProcessIds);

internal sealed record RealWorkerProcessMatch(int ProcessId, string CommandLine);

internal static class RealWorkerProcessGuard
{
    public const string CommandLineEnumerationUnavailableMessage =
        "Skipping real-worker process guard: process command-line enumeration is unavailable on this platform.";

    private static readonly string[] ScopedTempRootMarkers =
    [
        "mcg-orchestrator-tests",
        "mcg-conductor-driver",
        ".orchestrator-worktrees",
        DotnetBuildEnvironmentManager.RootDirectoryName,
        "mcg-chaos-tests"
    ];

    private static readonly string[] RealWorkerCommandSignatures =
    [
        "codex exec",
        "@openai/codex",
        "gpt-5.3-codex-spark",
        "gpt-5-codex",
        "gpt-5.5-codex",
        "claude -p",
        "claude --print",
        "claude --model"
    ];

    public static RealWorkerProcessSnapshot CaptureSnapshot()
    {
        var matches = FindCurrentMatches(out var available);
        return new RealWorkerProcessSnapshot(
            available,
            matches.Select(match => match.ProcessId).ToHashSet());
    }

    public static IReadOnlyList<RealWorkerProcessMatch> FindNewMatches(RealWorkerProcessSnapshot baseline)
    {
        if (!baseline.CommandLineEnumerationAvailable)
        {
            return [];
        }

        return FindCurrentMatches(out _)
            .Where(match => !baseline.MatchingProcessIds.Contains(match.ProcessId))
            .ToArray();
    }

    public static void AssertNoNewMatches(RealWorkerProcessSnapshot baseline)
    {
        if (!baseline.CommandLineEnumerationAvailable)
        {
            Console.Error.WriteLine(CommandLineEnumerationUnavailableMessage);
            return;
        }

        var matches = FindNewMatches(baseline);
        Assert.True(matches.Count == 0, FormatFailure(matches));
    }

    public static string FormatFailure(IReadOnlyList<RealWorkerProcessMatch> matches)
    {
        var lines = new List<string>
        {
            "Infrastructure tests spawned scoped real worker process command lines:"
        };

        lines.AddRange(matches.Select(match => $"pid={match.ProcessId} commandLine={match.CommandLine}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<RealWorkerProcessMatch> FindCurrentMatches(out bool commandLineEnumerationAvailable) =>
        FindCurrentMatches(
            Environment.ProcessId,
            ProcessCommandLines.Snapshot(),
            FindOwnedProcessTempRoots(Environment.ProcessId),
            out commandLineEnumerationAvailable);

    internal static IReadOnlyList<RealWorkerProcessMatch> FindCurrentMatches(
        int ownerProcessId,
        ProcessCommandLineSnapshot processSnapshot,
        IReadOnlyList<string> ownedProcessTempRoots,
        out bool commandLineEnumerationAvailable)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerProcessId);
        ArgumentNullException.ThrowIfNull(processSnapshot);
        ArgumentNullException.ThrowIfNull(ownedProcessTempRoots);

        var ownerFound = processSnapshot.TryGetRecord(ownerProcessId, out var owner);
        commandLineEnumerationAvailable =
            processSnapshot.Failure is null &&
            ownerFound &&
            owner.Status == ProcessInspectionStatus.Available &&
            !string.IsNullOrWhiteSpace(owner.CommandLine);
        if (!commandLineEnumerationAvailable)
        {
            return [];
        }

        var descendantProcessIds = SelectConservativeDescendantProcessIdsForRefusal(
                ownerProcessId,
                owner.StartedAt ?? DateTimeOffset.MinValue,
                processSnapshot.Records)
            .ToHashSet();
        return processSnapshot.Records.Values
            .Where(record =>
                record.ProcessId != ownerProcessId &&
                (descendantProcessIds.Contains(record.ProcessId) ||
                    IsWithinOwnedProcessTempRoot(record.CommandLine, ownedProcessTempRoots)) &&
                record.Status == ProcessInspectionStatus.Available &&
                !string.IsNullOrWhiteSpace(record.CommandLine) &&
                IsScopedToTestTempRoot(record.CommandLine) &&
                LooksLikeRealWorkerCommandLine(record.CommandLine))
            .Select(record => new RealWorkerProcessMatch(record.ProcessId, record.CommandLine!))
            .ToArray();
    }

    private static IReadOnlyList<int> SelectConservativeDescendantProcessIdsForRefusal(
        int ownerProcessId,
        DateTimeOffset ownerEarliestPossibleStart,
        IReadOnlyDictionary<int, ProcessInspectionRecord> records)
    {
        if (!records.ContainsKey(ownerProcessId))
        {
            return [];
        }

        var childrenByParent = records.Values
            .Where(record =>
                record.ProcessId > 0 &&
                record.ParentProcessId > 0 &&
                record.ProcessId != ownerProcessId)
            .GroupBy(record => record.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var candidates = new List<int>();
        var visited = new HashSet<int> { ownerProcessId };
        var queue = new Queue<(int ProcessId, DateTimeOffset EarliestPossibleStart)>();
        queue.Enqueue((ownerProcessId, ownerEarliestPossibleStart));
        while (queue.TryDequeue(out var parent))
        {
            if (!childrenByParent.TryGetValue(parent.ProcessId, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (!visited.Add(child.ProcessId) ||
                    child.Status == ProcessInspectionStatus.DeadOrRecycled ||
                    child.StartedAt is { } childStartedAt &&
                    childStartedAt < parent.EarliestPossibleStart)
                {
                    continue;
                }

                candidates.Add(child.ProcessId);
                queue.Enqueue((
                    child.ProcessId,
                    child.StartedAt ?? parent.EarliestPossibleStart));
            }
        }

        return candidates.OrderBy(processId => processId).ToArray();
    }

    internal static IReadOnlyList<string> FindOwnedProcessTempRoots(int ownerProcessId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var expectedName = $"p{ownerProcessId:x}";
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in new[] { "TMP", "TEMP" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            try
            {
                var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
                if (string.Equals(Path.GetFileName(normalized), expectedName, StringComparison.OrdinalIgnoreCase))
                {
                    roots.Add(normalized);
                }
            }
            catch
            {
                // An invalid ambient temp path is not an ownership token.
            }
        }

        return roots.OrderBy(root => root, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsWithinOwnedProcessTempRoot(
        string? commandLine,
        IReadOnlyList<string> ownedProcessTempRoots)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        foreach (var root in ownedProcessTempRoots)
        {
            var searchFrom = 0;
            while (searchFrom < commandLine.Length)
            {
                var index = commandLine.IndexOf(root, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    break;
                }

                var after = index + root.Length;
                if (after == commandLine.Length ||
                    commandLine[after] is '\\' or '/' or '"' or '\'' or ' ')
                {
                    return true;
                }

                searchFrom = after;
            }
        }

        return false;
    }

    private static bool IsScopedToTestTempRoot(string commandLine) =>
        ScopedTempRootMarkers.Any(marker => commandLine.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeRealWorkerCommandLine(string commandLine) =>
        RealWorkerCommandSignatures.Any(signature => commandLine.Contains(signature, StringComparison.OrdinalIgnoreCase));
}
