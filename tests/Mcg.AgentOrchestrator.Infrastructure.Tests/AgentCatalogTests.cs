using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Dashboard.Rendering;
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

    Assert.Equal(6, catalog.Agents.Count);

    foreach (var role in Enum.GetValues<AgentRole>())
    {
        var agent = catalog.GetRequired(role);
        Assert.Equal("OpenAI", agent.Model.ProviderName);
        Assert.Equal("gpt-5.4-mini", agent.Model.ModelName);
        Assert.Equal(AgentCatalog.RoutineApiMaxOutputTokens, agent.Model.MaxOutputTokens);
        Assert.Equal(AgentExecutionPolicy.PreferSubscription, agent.ExecutionPolicy);
        Assert.Equal("codex-cli", agent.Subscription!.WorkerProfileName);
        var expectedSubscriptionModel = role == AgentRole.Ideation
            ? AgentCatalog.OpenAiSubscriptionModelAlias
            : AgentCatalog.OpenAiSolSubscriptionModelAlias;
        Assert.Equal(expectedSubscriptionModel, agent.Subscription.ModelAlias);
        Assert.Equal("OpenAI", agent.ComplexModel!.ProviderName);
        Assert.Equal("gpt-5.5", agent.ComplexModel.ModelName); // Deliberate paid API complex-model name from OpenAiComplex(), independent of the subscription alias.
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
    [Xunit.Fact(DisplayName = "Output_caps_resolve_from_single_policy_source")]
    public void OutputCapsResolveFromSinglePolicySource()
{
    // Pins the catalog cap aliases and Ollama defaults to OutputTokenPolicy so a
    // future edit cannot reintroduce a second cap literal unnoticed.
    Assert.Equal(OutputTokenPolicy.RoutinePaidMaxOutputTokens, AgentCatalog.RoutineApiMaxOutputTokens);
    Assert.Equal(OutputTokenPolicy.ComplexPaidMaxOutputTokens, AgentCatalog.ComplexApiMaxOutputTokens);

    foreach (var agent in AgentCatalog.OllamaDefault().Agents)
    {
        Assert.Equal(OutputTokenPolicy.RoutineLocalMaxOutputTokens, agent.Model.MaxOutputTokens);
        Assert.Equal(OutputTokenPolicy.ComplexLocalMaxOutputTokens, agent.ComplexModel!.MaxOutputTokens);
    }
}
    [Xunit.Fact(DisplayName = "AgentCatalog_AnthropicDefault_pairs_haiku_base_with_sonnet_complex")]
    public void AgentCatalogAnthropicDefaultPairsHaikuBaseWithSonnetComplex()
{
    var catalog = AgentCatalog.AnthropicDefault();

    Assert.Equal(6, catalog.Agents.Count);

    foreach (var role in Enum.GetValues<AgentRole>())
    {
        var agent = catalog.GetRequired(role);
        Assert.Equal("Anthropic", agent.Model.ProviderName);
        Assert.Equal("claude-haiku-4-5", agent.Model.ModelName);
        Assert.Equal(AgentCatalog.RoutineApiMaxOutputTokens, agent.Model.MaxOutputTokens);
        Assert.Equal(AgentExecutionPolicy.PreferSubscription, agent.ExecutionPolicy);
        Assert.Equal("claude-cli", agent.Subscription!.WorkerProfileName);
        Assert.Equal("Anthropic", agent.ComplexModel!.ProviderName);
        Assert.Equal("claude-sonnet-4-6", agent.ComplexModel.ModelName);
        Assert.Equal(AgentCatalog.ComplexApiMaxOutputTokens, agent.ComplexModel.MaxOutputTokens);
    }
}
    [Xunit.Fact(DisplayName = "DashboardAgentOptionCatalog_Anthropic_contains_pinned_Opus_5_without_Fable")]
    public void DashboardAgentOptionCatalogAnthropicContainsPinnedOpus5WithoutFable()
{
    var subscriptionModels = DashboardAgentOptionCatalog.SubscriptionModelOptions("Anthropic");
    var apiModels = DashboardAgentOptionCatalog.ApiModelOptions("Anthropic");

    Assert.Contains(
        subscriptionModels,
        option => option.Value == "opus-5"
            && option.Label.Contains("Opus 5", StringComparison.Ordinal)
            && option.Label.Contains("pinned", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(
        subscriptionModels,
        option => option.Value == "opus" && option.Label == "Claude Opus (latest)");
    Assert.DoesNotContain(subscriptionModels, option => option.Value == "fable");
    Assert.Contains(apiModels, option => option.Value == "claude-opus-5");
}
    [Xunit.Fact(DisplayName = "Agents_configuration_Opus_5_alternates_preserve_counts_and_primaries")]
    public void AgentsConfigurationOpus5AlternatesPreserveCountsAndPrimaries()
{
    // Mirrors only the order-sensitive fields from gitignored runtime state;
    // the test must not read or mutate the operator's live agents.json.
    (string Id, string Name, AgentRole Role, string Provider, string? ModelAlias)[] fixture =
    [
        ("openai-planner", "OpenAI planner", AgentRole.Planner, "OpenAI", AgentCatalog.OpenAiLunaSubscriptionModelAlias),
        ("ollama-ideation", "Ollama ideation", AgentRole.Ideation, "Ollama", null),
        ("openai-researcher", "OpenAI researcher", AgentRole.Researcher, "OpenAI", AgentCatalog.OpenAiSolSubscriptionModelAlias),
        ("openai-developer", "OpenAI developer", AgentRole.Developer, "OpenAI", AgentCatalog.OpenAiSolSubscriptionModelAlias),
        ("openai-tester", "OpenAI tester", AgentRole.Tester, "OpenAI", AgentCatalog.OpenAiLunaSubscriptionModelAlias),
        ("openai-reviewer", "OpenAI reviewer", AgentRole.Reviewer, "OpenAI", AgentCatalog.OpenAiSolSubscriptionModelAlias),
        ("anthropic-reviewer-opus-5", "Opus 5", AgentRole.Reviewer, "Anthropic", "claude-opus-5"),
        ("anthropic-planner-opus-5", "Opus 5", AgentRole.Planner, "Anthropic", "claude-opus-5"),
        ("anthropic-researcher-opus-5", "Opus 5", AgentRole.Researcher, "Anthropic", "claude-opus-5"),
        ("anthropic-developer-opus-5", "Opus 5", AgentRole.Developer, "Anthropic", "claude-opus-5"),
        ("anthropic-tester-opus-5", "Opus 5", AgentRole.Tester, "Anthropic", "claude-opus-5")
    ];
    var expectedPrimaryIds = new Dictionary<AgentRole, string>
    {
        [AgentRole.Reviewer] = "openai-reviewer",
        [AgentRole.Planner] = "openai-planner",
        [AgentRole.Researcher] = "openai-researcher",
        [AgentRole.Developer] = "openai-developer",
        [AgentRole.Tester] = "openai-tester"
    };

    foreach (var (role, expectedPrimaryId) in expectedPrimaryIds)
    {
        var roleAgents = fixture.Where(agent => agent.Role == role).ToList();
        Assert.Equal(2, roleAgents.Count);
        Assert.Equal(expectedPrimaryId, roleAgents[0].Id);

        var alternate = Assert.Single(roleAgents.Skip(1));
        Assert.Equal($"anthropic-{role.ToString().ToLowerInvariant()}-opus-5", alternate.Id);
        Assert.Equal("Opus 5", alternate.Name);
        Assert.Equal("Anthropic", alternate.Provider);
        Assert.Equal("claude-opus-5", alternate.ModelAlias);
    }

    Assert.DoesNotContain(
        fixture,
        agent => agent.ModelAlias == "fable" || agent.Name == "Fable");
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

    Assert.Equal(6, catalog.Agents.Count);
    Assert.Equal("Anthropic developer", catalog.GetRequired(AgentRole.Developer).Name);
    Assert.Equal("claude-test", catalog.GetRequired(AgentRole.Developer).Model.ModelName);
}
    [Xunit.Fact(DisplayName = "AgentCatalog_add_or_replace_by_id_preserves_same_role_primary")]
    public void AgentCatalogAddOrReplaceByIdPreservesSameRolePrimary()
{
    var alternate = new AgentDefinition(
        new AgentId("anthropic-developer-claude"),
        "Anthropic developer",
        AgentRole.Developer,
        new ModelProfile("Anthropic", "claude-test", ModelCapability.Text | ModelCapability.Code, SubscriptionMode.ApiKey));

    var catalog = AgentCatalog.Default().AddOrReplaceById(alternate);
    var replacement = alternate with
    {
        Name = "Anthropic fallback developer",
        Model = alternate.Model with { ModelName = "claude-updated" }
    };
    catalog = catalog.AddOrReplaceById(replacement);

    var developers = catalog.Agents.Where(agent => agent.Role == AgentRole.Developer).ToList();
    Assert.Equal(7, catalog.Agents.Count);
    Assert.Equal("openai-developer", developers[0].Id.Value);
    Assert.Equal("anthropic-developer-claude", developers[1].Id.Value);
    Assert.Equal("Anthropic fallback developer", developers[1].Name);
    Assert.Equal("claude-updated", developers[1].Model.ModelName);
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
    [Xunit.Fact(DisplayName = "AgentCatalogStore_roundtrips_same_role_alternates")]
    public void AgentCatalogStoreRoundtripsSameRoleAlternates()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "agents.json");
    var alternate = new AgentDefinition(
        new AgentId("anthropic-developer-claude"),
        "Anthropic developer",
        AgentRole.Developer,
        new ModelProfile("Anthropic", "claude-test", ModelCapability.Text | ModelCapability.Code, SubscriptionMode.ApiKey));
    var catalog = AgentCatalog.Default().AddOrReplaceById(alternate);

    AgentCatalogStore.Save(path, catalog);
    var restored = AgentCatalogStore.Load(path);

    var developers = restored.Agents.Where(agent => agent.Role == AgentRole.Developer).ToList();
    Assert.Equal(2, developers.Count);
    Assert.Equal("openai-developer", developers[0].Id.Value);
    Assert.Equal("anthropic-developer-claude", developers[1].Id.Value);
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
    [Xunit.Fact(DisplayName = "AgentCatalogStore_save_leaves_no_tmp_file")]
    public void AgentCatalogStoreSaveLeavesNoTmpFile()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "agents.json");

    AgentCatalogStore.Save(path, AgentCatalog.Default());

    Assert.False(File.Exists(path + ".tmp"));
    Assert.True(File.Exists(path));
}
    [Xunit.Fact(DisplayName = "AgentCatalogStore_corrupt_file_recovers_from_bak")]
    public void AgentCatalogStoreCorruptFileRecoversFromBak()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "agents.json");
    var firstCatalog = AgentCatalog.Default().UpsertRole(new AgentDefinition(
        new AgentId("openai-developer"),
        "OpenAI developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-bak-model", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey)));

    // First save writes path; second save moves path → .bak and writes new content to path
    AgentCatalogStore.Save(path, firstCatalog);
    AgentCatalogStore.Save(path, AgentCatalog.Default());
    File.WriteAllText(path, "{{corrupt}}");

    var recovered = AgentCatalogStore.Load(path);

    Assert.Equal("gpt-bak-model", recovered.GetRequired(AgentRole.Developer).Model.ModelName);
}
    [Xunit.Fact(DisplayName = "AgentCatalogStore_corrupt_file_without_bak_falls_back_to_defaults")]
    public void AgentCatalogStoreCorruptFileWithoutBakFallsBackToDefaults()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "agents.json");
    File.WriteAllText(path, "{{corrupt}}");

    var catalog = AgentCatalogStore.Load(path);

    Assert.Equal("OpenAI", catalog.GetRequired(AgentRole.Developer).Model.ProviderName);
    Assert.Equal("gpt-5.4-mini", catalog.GetRequired(AgentRole.Developer).Model.ModelName);
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
            Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "medium")),
        new AgentDefinition(
            new AgentId("openai-planner"),
            "OpenAI planner",
            AgentRole.Planner,
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

    Assert.Equal(AgentCatalog.OpenAiSolSubscriptionModelAlias, restored.GetRequired(AgentRole.Developer).Subscription!.ModelAlias);
    Assert.Equal(AgentCatalog.OpenAiSolSubscriptionModelAlias, restored.GetRequired(AgentRole.Planner).Subscription!.ModelAlias);
    Assert.Equal(AgentCatalog.StaleOpenAiCodexSubscriptionModelAlias, restored.GetRequired(AgentRole.Reviewer).Subscription!.ModelAlias);
}

    [Xunit.Fact]
    public void AgentCatalogStoreLoadMigratesPersistedFiveRoleGpt55AliasesToSol()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "agents.json");
        AgentRole[] roles =
        [
            AgentRole.Researcher,
            AgentRole.Planner,
            AgentRole.Developer,
            AgentRole.Tester,
            AgentRole.Reviewer
        ];
        var catalog = new AgentCatalog(roles.Select(role => new AgentDefinition(
            new AgentId($"openai-{role.ToString().ToLowerInvariant()}"),
            $"OpenAI {role}",
            role,
            new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text | ModelCapability.Code, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "low"))).ToArray());
        AgentCatalogStore.Save(path, catalog);

        var restored = AgentCatalogStore.Load(path);

        Assert.All(roles, role =>
            Assert.Equal(AgentCatalog.OpenAiSolSubscriptionModelAlias, restored.GetRequired(role).Subscription!.ModelAlias));
    }
    [Xunit.Fact(DisplayName = "AgentCatalogStore_load_preserves_new_and_unknown_subscription_aliases")]
    public void AgentCatalogStoreLoadPreservesNewAndUnknownSubscriptionAliases()
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
            Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiTerraSubscriptionModelAlias)),
        new AgentDefinition(
            new AgentId("openai-reviewer"),
            "OpenAI reviewer",
            AgentRole.Reviewer,
            new ModelProfile("OpenAI", "gpt-custom", ModelCapability.Text | ModelCapability.Code, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-future-alias"))
    ]);

    AgentCatalogStore.Save(path, catalog);

    var restored = AgentCatalogStore.Load(path);

    var developerSubscription = restored.GetRequired(AgentRole.Developer).Subscription!;
    var reviewerSubscription = restored.GetRequired(AgentRole.Reviewer).Subscription!;
    Assert.Equal(AgentCatalog.OpenAiTerraSubscriptionModelAlias, developerSubscription.ModelAlias);
    Assert.Equal("medium", developerSubscription.ReasoningEffort);
    Assert.Equal("gpt-future-alias", reviewerSubscription.ModelAlias);
    Assert.Equal(AgentCatalog.RoutineSubscriptionReasoningEffort, reviewerSubscription.ReasoningEffort);
}
}
