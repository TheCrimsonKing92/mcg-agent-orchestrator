using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

public sealed class AgentCatalogTests
{
    [Xunit.Fact(DisplayName = "AgentCatalog_default_contains_sdlc_roles")]
    public void AgentCatalogDefaultContainsSdlcRoles()
{
    var catalog = AgentCatalog.Default();

    Assert.Equal(5, catalog.Agents.Count);

    foreach (var role in Enum.GetValues<AgentRole>())
    {
        var agent = catalog.GetRequired(role);
        Assert.Equal("OpenAI", agent.Model.ProviderName);
        Assert.Equal("gpt-5.4-mini", agent.Model.ModelName);
        Assert.Equal(AgentCatalog.RoutineApiMaxOutputTokens, agent.Model.MaxOutputTokens);
        Assert.Equal(AgentExecutionPolicy.PreferSubscription, agent.ExecutionPolicy);
        Assert.Equal("codex-cli", agent.Subscription!.WorkerProfileName);
        Assert.Equal(AgentCatalog.OpenAiSubscriptionModelAlias, agent.Subscription.ModelAlias);
        Assert.Equal("OpenAI", agent.ComplexModel!.ProviderName);
        Assert.Equal("gpt-5.5", agent.ComplexModel.ModelName);
        Assert.Equal(AgentCatalog.ComplexApiMaxOutputTokens, agent.ComplexModel.MaxOutputTokens);
    }

    foreach (var role in Enum.GetValues<AgentRole>())
    {
        var agent = catalog.GetRequired(role);
        Assert.Equal(AgentCatalog.RoutineReasoningEffort, agent.Model.ReasoningEffort);
        Assert.Equal(AgentCatalog.RoutineSubscriptionReasoningEffort, agent.Subscription!.ReasoningEffort);
        Assert.Equal(AgentCatalog.ComplexReasoningEffort, agent.ComplexModel!.ReasoningEffort);
    }
}
    [Xunit.Fact(DisplayName = "AgentCatalog_upsert_replaces_role")]
    public void AgentCatalogUpsertReplacesRole()
{
    var replacement = new AgentDefinition(
        new AgentId("anthropic-developer"),
        "Anthropic developer",
        AgentRole.Developer,
        new ModelProfile("Anthropic", "claude-test", ModelCapability.Text | ModelCapability.Code, SubscriptionMode.ApiKey));

    var catalog = AgentCatalog.Default().UpsertRole(replacement);

    Assert.Equal(5, catalog.Agents.Count);
    Assert.Equal("Anthropic developer", catalog.GetRequired(AgentRole.Developer).Name);
    Assert.Equal("claude-test", catalog.GetRequired(AgentRole.Developer).Model.ModelName);
}
    [Xunit.Fact(DisplayName = "AgentCatalogStore_roundtrips_agents")]
    public void AgentCatalogStoreRoundtripsAgents()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "agents.json");
    var catalog = AgentCatalog.Default().UpsertRole(new AgentDefinition(
        new AgentId("openai-reviewer"),
        "OpenAI reviewer",
        AgentRole.Reviewer,
        new ModelProfile("OpenAI", "gpt-review", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey)));

    AgentCatalogStore.Save(path, catalog);
    var restored = AgentCatalogStore.Load(path);

    Assert.Equal("OpenAI reviewer", restored.GetRequired(AgentRole.Reviewer).Name);
    Assert.Equal("gpt-review", restored.GetRequired(AgentRole.Reviewer).Model.ModelName);
}
    [Xunit.Fact(DisplayName = "AgentCatalogStore_load_adds_missing_paid_provider_cost_defaults_without_overriding_explicit_values")]
    public void AgentCatalogStoreLoadAddsMissingPaidProviderCostDefaultsWithoutOverridingExplicitValues()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "agents.json");
    var catalog = new AgentCatalog(
    [
        new AgentDefinition(
            new AgentId("openai-developer"),
            "OpenAI developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-custom", ModelCapability.Text | ModelCapability.Code, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli"),
            ComplexModel: new ModelProfile("OpenAI", "gpt-complex", ModelCapability.Text | ModelCapability.Code, SubscriptionMode.ApiKey)),
        new AgentDefinition(
            new AgentId("anthropic-reviewer"),
            "Anthropic reviewer",
            AgentRole.Reviewer,
            new ModelProfile("Anthropic", "claude-custom", ModelCapability.Text | ModelCapability.Code, SubscriptionMode.ApiKey, "custom", 4096),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("claude-cli", ReasoningEffort: "custom-subscription")),
        new AgentDefinition(
            new AgentId("ollama-tester"),
            "Ollama tester",
            AgentRole.Tester,
            new ModelProfile("Ollama", "qwen-local", ModelCapability.Text | ModelCapability.Code, SubscriptionMode.LocalBridge))
    ]);

    AgentCatalogStore.Save(path, catalog);

    var restored = AgentCatalogStore.Load(path);
    var openAi = restored.GetRequired(AgentRole.Developer);
    var anthropic = restored.GetRequired(AgentRole.Reviewer);
    var ollama = restored.GetRequired(AgentRole.Tester);

    Assert.Equal(AgentCatalog.RoutineApiMaxOutputTokens, openAi.Model.MaxOutputTokens);
    Assert.Equal(AgentCatalog.RoutineReasoningEffort, openAi.Model.ReasoningEffort);
    Assert.Equal(AgentCatalog.RoutineSubscriptionReasoningEffort, openAi.Subscription!.ReasoningEffort);
    Assert.Equal(AgentCatalog.ComplexApiMaxOutputTokens, openAi.ComplexModel!.MaxOutputTokens);
    Assert.Equal(AgentCatalog.ComplexReasoningEffort, openAi.ComplexModel.ReasoningEffort);
    Assert.Equal(4096, anthropic.Model.MaxOutputTokens);
    Assert.Equal("custom", anthropic.Model.ReasoningEffort);
    Assert.Equal("custom-subscription", anthropic.Subscription!.ReasoningEffort);
    Assert.True(ollama.Model.MaxOutputTokens is null);
    Assert.True(ollama.Model.ReasoningEffort is null);
}
    [Xunit.Fact(DisplayName = "AgentCatalogStore_load_repairs_stale_openai_codex_subscription_default")]
    public void AgentCatalogStoreLoadRepairsStaleOpenAiCodexSubscriptionDefault()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "agents.json");
    var catalog = new AgentCatalog(
    [
        new AgentDefinition(
            new AgentId("openai-developer"),
            "OpenAI developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-custom", ModelCapability.Text | ModelCapability.Code, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.StaleOpenAiCodexSubscriptionModelAlias, "medium")),
        new AgentDefinition(
            new AgentId("openai-reviewer"),
            "OpenAI reviewer",
            AgentRole.Reviewer,
            new ModelProfile("OpenAI", "gpt-custom", ModelCapability.Text | ModelCapability.Code, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("custom-codex", AgentCatalog.StaleOpenAiCodexSubscriptionModelAlias, "medium"))
    ]);

    AgentCatalogStore.Save(path, catalog);

    var restored = AgentCatalogStore.Load(path);

    Assert.Equal(AgentCatalog.OpenAiSubscriptionModelAlias, restored.GetRequired(AgentRole.Developer).Subscription!.ModelAlias);
    Assert.Equal(AgentCatalog.StaleOpenAiCodexSubscriptionModelAlias, restored.GetRequired(AgentRole.Reviewer).Subscription!.ModelAlias);
}
}
