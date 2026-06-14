using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintLoopHealthReport(LoopHealthSnapshot snapshot)
    {
        var windowLabel = snapshot.GoalCount == 1 ? "1 goal" : $"{snapshot.GoalCount} goals";
        Console.WriteLine();
        Console.WriteLine($"Loop health report ({windowLabel}):");
        Console.WriteLine($"  Goals: {snapshot.GoalCount} total, {snapshot.CompletedGoalCount} completed");
        Console.WriteLine($"  Tasks: {snapshot.TotalTaskCount} total, {snapshot.TotalDispatchCount} dispatches");
        Console.WriteLine();
        Console.WriteLine($"  Dispatches / successful merge:  {FormatRate(snapshot.DispatchesPerSuccessfulMerge, "dispatches/merge", snapshot.CompletedGoalCount == 0 ? " (no completed goals)" : string.Empty)}");
        Console.WriteLine($"  False-completion catch rate:    {snapshot.FalseCompletionCatchRate:P0}");
        Console.WriteLine($"  Operator prompts / goal:        {snapshot.OperatorPromptsPerGoal:F2}");
        Console.WriteLine($"  Rework / retry rate:            {snapshot.ReworkRetryRate:P0}");
        Console.WriteLine($"  Median time to acceptance:      {FormatMedianHours(snapshot.MedianTimeToAcceptanceHours)}");

        Console.WriteLine();
        Console.WriteLine("  Per-model outcome mix:");
        if (snapshot.ModelOutcomeMix.Count == 0)
        {
            Console.WriteLine("    No completed or failed dispatches with model selection found.");
        }
        else
        {
            foreach (var record in snapshot.ModelOutcomeMix)
            {
                Console.WriteLine($"    {record.ProviderName}/{record.ModelName}: {record.Recommendation}");
                Console.WriteLine($"      completed={record.Completed} failed={record.Failed} " +
                    $"adequate={record.SelfRatedAdequate} overkill={record.SelfRatedOverkill} " +
                    $"underpowered={record.SelfRatedUnderpowered}");
            }
        }

        Console.WriteLine();
    }

    private static string FormatRate(double value, string unit, string suffix = "")
    {
        return $"{value:F1} {unit}{suffix}";
    }

    private static string FormatMedianHours(double? hours)
    {
        if (hours is null)
        {
            return "n/a (no completed goals)";
        }

        if (hours.Value < 1.0)
        {
            return $"{hours.Value * 60.0:F0} min";
        }

        return $"{hours.Value:F1} hr";
    }
}
