using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintAcceptanceQueuePlan(AcceptanceQueuePlan plan)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"Acceptance queue: {plan.Items.Count} goal(s), ready={plan.ReadyCount}, held={plan.HeldCount}, blocked={plan.BlockedCount}, policy={plan.Policy.Name}");
        if (plan.Items.Count == 0)
        {
            Console.WriteLine("  none");
            Console.WriteLine();
            return;
        }

        foreach (var item in plan.Items)
        {
            Console.WriteLine(
                $"  {item.GoalPrefix}: {item.Disposition} status={item.Status} branch={item.BranchName} diff={item.HasBranchDiff} dirty={item.WorktreeDirty?.ToString() ?? "unknown"}");
            Console.WriteLine($"     objective: {OutputTextPreview.CreateSummary(item.Objective).Text}");
            Console.WriteLine($"     reason: {OutputTextPreview.CreateTimeline(item.Reason).Text}");
            Console.WriteLine($"     command: {item.SuggestedCommand}");
        }

        Console.WriteLine();
    }
}
