namespace Mcg.AgentOrchestrator.Core;

public enum ProviderKind
{
    Unknown,
    OpenAICodexCli,
    AnthropicClaudeCli,
    OpenAICodexSpark,
    OpenAIJudge,
    OllamaQwenCodeCli
}

public enum ProviderFailureKind
{
    Unknown,
    RateLimit,
    Sandbox1312,
    Connectivity
}

public sealed record WorkerProviderIdentity(
    ProviderKind Kind,
    bool UsesCodexExitFileBehavior);

public sealed record WorkerCapabilities(
    bool CanSelfCommit,
    bool CanSelfVerify,
    bool SupportsInteractiveSession,
    bool SupportsPlanMode);

public sealed record WorkerQuota(
    int? MaxPromptTokens = null,
    int? MaxOutputTokens = null);

public sealed record WorkerProviderOutcome(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public static class WorkerProviderResolver
{
    private static readonly WorkerProviderIdentity Unknown =
        new(ProviderKind.Unknown, UsesCodexExitFileBehavior: false);

    private static readonly Dictionary<string, WorkerProviderIdentity> KnownProfiles =
        new Dictionary<string, WorkerProviderIdentity>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex-cli"] = new(ProviderKind.OpenAICodexCli, UsesCodexExitFileBehavior: true),
            ["claude-cli"] = new(ProviderKind.AnthropicClaudeCli, UsesCodexExitFileBehavior: false),
            ["codex-spark"] = new(ProviderKind.OpenAICodexSpark, UsesCodexExitFileBehavior: true),
            ["gpt-5.5-judge"] = new(ProviderKind.OpenAIJudge, UsesCodexExitFileBehavior: true),
            ["qwen-code-cli"] = new(ProviderKind.OllamaQwenCodeCli, UsesCodexExitFileBehavior: false)
        };

    public static WorkerProviderIdentity Resolve(string? workerProfileName)
    {
        if (workerProfileName is not null && KnownProfiles.TryGetValue(workerProfileName, out var identity))
        {
            return identity;
        }

        return Unknown;
    }
}
