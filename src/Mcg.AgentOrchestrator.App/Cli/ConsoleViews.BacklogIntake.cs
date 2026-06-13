using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintBacklogIntakePlan(BacklogIntakePlan plan)
{
    Console.WriteLine();
    Console.WriteLine($"Backlog intake: {plan.Items.Count} item(s) from {plan.BacklogPath}");
    foreach (var item in plan.Items)
    {
        Console.WriteLine();
        Console.WriteLine($"## {item.Heading}");
        Console.WriteLine($"Roles: {string.Join(", ", item.Roles)}");
        Console.WriteLine($"Risks: {string.Join(", ", item.RiskLabels)}");
        Console.WriteLine("Target files/scopes:");
        foreach (var target in item.TargetFiles)
        {
            Console.WriteLine($"  - {target}");
        }

        Console.WriteLine("Verification:");
        foreach (var check in item.Verification)
        {
            Console.WriteLine($"  - {check}");
        }

        Console.WriteLine("Dependencies:");
        foreach (var dependency in item.Dependencies)
        {
            Console.WriteLine($"  - {dependency}");
        }

        Console.WriteLine($"Workspace: {item.WorkspacePlan}");
        Console.WriteLine($"Acceptance: {item.AcceptanceChecks}");
        Console.WriteLine($"Follow-up: {item.FollowUpUpdates}");
        Console.WriteLine("Create commands:");
        Console.WriteLine($"  backlog-intake \"{EscapeDoubleQuoted(item.Heading)}\" --create-goal");
        Console.WriteLine($"  backlog-intake \"{EscapeDoubleQuoted(item.Heading)}\" --create-simple-goal");
        Console.WriteLine("Ready objective:");
        Console.WriteLine(item.SuggestedObjective);
    }

    Console.WriteLine();
}

private static string EscapeDoubleQuoted(string value)
{
    return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
}
