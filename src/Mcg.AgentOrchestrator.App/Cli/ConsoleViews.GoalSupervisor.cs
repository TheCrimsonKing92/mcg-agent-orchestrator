using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintGoalSupervisorPlan(GoalSupervisorPlan plan, IReadOnlyList<string>? appliedActions = null)
    {
        Console.WriteLine($"Supervisor goal: {plan.GoalPrefix}");
        Console.WriteLine($"Policy: {plan.PolicyName}");
        Console.WriteLine($"Apply safe: {plan.ApplySafe}");
        if (appliedActions is { Count: > 0 })
        {
            Console.WriteLine($"Applied actions: {appliedActions.Count}");
            foreach (var action in appliedActions)
            {
                Console.WriteLine($"  {action}");
            }
        }

        Console.WriteLine("Proposals:");
        foreach (var proposal in plan.Proposals)
        {
            var task = proposal.TaskNumber is null ? "goal" : $"task {proposal.TaskNumber}";
            Console.WriteLine(
                $"  {proposal.Kind} {task}: canApply={proposal.CanApply}; gate={proposal.RequiresOperatorGate}; policyAllows={proposal.PolicyAllows}; command={proposal.SuggestedCommand}");
            Console.WriteLine($"    reason: {proposal.Reason}");
        }
    }
}
