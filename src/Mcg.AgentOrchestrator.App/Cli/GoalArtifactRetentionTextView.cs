using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalArtifactRetentionTextView
{
    public static void PrintGoalArtifactRetentionPlan(GoalArtifactRetentionPlan plan)
    {
        Console.WriteLine($"Retention plan goal: {plan.GoalPrefix}");
        Console.WriteLine($"State: {plan.State}");
        Console.WriteLine($"Dry run: {plan.DryRun}");
        Console.WriteLine("Artifacts:");
        foreach (var item in plan.Items)
        {
            Console.WriteLine(
                $"  {item.Kind}: {item.Decision}; exists={item.Exists}; path={item.Path}");
            Console.WriteLine($"    reason: {item.Reason}");
            if (!string.IsNullOrWhiteSpace(item.SuggestedCommand))
            {
                Console.WriteLine($"    command: {item.SuggestedCommand}");
            }
        }
    }
}
