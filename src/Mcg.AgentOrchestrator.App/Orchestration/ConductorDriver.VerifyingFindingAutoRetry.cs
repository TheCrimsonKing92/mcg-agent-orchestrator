using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private static bool TryBuildMissingFindingResultRetry(
        Goal goal,
        TaskSpec triggeringTask,
        int round,
        out VerifyingFindingAutoRetryDecision decision)
    {
        decision = VerifyingFindingAutoRetryDecision.None;
        if (triggeringTask.RequiredRole != AgentRole.Reviewer ||
            triggeringTask.LastVerification is not { MergedReviewFindings: null } latestVerification ||
            VerifyingFindingCurrency.HasCurrentOpenBlockingFinding(goal, AgentRole.Reviewer, latestVerification.CompletedAt) ||
            VerifyingFindingCurrency.HasCurrentOpenBlockingFinding(goal, AgentRole.Tester, latestVerification.CompletedAt))
        {
            return false;
        }

        decision = VerifyingFindingAutoRetryDecision.Retry(
            triggeringTask,
            $"auto-review-retry round {round}: {triggeringTask.RequiredRole} task {triggeringTask.Id.Value[..8]} " +
            "reported needs-work, but its structured finding result was missing or unparseable and no current open blocking finding exists. " +
            "Re-run the verifying role against the current Developer output; do not reopen the Developer from superseded finding history.",
            null,
            RetryRoundKind.Mechanical,
            RetryCause.EnvironmentApparatusFailure);
        return true;
    }
}
