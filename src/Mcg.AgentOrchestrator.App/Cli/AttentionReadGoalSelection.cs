using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class AttentionReadGoalSelection
{
    internal sealed record Result(bool Decline, IReadOnlyList<GoalId> GoalIds);

    internal static Result Select(
        IReadOnlyList<GoalSummary> metadata,
        IReadOnlyList<HumanInputRequestSnapshot> openWaits,
        IReadOnlyList<CollaborationItem> storedItems,
        IReadOnlyList<CollaborationItem> attentionQueue)
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
            .Select(id => goals[id].Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => new GoalId(id))
            .ToArray();
        return new Result(false, ids);
    }
}
