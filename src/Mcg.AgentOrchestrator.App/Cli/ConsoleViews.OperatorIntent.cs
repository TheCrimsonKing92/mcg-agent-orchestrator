using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintOperatorIntentTemplates(IReadOnlyList<OperatorIntentTemplate> templates)
{
    Console.WriteLine();
    Console.WriteLine($"Intent templates: {templates.Count}");
    foreach (var template in templates)
    {
        Console.WriteLine($"  {template.Name}: {template.Description}");
    }

    Console.WriteLine();
    Console.WriteLine("Create commands:");
    Console.WriteLine("  intent-template <template> <request> --create-goal");
    Console.WriteLine("  intent-template <template> <request> --create-simple-goal");
    Console.WriteLine();
}

public static void PrintOperatorIntentPlan(OperatorIntentPlan plan)
{
    Console.WriteLine();
    Console.WriteLine($"Intent template: {plan.Template.Name}");
    Console.WriteLine(plan.Template.Description);
    Console.WriteLine();
    Console.WriteLine(plan.ReadyObjective);
    Console.WriteLine();
    Console.WriteLine("Create commands:");
    Console.WriteLine($"  intent-template {plan.Template.Name} <request> --create-goal");
    Console.WriteLine($"  intent-template {plan.Template.Name} <request> --create-simple-goal");
    Console.WriteLine();
}
}
