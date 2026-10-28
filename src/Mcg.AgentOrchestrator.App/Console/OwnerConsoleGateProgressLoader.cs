using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleGateProgressLoader(string logPath, string manifestPath, string durationPath, TimeProvider clock,
    string? executorConfigPath = null, int? coreBudget = null,
    OwnerConsoleJsonlTail? conductTail = null, OwnerConsoleJsonlTail? durationTail = null,
    OwnerConsoleHostRowOwner? owners = null)
{
    private static readonly OwnerConsoleGateFailureNames FailureNames = new();
    private readonly OwnerConsoleJsonlTail _conductTail = conductTail ?? new(logPath);
    private readonly OwnerConsoleJsonlTail _durationTail = durationTail ?? new(durationPath);
    internal OwnerConsoleGateProgress? Load(IReadOnlyDictionary<string, string> titles)
    {
        try
        {
            var now = clock.GetUtcNow();
            var events = _conductTail.Select("gate-progress", OwnerConsoleGateProgress.ParseEvent, 1_048_576)
                .OfType<OwnerConductEvent>().ToArray();
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var engine = manifest.RootElement.GetProperty("engine");
            var maxConcurrentShards = engine.TryGetProperty("maxConcurrentShards", out var shards) &&
                shards.ValueKind == JsonValueKind.Number && shards.TryGetInt32(out var value) && value > 0 ? value : 1;
            var executors = executorConfigPath is null ? Array.Empty<OwnerConsoleGateExecutorSlots>() :
                RemoteLaneExecutorConfiguration.LoadExecutors(executorConfigPath)
                    .Select(executor => new OwnerConsoleGateExecutorSlots(executor.Id, executor.Slots)).ToArray();
            var lanes = engine.GetProperty("infrastructureTestLanes").EnumerateArray()
                .Select(lane => new OwnerConsoleGateManifestLane(lane.GetProperty("name").GetString()!,
                    lane.GetProperty("estimatedSerialSeconds").GetDouble())).ToArray();
            var history = File.Exists(durationPath)
                ? _durationTail.Select("lane-duration", ParseDuration, long.MaxValue).OfType<OwnerConsoleGateDuration>().ToArray()
                : [];
            var capacity = new OwnerConsoleGateCapacity(executors,
                Math.Min(maxConcurrentShards, coreBudget ?? Math.Max(1, Environment.ProcessorCount / 2)), owners);
            var gate = OwnerConsoleGateProgress.Build(now, events, lanes, history,
                id => titles.FirstOrDefault(pair => pair.Key.StartsWith(id, StringComparison.Ordinal)).Value,
                capacity: capacity.LocalBudget);
            return gate with
            {
                Ended = OwnerConsoleGateEnd.Read(events),
                Capacity = capacity,
                HostActivity = OwnerConsoleHostEvidenceRuns.Fold(
                    _conductTail.Select("host-evidence", OwnerConsoleHostEvidenceRuns.Parse, 1_048_576), now),
                Lanes = gate.Lanes.Select(row => row.State == OwnerConsoleGateLaneState.Done && row.Outcome == "failed" && !row.IsAttribution
                    ? row with { FailingTestNames = FailureNames.Read(row) } : row).ToArray()
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or
            InvalidOperationException or FormatException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static OwnerConsoleGateDuration? ParseDuration(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return new(root.GetProperty("laneName").GetString()!, root.GetProperty("durationSeconds").GetDouble(),
                root.GetProperty("recordedAt").GetDateTimeOffset());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return null; }
    }
}
