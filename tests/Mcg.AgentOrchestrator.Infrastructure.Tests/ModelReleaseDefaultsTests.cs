using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ModelReleaseDefaultsTests
{
    [Fact]
    public void FactoriesAndCatalogUseCurrentDefaults()
    {
        var xaiFromFactory = AgentDefinitionFactory.Create(new AgentDefinitionInput("Developer", "xAI", "grok-4.6", ExecutionPolicy: "PreferSubscription"));
        var xaiFromParser = AgentDefinitionFactory.Create(new AgentDefinitionInput("Developer", "xAI", "grok-4.6", null, ExecutionPolicy: "PreferSubscription"));
        Assert.Equal(AgentCatalog.XaiSubscriptionModelAlias, xaiFromFactory.Subscription!.ModelAlias);
        Assert.Equal(AgentCatalog.XaiSubscriptionModelAlias, xaiFromParser.Subscription!.ModelAlias);

        var anthropicFromFactory = AgentDefinitionFactory.Create(new AgentDefinitionInput("Developer", "Anthropic", "claude-haiku-4-5"));
        var anthropicFromParser = AgentDefinitionFactory.Create(new AgentDefinitionInput("Developer", "Anthropic", "claude-haiku-4-5", null));
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
                    AgentDefinitionFactory.Create(new AgentDefinitionInput("Developer", provider, "base-model", null))
                }));
        var names = agents.SelectMany(agent => new[] {
            agent.Model.ModelName, agent.ComplexModel?.ModelName, agent.Subscription?.ModelAlias })
            .Concat(new[] { ProviderModelDefaults.OpenAi, ProviderModelDefaults.Anthropic, ProviderModelDefaults.Ollama });
        Assert.DoesNotContain(names, name => name?.Contains("fable", StringComparison.OrdinalIgnoreCase) == true);
    }
}
