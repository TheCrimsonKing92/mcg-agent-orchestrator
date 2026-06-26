namespace Mcg.AgentOrchestrator.Core;

public enum ProviderKind
{
    Unknown,
    OpenAICodexCli,
    AnthropicClaudeCli
}

public sealed record WorkerProviderIdentity(
    ProviderKind Kind,
    bool UsesCodexExitFileBehavior);

public static class WorkerProviderResolver
{
    private static readonly WorkerProviderIdentity Unknown =
        new(ProviderKind.Unknown, UsesCodexExitFileBehavior: false);

    private static readonly Dictionary<string, WorkerProviderIdentity> KnownProfiles =
        new Dictionary<string, WorkerProviderIdentity>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex-cli"] = new(ProviderKind.OpenAICodexCli, UsesCodexExitFileBehavior: true),
            ["claude-cli"] = new(ProviderKind.AnthropicClaudeCli, UsesCodexExitFileBehavior: false)
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
