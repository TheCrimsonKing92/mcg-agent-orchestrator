using Mcg.AgentOrchestrator.Core;

public sealed class WorkerProviderResolverTests
{
    [Xunit.Theory(DisplayName = "WorkerProviderResolver_maps_known_and_unknown_profiles")]
    [Xunit.InlineData("codex-cli", ProviderKind.OpenAICodexCli, true)]
    [Xunit.InlineData("claude-cli", ProviderKind.AnthropicClaudeCli, false)]
    [Xunit.InlineData("custom-agent", ProviderKind.Unknown, false)]
    public void WorkerProviderResolverMapsKnownAndUnknownProfiles(
        string workerProfileName,
        ProviderKind expectedKind,
        bool expectedUsesCodexExitFileBehavior)
    {
        var identity = WorkerProviderResolver.Resolve(workerProfileName);

        Assert.Equal(expectedKind, identity.Kind);
        Assert.Equal(expectedUsesCodexExitFileBehavior, identity.UsesCodexExitFileBehavior);
    }
}
