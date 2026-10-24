using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleGateProgressLoader(string logPath, string manifestPath, string durationPath, TimeProvider clock,
    string? executorConfigPath = null, int? coreBudget = null)
{
    private static readonly OwnerConsoleGateFailureNames FailureNames = new();
    internal OwnerConsoleGateProgress? Load(IReadOnlyDictionary<string, string> titles)
    {
        try
        {
            var tail = ReadTail().ToArray();
            var now = clock.GetUtcNow();
            var events = tail.Select(OwnerConsoleGateProgress.ParseEvent).OfType<OwnerConductEvent>().ToArray();
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
            var history = new List<OwnerConsoleGateDuration>();
            if (File.Exists(durationPath))
                foreach (var line in SharedJsonlFile.ReadAllLines(durationPath))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        history.Add(new(root.GetProperty("laneName").GetString()!, root.GetProperty("durationSeconds").GetDouble(),
                            root.GetProperty("recordedAt").GetDateTimeOffset()));
                    }
                    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
                }
            var gate = OwnerConsoleGateProgress.Build(now, events, lanes, history,
                id => titles.FirstOrDefault(pair => pair.Key.StartsWith(id, StringComparison.Ordinal)).Value);
            return gate with
            {
                Ended = OwnerConsoleGateEnd.Read(events),
                Capacity = new(executors, Math.Min(maxConcurrentShards, coreBudget ?? Math.Max(1, Environment.ProcessorCount / 2))),
                HostActivity = OwnerConsoleHostEvidenceRuns.Read(tail, now),
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

    private IEnumerable<string> ReadTail()
    {
        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var offset = Math.Max(0, length - 1_048_576);
        stream.Position = offset;
        var buffer = new byte[(int)(length - offset)];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0) break;
            read += count;
        }
        using var data = new MemoryStream(buffer, 0, read);
        using var reader = new StreamReader(data);
        if (offset > 0) reader.ReadLine();
        while (reader.ReadLine() is { } line) yield return line;
    }
}
