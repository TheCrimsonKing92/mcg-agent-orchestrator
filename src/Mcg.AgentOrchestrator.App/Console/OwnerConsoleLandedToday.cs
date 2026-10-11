using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleLandedToday
{
    internal static int Count(string? directory, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;
        var zone = clock.LocalTimeZone;
        var today = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).Date;
        string[] files;
        try { files = Directory.GetFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }

        var goals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            try
            {
                var written = new DateTimeOffset(File.GetLastWriteTimeUtc(file));
                if (TimeZoneInfo.ConvertTime(written, zone).Date < today) continue;
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    if (!line.Contains("GoalLanded", StringComparison.Ordinal)) continue;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        if (root.ValueKind != JsonValueKind.Object ||
                            !root.TryGetProperty("eventType", out var type) || type.ValueKind != JsonValueKind.String ||
                            type.GetString() != "GoalLanded" ||
                            !root.TryGetProperty("goalId", out var id) || id.ValueKind != JsonValueKind.String ||
                            string.IsNullOrWhiteSpace(id.GetString()) ||
                            !root.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.String ||
                            !timestamp.TryGetDateTimeOffset(out var landed)) continue;
                        if (TimeZoneInfo.ConvertTime(landed, zone).Date == today) goals.Add(id.GetString()!);
                    }
                    catch (JsonException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return goals.Count;
    }
}
