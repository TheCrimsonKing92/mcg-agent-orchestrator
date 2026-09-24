using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptancePrecheck
{
    internal static bool HasCompletedPassedVerificationForAllTasks(Goal goal) =>
        goal.Tasks.Count > 0 &&
        goal.Tasks.All(task =>
            task.Status == WorkTaskStatus.Cancelled ||
            (task.Status == WorkTaskStatus.Completed && CountsAsPassed(task.LastVerification)));

    private static bool CountsAsPassed(TaskVerificationRecord? verification) =>
        verification is not null &&
        (verification.Succeeded ||
         (verification.CompletionVerdictVerifiedSuccess &&
          string.IsNullOrWhiteSpace(verification.OrchestratorFailureReason)));
}
