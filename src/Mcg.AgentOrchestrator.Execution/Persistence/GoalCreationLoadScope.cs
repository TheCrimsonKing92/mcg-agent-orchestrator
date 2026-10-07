using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>The goal and backlog identities consulted by a scoped goal transaction.</summary>
public sealed record GoalCreationLoadScope(
    IReadOnlyCollection<GoalId> GoalIds,
    IReadOnlyCollection<string> BacklogItemIds,
    IReadOnlyCollection<GoalId>? DependsOnClosureRoots = null)
{
    public IReadOnlyCollection<GoalId> DependsOnClosureRoots { get; init; } = DependsOnClosureRoots ?? [];

    public static GoalCreationLoadScope ForGoals(
        IEnumerable<GoalId> goalIds, IEnumerable<GoalId> dependsOnClosureRoots) =>
        new(goalIds.Distinct().ToArray(), [], dependsOnClosureRoots.Distinct().ToArray());

    public static GoalCreationLoadScope ForPreparedGoal(GoalSnapshot prepared, BacklogItem? sourceItem)
    {
        var goalIds = new HashSet<string>(prepared.DependsOn ?? [], StringComparer.Ordinal) { prepared.Id };
        var backlogItemIds = new HashSet<string>(StringComparer.Ordinal);
        if (prepared.SourceBacklogItemId is { } sourceId)
            backlogItemIds.Add(sourceId);
        foreach (var dependency in sourceItem?.Dependencies ?? [])
        {
            if (dependency.TargetKind == BacklogDependencyTargetKind.Goal)
                goalIds.Add(dependency.PrerequisiteId);
            else
                backlogItemIds.Add(dependency.PrerequisiteId);
        }
        return new GoalCreationLoadScope(goalIds.Select(id => new GoalId(id)).ToArray(), backlogItemIds.ToArray());
    }
}
