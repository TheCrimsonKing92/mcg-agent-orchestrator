using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record PostLandingCanaryConfiguration(
    bool Enabled,
    int TimeoutSeconds,
    IReadOnlyList<string> AdditionalEnginePathPrefixes)
{
    internal const int DefaultTimeoutSeconds = 300;

    internal static PostLandingCanaryConfiguration Default { get; } =
        new(true, DefaultTimeoutSeconds, []);

    internal TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    internal static PostLandingCanaryConfiguration Load(string basePath)
    {
        var path = Path.Combine(basePath, "appsettings.json");
        if (!File.Exists(path))
        {
            return Default;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("PostLandingCanary", out var section))
        {
            return Default;
        }

        var enabled = section.TryGetProperty("Enabled", out var enabledElement)
            ? enabledElement.GetBoolean()
            : Default.Enabled;
        var timeoutSeconds = section.TryGetProperty("TimeoutSeconds", out var timeoutElement)
            ? timeoutElement.GetInt32()
            : Default.TimeoutSeconds;
        if (timeoutSeconds is < 1 or > DefaultTimeoutSeconds)
        {
            throw new InvalidDataException(
                $"PostLandingCanary TimeoutSeconds must be between 1 and the hard ceiling of {DefaultTimeoutSeconds}.");
        }

        var additionalPrefixes = section.TryGetProperty("AdditionalEnginePathPrefixes", out var prefixesElement)
            ? prefixesElement.EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray()
            : [];
        return new PostLandingCanaryConfiguration(enabled, timeoutSeconds, additionalPrefixes);
    }
}
