using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleNotificationSettings
{
    internal TimeSpan AttentionThreshold { get; init; } = TimeSpan.FromMinutes(30);
    internal TimeSpan StaleTickThreshold { get; init; } = TimeSpan.FromMinutes(15);
    internal TimeSpan CalmCoalesce { get; init; } = TimeSpan.FromSeconds(60);
    internal string? CalmSoundFile { get; init; }
    internal string? AttentionSoundFile { get; init; }
    internal string? AlarmSoundFile { get; init; }

    internal static OwnerConsoleNotificationSettings Load(string path)
    {
        var defaults = new OwnerConsoleNotificationSettings();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return defaults;
            var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in document.RootElement.EnumerateObject()) fields[field.Name] = field.Value;
            return defaults with
            {
                AttentionThreshold = Duration("attentionThresholdMinutes", 60, defaults.AttentionThreshold),
                StaleTickThreshold = Duration("staleTickThresholdMinutes", 60, defaults.StaleTickThreshold),
                CalmCoalesce = Duration("calmCoalesceSeconds", 1, defaults.CalmCoalesce),
                CalmSoundFile = Sound("calmSoundFile"),
                AttentionSoundFile = Sound("attentionSoundFile"),
                AlarmSoundFile = Sound("alarmSoundFile")
            };

            TimeSpan Duration(string key, double secondsPerUnit, TimeSpan fallback)
            {
                if (!fields.TryGetValue(key, out var field) || field.ValueKind != JsonValueKind.Number ||
                    !field.TryGetDouble(out var value) || !double.IsFinite(value) || value <= 0) return fallback;
                try { return TimeSpan.FromSeconds(value * secondsPerUnit) is { Ticks: > 0 } duration ? duration : fallback; }
                catch { return fallback; }
            }
            string? Sound(string key) => fields.TryGetValue(key, out var field) &&
                field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString())
                    ? field.GetString() : null;
        }
        catch { return defaults; }
    }
}
