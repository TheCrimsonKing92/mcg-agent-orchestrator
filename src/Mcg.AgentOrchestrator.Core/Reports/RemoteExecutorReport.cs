using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

public sealed record RemoteExecutorOutcomeRow(DateTimeOffset ObservedAt, string ExecutorId,
    string GateAttemptId, string Lane, string Outcome, string? Reason,
    [property: JsonIgnore] double? RemoteSeconds = null,
    [property: JsonIgnore] DateTimeOffset? StartedAt = null);
public sealed record RemoteExecutorProbeRow(DateTimeOffset ObservedAt, string ExecutorId,
    bool Reachable, int ExitCode, bool TimedOut, string? TaskState, bool? PowerOnline,
    IReadOnlyList<string> Reasons, string StderrTail);
public sealed record RemoteExecutorReportRow(string ExecutorId, int Attempts,
    IReadOnlyDictionary<string, int> OutcomeCounts, double? AcceptedShare, DateTimeOffset? LastSuccessAt,
    IReadOnlyList<RemoteExecutorOutcomeRow> RecentAttempts, RemoteExecutorProbeRow? LastProbe,
    int? ConfiguredSlots, int PeakConcurrentAttempts, int ConcurrentAttempts,
    IReadOnlyList<RemoteExecutorLaneSeconds> Lanes);
public sealed record RemoteExecutorLaneSeconds(string Lane, int Samples, double MedianSeconds,
    double LastSeconds, double? SoloMedianSeconds, double? ConcurrentMedianSeconds);

public sealed record RemoteExecutorReport(IReadOnlyList<RemoteExecutorReportRow> Executors,
    int UnreadableOutcomeLines, int UnreadableProbeLines)
{
    public static RemoteExecutorReport Build(IEnumerable<string> configuredIds,
        IEnumerable<RemoteExecutorOutcomeRow> outcomes, IEnumerable<RemoteExecutorProbeRow> probes,
        DateTimeOffset? since = null, int last = 10, int unreadableOutcomeLines = 0, int unreadableProbeLines = 0,
        IReadOnlyDictionary<string, int>? configuredSlots = null)
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
            var timed = window.Select((row, index) => (Row: row, Index: index))
                .Where(item => HasInterval(item.Row)).ToArray();
            var concurrent = new bool[window.Length];
            for (var i = 0; i < timed.Length; i++)
                for (var j = i + 1; j < timed.Length; j++)
                    if (timed[i].Row.StartedAt < timed[j].Row.ObservedAt &&
                        timed[j].Row.StartedAt < timed[i].Row.ObservedAt)
                        concurrent[timed[i].Index] = concurrent[timed[j].Index] = true;
            // A peak occurs at a start; ends at that same instant are already out of flight.
            var peak = timed.Select(item => timed.Count(other =>
                other.Row.StartedAt <= item.Row.StartedAt && item.Row.StartedAt < other.Row.ObservedAt))
                .DefaultIfEmpty(0).Max();
            var lanes = window.Select((row, index) => (Row: row, Index: index))
                .Where(item => item.Row.Outcome == "accepted" && item.Row.RemoteSeconds.HasValue)
                .GroupBy(item => item.Row.Lane).OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new RemoteExecutorLaneSeconds(group.Key, group.Count(),
                    Median(group.Select(item => item.Row.RemoteSeconds!.Value))!.Value,
                    group.OrderByDescending(item => item.Row.ObservedAt).First().Row.RemoteSeconds!.Value,
                    Median(group.Where(item => HasInterval(item.Row) && !concurrent[item.Index])
                        .Select(item => item.Row.RemoteSeconds!.Value)),
                    Median(group.Where(item => concurrent[item.Index]).Select(item => item.Row.RemoteSeconds!.Value))))
                .ToArray();
            return new RemoteExecutorReportRow(id, window.Length, counts,
                window.Length == 0 ? null : (double)window.Count(row => row.Outcome == "accepted") / window.Length,
                attempts.Where(row => row.Outcome == "accepted").Select(row => (DateTimeOffset?)row.ObservedAt).Max(),
                window.OrderByDescending(row => row.ObservedAt).Take(last)
                    .Select(row => row with { Reason = row.Reason is { Length: > 160 } reason ? reason[..160] : row.Reason }).ToArray(),
                allProbes.Where(row => row.ExecutorId == id).OrderByDescending(row => row.ObservedAt).FirstOrDefault(),
                configuredSlots is not null && configuredSlots.TryGetValue(id, out var slots) ? slots : null,
                peak, concurrent.Count(value => value), lanes);
        }).ToArray();
        return new(rows, unreadableOutcomeLines, unreadableProbeLines);
    }

    private static bool HasInterval(RemoteExecutorOutcomeRow row) =>
        row.StartedAt is { } start && start < row.ObservedAt;

    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return null;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] :
            sorted[middle - 1] + (sorted[middle] - sorted[middle - 1]) / 2;
    }
}
