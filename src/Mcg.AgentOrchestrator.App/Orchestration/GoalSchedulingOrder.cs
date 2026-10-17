using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalSchedulingOrder
{
    internal static IReadOnlyList<Goal> BaseAcceptance(IEnumerable<Goal> goals) => goals
        .OrderBy(ParallelAcceptanceVerifiedAt)
        .ThenBy(goal => goal.Timeline.FirstOrDefault()?.OccurredAt ?? DateTimeOffset.MinValue)
        .ThenBy(goal => goal.Id.Value, StringComparer.Ordinal).ToArray();

    internal static IReadOnlyList<Goal> ForAcceptance(IEnumerable<Goal> goals) => BaseAcceptance(goals)
        .Select((goal, rank) => (Goal: goal, Key: new GoalSchedulingKey(GoalEffectivePriorityIndex.EffectiveLevel(goal), rank)))
        .OrderBy(item => item.Key).Select(item => item.Goal).ToArray();

    internal static IReadOnlyList<Goal> ForWalk(IEnumerable<Goal> goals) => goals
        .Select((goal, rank) => (Goal: goal, Key: new GoalSchedulingKey(GoalEffectivePriorityIndex.EffectiveLevel(goal, dispatch: true), rank)))
        .OrderBy(item => item.Key).Select(item => item.Goal).ToArray();

    internal static bool GroupKeepsHead(IEnumerable<string> memberIds, IEnumerable<Goal> orderedEligible,
        IReadOnlySet<string> liveIds)
    {
        var head = orderedEligible.FirstOrDefault(goal => !liveIds.Contains(goal.Id.Value));
        return head is null || GoalEffectivePriorityIndex.EffectiveLevel(head) < GoalPriority.High ||
            memberIds.Contains(head.Id.Value, StringComparer.Ordinal);
    }

    private static DateTimeOffset ParallelAcceptanceVerifiedAt(Goal goal)
    {
        var lastVerification = goal.Tasks
            .Select(task => task.LastVerification?.CompletedAt)
            .Where(completedAt => completedAt.HasValue)
            .Select(completedAt => completedAt!.Value)
            .DefaultIfEmpty(goal.Timeline.FirstOrDefault()?.OccurredAt ?? DateTimeOffset.MinValue)
            .Max();
        return goal.Timeline
            .Where(evt =>
                evt.Kind == ProgressKind.GoalPolicyDecision &&
                evt.Message.Contains("Verified", StringComparison.OrdinalIgnoreCase))
            .Select(evt => evt.OccurredAt)
            .DefaultIfEmpty(lastVerification)
            .Min();
    }
}
