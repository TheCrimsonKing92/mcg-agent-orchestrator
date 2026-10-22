using System.Globalization;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorGateLaneProgressScanner
{
    internal static DateTimeOffset? LatestProgress(string root, DateTimeOffset now, TimeSpan window)
    {
        DateTimeOffset? latest = null;
        if (!Directory.Exists(root)) return null;
        var earliestWrite = (now - window).UtcDateTime;
        try
        {
            // FileInfo write times come from enumeration; only recent files need their bodies opened.
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.gate-heartbeat.json",
                new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = ConductorAttemptHeartbeatScanner.MaxHeartbeatRecursionDepth, IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                try
                {
                    if (file.LastWriteTimeUtc < earliestWrite || file.LastWriteTimeUtc > now.UtcDateTime) continue;
                    using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var document = JsonDocument.Parse(stream);
                    if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                    foreach (var property in document.RootElement.EnumerateObject())
                        if (property.Name.Equals("lastProgressAt", StringComparison.OrdinalIgnoreCase) &&
                            property.Value.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) &&
                            at <= now && (latest is null || at > latest)) latest = at;
                }
                catch (IOException) { } // A heartbeat can be replaced while we read it.
                catch (UnauthorizedAccessException) { }
                catch (JsonException) { }
            }
        }
        catch (IOException) { } // A folder can disappear during enumeration; preserve progress already read.
        catch (UnauthorizedAccessException) { }
        return latest;
    }
}
