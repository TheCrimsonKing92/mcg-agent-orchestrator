using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintGoalDagPlan(GoalDagPlan plan, bool sliceBatch = false)
    {
        Console.WriteLine();
        Console.WriteLine(sliceBatch
            ? $"Dormant slice-batch intake preview: {plan.Nodes.Count} child node(s) for direction: {plan.Direction}"
            : $"DAG plan: {plan.Nodes.Count} node(s) for direction: {plan.Direction}");
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
            Console.WriteLine(sliceBatch
                ? "Confirm dormant intake: plan <direction> --slice-batch --confirm-plan | plan --text-file <path> --slice-batch --confirm-plan"
                : "Confirm: plan <direction> --confirm-plan | plan --text-file <path> --confirm-plan");
        else
            Console.WriteLine("Fix the direction and re-run to preview before confirming.");
    }
}
