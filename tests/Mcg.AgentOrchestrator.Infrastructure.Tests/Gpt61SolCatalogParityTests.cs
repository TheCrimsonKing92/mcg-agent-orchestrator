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

}
