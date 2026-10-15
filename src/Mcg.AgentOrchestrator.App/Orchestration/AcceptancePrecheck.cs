using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptancePrecheck
{
    internal static bool HasCompletedPassedVerificationForAllTasks(Goal goal) =>
        goal.Tasks.Count > 0 &&
        goal.Tasks.All(task =>
            (task.Status == WorkTaskStatus.Cancelled && task.RequiredRole != AgentRole.Reviewer) ||
            (task.Status == WorkTaskStatus.Completed && CountsAsPassed(goal, task)));

    private static bool CountsAsPassed(Goal goal, TaskSpec task) =>
        task.LastVerification is { } verification &&
        WorkerResultBlockers.AssessSubscriptionVerification(task, verification).AllowsCompletion &&
        (WorkerResultBlockers.AssessSubscriptionVerification(task, verification).Status == VerificationTerminalContractStatus.NotApplicable ||
         AgentOrchestratorKernel.BuildVerificationGate(goal).Tasks.Single(gate => gate.TaskId == task.Id).GateStatus == VerificationGateStatus.Passed) &&
        (verification.Succeeded ||
         (verification.CompletionVerdictVerifiedSuccess &&
          string.IsNullOrWhiteSpace(verification.OrchestratorFailureReason)));
}
