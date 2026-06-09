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
        Assert.Equal("gpt-5.3-codex", agent.Subscription.ModelAlias);
        Assert.Equal("OpenAI", agent.ComplexModel!.ProviderName);
        Assert.Equal("gpt-5.5", agent.ComplexModel.ModelName);
        Assert.Equal(AgentCatalog.ComplexApiMaxOutputTokens, agent.ComplexModel.MaxOutputTokens);
    }

    foreach (var role in Enum.GetValues<AgentRole>())
    {
        var agent = catalog.GetRequired(role);
        Assert.Equal(AgentCatalog.RoutineReasoningEffort, agent.Model.ReasoningEffort);
        Assert.Equal(AgentCatalog.RoutineReasoningEffort, agent.Subscription!.ReasoningEffort);
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
}

