using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

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

    internal static string? TryDescribeStreamCompleteHold(Goal goal)
    {
        if (goal.SliceBatchParentId is null ||
            goal.Status is not (GoalStatus.Verified or GoalStatus.Verifying) ||
            !AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal))
        {
            return null;
        }

        return $"Slice-batch child {goal.Id.Value[..8]} stream is complete and waits for composition into parent goal {goal.SliceBatchParentId.Value}.";
    }

    internal static bool AreAllChildrenStreamComplete(Goal parent, IReadOnlyCollection<Goal> children) =>
        parent.SliceBatchParentId is null && children.Count > 0 &&
        children.All(child => child.SliceBatchParentId == parent.Id &&
            TryDescribeStreamCompleteHold(child) is not null);

    internal static string? TryDescribeCompositionHold(Goal parent, StreamCompositionResult result)
    {
        var prefix = $"Slice-batch parent {parent.Id.Value[..8]} composition held:";
        if (result.Ejection is { } ejection)
        {
            var paths = string.Join(", ", ejection.ConflictPaths.Order(StringComparer.Ordinal));
            return $"{prefix} child {ejection.GoalId.Value[..8]} {ejection.Reason} on {paths}; {ejection.Detail}";
        }
        if (result.BuildCheck is { Passed: false } build)
        {
            var children = string.Join('+', result.ComposedChildren.Select(id => id.Value[..8]));
            return $"{prefix} composed children {children} fail the build in project {build.FailingProject}; {build.OutputExcerpt}";
        }
        return null;
    }

    private HashSet<GoalId> LoadParentIds() => goalSource()
        .Where(candidate => candidate.SliceBatchParentId is not null)
        .Select(candidate => candidate.SliceBatchParentId!)
        .ToHashSet();
}
