using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: default catalogs and enum metadata are read in memory only.
public sealed class LunaLaneNewNameEmissionTests
{
    [Fact]
    public void DefaultCatalogs_LunaLane_ExposeOnlyCurrentNames()
    {
        Assert.Equal("codex-luna", WorkerProfileDispatcher.OpenAiLunaSubscriptionProfileName);
        var profiles = WorkerProfileCatalog.Default();
        var providers = WorkerProviderCatalog.Default();
        Assert.Contains(profiles.Profiles, profile => profile.Name == "codex-luna");
        Assert.Contains(providers.Providers, provider => provider.ProfileName == "codex-luna");
        Assert.All(profiles.Profiles, profile => Assert.DoesNotContain("spark", profile.Name, StringComparison.OrdinalIgnoreCase));
        Assert.All(providers.Providers, provider => Assert.DoesNotContain("spark", provider.ProfileName, StringComparison.OrdinalIgnoreCase));
        var names = Enum.GetNames<ProviderKind>();
        Assert.Contains("OpenAICodexLuna", names);
        Assert.All(names, name => Assert.DoesNotContain("spark", name, StringComparison.OrdinalIgnoreCase));

        var identity = new SubscriptionCliProgressiveReviewGlanceRunner(profiles).GetContractIdentity();
        Assert.Equal("OpenAICodexLuna", identity.Provider);
        Assert.Equal("codex-luna", identity.Profile);
    }
}
