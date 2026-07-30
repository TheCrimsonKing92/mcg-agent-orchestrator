using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AcceptanceAttemptRetryInvalidationResult(
    bool AttemptInvalidated,
    bool GoalReopened)
{
    public bool Changed => AttemptInvalidated || GoalReopened;
}

internal static class AcceptanceAttemptRetryInvalidation
{
    internal static AcceptanceAttemptRetryInvalidationResult Apply(
        AgentOrchestratorKernel kernel,
        Goal goal,
        ConductorParallelAcceptanceAttemptCoordinator coordinator,
        string reason)
    {
        if (goal.Status != GoalStatus.Verifying ||
            goal.Tasks.All(task => task.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled))
        {
            return new AcceptanceAttemptRetryInvalidationResult(false, false);
        }

        var invalidated = coordinator.InvalidateCurrent(goal.Id.Value, reason);
        var reopened = kernel.ReopenVerifyingGoalAfterAcceptanceAttemptInvalidated(goal.Id, reason);
        return new AcceptanceAttemptRetryInvalidationResult(invalidated, reopened);
    }
}
