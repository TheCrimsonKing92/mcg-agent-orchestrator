using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record PostLandingCanaryConfiguration(
    bool Enabled,
    int TimeoutSeconds,
    IReadOnlyList<string> AdditionalEnginePathPrefixes,
    int MaxAttempts = 4,
    IReadOnlyList<int>? RetryBackoffSeconds = null)
{
    internal const int DefaultTimeoutSeconds = 300;
    internal const int DefaultMaxAttempts = 4;
    internal static readonly IReadOnlyList<int> DefaultRetryBackoffSeconds = [120, 300, 900];

    internal static PostLandingCanaryConfiguration Default { get; } =
        new(true, DefaultTimeoutSeconds, [], DefaultMaxAttempts, DefaultRetryBackoffSeconds);

    internal TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    internal TimeSpan RetryDelay(int completedAttemptCount)
    {
        var delays = RetryBackoffSeconds ?? DefaultRetryBackoffSeconds;
        var index = Math.Clamp(completedAttemptCount - 1, 0, delays.Count - 1);
        return TimeSpan.FromSeconds(delays[index]);
    }

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
        var maxAttempts = section.TryGetProperty("MaxAttempts", out var maxAttemptsElement)
            ? maxAttemptsElement.GetInt32()
            : Default.MaxAttempts;
        if (maxAttempts is < 1 or > 20)
        {
            throw new InvalidDataException("PostLandingCanary MaxAttempts must be between 1 and 20.");
        }

        var retryBackoffSeconds = section.TryGetProperty("RetryBackoffSeconds", out var backoffElement)
            ? backoffElement.EnumerateArray().Select(item => item.GetInt32()).ToArray()
            : DefaultRetryBackoffSeconds;
        if (retryBackoffSeconds.Count == 0 ||
            retryBackoffSeconds.Any(seconds => seconds is < 1 or > 3600))
        {
            throw new InvalidDataException(
                "PostLandingCanary RetryBackoffSeconds must contain values between 1 and 3600.");
        }

        return new PostLandingCanaryConfiguration(
            enabled,
            timeoutSeconds,
            additionalPrefixes,
            maxAttempts,
            retryBackoffSeconds);
    }
}
