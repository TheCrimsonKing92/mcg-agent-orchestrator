using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintGoalRollbackPlan(GoalRollbackPlan plan)
    {
        Console.WriteLine($"Goal rollback {plan.GoalPrefix}: {OutputTextPreview.CreateTimeline(plan.Reason).Text}");
        Console.WriteLine($"Dry run: {plan.DryRun}");
        Console.WriteLine($"Can apply: {plan.CanApply}");
        Console.WriteLine($"Rollback branch: {plan.RollbackBranch}");
        Console.WriteLine($"Detail: {plan.Detail}");
        if (!string.IsNullOrWhiteSpace(plan.SuggestedCommand))
        {
            Console.WriteLine($"Command: {plan.SuggestedCommand}");
        }
    }
}
