namespace Mcg.AgentOrchestrator.Core;

// Capacity history is derived from consumed host evidence, never from worker output text.
public static class WorkerBuildCapacityEvidence
{
    public static bool LastDispatchEndedOnCapacity(TaskSpec task) =>
        task.LastDispatch is { } dispatch && task.WorkerBuildInvocations
            .Where(invocation => invocation.Consumed && invocation.DispatchAt == dispatch.DispatchedAt)
            .OrderBy(invocation => invocation.Deadline)
            .LastOrDefault()?.Outcome == WorkerBuildOutcome.CapacityBeforeStart;

    public static int CapacityDispatchCount(TaskSpec task) => task.WorkerBuildInvocations
        .Where(invocation => invocation.Consumed && invocation.Outcome == WorkerBuildOutcome.CapacityBeforeStart)
        .Select(invocation => invocation.DispatchId).Distinct(StringComparer.Ordinal).Count();
}
