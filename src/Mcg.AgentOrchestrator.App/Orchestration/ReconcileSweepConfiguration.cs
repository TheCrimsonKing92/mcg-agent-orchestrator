using System.Globalization;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ReconcileSweepOptions(
    IReadOnlySet<string> AutoRemediationAllowlist,
    int MaximumAttempts,
    TimeSpan HeartbeatInterval)
{
    public static ReconcileSweepOptions Default { get; } = new(
        new HashSet<string>(["completed-branch-unmerged"], StringComparer.Ordinal),
        3,
        TimeSpan.FromMinutes(5));

    public ReconcileSweepOptions Validate()
    {
        if (MaximumAttempts < 1)
        {
            throw new InvalidOperationException("ReconcileSweep:MaximumAttempts must be at least 1.");
        }

        if (HeartbeatInterval <= TimeSpan.Zero || HeartbeatInterval > TimeSpan.FromMinutes(10))
        {
            throw new InvalidOperationException("ReconcileSweep:HeartbeatInterval must be greater than zero and no more than 10 minutes.");
        }

        return this;
    }
}

internal static class ReconcileSweepConfiguration
{
    public static ReconcileSweepOptions Load(string basePath)
    {
        var settingsPath = Path.Combine(basePath, "appsettings.json");
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(settingsPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.TryGetProperty("ReconcileSweep", out var section))
            {
                foreach (var property in section.EnumerateObject().Where(property => property.Value.ValueKind != JsonValueKind.Array))
                    values[property.Name] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : property.Value.ToString();
            }
        }
        OverlayEnvironment(values, "MaximumAttempts");
        OverlayEnvironment(values, "HeartbeatInterval");
        var options = Read(values);
        if (TryReadConfiguredAllowlist(settingsPath, out var configuredAllowlist))
        {
            options = options with { AutoRemediationAllowlist = configuredAllowlist };
        }
        return options.Validate();
    }

    internal static ReconcileSweepOptions Read(
        IReadOnlyDictionary<string, string?> values,
        bool allowlistConfigured = false,
        IReadOnlySet<string>? allowlist = null)
    {
        var defaults = ReconcileSweepOptions.Default;
        var maximumAttempts = ReadInt(values, "MaximumAttempts", defaults.MaximumAttempts);
        var heartbeatInterval = ReadTimeSpan(values, "HeartbeatInterval", defaults.HeartbeatInterval);
        return new ReconcileSweepOptions(
            allowlistConfigured ? allowlist ?? new HashSet<string>(StringComparer.Ordinal) : defaults.AutoRemediationAllowlist,
            maximumAttempts,
            heartbeatInterval).Validate();
    }

    private static void OverlayEnvironment(IDictionary<string, string?> values, string key)
    {
        var value = Environment.GetEnvironmentVariable($"ReconcileSweep__{key}");
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
            : throw new InvalidOperationException($"ReconcileSweep:{key} must be an integer.");
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
            : throw new InvalidOperationException($"ReconcileSweep:{key} must be a TimeSpan.");
    }

    private static bool TryReadConfiguredAllowlist(string settingsPath, out IReadOnlySet<string> allowlist)
    {
        allowlist = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(settingsPath))
        {
            return false;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        if (!document.RootElement.TryGetProperty("ReconcileSweep", out var reconcileSection) ||
            !reconcileSection.TryGetProperty("AutoRemediationAllowlist", out var allowlistElement) ||
            allowlistElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        allowlist = allowlistElement.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            .Select(item => item.GetString()!.Trim())
            .ToHashSet(StringComparer.Ordinal);
        return true;
    }
}
