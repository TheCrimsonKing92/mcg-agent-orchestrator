namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record HostHealthAssessment(
    bool IsDegraded, double? BaselineMs, double? LatestLaunchMs, double? Ratio,
    double? LatestPagedPoolMb, int ConsecutiveCount, int RecordCount);

internal static class GateHostHealthEvaluator
{
    internal const int MinimumRecords = 5;
    internal const int RetainedRecordWindow = 200;
    internal const int DegradedLaunchMultiplier = 3;
    internal const int ConsecutiveDegradedRecords = 3;

    internal static HostHealthAssessment Evaluate(IReadOnlyList<HostHealthLedgerRecord> records)
    {
        var retained = records.TakeLast(RetainedRecordWindow).ToArray();
        if (retained.Length == 0)
            return new(false, null, null, null, null, 0, 0);
        var baseline = retained.Min(record => record.LaunchMs);
        var latest = retained[^1];
        var consecutive = retained.Reverse()
            .TakeWhile(record => record.LaunchMs >= baseline * DegradedLaunchMultiplier).Count();
        return new(retained.Length >= MinimumRecords && consecutive >= ConsecutiveDegradedRecords,
            baseline, latest.LaunchMs, baseline > 0 ? latest.LaunchMs / baseline : null,
            latest.PagedPoolMb, consecutive, retained.Length);
    }
}
