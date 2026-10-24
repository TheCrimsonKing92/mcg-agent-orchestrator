using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerGoalDetailQuery(IOrchestratorStateQueries state, IGoalEventTail tail,
    Func<OwnerConsoleGateProgress?>? gate = null)
{
    internal async Task<OwnerGoalDetailSnapshot?> LoadAsync(string goalId, CancellationToken cancellationToken)
    {
        var kernel = await state.LoadGoalsAsync([new GoalId(goalId)], cancellationToken);
        var goal = kernel.Goals.SingleOrDefault(goal => goal.Id.Value == goalId);
        if (goal is null) return null;
        int? position = null;
        if (goal.Status == GoalStatus.Verified && goal.CurrentHold is null)
        {
            var metadata = await state.ListGoalMetadataAsync(cancellationToken);
            var ids = metadata.Where(item => item.Status == nameof(GoalStatus.Verified)).Select(item => new GoalId(item.Id)).ToArray();
            var queued = await state.LoadGoalsAsync(ids, cancellationToken);
            var ordered = ConductorBatchLoop.OrderParallelAcceptanceEligibleGoals(queued.Goals
                .Where(item => item.Status == GoalStatus.Verified && item.CurrentHold is null).ToArray());
            var index = ordered.ToList().FindIndex(item => item.Id == goal.Id);
            if (index >= 0) position = index + 1;
        }
        var roles = goal.Tasks.ToDictionary(task => task.Id.Value, task => task.RequiredRole);
        var events = tail.ReadLast(goalId, 100)
            .Select(line => OwnerGoalLifecycleEvent.TryParse(line, goalId, out var item) ? item : null)
            .Where(item => item is not null).Select(item => OwnerGoalLifecycleEvent.WithRole(item!, roles)).ToArray();
        return new OwnerGoalDetailSnapshot(goal, position, events).WithGate(gate?.Invoke());
    }
}
