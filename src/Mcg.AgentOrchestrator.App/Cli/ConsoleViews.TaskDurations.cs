using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintTaskDurationStats(IReadOnlyList<TaskDurationStatsRecord> records)
    {
        Console.WriteLine();
        Console.WriteLine($"Task duration stats ({records.Count} group(s)):");
        if (records.Count == 0)
        {
            Console.WriteLine("  No dispatch timing history found.");
            Console.WriteLine();
            return;
        }

        foreach (var record in records)
        {
            var medianRuntime = record.HasPublishedStats
                ? FormatDuration(record.MedianLegitimateRuntime)
                : FormatInsufficient(record.TaskCount);
            var p90Runtime = record.HasPublishedStats
                ? FormatDuration(record.P90LegitimateRuntime)
                : FormatInsufficient(record.TaskCount);
            var medianOverhead = record.HasPublishedStats
                ? FormatDuration(record.MedianFailureInterventionOverhead)
                : FormatInsufficient(record.TaskCount);
            Console.WriteLine(
                $"  {record.Scope}: tasks={record.TaskCount} attempts={record.AttemptCount} " +
                $"legit median={medianRuntime} p90={p90Runtime} " +
                $"overhead median={medianOverhead} " +
                $"failureRate={record.FailureRate:P0}");
        }

        Console.WriteLine();
    }

    private static string FormatDuration(TimeSpan? duration)
    {
        if (duration is null)
        {
            return "n/a";
        }

        var value = duration.Value;
        return value.TotalMinutes >= 1
            ? $"{value.TotalMinutes:0.#}m"
            : $"{value.TotalSeconds:0.#}s";
    }

    private static string FormatInsufficient(int sampleCount) => $"n/a (n={sampleCount})";
}
