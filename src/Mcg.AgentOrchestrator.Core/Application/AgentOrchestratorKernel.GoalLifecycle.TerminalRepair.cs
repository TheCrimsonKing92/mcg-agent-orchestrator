namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    public int CompleteTerminalGoalHumanInputRequests(GoalId goalId, IReadOnlySet<TaskId> taskIds, string reason)
    {
        var goal = GetGoal(goalId);
        if (!IsReopenProtectedGoalStatus(goal.Status))
            throw new InvalidOperationException($"Goal '{goalId}' is not a protected terminal goal.");

        var requests = _humanInputRequests.Values
            .Where(request => request.GoalId == goalId &&
                              request.TaskId is { } taskId && taskIds.Contains(taskId) &&
                              !request.IsCompleted)
            .OrderBy(request => request.RequestedAt)
            .ToArray();
        foreach (var request in requests)
        {
            request.Complete(reason, _clock.UtcNow, briefVersion: goal.AuthoritativeBrief.Version);
            Append(goal, request.TaskId, ProgressKind.HumanInputReceived, reason);
        }

        return requests.Length;
    }
}
