using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalAbandonTextView
{
    public static void PrintGoalAbandonPlan(GoalAbandonPlan plan)
    {
        Console.WriteLine($"Goal abandon {plan.GoalPrefix} {plan.GoalStatus}: {OutputTextPreview.CreateTimeline(plan.Reason).Text}");
        Console.WriteLine($"Dry run: {plan.DryRun}");
        Console.WriteLine($"Can apply: {plan.CanApply}");
        Console.WriteLine("Steps:");
        foreach (var step in plan.Steps)
        {
            Console.WriteLine($"  {step.Kind}: {step.Disposition}; {OutputTextPreview.CreateTimeline(step.Detail).Text}");
            if (!string.IsNullOrWhiteSpace(step.SuggestedCommand))
            {
                Console.WriteLine($"    command: {step.SuggestedCommand}");
            }
        }

        Console.WriteLine("Retention:");
        foreach (var item in plan.RetentionPlan.Items)
        {
            Console.WriteLine($"  {item.Kind}: {item.Decision}; exists={item.Exists}");
        }
    }
}
