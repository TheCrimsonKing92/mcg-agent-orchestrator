using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class SliceBatchParentExecutionGuard(
    Func<IReadOnlyCollection<Goal>> goalSource)
{
    private HashSet<GoalId>? _parentIdsThisTick;

    internal void BeginTick() => _parentIdsThisTick = LoadParentIds();

    internal bool IsSliceBatchParent(Goal goal) =>
        goal.SliceBatchParentId is null &&
        (_parentIdsThisTick ??= LoadParentIds()).Contains(goal.Id);

    internal string? TryDescribeHold(Goal goal)
    {
        if (!IsSliceBatchParent(goal))
        {
            return null;
        }

        return $"Slice-batch parent {goal.Id.Value[..8]} owns child goals and does not execute worker tasks.";
    }

    private HashSet<GoalId> LoadParentIds() => goalSource()
        .Where(candidate => candidate.SliceBatchParentId is not null)
        .Select(candidate => candidate.SliceBatchParentId!)
        .ToHashSet();
}
