using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>
/// Persists conductor batch-loop tick events in the run-event store.
/// </summary>
public static class ConductorTickPusher
{
    internal const int MaxPersistedProgressLines = 32;
    internal const int MaxPersistedProgressLineChars = 240;

    public static Action<BatchTickSummary> CreateStoreCallback(string runEventStorePath)
    {
        return tick => TryRecord(runEventStorePath, tick);
    }

    public static void TryRecord(string runEventStorePath, BatchTickSummary tick)
    {
        try
        {
            var dto = ToDto(tick);
            var store = new SqliteRunEventStore(runEventStorePath);
            store.AppendAsync(new RunEventAppend(
                RunEventTypes.ConductorTick,
                GoalId: null,
                Operation: "conduct:tick",
                Status: tick.WatchSleeping ? "Sleeping" : "Active",
                Detail: $"tick={tick.Tick} advanced={tick.Advanced} held={tick.Held} escalated={tick.Escalated} retried={tick.Retried} done={tick.Done}",
                PayloadJson: JsonSerializer.Serialize(dto),
                OccurredAt: DateTimeOffset.UtcNow))
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // Observability is advisory; never fail the conductor because a dashboard event write failed.
        }
    }

    private static object ToDto(BatchTickSummary tick)
    {
        var progressLines = CompactProgressLines(tick.ProgressLines);
        return new
        {
            tick.Tick,
            tick.Advanced,
            tick.Held,
            tick.Escalated,
            tick.Retried,
            tick.Done,
            tick.WatchSleeping,
            progressLines,
            operatorDispositionCount = tick.OperatorDispositions?.Count ?? 0
        };
    }

    private static IReadOnlyList<string> CompactProgressLines(IReadOnlyList<string>? progressLines)
    {
        if (progressLines is null || progressLines.Count == 0)
        {
            return [];
        }

        return progressLines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(MaxPersistedProgressLines)
            .Select(line => line.Length <= MaxPersistedProgressLineChars
                ? line
                : line[..MaxPersistedProgressLineChars])
            .ToList();
    }
}
