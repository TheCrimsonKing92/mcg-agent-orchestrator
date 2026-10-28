using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class AttentionReadGoalSelection
{
    internal sealed record Result(bool Decline, IReadOnlyList<GoalId> GoalIds);

    internal static IReadOnlyList<GoalId>? ReadPostLandingDutyGoalIds(
        IOrchestratorStateQueries queries, IReadOnlyList<GoalSummary> metadata, AttentionReadProjection? projection)
    {
        if (projection is not null)
            return (projection.PostLandingDuties ?? []).Select(row => new GoalId(row.GoalId)).Distinct().ToArray();

        // Null means unsupported, not empty. Explicit IDs hydrate archived snapshots too.
        var ids = metadata.Select(goal => new GoalId(goal.Id)).ToArray();
        if (ids.Length == 0) return [];
        var kernel = queries.LoadGoalsAsync(ids).GetAwaiter().GetResult();
        if (kernel.Goals.Count != ids.Length || !ids.ToHashSet().SetEquals(kernel.Goals.Select(goal => goal.Id)))
            return null;
        return kernel.Goals.Where(goal => goal.OutstandingPostLandingDuties.Count > 0)
            .Select(goal => goal.Id).ToArray();
    }

    internal static Goal Resolve(AgentOrchestratorKernel kernel, string goalPrefix)
    {
        var matches = kernel.Goals
            .Where(goal => goal.Id.Value.StartsWith(goalPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new KeyNotFoundException($"No goal found matching prefix '{goalPrefix}'."),
            _ => throw new InvalidOperationException($"Goal prefix '{goalPrefix}' is ambiguous ({matches.Count} matches).")
        };
    }

    internal static Result Select(
        IReadOnlyList<GoalSummary> metadata,
        IReadOnlyList<HumanInputRequestSnapshot> openWaits,
        IReadOnlyList<CollaborationItem> storedItems,
        IReadOnlyList<CollaborationItem> attentionQueue,
        IReadOnlyList<GoalId>? postLandingDutyGoalIds = null)
    {
        var goals = metadata.ToDictionary(goal => goal.Id, StringComparer.OrdinalIgnoreCase);
        var parked = metadata
            .Where(goal => goal.Status.Equals(nameof(GoalStatus.Parked), StringComparison.OrdinalIgnoreCase))
            .Select(goal => goal.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (openWaits.Any(wait => parked.Contains(wait.GoalId)) ||
            storedItems.Any(item => item.GoalId is not null && parked.Contains(item.GoalId) &&
                item.Type is CollaborationItemType.Decision or CollaborationItemType.Clarification or CollaborationItemType.Verify &&
                item.Status is not CollaborationItemStatus.Resolved and not CollaborationItemStatus.Closed))
            return new Result(true, []);

        string[] nonLiveStatuses =
        [
            nameof(GoalStatus.Parked), nameof(GoalStatus.Completed), nameof(GoalStatus.Failed),
            nameof(GoalStatus.Cancelled), nameof(GoalStatus.Superseded)
        ];
        var ids = openWaits.Select(wait => wait.GoalId)
            .Concat(attentionQueue
                .Where(item => CollaborationItemLifecycle.IsReachUpType(item.Type) && item.GoalId is not null)
                .Select(item => item.GoalId!))
            .Where(id => goals.TryGetValue(id, out var goal) &&
                !nonLiveStatuses.Contains(goal.Status, StringComparer.OrdinalIgnoreCase))
            .Concat((postLandingDutyGoalIds ?? []).Select(id => id.Value).Where(goals.ContainsKey))
            .Select(id => goals[id].Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => new GoalId(id))
            .ToArray();
        return new Result(false, ids);
    }
}
