using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Best-effort observations of settled verdicts and remote claims, never verdict authority.
internal static class AcceptanceGateLaneProgress
{
    internal static void CheckCompleted(GoalId? goalId, AcceptanceCheckResult result,
        Action<AcceptanceGateProgress> sink)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = TimeSpan.FromMilliseconds(result.DurationMilliseconds ?? 0);
            if (!result.TestResultIsExplicitCrossAttemptReuse) AcceptanceLaneDurationStore.RecordCheck(result.Name, result.Passed, elapsed);
            sink(new AcceptanceGateProgress(goalId?.Value, "check-complete", result.Name, null,
                Environment.ProcessId, null, now - elapsed, now, now, elapsed, 0,
                result.GateHeartbeatPath ?? "", Outcome: result.Passed ? "passed" : "failed",
                FailedTestCount: result.FailingTestIdentities?.Count ?? 0));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    internal static void RemoteStarted(GoalId? goalId, string lane, string executorId, int slotIndex,
        DotnetBuildStorageRoot storageRoot, Action<AcceptanceGateProgress> sink)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            sink(new AcceptanceGateProgress(goalId?.Value, "remote-shard-started", lane, slotIndex,
                Environment.ProcessId, null, now, now, now, TimeSpan.Zero, 0,
                GateHeartbeatArtifacts.GetStableSlotPath(slotIndex, storageRoot), Executor: executorId));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }
}
