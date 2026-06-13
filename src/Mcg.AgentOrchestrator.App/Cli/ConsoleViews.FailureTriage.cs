using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintFailureTriageReport(FailureTriageReport report)
    {
        Console.WriteLine($"Failure triage goal: {report.GoalPrefix}");
        Console.WriteLine($"Policy: {report.PolicyName}");
        Console.WriteLine("Findings:");
        foreach (var item in report.Items)
        {
            var target = item.TaskNumber is null ? "goal" : $"task {item.TaskNumber}";
            Console.WriteLine(
                $"  {item.Cause} {target}: action={item.Action}; canAutoApply={item.CanAutoApply}; gate={item.RequiresOperatorGate}; policyAllows={item.PolicyAllows}; command={item.SuggestedCommand}");
            Console.WriteLine($"    explanation: {item.Explanation}");
        }
    }
}
