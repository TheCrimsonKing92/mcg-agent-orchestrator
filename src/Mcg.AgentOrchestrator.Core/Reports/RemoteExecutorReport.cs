namespace Mcg.AgentOrchestrator.Core;

public sealed record RemoteExecutorOutcomeRow(DateTimeOffset ObservedAt, string ExecutorId,
    string GateAttemptId, string Lane, string Outcome, string? Reason);
public sealed record RemoteExecutorProbeRow(DateTimeOffset ObservedAt, string ExecutorId,
    bool Reachable, int ExitCode, bool TimedOut, string? TaskState, bool? PowerOnline,
    IReadOnlyList<string> Reasons, string StderrTail);
public sealed record RemoteExecutorReportRow(string ExecutorId, int Attempts,
    IReadOnlyDictionary<string, int> OutcomeCounts, double? AcceptedShare, DateTimeOffset? LastSuccessAt,
    IReadOnlyList<RemoteExecutorOutcomeRow> RecentAttempts, RemoteExecutorProbeRow? LastProbe);

public sealed record RemoteExecutorReport(IReadOnlyList<RemoteExecutorReportRow> Executors,
    int UnreadableOutcomeLines, int UnreadableProbeLines)
{
    public static RemoteExecutorReport Build(IEnumerable<string> configuredIds,
        IEnumerable<RemoteExecutorOutcomeRow> outcomes, IEnumerable<RemoteExecutorProbeRow> probes,
        DateTimeOffset? since = null, int last = 10, int unreadableOutcomeLines = 0, int unreadableProbeLines = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(last);
        var all = outcomes.ToArray();
        var allProbes = probes.ToArray();
        var ids = configuredIds.Concat(all.Select(row => row.ExecutorId))
            .Concat(allProbes.Select(row => row.ExecutorId)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var rows = ids.Select(id =>
        {
            var attempts = all.Where(row => row.ExecutorId == id &&
                row.Outcome is not ("not-eligible-exclusive-resource" or "late-after-fallback")).ToArray();
            var window = attempts.Where(row => since is null || row.ObservedAt >= since).ToArray();
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var group in window.GroupBy(row => row.Outcome)) counts.Add(group.Key, group.Count());
            return new RemoteExecutorReportRow(id, window.Length, counts,
                window.Length == 0 ? null : (double)window.Count(row => row.Outcome == "accepted") / window.Length,
                attempts.Where(row => row.Outcome == "accepted").Select(row => (DateTimeOffset?)row.ObservedAt).Max(),
                window.OrderByDescending(row => row.ObservedAt).Take(last)
                    .Select(row => row with { Reason = row.Reason is { Length: > 160 } reason ? reason[..160] : row.Reason }).ToArray(),
                allProbes.Where(row => row.ExecutorId == id).OrderByDescending(row => row.ObservedAt).FirstOrDefault());
        }).ToArray();
        return new(rows, unreadableOutcomeLines, unreadableProbeLines);
    }
}
