namespace Mcg.AgentOrchestrator.Core;

public enum ProviderKind
{
    Unknown,
    OpenAICodexCli,
    AnthropicClaudeCli,
    OpenAICodexSpark,
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
