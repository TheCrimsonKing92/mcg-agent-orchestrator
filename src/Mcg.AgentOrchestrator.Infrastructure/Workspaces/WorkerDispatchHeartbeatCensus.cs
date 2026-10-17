using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerDispatchHeartbeatCensus
{
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(2);

    internal static GateLoadSample Count(
        string directory, DateTimeOffset now, Func<string, string>? readAllText = null)
    {
        try
        {
            if (!Directory.Exists(directory)) return GateLoadSample.Unavailable("log-directory-missing");
            var count = 0;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.heartbeat.json"))
            {
                try
                {
                    // Check metadata first: historical dispatches must never be opened.
                    if (now.UtcDateTime - file.LastWriteTimeUtc > Freshness) continue;
                    using var document = JsonDocument.Parse((readAllText ?? File.ReadAllText)(file.FullName));
                    var row = document.RootElement;
                    if (!row.TryGetProperty("kind", out var kind) ||
                        !string.Equals(kind.GetString(), "worker", StringComparison.OrdinalIgnoreCase) ||
                        !row.TryGetProperty("state", out var state)) continue;
                    var stateText = state.GetString();
                    if (!string.Equals(stateText, "running", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(stateText, "launched", StringComparison.OrdinalIgnoreCase)) continue;
                    if (row.TryGetProperty("lastObservedAt", out var observed) &&
                        observed.TryGetDateTimeOffset(out var timestamp) && now - timestamp <= Freshness)
                        count++;
                }
                catch (Exception) { /* A dispatch can replace or remove its heartbeat while we read it. */ }
            }
            return GateLoadSample.Available(count);
        }
        catch (Exception exception)
        {
            return GateLoadSample.Unavailable($"probe-error:{exception.GetType().Name}");
        }
    }
}
