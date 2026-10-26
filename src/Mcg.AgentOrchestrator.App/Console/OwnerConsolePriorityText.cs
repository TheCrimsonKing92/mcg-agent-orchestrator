using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsolePriorityText
{
    internal static string Column(Goal goal) => GoalEffectivePriorityIndex.StoredLevel(goal).ToString();

    internal static string? DetailLine(Goal goal)
    {
        var level = GoalEffectivePriorityIndex.StoredLevel(goal);
        if (level == GoalPriority.Normal) return null;
        if (level <= goal.Priority) return "Priority: " + level + " (set with goal-priority)";
        var source = goal.PriorityScheduling?.InheritedFromGoalId is { } donor
            ? "inherited from dependent goal " + OwnerConsoleViewModelBuilder.Prefix(donor)
            : "inherited from the goal's stored level";
        var own = goal.Priority == GoalPriority.Normal ? "not set with goal-priority" : "goal-priority set " + goal.Priority;
        return "Priority: " + level + " (" + source + "; " + own + ")";
    }

    internal static string? Warning(Goal goal, IReadOnlyList<OwnerConductEvent> events)
    {
        var matches = events.Where(item => (item.Detail.StartsWith("PRIORITY_DILUTION ", StringComparison.Ordinal) ||
                                           item.Detail.StartsWith("PRIORITY_OVERLAP ", StringComparison.Ordinal)) &&
                                          OwnerActivityNarrator.Field(item, "goal") == OwnerConsoleViewModelBuilder.Prefix(goal.Id.Value))
            .OrderByDescending(item => item.Timestamp).ToArray();
        return matches.Length == 0 ? null : matches[0].Detail + (matches.Length > 1 ? $" (+{matches.Length - 1} more)" : "");
    }
}
