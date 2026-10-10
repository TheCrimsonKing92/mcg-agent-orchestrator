using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Accepted durations from the bounded history tail, keyed by exact lane and filter.
internal static class RemoteLaneOfferHistoryReader
{
    internal const int LineLimit = 500;

    internal static IReadOnlyDictionary<(string Lane, string Filter), RemoteExecutorLaneSeconds> Read(string path)
    {
        var rows = new List<RemoteExecutorOutcomeRow>();
        var keys = new Dictionary<string, (string Lane, string Filter)>(StringComparer.Ordinal);
        try
        {
            // Retain and parse only the tail, including malformed and non-accepted lines in the limit.
            foreach (var line in SharedJsonlFile.ReadLines(path).TakeLast(LineLimit))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.GetProperty("outcome").GetString() != "accepted") continue;
                    var lane = root.GetProperty("lane").GetString();
                    var filter = root.GetProperty("expected").GetProperty("filter").GetString();
                    if (string.IsNullOrWhiteSpace(lane) || string.IsNullOrWhiteSpace(filter)) continue;
                    var attempt = root.GetProperty("attempt");
                    double? seconds = null;
                    if (attempt.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array &&
                        attempt.TryGetProperty("fetches", out var fetches) && fetches.ValueKind == JsonValueKind.Array)
                    {
                        var starts = steps.EnumerateArray().Select(step => step.GetProperty("started_at").GetDateTimeOffset()).ToArray();
                        var ends = fetches.EnumerateArray().Select(step => step.GetProperty("ended_at").GetDateTimeOffset()).ToArray();
                        if (starts.Length > 0 && ends.Length > 0) seconds = (ends.Max() - starts.Min()).TotalSeconds;
                    }
                    if (seconds is not > 0 && attempt.TryGetProperty("last_status", out var status) &&
                        status.ValueKind == JsonValueKind.Object && status.TryGetProperty("seconds", out var duration) &&
                        duration.TryGetDouble(out var fallback)) seconds = fallback;
                    if (seconds is not { } total || !double.IsFinite(total) || total <= 0) continue;
                    var key = JsonSerializer.Serialize(new[] { lane, filter });
                    keys[key] = (lane, filter);
                    // One aggregate executor combines all hosts; no interval is needed for the report median.
                    rows.Add(new(DateTimeOffset.MinValue, "offer-history", "", key, "accepted", null, total));
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return RemoteExecutorReport.Build([], rows, []).Executors.SelectMany(executor => executor.Lanes)
            .ToDictionary(lane => keys[lane.Lane]);
    }
}
