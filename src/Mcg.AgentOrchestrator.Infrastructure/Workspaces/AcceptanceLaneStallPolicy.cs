namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceLaneStallPolicy
{
    /// <summary>
    /// Longest healthy silence was 366 s in 1,083 lane/check invocations; ten minutes
    /// leaves headroom above that measured maximum.
    /// </summary>
    internal const int StallLimitSeconds = 600;
    internal const string LaneStalledReason = "lane-stalled";
    internal static TimeSpan StallLimit => TimeSpan.FromSeconds(StallLimitSeconds);

    internal static bool IsStalled(DateTimeOffset lastProgressAt, DateTimeOffset now, TimeSpan limit) =>
        now >= lastProgressAt && now - lastProgressAt >= limit;

    // Keep elapsed/budget in the existing timeout name: the selection-rerun parser requires them.
    internal static string FormatStallSuffix(TimeSpan? silent, TimeSpan? limit) =>
        silent is { } silence && limit is { } stallLimit
            ? FormattableString.Invariant($" reason={LaneStalledReason} silent={(long)silence.TotalSeconds} limit={(long)stallLimit.TotalSeconds}")
            : string.Empty;
}
