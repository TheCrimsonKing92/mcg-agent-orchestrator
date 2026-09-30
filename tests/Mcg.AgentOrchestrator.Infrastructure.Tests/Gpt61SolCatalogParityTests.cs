using Mcg.AgentOrchestrator.App.Dashboard.Rendering;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class Gpt61SolCatalogParityTests
{
    [Fact]
    public void Gpt61SolUsesTheGpt6SolReasoningDefault()
    {
        Assert.Equal("medium", AgentCatalog.DefaultSubscriptionReasoningEffort("OpenAI", AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias));
        Assert.Equal(
            AgentCatalog.DefaultSubscriptionReasoningEffort("OpenAI", AgentCatalog.OpenAiGpt6SolSubscriptionModelAlias),
            AgentCatalog.DefaultSubscriptionReasoningEffort("OpenAI", AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias));
    }

    [Theory]
    [InlineData(AgentRole.Developer, "OpenAI", true)]
    [InlineData(AgentRole.Tester, "OpenAI", true)]
    [InlineData(AgentRole.Reviewer, "OpenAI", true)]
    [InlineData(AgentRole.Planner, "OpenAI", true)]
    [InlineData(AgentRole.Researcher, "OpenAI", true)]
    [InlineData(AgentRole.Ideation, "OpenAI", false)]
    [InlineData(AgentRole.Developer, "Anthropic", false)]
    public void Gpt61SolMatchesGpt6SolTypedContextRules(AgentRole role, string provider, bool expected)
    {
        Assert.Equal(expected, WorkerContextHelpers.UsesTypedContextPackage(role, provider, AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias));
        Assert.Equal(
            WorkerContextHelpers.UsesTypedContextPackage(role, provider, AgentCatalog.OpenAiGpt6SolSubscriptionModelAlias),
            WorkerContextHelpers.UsesTypedContextPackage(role, provider, AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias));
    }

    [Theory]
    [InlineData(AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias, AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias)]
    [InlineData(AgentCatalog.StaleOpenAiCodexSubscriptionModelAlias, AgentCatalog.OpenAiGpt6SolSubscriptionModelAlias)]
    public void LoadKeepsNewAliasAndRepairsOnlyStaleCodexAlias(string storedAlias, string expectedAlias)
    {
        var path = Path.Combine(CreateTempDirectory(), "agents.json");
        AgentCatalogStore.Save(path, new AgentCatalog([
            new AgentDefinition(
                new AgentId("openai-developer"), "OpenAI developer", AgentRole.Developer,
                new ModelProfile("OpenAI", "gpt-custom", ModelCapability.Text | ModelCapability.Code, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
                Subscription: new SubscriptionLaunchProfile("codex-cli", storedAlias))
        ]));

        var subscription = AgentCatalogStore.Load(path).GetRequired(AgentRole.Developer).Subscription!;
        Assert.Equal(expectedAlias, subscription.ModelAlias);
        Assert.Equal("medium", subscription.ReasoningEffort);
    }

    [Fact]
    public void DashboardOffersGpt61SolWithGpt6SolReasoningAndApiModels()
    {
        var options = DashboardAgentOptionCatalog.ForProvider("OpenAI");
        Assert.Contains(options.SubscriptionModels, option =>
            option.Value == AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias && option.Label == "GPT-6.1 Sol");
        Assert.Equal(
            DashboardAgentOptionCatalog.SubscriptionReasoningOptions("OpenAI", AgentCatalog.OpenAiGpt6SolSubscriptionModelAlias).Select(option => option.Value),
            DashboardAgentOptionCatalog.SubscriptionReasoningOptions("OpenAI", AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias).Select(option => option.Value));
        Assert.Contains(options.SubscriptionReasoningByModel[AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias], option => option.Value == "ultra");
        Assert.Equal("medium", options.DefaultSubscriptionReasoningByModel[AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias]);
        Assert.Equal("gpt-5.4-mini", options.ApiModels[0].Value);
        foreach (var alias in new[] {
            AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias,
            AgentCatalog.OpenAiGpt6SolSubscriptionModelAlias,
            AgentCatalog.OpenAiGpt6LunaSubscriptionModelAlias,
            AgentCatalog.OpenAiGpt6AstraSubscriptionModelAlias })
        {
            Assert.Contains(options.ApiModels, option => option.Value == alias);
        }
    }
}
