using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Dashboard.Rendering;
using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ModelReleaseDefaultsTests
{
    [Fact]
    public void AnthropicApiOptionsIncludeSeptemberModelsWithoutFableSubscription()
    {
        var apiModels = DashboardAgentOptionCatalog.ApiModelOptions("Anthropic");
        Assert.Equal("claude-sonnet-4-6", apiModels[0].Value);
        Assert.Contains(apiModels, option => option.Value == "claude-opus-5-5" && option.Label == "Claude Opus 5.5");
        Assert.Contains(apiModels, option => option.Value == AgentCatalog.AnthropicComplexModelName && option.Label == "Claude Sonnet 5.5");
        Assert.Contains(apiModels, option => option.Value == "claude-sonnet-5" && option.Label == "Claude Sonnet 5");
        Assert.Contains(apiModels, option => option.Value == "claude-fable-5-1" && option.Label == "Claude Fable 5.1");
        Assert.DoesNotContain(DashboardAgentOptionCatalog.SubscriptionModelOptions("Anthropic"),
            option => option.Value.Contains("fable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void XaiDashboardOptionsUseGrokModelsAndProfile()
    {
        var expected = new[] { AgentCatalog.XaiSubscriptionModelAlias, "grok-4.7-build-fast", "grok-4.6", "grok-4.5" };
        Assert.Equal(expected, DashboardAgentOptionCatalog.ApiModelOptions("xAI").Select(option => option.Value));
        Assert.Equal(expected, DashboardAgentOptionCatalog.SubscriptionModelOptions("xAI").Select(option => option.Value));
        Assert.Equal(AgentCatalog.XaiSubscriptionModelAlias, DashboardAgentOptionCatalog.DefaultSubscriptionModelAlias("xAI"));
        Assert.Equal(WorkerProfileDispatcher.XaiSubscriptionProfileName, DashboardAgentOptionCatalog.DefaultSubscriptionProfile("xAI"));
        Assert.Equal(new[] { "" }, DashboardAgentOptionCatalog.ApiReasoningOptions("xAI").Select(option => option.Value));
        Assert.Equal(new[] { "" }, DashboardAgentOptionCatalog.SubscriptionReasoningOptions("xAI", AgentCatalog.XaiSubscriptionModelAlias).Select(option => option.Value));
        Assert.Contains("xAI: {", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    }

    [Fact]
    public void FactoriesAndCatalogUseCurrentDefaults()
    {
        var xaiFromFactory = AgentDefinitionFactory.Create(new AgentDefinitionInput("Developer", "xAI", "grok-4.6", ExecutionPolicy: "PreferSubscription"));
        var xaiFromParser = DashboardRequestParser.CreateAgentDefinition(new AgentSubmissionDto("Developer", "xAI", "grok-4.6", null, ExecutionPolicy: "PreferSubscription"));
        Assert.Equal(AgentCatalog.XaiSubscriptionModelAlias, xaiFromFactory.Subscription!.ModelAlias);
        Assert.Equal(AgentCatalog.XaiSubscriptionModelAlias, xaiFromParser.Subscription!.ModelAlias);

        var anthropicFromFactory = AgentDefinitionFactory.Create(new AgentDefinitionInput("Developer", "Anthropic", "claude-haiku-4-5"));
        var anthropicFromParser = DashboardRequestParser.CreateAgentDefinition(new AgentSubmissionDto("Developer", "Anthropic", "claude-haiku-4-5", null));
        Assert.Equal(AgentCatalog.AnthropicComplexModelName, anthropicFromFactory.ComplexModel!.ModelName);
        Assert.Equal(AgentCatalog.AnthropicComplexModelName, anthropicFromParser.ComplexModel!.ModelName);
        Assert.All(AgentCatalog.AnthropicDefault().Agents, agent =>
        {
            Assert.Equal("claude-haiku-4-5", agent.Model.ModelName);
            Assert.Equal(AgentCatalog.AnthropicComplexModelName, agent.ComplexModel!.ModelName);
        });
        Assert.Equal(AgentCatalog.AnthropicComplexModelName, ProviderModelDefaults.Anthropic);
    }

    [Fact]
    public void FableIsSelectableButNeverAFactoryOrCatalogDefault()
    {
        var catalogs = new[] {
            AgentCatalog.Default(), AgentCatalog.AnthropicDefault(),
            AgentCatalog.OllamaDefault(), AgentCatalog.LlamaCppDefault() };
        var agents = catalogs.SelectMany(catalog => catalog.Agents).Concat(
            new[] { "OpenAI", "Anthropic", "xAI", "Ollama", "LlamaCpp" }.SelectMany(provider =>
                new[] {
                    AgentDefinitionFactory.Create(new AgentDefinitionInput("Developer", provider, "base-model")),
                    DashboardRequestParser.CreateAgentDefinition(new AgentSubmissionDto("Developer", provider, "base-model", null))
                }));
        var names = agents.SelectMany(agent => new[] {
            agent.Model.ModelName, agent.ComplexModel?.ModelName, agent.Subscription?.ModelAlias })
            .Concat(new[] { ProviderModelDefaults.OpenAi, ProviderModelDefaults.Anthropic, ProviderModelDefaults.Ollama });
        Assert.DoesNotContain(names, name => name?.Contains("fable", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(DashboardAgentOptionCatalog.ApiModelOptions("Anthropic"), option => option.Value == "claude-fable-5-1");
    }
}
