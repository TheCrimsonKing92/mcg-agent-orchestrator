using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

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
        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
        var options = Read(configuration.GetSection("ReconcileSweep"));
        if (TryReadConfiguredAllowlist(settingsPath, out var configuredAllowlist))
        {
            options = options with { AutoRemediationAllowlist = configuredAllowlist };
        }
        return options.Validate();
    }

    internal static ReconcileSweepOptions Read(IConfiguration section)
    {
        var defaults = ReconcileSweepOptions.Default;
        var allowlistSection = section.GetSection("AutoRemediationAllowlist");
        var configuredKinds = allowlistSection.GetChildren()
            .Select(child => child.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        var hasConfiguredAllowlist = allowlistSection.Exists();
        var maximumAttempts = ReadInt(section, "MaximumAttempts", defaults.MaximumAttempts);
        var heartbeatInterval = ReadTimeSpan(section, "HeartbeatInterval", defaults.HeartbeatInterval);
        return new ReconcileSweepOptions(
            hasConfiguredAllowlist ? configuredKinds : defaults.AutoRemediationAllowlist,
            maximumAttempts,
            heartbeatInterval).Validate();
    }

    private static int ReadInt(IConfiguration section, string key, int fallback)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"ReconcileSweep:{key} must be an integer.");
    }

    private static TimeSpan ReadTimeSpan(IConfiguration section, string key, TimeSpan fallback)
    {
        var value = section[key];
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
