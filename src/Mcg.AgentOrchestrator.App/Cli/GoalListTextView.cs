using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalListTextView
{
    public static void Print(AgentOrchestratorKernel kernel)
    {
        foreach (var goal in kernel.Goals.OrderByDescending(goal => goal.Timeline.FirstOrDefault()?.OccurredAt ?? DateTimeOffset.MinValue))
        {
            var condition = GoalLifecycle.HasActiveFailedTask(goal)
                ? $" [{GoalLifecycle.ActiveWithFailedTaskCondition}]"
                : string.Empty;
            var priority = goal.Priority == GoalPriority.Normal ? string.Empty : $" [priority={GoalPriorities.Format(goal.Priority)}]";
            Console.WriteLine($"{goal.Id.Value[..8]} {goal.Status}{condition}{priority}: {OutputTextPreview.CreateSummary(goal.Objective).Text}");
        }
    }

    // Metadata-only listing: renders the same line as the kernel overload from GoalSummary rows
    // (already ordered by the repository) without hydrating any goal snapshot.
    public static void Print(IReadOnlyList<GoalSummary> goals)
    {
        foreach (var goal in goals)
        {
            var prefix = goal.Id.Length >= 8 ? goal.Id[..8] : goal.Id;
            var condition = string.IsNullOrWhiteSpace(goal.Condition) ? string.Empty : $" [{goal.Condition}]";
            Console.WriteLine($"{prefix} {goal.Status}{condition}: {OutputTextPreview.CreateSummary(goal.Objective).Text}");
        }
    }

    public static void PrintCleanupDebtWarning(IReadOnlyList<GoalWorktreeCleanupDebt> debts)
    {
        var escalatedCount = debts.Count(debt => debt.EscalatedAtUtc is not null);
        if (escalatedCount > 0)
        {
            Console.WriteLine(
                $"WARNING: stale worktree cleanup escalated={escalatedCount} pending={debts.Count}; run cleanup-status.");
        }
    }
}
