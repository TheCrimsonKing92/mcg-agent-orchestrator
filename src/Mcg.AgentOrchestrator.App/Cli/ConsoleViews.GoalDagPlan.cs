using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintGoalDagPlan(GoalDagPlan plan)
    {
        Console.WriteLine();
        Console.WriteLine($"DAG plan: {plan.Nodes.Count} node(s) for direction: {plan.Direction}");
        foreach (var node in plan.Nodes)
        {
            Console.WriteLine();
            Console.WriteLine($"  {node.Id}: {node.Objective}");
            Console.WriteLine($"  Depends on: {(node.DependsOn.Count == 0 ? "none" : string.Join(", ", node.DependsOn))}");
        }

        var edges = plan.Nodes
            .SelectMany(n => n.DependsOn.Select(dep => (From: n.Id, To: dep)))
            .ToList();
        if (edges.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Dependency edges:");
            foreach (var (from, to) in edges)
                Console.WriteLine($"  {from} -> {to}");
        }

        if (plan.ValidationErrors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Validation errors:");
            foreach (var error in plan.ValidationErrors)
                Console.WriteLine($"  ERROR: {error}");
        }

        Console.WriteLine();
        if (plan.IsValid)
            Console.WriteLine("Confirm: plan <direction> --confirm-plan");
        else
            Console.WriteLine("Fix the direction and re-run to preview before confirming.");
    }
}
