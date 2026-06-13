using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintGoalDrainPlan(GoalDrainPlan plan, IReadOnlyList<string>? appliedActions = null)
    {
        Console.WriteLine($"Goal drain: policy={plan.PolicyName}; apply={plan.Apply}");
        Console.WriteLine(
            $"Drain policy: {plan.DrainPolicy.Name}; maxStarts={plan.DrainPolicy.MaxSubscriptionStartsPerDrain}; roles={string.Join(",", plan.DrainPolicy.AllowedRoles)}; providers={string.Join(",", plan.DrainPolicy.AllowedProviders)}; largePrompts={plan.DrainPolicy.LargePromptBehavior}; readinessRiskConfirmation={plan.DrainPolicy.RequireReadinessRiskConfirmation}; acceptanceGate={plan.DrainPolicy.RequireAcceptanceGate}; windows={FormatDrainWindows(plan.DrainPolicy)}");
        Console.WriteLine($"Schedule: {(plan.Schedule.IsOpen ? "open" : "closed")}; {plan.Schedule.Detail}");
        Console.WriteLine($"Ready subscription goals: {plan.ReadySubscriptionGoalCount}; first batch: {plan.FirstBatchGoalCount}; acceptance ready: {plan.AcceptanceReadyCount}; gates: {plan.OperatorGateCount}; safe supervisor actions: {plan.SafeSupervisorActionCount}");
        if (appliedActions is not null)
        {
            Console.WriteLine($"Applied actions: {appliedActions.Count}");
            foreach (var action in appliedActions)
            {
                Console.WriteLine($"  - {action}");
            }
        }

        Console.WriteLine("Items:");
        if (plan.Items.Count == 0)
        {
            Console.WriteLine("  none");
            return;
        }

        foreach (var item in plan.Items)
        {
            Console.WriteLine($"  {item.Stage} {item.GoalPrefix}: canApply={item.CanApply}; gate={item.RequiresOperatorGate}");
            Console.WriteLine($"    {OutputTextPreview.CreateTimeline(item.Detail).Text}");
            Console.WriteLine($"    command: {item.SuggestedCommand}");
        }
    }

    private static string FormatDrainWindows(GoalDrainPolicy policy)
    {
        var windows = policy.AllowedLocalTimeWindows ?? [];
        return windows.Count == 0 ? "always" : string.Join(",", windows);
    }
}
