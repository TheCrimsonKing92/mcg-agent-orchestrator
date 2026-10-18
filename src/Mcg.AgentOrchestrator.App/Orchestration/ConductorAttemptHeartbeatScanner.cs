using System.Globalization;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorAttemptHeartbeatScanner
{
    internal static DateTimeOffset? LatestProgress(string root, DateTimeOffset now, TimeSpan window) =>
        ReadLive(root, now, window).Select(at => (DateTimeOffset?)at).Max();

    internal static int CountLive(string root, DateTimeOffset now, TimeSpan window) =>
        ReadLive(root, now, window).Count;

    private static List<DateTimeOffset> ReadLive(string root, DateTimeOffset now, TimeSpan window)
    {
        var progress = new List<DateTimeOffset>();
        if (!Directory.Exists(root)) return progress;
        var earliestWrite = (now - window).UtcDateTime;
        try
        {
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.heartbeat.json",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                try
                {
                    if (file.Name.EndsWith(".gate-heartbeat.json", StringComparison.OrdinalIgnoreCase) ||
                        file.LastWriteTimeUtc < earliestWrite) continue;
                    using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var document = JsonDocument.Parse(stream);
                    if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                    string? state = null;
                    DateTimeOffset? lastProgress = null;
                    // Attempt writers and GateHeartbeatSnapshot use different shapes; these fields are shared.
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind != JsonValueKind.String) continue;
                        if (property.Name.Equals("state", StringComparison.OrdinalIgnoreCase))
                            state = property.Value.GetString();
                        else if (property.Name.Equals("lastProgressAt", StringComparison.OrdinalIgnoreCase) &&
                            DateTimeOffset.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                            lastProgress = at;
                    }
                    if (string.Equals(state, "running", StringComparison.OrdinalIgnoreCase) &&
                        lastProgress is { } observed && observed <= now) progress.Add(observed);
                }
                catch (IOException) { } // A writer may replace or lock a heartbeat during the scan.
                catch (UnauthorizedAccessException) { }
                catch (JsonException) { }
            }
        }
        catch (IOException) { } // Preserve observations if a folder disappears during enumeration.
        catch (UnauthorizedAccessException) { }
        return progress;
    }
}
