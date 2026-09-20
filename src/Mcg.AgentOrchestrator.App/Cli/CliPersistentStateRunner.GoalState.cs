using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliPersistentStateRunner
{
    private static GoalSnapshot ExportGoalSnapshot(AgentOrchestratorKernel kernel, GoalId goalId) =>
        kernel.ExportSnapshot().Goals.FirstOrDefault(goal => goal.Id == goalId.Value)
            ?? throw new InvalidOperationException($"Goal '{goalId.Value}' no longer exists.");

    private static GoalStateSnapshot ExportGoalStateSnapshot(AgentOrchestratorKernel kernel, GoalId goalId)
    {
        var snapshot = kernel.ExportSnapshot();
        var goal = snapshot.Goals.FirstOrDefault(goal => goal.Id == goalId.Value)
            ?? throw new InvalidOperationException($"Goal '{goalId.Value}' no longer exists.");
        var humanInputRequests = snapshot.HumanInputRequests
            .Where(request => string.Equals(request.GoalId, goalId.Value, StringComparison.Ordinal))
            .ToArray();
        return new GoalStateSnapshot(goal, humanInputRequests);
    }

    private static GoalStateSnapshot MergeHumanInputCheckpoint(
        GoalStateSnapshot stored,
        GoalStateSnapshot current)
    {
        var requests = stored.HumanInputRequests.ToDictionary(request => request.Id, StringComparer.Ordinal);
        foreach (var request in current.HumanInputRequests)
        {
            if (!requests.TryGetValue(request.Id, out var existing) || request.IsCompleted && !existing.IsCompleted)
                requests[request.Id] = request;
        }

        return new GoalStateSnapshot(current.Goal, requests.Values.ToArray());
    }
}
