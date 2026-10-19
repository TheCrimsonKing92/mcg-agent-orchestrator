using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class AttentionHumanWaitQuery
{
    internal static IReadOnlyList<HumanInputRequest> Read(
        AgentOrchestratorKernel kernel,
        GoalId? goalId,
        bool includeHistory)
    {
        return kernel.HumanInputRequests
            .Where(request => goalId is null || request.GoalId == goalId)
            .Where(request => includeHistory || !request.IsCompleted)
            .Where(request => includeHistory || request.PlannerIdentity is null ||
                request.PlannerIdentity.BriefVersion == kernel.GetGoal(request.GoalId).AuthoritativeBrief.Version)
            .Where(request => includeHistory || IsLiveAttentionGoal(kernel, request.GoalId))
            .OrderBy(request => request.CreatedAt)
            .ToList();
    }

    internal static bool IsLiveAttentionGoal(AgentOrchestratorKernel kernel, GoalId goalId)
    {
        var goal = kernel.Goals.FirstOrDefault(candidate => candidate.Id == goalId);
        return goal is not null && goal.Status is not GoalStatus.Parked
            and not GoalStatus.Completed
            and not GoalStatus.Failed
            and not GoalStatus.Cancelled
            and not GoalStatus.Superseded;
    }

}
