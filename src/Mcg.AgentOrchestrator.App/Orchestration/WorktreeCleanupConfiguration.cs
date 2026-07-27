using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class WorktreeCleanupConfiguration
{
    public static GoalWorktreeCleanupOptions Load(string basePath)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
        return Read(configuration.GetSection("WorktreeCleanup"));
    }

    internal static GoalWorktreeCleanupOptions Read(IConfiguration section)
    {
        var defaults = GoalWorktreeCleanupOptions.Default;
        return new GoalWorktreeCleanupOptions(
            ReadTimeSpan(section, "SweepInterval", defaults.SweepInterval),
            ReadInt(section, "EscalationThreshold", defaults.EscalationThreshold),
            ReadTimeSpan(section, "EscalatedRetryInterval", defaults.EscalatedRetryInterval))
            .Validate();
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
            : throw new InvalidOperationException($"WorktreeCleanup:{key} must be an integer.");
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
            : throw new InvalidOperationException($"WorktreeCleanup:{key} must be a TimeSpan.");
    }
}
