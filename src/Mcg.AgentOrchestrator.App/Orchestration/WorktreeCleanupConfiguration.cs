using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class WorktreeCleanupConfiguration
{
    public static GoalWorktreeCleanupOptions Load(string basePath)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(basePath, "appsettings.json");
        if (File.Exists(path))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("WorktreeCleanup", out var section))
            {
                foreach (var property in section.EnumerateObject())
                    values[property.Name] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : property.Value.ToString();
            }
        }

        OverlayEnvironment(values, "SweepInterval");
        OverlayEnvironment(values, "EscalationThreshold");
        OverlayEnvironment(values, "EscalatedRetryInterval");
        return Read(values);
    }

    internal static GoalWorktreeCleanupOptions Read(IReadOnlyDictionary<string, string?> values)
    {
        var defaults = GoalWorktreeCleanupOptions.Default;
        return new GoalWorktreeCleanupOptions(
            ReadTimeSpan(values, "SweepInterval", defaults.SweepInterval),
            ReadInt(values, "EscalationThreshold", defaults.EscalationThreshold),
            ReadTimeSpan(values, "EscalatedRetryInterval", defaults.EscalatedRetryInterval))
            .Validate();
    }

    private static void OverlayEnvironment(IDictionary<string, string?> values, string key)
    {
        var value = Environment.GetEnvironmentVariable($"WorktreeCleanup__{key}");
        if (value is not null)
            values[key] = value;
    }

    private static int ReadInt(IReadOnlyDictionary<string, string?> values, string key, int fallback)
    {
        values.TryGetValue(key, out var value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"WorktreeCleanup:{key} must be an integer.");
    }

    private static TimeSpan ReadTimeSpan(IReadOnlyDictionary<string, string?> values, string key, TimeSpan fallback)
    {
        values.TryGetValue(key, out var value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"WorktreeCleanup:{key} must be a TimeSpan.");
    }
}
