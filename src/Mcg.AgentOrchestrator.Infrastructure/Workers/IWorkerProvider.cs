using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IWorkerProvider
{
    WorkerProviderIdentity Identity { get; }

    string ProfileName { get; }

    string ProviderName { get; }

    WorkerCapabilities Capabilities { get; }

    WorkerQuota Quota { get; }

    ProviderFailureKind ParseOutcome(WorkerProviderOutcome outcome);
}

public sealed class StaticWorkerProvider : IWorkerProvider
{
    public StaticWorkerProvider(
        WorkerProviderIdentity identity,
        string profileName,
        string providerName,
        WorkerCapabilities capabilities,
        WorkerQuota? quota = null)
    {
        Identity = identity;
        ProfileName = string.IsNullOrWhiteSpace(profileName)
            ? throw new ArgumentException("Profile name cannot be empty.", nameof(profileName))
            : profileName;
        ProviderName = string.IsNullOrWhiteSpace(providerName)
            ? throw new ArgumentException("Provider name cannot be empty.", nameof(providerName))
            : providerName;
        Capabilities = capabilities;
        Quota = quota ?? new WorkerQuota();
    }

    public WorkerProviderIdentity Identity { get; }

    public string ProfileName { get; }

    public string ProviderName { get; }

    public WorkerCapabilities Capabilities { get; }

    public WorkerQuota Quota { get; }

    public ProviderFailureKind ParseOutcome(WorkerProviderOutcome outcome)
    {
        if (outcome.ExitCode == 0)
        {
            return ProviderFailureKind.Unknown;
        }

        var text = string.Join(Environment.NewLine, outcome.StandardOutput, outcome.StandardError);
        if (ContainsRateLimitSignal(text))
        {
            return ProviderFailureKind.RateLimit;
        }

        if (text.Contains("1312", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderFailureKind.Sandbox1312;
        }

        if (ContainsConnectivitySignal(text))
        {
            return ProviderFailureKind.Connectivity;
        }

        return ProviderFailureKind.Unknown;
    }

    private static bool ContainsRateLimitSignal(string text) =>
        text.Contains("usage limit", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("429", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsConnectivitySignal(string text) =>
        text.Contains("websocket", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("could not resolve host", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("temporary failure in name resolution", StringComparison.OrdinalIgnoreCase);
}

public sealed class WorkerProviderCatalog
{
    private readonly IReadOnlyList<IWorkerProvider> _providers;

    public WorkerProviderCatalog(IReadOnlyList<IWorkerProvider> providers)
    {
        _providers = providers.Count == 0
            ? throw new ArgumentException("At least one worker provider is required.", nameof(providers))
            : providers;
    }

    public IReadOnlyList<IWorkerProvider> Providers => _providers;

    public IWorkerProvider Resolve(ProviderKind kind) =>
        _providers.FirstOrDefault(provider => provider.Identity.Kind == kind)
        ?? DefaultUnknownProvider.Instance;

    public bool TryResolve(ProviderKind kind, out IWorkerProvider provider)
    {
        provider = _providers.FirstOrDefault(candidate => candidate.Identity.Kind == kind)
            ?? DefaultUnknownProvider.Instance;
        return provider.Identity.Kind != ProviderKind.Unknown;
    }

    public IWorkerProvider ResolveProfile(string? profileName)
    {
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            var match = _providers.FirstOrDefault(provider =>
                provider.ProfileName.Equals(profileName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return DefaultUnknownProvider.Instance;
    }

    public bool TryResolveProfile(string? profileName, out IWorkerProvider provider)
    {
        provider = ResolveProfile(profileName);
        return provider.Identity.Kind != ProviderKind.Unknown;
    }

    public IWorkerProvider ResolveModelProvider(string providerName) =>
        TryResolveModelProvider(providerName, out var provider)
            ? provider
            : throw new InvalidOperationException($"Provider '{providerName}' does not have a default subscription worker profile.");

    public bool TryResolveModelProvider(string providerName, out IWorkerProvider provider)
    {
        provider = _providers.FirstOrDefault(candidate =>
            candidate.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) &&
            candidate.Identity.Kind is ProviderKind.OpenAICodexCli or ProviderKind.AnthropicClaudeCli or ProviderKind.OllamaQwenCodeCli)
            ?? DefaultUnknownProvider.Instance;
        return provider.Identity.Kind != ProviderKind.Unknown;
    }

    public static WorkerProviderCatalog Default() => new(
    [
        new StaticWorkerProvider(
            new WorkerProviderIdentity(ProviderKind.OpenAICodexCli, UsesCodexExitFileBehavior: true),
            WorkerProfileDispatcher.OpenAiSubscriptionProfileName,
            "OpenAI",
            new WorkerCapabilities(
                CanSelfCommit: false,
                CanSelfVerify: true,
                SupportsInteractiveSession: true,
                SupportsPlanMode: true)),
        new StaticWorkerProvider(
            new WorkerProviderIdentity(ProviderKind.AnthropicClaudeCli, UsesCodexExitFileBehavior: false),
            WorkerProfileDispatcher.AnthropicSubscriptionProfileName,
            "Anthropic",
            new WorkerCapabilities(
                CanSelfCommit: true,
                CanSelfVerify: true,
                SupportsInteractiveSession: true,
                SupportsPlanMode: true)),
        new StaticWorkerProvider(
            new WorkerProviderIdentity(ProviderKind.OpenAICodexSpark, UsesCodexExitFileBehavior: true),
            "codex-spark",
            "OpenAI",
            new WorkerCapabilities(
                CanSelfCommit: false,
                CanSelfVerify: true,
                SupportsInteractiveSession: false,
                SupportsPlanMode: true)),
        new StaticWorkerProvider(
            new WorkerProviderIdentity(ProviderKind.OpenAICodexOssCli, UsesCodexExitFileBehavior: true),
            "codex-oss-cli",
            "Ollama",
            new WorkerCapabilities(
                CanSelfCommit: false,
                CanSelfVerify: true,
                SupportsInteractiveSession: false,
                SupportsPlanMode: true)),
        new StaticWorkerProvider(
            new WorkerProviderIdentity(ProviderKind.OllamaQwenCodeCli, UsesCodexExitFileBehavior: false),
            WorkerProfileDispatcher.OllamaSubscriptionProfileName,
            "Ollama",
            new WorkerCapabilities(
                CanSelfCommit: true,
                CanSelfVerify: false,
                SupportsInteractiveSession: false,
                SupportsPlanMode: false))
    ]);

    private sealed class DefaultUnknownProvider : IWorkerProvider
    {
        public static readonly DefaultUnknownProvider Instance = new();

        public WorkerProviderIdentity Identity { get; } =
            new(ProviderKind.Unknown, UsesCodexExitFileBehavior: false);

        public string ProfileName => "unknown";

        public string ProviderName => "unknown";

        public WorkerCapabilities Capabilities { get; } =
            new(CanSelfCommit: true, CanSelfVerify: false, SupportsInteractiveSession: false, SupportsPlanMode: false);

        public WorkerQuota Quota { get; } = new();

        public ProviderFailureKind ParseOutcome(WorkerProviderOutcome outcome) => ProviderFailureKind.Unknown;
    }
}
