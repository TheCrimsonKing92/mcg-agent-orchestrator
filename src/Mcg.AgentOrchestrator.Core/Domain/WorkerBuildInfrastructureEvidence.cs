namespace Mcg.AgentOrchestrator.Core;

// Infrastructure classification belongs to typed host observations, including unconsumed requests.
public static class WorkerBuildInfrastructureEvidence
{
    public static WorkerBuildInfrastructureBlocker Classify(TaskSpec task)
    {
        if (task.LastDispatch is not { } dispatch)
            return WorkerBuildInfrastructureBlocker.None;
        var invocations = task.WorkerBuildInvocations
            .Where(invocation => invocation.DispatchAt == dispatch.DispatchedAt).ToArray();
        if (invocations.Length == 0 || invocations.Any(invocation =>
                !invocation.IsTerminal || !invocation.CustodyReleased || invocation.Outcome == WorkerBuildOutcome.CompilerFailure))
            return WorkerBuildInfrastructureBlocker.None;
        var latest = invocations.OrderBy(invocation => invocation.Deadline).Last();
        return latest.Outcome switch
        {
            WorkerBuildOutcome.CapacityBeforeStart => WorkerBuildInfrastructureBlocker.QueueDeadline,
            WorkerBuildOutcome.Interrupted => WorkerBuildInfrastructureBlocker.BuildApparatus,
            WorkerBuildOutcome.ApparatusFailure when !latest.Observations.Any(observation =>
                observation.Phase == WorkerBuildPhase.CompilerStarted) => WorkerBuildInfrastructureBlocker.BuildApparatus,
            WorkerBuildOutcome.TimeoutAfterStart => WorkerBuildInfrastructureBlocker.CompileTimeout,
            _ => WorkerBuildInfrastructureBlocker.None
        };
    }

    public static bool LastDispatchEndedOnQueueCapacity(TaskSpec task) =>
        WorkerBuildCapacityEvidence.LastDispatchEndedOnCapacity(task) || Classify(task) == WorkerBuildInfrastructureBlocker.QueueDeadline;

    public static int QueueCapacityDispatchCount(TaskSpec task) => task.WorkerBuildInvocations
        .Where(invocation => invocation.IsTerminal && invocation.CustodyReleased && invocation.Outcome == WorkerBuildOutcome.CapacityBeforeStart)
        .Select(invocation => invocation.DispatchId).Distinct(StringComparer.Ordinal).Count();
}
