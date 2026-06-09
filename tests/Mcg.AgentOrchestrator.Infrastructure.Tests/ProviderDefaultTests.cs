using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ProviderDefaultTests
{
    [Xunit.Fact(DisplayName = "Provider_defaults_keep_openai_fallback_aligned_with_base_agent_model")]
    public void ProviderDefaultsKeepOpenAiFallbackAlignedWithBaseAgentModel()
    {
        var developer = AgentCatalog.Default().GetRequired(AgentRole.Developer);

        Assert.Equal(developer.Model.ModelName, ProviderModelDefaults.OpenAi);
        Assert.False(developer.ComplexModel!.ModelName.Equals(ProviderModelDefaults.OpenAi, StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Provider_smoke_uses_cost_aware_openai_default_when_model_env_is_unset")]
    public void ProviderSmokeUsesCostAwareOpenAiDefaultWhenModelEnvIsUnset()
    {
        var previousKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var previousModel = Environment.GetEnvironmentVariable("OPENAI_MODEL");

        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
            Environment.SetEnvironmentVariable("OPENAI_MODEL", null);

            var created = ProviderSmokeRunner.TryCreateLiveProvider("openai", out _, out var modelName, out _);

            Assert.True(created);
            Assert.Equal(ProviderModelDefaults.OpenAi, modelName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousKey);
            Environment.SetEnvironmentVariable("OPENAI_MODEL", previousModel);
        }
    }
}
