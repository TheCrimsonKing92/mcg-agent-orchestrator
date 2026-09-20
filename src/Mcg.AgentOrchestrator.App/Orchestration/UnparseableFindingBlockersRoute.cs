using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class UnparseableFindingBlockersRoute
{
    internal static bool PrefersBlockersTextRoute(TaskSpec triggeringTask, string blockersText)
    {
        ArgumentNullException.ThrowIfNull(triggeringTask);

        return triggeringTask.RequiredRole == AgentRole.Reviewer &&
            triggeringTask.LastVerification is
            {
                WorkerResultPresent: true,
                MergedReviewFindings: null
            } &&
            !string.IsNullOrWhiteSpace(blockersText);
    }
}
