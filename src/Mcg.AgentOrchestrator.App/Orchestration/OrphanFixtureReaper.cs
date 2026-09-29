using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IOrphanFixtureReaperSources
{
    string? ReadPolicyJson();
    IReadOnlyDictionary<int, ProcessInspectionRecord> ReadSnapshot();
    IReadOnlyList<string> SharedTestRoots { get; }
    IReadOnlyList<string> EvidenceRoots { get; }
    IReadOnlyList<OwnedFixtureRoot> ListOwnedRoots();
    IReadOnlyCollection<int> ReadConductorPids();
    IReadOnlyCollection<int> ReadSupervisorPids();
    IReadOnlyCollection<int> ReadRegisteredPids();
    IReadOnlyCollection<int> ReadLineagePids(IReadOnlyDictionary<int, ProcessInspectionRecord> snapshot);
    ProcessInspectionRecord? ReadCurrent(int processId);
    bool TryStopTree(int processId);
}

internal sealed class OrphanFixtureReaper(IOrphanFixtureReaperSources sources, TimeProvider clock)
{
    internal const int MaxStopAttemptsPerPass = 10;
    private static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(15);
    internal static readonly IReadOnlySet<string> CommandLineOnlyExecutables =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "powershell.exe", "pwsh.exe", "cmd.exe", "dotnet.exe", "testhost.exe" };

    internal IReadOnlyList<string> RunPass()
    {
        bool? enabled;
        try { enabled = ParseEnabled(sources.ReadPolicyJson()); }
        catch { return ["SWEEP_ORPHAN_FIXTURE_SKIPPED reason=policy-unavailable"]; }
        if (enabled is null)
            return ["SWEEP_ORPHAN_FIXTURE_SKIPPED reason=policy-unavailable"];
        if (!enabled.Value)
            return ["SWEEP_ORPHAN_FIXTURE_SKIPPED reason=disabled-by-policy"];

        IReadOnlyDictionary<int, ProcessInspectionRecord> snapshot;
        try { snapshot = sources.ReadSnapshot(); }
        catch { return Unavailable("process-snapshot"); }
        if (snapshot.Count == 0)
            return [];

        var protectedPids = new HashSet<int>();
        if (!AddProtection("conductor-lock", () => sources.ReadConductorPids(), protectedPids, out var failure) ||
            !AddProtection("supervisor-lease", () => sources.ReadSupervisorPids(), protectedPids, out failure) ||
            !AddProtection("spawn-registry", () => sources.ReadRegisteredPids(), protectedPids, out failure) ||
            !AddProtection("lineage", () => sources.ReadLineagePids(snapshot), protectedPids, out failure))
            return Unavailable(failure!);

        IReadOnlyList<OwnedFixtureRoot> roots;
        try { roots = sources.ListOwnedRoots(); }
        catch { return ["SWEEP_ORPHAN_FIXTURE_SKIPPED reason=evidence-source-unavailable source=owned-root-listing"]; }
        var ownRoots = roots.GroupBy(root => root.ProcessId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(root => root.CreatedAt).ToArray());
        var now = clock.GetUtcNow();
        var candidates = new List<(ProcessInspectionRecord Process, string Evidence, string Parent)>();
        var lines = new List<string>();
        foreach (var process in snapshot.Values)
        {
            if (process.ProcessId <= 0 || process.Status != ProcessInspectionStatus.Available ||
                process.StartedAt is not { } started || string.IsNullOrWhiteSpace(process.ExecutablePath) ||
                now - started <= MinimumAge || protectedPids.Contains(process.ProcessId))
                continue;
            var parent = ParentProof(process, snapshot);
            if (parent is null)
                continue;
            var root = ownRoots.GetValueOrDefault(process.ProcessId)?
                .FirstOrDefault(candidate => candidate.CreatedAt >= started.AddSeconds(-2) &&
                    IsOwnRoot(candidate, process.ProcessId, sources.SharedTestRoots));
            var commandEvidence = root is null ? MatchCommandPath(process.CommandLine, sources.EvidenceRoots) : null;
            if (root is null && commandEvidence is null)
                continue;
            if (root is null && !CommandLineOnlyExecutables.Contains(Path.GetFileName(process.ExecutablePath)))
            {
                lines.Add($"SWEEP_ORPHAN_FIXTURE_SKIPPED reason=executable-not-allowed pid={process.ProcessId} name={Clean(process.Name)}");
                continue;
            }
            candidates.Add((process, root?.Key ?? commandEvidence!, parent));
        }

        var ordered = candidates.OrderBy(candidate => candidate.Process.StartedAt)
            .ThenBy(candidate => candidate.Process.ProcessId).ToArray();
        foreach (var candidate in ordered.Take(MaxStopAttemptsPerPass))
        {
            var process = candidate.Process;
            string? refusal = HasProtectedDescendant(process.ProcessId, snapshot, protectedPids)
                ? "protected-descendant" : null;
            if (refusal is null)
            {
                try
                {
                    var current = sources.ReadCurrent(process.ProcessId);
                    refusal = RepoProcessCliCommand.EvaluateStopRevalidation(
                        ToSnapshot(process), current is null ? null : ToSnapshot(current), []);
                    if (refusal is null && !sources.TryStopTree(process.ProcessId))
                        refusal = "stop-failed";
                }
                catch { refusal = "stop-failed"; }
            }
            lines.Add(refusal is null
                ? $"SWEEP_ORPHAN_FIXTURE_REAPED pid={process.ProcessId} name={Clean(process.Name)} started={process.StartedAt:O} evidence={FormatEvidence(candidate.Evidence)} parent={candidate.Parent}"
                : $"SWEEP_ORPHAN_FIXTURE_REFUSED pid={process.ProcessId} name={Clean(process.Name)} evidence={FormatEvidence(candidate.Evidence)} reason={refusal}");
        }
        if (ordered.Length > MaxStopAttemptsPerPass)
        {
            var deferred = ordered.Skip(MaxStopAttemptsPerPass).Select(candidate => candidate.Process.ProcessId);
            lines.Add($"SWEEP_ORPHAN_FIXTURE_DEFERRED count={ordered.Length - MaxStopAttemptsPerPass} pids={string.Join(',', deferred)}");
        }
        return lines;
    }

    private static bool? ParseEnabled(string? json)
    {
        if (json is null) return true;
        using var document = JsonDocument.Parse(json,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
        if (!document.RootElement.TryGetProperty("orphanFixtureReaperEnabled", out var value)) return true;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool AddProtection(string name, Func<IReadOnlyCollection<int>> read,
        HashSet<int> protectedPids, out string? failure)
    {
        try
        {
            protectedPids.UnionWith(read() ?? throw new InvalidDataException(name));
            failure = null;
            return true;
        }
        catch { failure = name; return false; }
    }

    private static string[] Unavailable(string source) =>
        [$"SWEEP_ORPHAN_FIXTURE_SKIPPED reason=protection-source-unavailable source={source}"];

    private static string? ParentProof(ProcessInspectionRecord process,
        IReadOnlyDictionary<int, ProcessInspectionRecord> snapshot)
    {
        if (process.ParentProcessId <= 0 || !snapshot.TryGetValue(process.ParentProcessId, out var parent))
            return "absent";
        return parent.StartedAt > process.StartedAt ? "recycled" : null;
    }

    private static string? MatchCommandPath(string? commandLine, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var command = commandLine.Replace('/', '\\');
        foreach (var root in roots)
        {
            var prefix = root.Replace('/', '\\').TrimEnd('\\') + "\\";
            var at = command.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (at == 0 || command[at - 1] is ' ' or '"' or '\'' or '='))
            {
                var end = at + prefix.Length;
                while (end < command.Length && !char.IsWhiteSpace(command[end]) &&
                       command[end] is not '"' and not '\'')
                    end++;
                return command[at..end];
            }
        }
        return null;
    }

    private static bool IsOwnRoot(OwnedFixtureRoot root, int processId, IReadOnlyList<string> sharedTestRoots)
    {
        var key = $"p{processId:x}";
        if (!string.Equals(root.Key, key, StringComparison.OrdinalIgnoreCase)) return false;
        var path = root.Path.Replace('/', '\\').TrimEnd('\\');
        return sharedTestRoots.Any(sharedRoot => string.Equals(path,
            sharedRoot.Replace('/', '\\').TrimEnd('\\') + "\\" + key,
            StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasProtectedDescendant(int processId,
        IReadOnlyDictionary<int, ProcessInspectionRecord> snapshot, HashSet<int> protectedPids)
    {
        foreach (var protectedPid in protectedPids)
        {
            var seen = new HashSet<int>();
            var cursor = protectedPid;
            while (snapshot.TryGetValue(cursor, out var child) && seen.Add(cursor) &&
                   snapshot.TryGetValue(child.ParentProcessId, out var parent) &&
                   parent.StartedAt <= child.StartedAt)
            {
                if (parent.ProcessId == processId) return true;
                cursor = parent.ProcessId;
            }
        }
        return false;
    }

    private static RepoProcessCliCommand.ProcessSnapshot ToSnapshot(ProcessInspectionRecord record) =>
        new(record.ProcessId, record.ParentProcessId, record.Name, record.ExecutablePath,
            record.StartedAt, record.CommandLine, record.Status);

    private static string Clean(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');

    private static string FormatEvidence(string value)
    {
        var clean = Clean(value);
        return clean.Any(char.IsWhiteSpace) ? $"\"{clean}\"" : clean;
    }
}
