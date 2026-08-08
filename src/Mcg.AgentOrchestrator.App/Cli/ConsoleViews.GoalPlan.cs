using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintGoalDependencyPlan(GoalDependencyPlan plan)
{
    Console.WriteLine();
    Console.WriteLine($"Goal plan: {plan.Nodes.Count} node(s), {plan.Edges.Count} dependency edge(s)");
    Console.WriteLine($"Compiled graph: {plan.CompiledGraph.GraphId} runnable={plan.CompiledGraph.IsRunnable}");
    foreach (var node in plan.Nodes)
    {
        var compiled = plan.CompiledGraph.Nodes.Single(item => item.Id == node.Id);
        Console.WriteLine();
        Console.WriteLine($"{node.Id}: {node.Heading}");
        Console.WriteLine($"Roles: {string.Join(", ", node.Intake.Roles)}");
        Console.WriteLine($"Risks: {string.Join(", ", node.Intake.RiskLabels)}");
        Console.WriteLine($"Depends on: {(node.DependsOn.Count == 0 ? "none" : string.Join(", ", node.DependsOn))}");
        Console.WriteLine($"Rollback boundary: {compiled.RollbackBoundary}");
        Console.WriteLine($"Parallel: {compiled.ParallelDisposition}" +
            (compiled.ParallelBatch is null ? "" : $" batch={compiled.ParallelBatch}") +
            $" create={compiled.CanCreateGoal}");
        Console.WriteLine("Target files/scopes:");
        foreach (var target in node.Intake.TargetFiles)
        {
            Console.WriteLine($"  - {target}");
        }

        Console.WriteLine("Capabilities:");
        foreach (var capability in compiled.RequiredCapabilities)
        {
            Console.WriteLine($"  - {capability}");
        }

        Console.WriteLine("Verification contracts:");
        foreach (var contract in compiled.VerificationContracts)
        {
            Console.WriteLine($"  - {contract}");
        }
    }

    if (plan.Edges.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Dependency edges:");
        foreach (var edge in plan.Edges)
        {
            Console.WriteLine($"  {edge.FromId} -> {edge.ToId}: {edge.Reason}");
        }
    }

    if (plan.CompiledGraph.Findings.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Compiled graph findings:");
        foreach (var finding in plan.CompiledGraph.Findings)
        {
            Console.WriteLine($"  {finding.Severity} {finding.NodeId}: {finding.Message}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("Parallel batches:");
    foreach (var batch in plan.ParallelPlan.Batches)
    {
        Console.WriteLine($"  batch {batch.Number}: {string.Join(", ", batch.IntentIds)}");
    }

    Console.WriteLine("Parallel decisions:");
    foreach (var decision in plan.ParallelPlan.Decisions)
    {
        Console.WriteLine($"  {decision.IntentId}: {decision.Disposition}" +
            (decision.BatchNumber is null ? "" : $" batch={decision.BatchNumber}") +
            $" ({string.Join("; ", decision.Reasons)})");
    }

    Console.WriteLine();
    Console.WriteLine("Create commands:");
    Console.WriteLine("  goal-plan --create-goals --backlog-coverage <full|slice>");
    Console.WriteLine("  goal-plan --create-simple-goals --backlog-coverage <full|slice>");
    Console.WriteLine();
}
}
