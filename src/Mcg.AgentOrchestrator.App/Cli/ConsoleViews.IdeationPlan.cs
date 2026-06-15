using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintIdeationPlan(IdeationPlan plan)
    {
        Console.WriteLine();
        Console.WriteLine($"Ideation: {plan.Ideas.Count} idea(s) proposed");

        for (var i = 0; i < plan.Ideas.Count; i++)
        {
            var idea = plan.Ideas[i];
            Console.WriteLine();
            Console.WriteLine($"  {i + 1}. {idea.Title}");
            Console.WriteLine($"     Rationale: {idea.Rationale}");
            Console.WriteLine($"     Scope:     {idea.Scope}");
            Console.WriteLine($"     Value:     {idea.Value}");
            Console.WriteLine($"     Effort:    {idea.Effort}");
            Console.WriteLine($"     Risk:      {idea.Risk}");
        }

        if (plan.ValidationErrors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Validation errors (hand-wavy ideas rejected):");
            foreach (var error in plan.ValidationErrors)
                Console.WriteLine($"  ERROR: {error}");
        }

        Console.WriteLine();
        if (plan.IsValid && plan.Ideas.Count > 0)
            Console.WriteLine("Append to BACKLOG.md: ideate --append-backlog");
        else if (!plan.IsValid)
            Console.WriteLine("Fix the validation errors before appending ideas to BACKLOG.md.");
    }
}
