using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorMaxDurationInFlightAttempt(
    string AttemptId,
    IReadOnlyList<string> GoalIds,
    DateTimeOffset StartedAt);

internal enum ConductorMaxDurationStopVerdictKind
{
    Stop,
    Defer,
    StopExpired
}

internal sealed record ConductorMaxDurationStopVerdict(
    ConductorMaxDurationStopVerdictKind Kind,
    IReadOnlyList<ConductorMaxDurationInFlightAttempt> Attempts);

internal static class ConductorMaxDurationStopDeferral
{
    internal static ConductorMaxDurationStopVerdict Decide(
        DateTimeOffset utcNow,
        DateTimeOffset deferralStartedAt,
        IReadOnlyList<ConductorMaxDurationInFlightAttempt> attempts,
        TimeSpan ceiling)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        var ordered = attempts.OrderBy(attempt => attempt.AttemptId, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0)
        {
            return new ConductorMaxDurationStopVerdict(ConductorMaxDurationStopVerdictKind.Stop, ordered);
        }

        var elapsed = utcNow - deferralStartedAt;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed < ceiling
            ? new ConductorMaxDurationStopVerdict(ConductorMaxDurationStopVerdictKind.Defer, ordered)
            : new ConductorMaxDurationStopVerdict(ConductorMaxDurationStopVerdictKind.StopExpired, ordered);
    }

    internal static string FormatDeferredEvent(
        int tick,
        DateTimeOffset utcNow,
        DateTimeOffset deferralStartedAt,
        TimeSpan ceiling,
        IReadOnlyList<ConductorMaxDurationInFlightAttempt> attempts) =>
        $"LOOP_STOP_DEFERRED tick={tick} reason=max-duration " +
        $"deferral_started_at={deferralStartedAt:O} ceiling_ms={Math.Max(0L, (long)ceiling.TotalMilliseconds)} " +
        $"attempts={string.Join(',', attempts.Select(attempt => attempt.AttemptId))} " +
        $"attempt_elapsed_ms={string.Join(',', attempts.Select(attempt => $"{attempt.AttemptId}:{Math.Max(0L, (long)(utcNow - attempt.StartedAt).TotalMilliseconds)}"))} " +
        $"attempt_started_at={string.Join(',', attempts.Select(attempt => $"{attempt.AttemptId}:{attempt.StartedAt:O}"))}";

    internal static string FormatStopDetail(
        TimeSpan maxDuration,
        ConductorMaxDurationStopVerdict verdict,
        Exception? snapshotFailure = null)
    {
        var detail = $"seconds={(int)maxDuration.TotalSeconds}";
        if (snapshotFailure is not null)
        {
            return $"{detail} inflightStateUnavailable=true error={snapshotFailure.GetType().Name}";
        }

        return verdict.Kind == ConductorMaxDurationStopVerdictKind.StopExpired
            ? $"{detail} deferralExpired=true attempts={string.Join(',', verdict.Attempts.Select(attempt => attempt.AttemptId))}"
            : detail;
    }
}

internal sealed partial class ConductorBatchLoop
{
    private sealed record MaxDurationAcceptanceSnapshot(
        IReadOnlyList<ConductorMaxDurationInFlightAttempt> Attempts,
        Exception? Failure = null);

    private static MaxDurationAcceptanceSnapshot BuildMaxDurationAcceptanceSnapshot(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        IReadOnlySet<string>? trackedAttemptIds)
    {
        try
        {
            var goalIds = kernel.Goals.Select(goal => goal.Id.Value).ToArray();
            var parallelAttempts = trackedAttemptIds is null
                ? driver.ParallelAcceptanceAttemptCoordinator.GetCapacityReservingAttempts(goalIds)
                : driver.ParallelAcceptanceAttemptCoordinator.GetUnreconciledAttempts(goalIds)
                    .Where(attempt => trackedAttemptIds.Contains(attempt.AttemptId))
                    .ToArray();
            var attempts = parallelAttempts
                .Select(attempt => new ConductorMaxDurationInFlightAttempt(
                    attempt.AttemptId,
                    [attempt.GoalId],
                    attempt.StartedAt))
                .ToList();
            attempts.AddRange(driver.GetActiveAcceptanceCohortCapacity().ActiveRoots
                .Where(root => trackedAttemptIds is null || trackedAttemptIds.Contains(root.Key))
                .Select(root => new ConductorMaxDurationInFlightAttempt(
                    root.Key,
                    root.MemberGoalIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                    root.StartedAt)));
            return new MaxDurationAcceptanceSnapshot(attempts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new MaxDurationAcceptanceSnapshot([], ex);
        }
    }
}
