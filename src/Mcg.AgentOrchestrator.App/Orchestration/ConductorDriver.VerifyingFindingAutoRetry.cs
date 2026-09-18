using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private static bool IsMissingFindingResultRetryEligible(
        Goal goal,
        TaskSpec triggeringTask,
        string blockersText)
    {
        if (UnparseableFindingBlockersRoute.PrefersBlockersTextRoute(triggeringTask, blockersText) ||
            triggeringTask.RequiredRole != AgentRole.Reviewer ||
            triggeringTask.LastVerification is not { MergedReviewFindings: null } latestVerification ||
            VerifyingFindingCurrency.HasCurrentOpenBlockingFinding(goal, AgentRole.Reviewer, latestVerification.CompletedAt) ||
            VerifyingFindingCurrency.HasCurrentOpenBlockingFinding(goal, AgentRole.Tester, latestVerification.CompletedAt))
        {
            return false;
        }

        return true;
    }
}
