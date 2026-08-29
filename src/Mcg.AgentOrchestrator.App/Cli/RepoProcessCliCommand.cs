using System.Diagnostics;
using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class RepoProcessCliCommand
{
    private const int MaxCommandLength = 420;

    public static void PrintInfo(IReadOnlyList<string> parts, TextWriter output) =>
        PrintInfo(parts, output, BuildSnapshots);

    internal static void PrintInfo(
        IReadOnlyList<string> parts,
        TextWriter output,
        Func<IEnumerable<int>?, IReadOnlyList<ProcessSnapshot>> buildSnapshots)
    {
        var options = RepoProcessOptions.Parse(parts);
        if (!options.HasQuery)
        {
            throw new ArgumentException("Usage: repo-process-info [--id <pid>] [--parent-id <pid>] [--name <name>] [--command-contains <text>] [--newest <n>] [--conduct-loop] [--dispatch-host] [--include-children] [--locks]");
        }

        IReadOnlyList<ProcessSnapshot> snapshots;
        try
        {
            snapshots = buildSnapshots(null);
        }
        catch (Exception ex)
        {
            PrintUnavailable(output, "enumerate", ex);
            return;
        }

        var byId = snapshots.ToDictionary(snapshot => snapshot.ProcessId);
        var childrenByParent = snapshots
            .Where(snapshot => snapshot.ParentProcessId > 0)
            .GroupBy(snapshot => snapshot.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.OrderBy(snapshot => snapshot.ProcessId).ToList());
        var seen = new HashSet<int>();
        var selected = new List<ProcessSnapshot>();

        foreach (var processId in options.Ids)
        {
            if (byId.TryGetValue(processId, out var snapshot))
            {
                AddSelected(snapshot, options.IncludeChildren, childrenByParent, seen, selected);
            }
            else if (seen.Add(processId))
            {
                output.WriteLine($"PROCESS id={processId} status=missing");
            }
        }

        foreach (var parentId in options.ParentIds)
        {
            if (childrenByParent.TryGetValue(parentId, out var children))
            {
                foreach (var child in children)
                {
                    AddSelected(child, options.IncludeChildren, childrenByParent, seen, selected);
                }
            }
        }

        var excludesInvocationLineage = options.LocksOnly || options.CommandContains.Count > 0;
        IReadOnlySet<int> currentInvocationLineage = excludesInvocationLineage
            ? BuildCurrentInvocationLineage(byId)
            : new HashSet<int>();

        var unavailableCandidates = snapshots
            .Where(snapshot =>
                snapshot.InspectionStatus is not ProcessInspectionStatus.Available and
                    not ProcessInspectionStatus.Exited and
                    not ProcessInspectionStatus.DeadOrRecycled &&
                !currentInvocationLineage.Contains(snapshot.ProcessId) &&
                MatchesNames(snapshot, options.Names) &&
                (options.CommandContains.Count > 0 ||
                    (options.LocksOnly && IsPotentialLockHolder(snapshot))))
            .OrderBy(snapshot => snapshot.ProcessId)
            .ToList();
        foreach (var unavailable in unavailableCandidates)
        {
            output.WriteLine(
                $"PROCESS_QUERY_UNAVAILABLE operation=filter id={unavailable.ProcessId} name={unavailable.Name} status={unavailable.InspectionStatus}");
        }

        if (options.Names.Count > 0 || options.CommandContains.Count > 0 || options.LocksOnly)
        {
            var query = snapshots.Where(snapshot =>
                MatchesNames(snapshot, options.Names) &&
                MatchesCommand(snapshot, options.CommandContains) &&
                (!excludesInvocationLineage || !currentInvocationLineage.Contains(snapshot.ProcessId)) &&
                (!options.LocksOnly || IsReportableLockHolder(snapshot, currentInvocationLineage)))
                .OrderByDescending(snapshot => snapshot.StartedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(snapshot => snapshot.ProcessId)
                .Take(options.Newest);
            foreach (var snapshot in query)
            {
                AddSelected(snapshot, options.IncludeChildren, childrenByParent, seen, selected);
            }
        }

        if (selected.Count == 0 && options.ShouldPrintEmptyMessage)
        {
            if (unavailableCandidates.Count > 0)
            {
                return;
            }

            output.WriteLine(options.LocksOnly
                ? "No orchestrator lock-holders running; in-tree build lock is FREE."
                : "No matching repo processes found.");
            return;
        }

        foreach (var snapshot in selected)
        {
            var prefix = options.LocksOnly && IsReportableLockHolder(snapshot, currentInvocationLineage)
                ? $"LOCK id={snapshot.ProcessId} kind={ClassifyKind(snapshot)}"
                : $"PROCESS id={snapshot.ProcessId}";
            output.WriteLine($"{prefix} parent={snapshot.ParentProcessId} name={snapshot.Name} created={FormatTime(snapshot.StartedAt)} path={snapshot.ExecutablePath ?? ""} command={ShortCommand(snapshot.CommandLine)}");
        }
    }

    public static bool Stop(IReadOnlyList<string> parts, TextWriter output)
    {
        var options = RepoProcessStopOptions.Parse(parts);
        var snapshots = BuildSnapshots(options.Ids).ToDictionary(snapshot => snapshot.ProcessId);
        var succeeded = true;
        foreach (var processId in options.Ids)
        {
            if (!snapshots.TryGetValue(processId, out var snapshot))
            {
                output.WriteLine($"PROCESS id={processId} status=missing");
                continue;
            }

            var refused = false;
            if (snapshot.InspectionStatus != ProcessInspectionStatus.Available ||
                string.IsNullOrWhiteSpace(snapshot.CommandLine))
            {
                output.WriteLine($"PROCESS id={processId} status=refused reason=inspection-unavailable");
                refused = true;
            }

            foreach (var needle in options.CommandContains)
            {
                if (!refused &&
                    snapshot.CommandLine!.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    output.WriteLine($"PROCESS id={processId} status=refused reason=command-mismatch expected={needle}");
                    refused = true;
                    break;
                }
            }

            if (refused)
            {
                succeeded = false;
                continue;
            }

            var currentSnapshot = BuildSnapshots([processId]).SingleOrDefault();
            if (EvaluateStopRevalidation(snapshot, currentSnapshot, options.CommandContains) is { } reason)
            {
                output.WriteLine($"PROCESS id={processId} status=refused reason={reason}");
                succeeded = false;
                continue;
            }

            var stopped = WorkerProcessJobs.TryKillOrFallback(processId);
            output.WriteLine($"PROCESS id={processId} status={(stopped ? "stopped" : "not-stopped")}");
            succeeded &= stopped;
        }

        return succeeded;
    }

    private static IReadOnlyList<ProcessSnapshot> BuildSnapshots(IEnumerable<int>? processIds = null)
    {
        var snapshot = processIds is null
            ? ProcessCommandLines.Snapshot()
            : ProcessCommandLines.Snapshot(processIds);
        return snapshot.Records.Values
            .Select(process => new ProcessSnapshot(
                process.ProcessId,
                process.ParentProcessId,
                process.Name,
                process.ExecutablePath,
                process.StartedAt,
                process.CommandLine,
                process.Status))
            .ToList();
    }

    private static void AddSelected(
        ProcessSnapshot snapshot,
        bool includeChildren,
        IReadOnlyDictionary<int, List<ProcessSnapshot>> childrenByParent,
        HashSet<int> seen,
        List<ProcessSnapshot> selected)
    {
        if (!seen.Add(snapshot.ProcessId))
        {
            return;
        }

        selected.Add(snapshot);
        if (!includeChildren || !childrenByParent.TryGetValue(snapshot.ProcessId, out var children))
        {
            return;
        }

        foreach (var child in children)
        {
            AddSelected(child, includeChildren, childrenByParent, seen, selected);
        }
    }

    private static bool MatchesNames(ProcessSnapshot snapshot, IReadOnlyList<string> names) =>
        names.Count == 0 ||
        names.Any(name =>
            snapshot.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            snapshot.Name.Equals(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase));

    private static bool MatchesCommand(ProcessSnapshot snapshot, IReadOnlyList<string> needles) =>
        needles.Count == 0 ||
        needles.All(needle => ContainsOrdinalIgnoreCase(snapshot.CommandLine ?? string.Empty, needle));

    private static bool IsLockHolder(ProcessSnapshot snapshot)
    {
        var command = snapshot.CommandLine ?? string.Empty;
        return ContainsOrdinalIgnoreCase(command, "App.dll") ||
            ContainsOrdinalIgnoreCase(command, "__dispatch-run") ||
            ContainsOrdinalIgnoreCase(command, "DispatchProcessHost");
    }

    private static bool IsReportableLockHolder(ProcessSnapshot snapshot, IReadOnlySet<int> currentInvocationLineage) =>
        IsLockHolder(snapshot) && !currentInvocationLineage.Contains(snapshot.ProcessId);

    private static HashSet<int> BuildCurrentInvocationLineage(IReadOnlyDictionary<int, ProcessSnapshot> snapshotsById)
    {
        var excluded = new HashSet<int>();
        var currentProcessId = Environment.ProcessId;
        if (!snapshotsById.TryGetValue(currentProcessId, out var current))
        {
            excluded.Add(currentProcessId);
            return excluded;
        }

        excluded.Add(current.ProcessId);
        var parentId = current.ParentProcessId;
        while (parentId > 0 && snapshotsById.TryGetValue(parentId, out var parent))
        {
            if (!IsRepoProcessWrapper(parent))
            {
                break;
            }

            excluded.Add(parent.ProcessId);
            parentId = parent.ParentProcessId;
        }

        return excluded;
    }

    private static bool IsRepoProcessWrapper(ProcessSnapshot snapshot)
    {
        var name = snapshot.Name ?? string.Empty;
        var command = snapshot.CommandLine ?? string.Empty;
        return name.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
            ContainsOrdinalIgnoreCase(command, "Invoke-RepoScript.ps1") ||
            ContainsOrdinalIgnoreCase(command, "Invoke-OrchestratorCommand.ps1") ||
            ContainsOrdinalIgnoreCase(command, "Get-RepoProcessInfo.ps1") ||
            ContainsOrdinalIgnoreCase(command, "Find-OrchestratorLocks.ps1");
    }

    private static string ClassifyKind(ProcessSnapshot snapshot)
    {
        var command = snapshot.CommandLine ?? string.Empty;
        if (ContainsOrdinalIgnoreCase(command, "__dispatch-run") ||
            ContainsOrdinalIgnoreCase(command, "DispatchProcessHost"))
        {
            return "dispatch-host";
        }

        if (ContainsOrdinalIgnoreCase(command, "conduct"))
        {
            return "conduct-loop";
        }

        if (ContainsOrdinalIgnoreCase(command, "serve-dashboard") ||
            ContainsOrdinalIgnoreCase(command, "-dashboard"))
        {
            return "dashboard";
        }

        return "app-host";
    }

    private static bool ContainsOrdinalIgnoreCase(string value, string expected) =>
        value.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static void PrintUnavailable(TextWriter output, string operation, Exception ex)
    {
        output.WriteLine($"PROCESS_QUERY_UNAVAILABLE operation={operation} reason={ex.GetType().Name}: {ex.Message}");
        output.WriteLine("BACKLOG_CANDIDATE title=\"Repo process helper degraded\" body=\"Process helper could not inspect local processes through the orchestrator CLI; preserve this disposition instead of requesting operator approval.\"");
    }

    private static string ShortCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "";
        }

        var normalized = string.Join(' ', command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaxCommandLength ? normalized : normalized[..MaxCommandLength] + "...";
    }

    private static string FormatTime(DateTimeOffset? value) => value?.ToString("o", CultureInfo.InvariantCulture) ?? "";

    private static string? NormalizeIdentityPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try { return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return null; }
    }

    private static bool IsPotentialLockHolder(ProcessSnapshot snapshot) =>
        snapshot.Name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
        snapshot.Name.Equals("DispatchProcessHost", StringComparison.OrdinalIgnoreCase) ||
        snapshot.Name.StartsWith("Mcg.AgentOrchestrator", StringComparison.OrdinalIgnoreCase);

    internal static string? EvaluateStopRevalidation(
        ProcessSnapshot recorded,
        ProcessSnapshot? current,
        IReadOnlyList<string> commandMarkers)
    {
        if (recorded.StartedAt is null || string.IsNullOrWhiteSpace(recorded.ExecutablePath))
        {
            return "identity-unavailable";
        }

        if (current is null ||
            current.InspectionStatus != ProcessInspectionStatus.Available ||
            string.IsNullOrWhiteSpace(current.CommandLine))
        {
            return "inspection-unavailable";
        }

        var recordedPath = NormalizeIdentityPath(recorded.ExecutablePath);
        var currentPath = NormalizeIdentityPath(current.ExecutablePath);
        if (current.StartedAt != recorded.StartedAt ||
            recordedPath is null ||
            currentPath is null ||
            !string.Equals(currentPath, recordedPath, StringComparison.OrdinalIgnoreCase))
        {
            return "identity-mismatch";
        }

        return commandMarkers.Any(marker =>
            current.CommandLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0)
                ? "command-changed"
                : null;
    }

    internal sealed record ProcessSnapshot(
        int ProcessId,
        int ParentProcessId,
        string Name,
        string? ExecutablePath,
        DateTimeOffset? StartedAt,
        string? CommandLine,
        ProcessInspectionStatus InspectionStatus);

    private sealed record RepoProcessOptions(
        IReadOnlyList<int> Ids,
        IReadOnlyList<int> ParentIds,
        IReadOnlyList<string> Names,
        IReadOnlyList<string> CommandContains,
        int Newest,
        bool IncludeChildren,
        bool LocksOnly)
    {
        public bool HasQuery => Ids.Count > 0 || ParentIds.Count > 0 || Names.Count > 0 || CommandContains.Count > 0 || LocksOnly;
        public bool ShouldPrintEmptyMessage => Ids.Count == 0 && ParentIds.Count == 0;

        public static RepoProcessOptions Parse(IReadOnlyList<string> parts)
        {
            var ids = new List<int>();
            var parentIds = new List<int>();
            var names = new List<string>();
            var commandContains = new List<string>();
            var newest = 25;
            var includeChildren = false;
            var locksOnly = false;

            for (var i = 1; i < parts.Count; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "--id":
                        ids.Add(ParseIntValue(parts, ref i, "--id"));
                        break;
                    case "--parent-id":
                        parentIds.Add(ParseIntValue(parts, ref i, "--parent-id"));
                        break;
                    case "--name":
                        names.Add(ParseStringValue(parts, ref i, "--name"));
                        break;
                    case "--command-contains":
                        commandContains.Add(ParseStringValue(parts, ref i, "--command-contains"));
                        break;
                    case "--newest":
                        newest = ParseIntValue(parts, ref i, "--newest");
                        if (newest < 1)
                        {
                            throw new ArgumentException("--newest must be at least 1.");
                        }
                        break;
                    case "--conduct-loop":
                        commandContains.Add("conduct");
                        commandContains.Add("--loop");
                        break;
                    case "--dispatch-host":
                        commandContains.Add("__dispatch-run");
                        break;
                    case "--include-children":
                        includeChildren = true;
                        break;
                    case "--locks":
                        locksOnly = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{parts[i]}'.");
                }
            }

            return new RepoProcessOptions(ids, parentIds, names, commandContains, newest, includeChildren, locksOnly);
        }
    }

    private sealed record RepoProcessStopOptions(IReadOnlyList<int> Ids, IReadOnlyList<string> CommandContains)
    {
        public static RepoProcessStopOptions Parse(IReadOnlyList<string> parts)
        {
            var ids = new List<int>();
            var commandContains = new List<string>();
            for (var i = 1; i < parts.Count; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "--id":
                        ids.Add(ParseIntValue(parts, ref i, "--id"));
                        break;
                    case "--command-contains":
                        commandContains.Add(ParseStringValue(parts, ref i, "--command-contains"));
                        break;
                    case "--force":
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{parts[i]}'.");
                }
            }

            if (ids.Count == 0)
            {
                throw new ArgumentException("Usage: repo-process-stop --id <pid> [--command-contains <text>] [--force]");
            }

            return new RepoProcessStopOptions(ids, commandContains);
        }
    }

    private static int ParseIntValue(IReadOnlyList<string> parts, ref int index, string flag)
    {
        var value = ParseStringValue(parts, ref index, flag);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException($"{flag} must be a positive integer.");
        }

        return parsed;
    }

    private static string ParseStringValue(IReadOnlyList<string> parts, ref int index, string flag)
    {
        if (index + 1 >= parts.Count || parts[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{flag} requires a value.");
        }

        index++;
        return parts[index];
    }

}
