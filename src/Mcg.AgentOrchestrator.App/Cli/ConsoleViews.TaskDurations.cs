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
            Console.WriteLine(
                $"  {record.Scope}: tasks={record.TaskCount} attempts={record.AttemptCount} " +
                $"legit median={FormatDuration(record.MedianLegitimateRuntime)} p90={FormatDuration(record.P90LegitimateRuntime)} " +
                $"overhead median={FormatDuration(record.MedianFailureInterventionOverhead)} " +
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
}
