using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class LogDirectoryFamilyRetention
{
    internal static readonly TimeSpan MaxAge = StorageRetentionMaintenance.WorkerDeletionAge;
    internal static readonly TimeSpan MinimumAge = StorageRetentionMaintenance.WorkerCompressionAge;
    internal const int KeepNewestRuns = 20;
    internal const int MaxRunsPerFamily = 500;

    private static readonly (string Family, Regex Name)[] Families =
    [
        ("spec-refinement", new(@"^spec-refinement-(?<goal>[0-9a-f]{8})-(?<stamp>[0-9]{17})",
            RegexOptions.CultureInvariant)),
        ("post-landing-canary", new(@"^post-landing-canary-(?<sha>[0-9a-f]+)-(?<run>[0-9a-f]{12})(?=[-.])",
            RegexOptions.CultureInvariant)),
        ("conduct-supervisor", new(@"^conduct-supervisor-(?<stamp>[0-9]{17})-[0-9]{3}-[0-9a-f]{32}",
            RegexOptions.CultureInvariant)),
        ("supervisor-handoff", new(@"^supervisor-handoff-(?<stamp>[0-9]{17})-[0-9a-f]{32}",
            RegexOptions.CultureInvariant)),
        ("supervisor-ready", new(@"^supervisor-ready-[0-9a-f]{32}\.json$",
            RegexOptions.CultureInvariant))
    ];

    internal static void Sweep(
        string logDirectory,
        IReadOnlyCollection<StorageRetentionGoal> goals,
        DateTimeOffset now,
        Func<IReadOnlyCollection<string>> protectedPaths,
        List<EvidenceRetentionDecision> decisions,
        int keepNewestRuns = KeepNewestRuns,
        int maxRunsPerFamily = MaxRunsPerFamily)
    {
        if (!Directory.Exists(logDirectory)) return;

        var runs = new Dictionary<string, LogRun>(StringComparer.Ordinal);
        try
        {
            foreach (var file in new DirectoryInfo(logDirectory).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                foreach (var family in Families)
                {
                    var match = family.Name.Match(file.Name);
                    if (!match.Success) continue;
                    if (!runs.TryGetValue(match.Value, out var run))
                    {
                        DateTimeOffset? stamp = DateTimeOffset.TryParseExact(match.Groups["stamp"].Value,
                            "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                            ? parsed : null;
                        run = new LogRun(family.Family, match.Value, match.Groups["goal"].Value, stamp);
                        runs.Add(match.Value, run);
                    }
                    run.Files.Add(file);
                    run.NewestWrite = file.LastWriteTimeUtc > run.NewestWrite
                        ? file.LastWriteTimeUtc : run.NewestWrite;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.DispatchLogs,
                EvidenceRetentionAction.DeferredLocked, logDirectory, null, EvidenceOwnerResolution.Unrecorded,
                "log-family-enumeration-failed", FailureExceptionType: ex.GetType().Name));
            return;
        }
        if (runs.Count == 0) return;

        HashSet<string> protectedSet;
        try
        {
            protectedSet = protectedPaths().Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.DispatchLogs,
                EvidenceRetentionAction.RetainedUndecidable, logDirectory, null, EvidenceOwnerResolution.Unrecorded,
                "log-family-protection-unavailable", FailureExceptionType: ex.GetType().Name));
            return;
        }

        foreach (var family in runs.Values.GroupBy(run => run.Family).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            // Start stamps rank long-running processes; recent writes still protect their entire run by age.
            var ranked = family.OrderByDescending(run => run.Stamp ?? run.NewestWrite)
                .ThenByDescending(run => run.Key, StringComparer.Ordinal).ToArray();
            for (var rank = 0; rank < ranked.Length; rank++)
            {
                var run = ranked[rank];
                var rule = Decide(run, rank, goals, now, protectedSet, keepNewestRuns, maxRunsPerFamily);
                foreach (var file in run.Files.OrderBy(file => file.Name, StringComparer.Ordinal))
                {
                    if (rule.Action != EvidenceRetentionAction.Deleted)
                    {
                        decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.DispatchLogs,
                            rule.Action, file.FullName, null, EvidenceOwnerResolution.Unrecorded, rule.Reason));
                        continue;
                    }
                    var length = StorageRetentionMaintenance.SafeLength(file.FullName);
                    var deletion = StorageRetentionMaintenance.TryDeleteExclusive(file.FullName);
                    decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.DispatchLogs,
                        deletion.Success ? EvidenceRetentionAction.Deleted : EvidenceRetentionAction.DeferredLocked,
                        file.FullName, null, EvidenceOwnerResolution.Unrecorded,
                        deletion.Success ? rule.Reason : "log-family-locked",
                        BytesAttempted: length, BytesReclaimed: deletion.Success ? length : 0,
                        FailureExceptionType: deletion.ExceptionType));
                }
            }
        }
    }

    private static (EvidenceRetentionAction Action, string Reason) Decide(
        LogRun run, int rank, IReadOnlyCollection<StorageRetentionGoal> goals, DateTimeOffset now,
        HashSet<string> protectedPaths, int keepNewestRuns, int maxRunsPerFamily)
    {
        if (run.Files.Any(file => protectedPaths.Contains(file.FullName)))
            return (EvidenceRetentionAction.Preserved, "log-family-running-conductor-log");
        if (run.Family == "spec-refinement")
        {
            if (goals.All(goal => goal.IsStoreStandIn))
                return (EvidenceRetentionAction.RetainedUndecidable, "log-family-goal-rules-skipped");
            var owners = goals.Where(goal => goal.GoalId.StartsWith(run.GoalPrefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (owners.Length > 1)
                return (EvidenceRetentionAction.RetainedUndecidable, "log-family-goal-prefix-ambiguous");
            if (owners.Length == 1 && !owners[0].IsTerminal)
                return (EvidenceRetentionAction.Preserved, "log-family-goal-non-terminal");
        }
        if (rank < keepNewestRuns)
            return (EvidenceRetentionAction.Preserved, "log-family-newest");
        var age = now - run.NewestWrite;
        if (age <= MinimumAge)
            return (EvidenceRetentionAction.Preserved, "log-family-within-age");
        if (age > MaxAge)
            return (EvidenceRetentionAction.Deleted, "log-family-past-age");
        if (rank >= maxRunsPerFamily)
            return (EvidenceRetentionAction.Deleted, "log-family-past-count-bound");
        return (EvidenceRetentionAction.Preserved, "log-family-within-bounds");
    }

    // All files sharing a matched prefix have one rank and one age, and protection applies to the whole run.
    private sealed class LogRun(string family, string key, string goalPrefix, DateTimeOffset? stamp)
    {
        internal string Family { get; } = family;
        internal string Key { get; } = key;
        internal string GoalPrefix { get; } = goalPrefix;
        internal DateTimeOffset? Stamp { get; } = stamp;
        internal DateTimeOffset NewestWrite { get; set; } = DateTimeOffset.MinValue;
        internal List<FileInfo> Files { get; } = [];
    }
}
