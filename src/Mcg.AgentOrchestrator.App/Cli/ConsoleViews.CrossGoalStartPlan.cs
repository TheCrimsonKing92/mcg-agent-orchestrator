using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintCrossGoalSubscriptionStartPlan(CrossGoalSubscriptionStartPlan plan)
    {
        Console.WriteLine();
        Console.WriteLine($"Cross-goal subscription start plan: {plan.Candidates.Count} candidate goal(s)");
        foreach (var candidate in plan.Candidates)
        {
            Console.WriteLine($"  {candidate.GoalPrefix}: tasks={string.Join(",", candidate.TaskNumbers)} provider={candidate.ProviderKey ?? "none"} costConfirm={candidate.RequiresCostConfirmation}");
            Console.WriteLine($"     objective: {OutputTextPreview.CreateSummary(candidate.Objective).Text}");
            Console.WriteLine($"     paths: {(candidate.TargetPaths.Count == 0 ? "none" : string.Join(", ", candidate.TargetPaths))}");
            Console.WriteLine($"     resources: {string.Join(", ", candidate.RequiredResources)}");
            Console.WriteLine($"     detail: {OutputTextPreview.CreateTimeline(candidate.Detail).Text}");
        }

        Console.WriteLine("Batches:");
        if (plan.ParallelPlan.Batches.Count == 0)
        {
            Console.WriteLine("  none");
        }
        else
        {
            foreach (var batch in plan.ParallelPlan.Batches)
            {
                Console.WriteLine($"  batch {batch.Number}: {string.Join(", ", batch.IntentIds.Select(ShortGoal))}");
            }
        }

        Console.WriteLine("Decisions:");
        foreach (var decision in plan.ParallelPlan.Decisions)
        {
            Console.WriteLine($"  {ShortGoal(decision.IntentId)}: {decision.Disposition}" +
                (decision.BatchNumber is null ? "" : $" batch={decision.BatchNumber}") +
                $" ({string.Join("; ", decision.Reasons)})");
        }

        Console.WriteLine();
    }

    private static string ShortGoal(string goalId) => goalId.Length <= 8 ? goalId : goalId[..8];
}
