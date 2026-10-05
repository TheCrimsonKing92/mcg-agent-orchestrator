using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private static bool TryDescribeCancelledReviewerBlocker(Goal goal, out string blocker)
    {
        blocker = string.Empty;
        if (goal.Tasks.Any(task => task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled)))
            return false;

        var reviewer = goal.Tasks.FirstOrDefault(task =>
            AgentOrchestratorKernel.IsCancelledReviewAwaitingRerun(goal, task));
        if (reviewer is null)
            return false;

        blocker = AgentOrchestratorKernel.FormatCancelledReviewRerunGuidance(goal, reviewer);
        return true;
    }
}
