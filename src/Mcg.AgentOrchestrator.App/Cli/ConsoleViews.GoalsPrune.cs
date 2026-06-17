using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintGoalsPrunePlan(GoalsPrunePlan plan)
{
    Console.WriteLine();
    Console.WriteLine(plan.DryRun
        ? $"goals-prune (dry-run): {plan.ReapableCount} reapable, {plan.Items.Count - plan.ReapableCount} skipped"
        : $"goals-prune: {plan.PrunedCount} pruned, {plan.Items.Count - plan.PrunedCount} skipped");

    foreach (var item in plan.Items)
    {
        var tag = item.Disposition switch
        {
            GoalPruneDisposition.Reapable => "REAPABLE",
            GoalPruneDisposition.Pruned   => "PRUNED",
            _                             => $"skipped/{item.Disposition}"
        };
        Console.WriteLine($"  {item.GoalPrefix} [{item.Status}] {tag}: {item.Reason}");
    }

    if (plan.DryRun)
    {
        Console.WriteLine();
        if (plan.ReapableCount > 0)
            Console.WriteLine($"Re-run with --confirm-prune to cancel {plan.ReapableCount} goal(s) and delete their merged branches.");
        else
            Console.WriteLine("No reapable goals found.");
    }
}
}
