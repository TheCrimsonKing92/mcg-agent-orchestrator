using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintModelOutcomeScorecard(IReadOnlyList<ModelOutcomeRecord> records)
    {
        Console.WriteLine();
        Console.WriteLine($"Model outcome scorecard ({records.Count} model(s)):");
        if (records.Count == 0)
        {
            Console.WriteLine("  No completed or failed dispatches with model selection found.");
            Console.WriteLine();
            return;
        }

        foreach (var record in records)
        {
            Console.WriteLine();
            Console.WriteLine($"  {record.ProviderName}/{record.ModelName}: {record.Recommendation}");
            Console.WriteLine($"    completed={record.Completed} failed={record.Failed} " +
                $"adequate={record.SelfRatedAdequate} overkill={record.SelfRatedOverkill} " +
                $"underpowered={record.SelfRatedUnderpowered} divergence={record.Divergence}");
            Console.WriteLine($"    reason: {record.Reason}");
        }

        Console.WriteLine();
    }
}
