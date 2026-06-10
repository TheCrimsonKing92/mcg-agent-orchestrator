using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

public sealed class HealthInspectorTests
{
    [Xunit.Fact(DisplayName = "OrchestratorHealthInspector_reports_provider_key_status")]
    public void OrchestratorHealthInspectorReportsProviderKeyStatus()
{
    var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        ["OPENAI_API_KEY"] = "set"
    };

    var report = OrchestratorHealthInspector.Inspect(environment, AgentCatalog.Default(), WorkerProfileCatalog.Default(), _ => false);

    Assert.True(report.Providers.Single(provider => provider.ProviderName == "OpenAI").IsConfigured);
    Assert.False(report.Providers.Single(provider => provider.ProviderName == "Anthropic").IsConfigured);
    Assert.Equal("ApiKey", report.Providers.Single(provider => provider.ProviderName == "OpenAI").Mode);
    Assert.Equal("Offline", report.Providers.Single(provider => provider.ProviderName == "Anthropic").Mode);
}
    [Xunit.Fact(DisplayName = "OrchestratorHealthInspector_recommends_ollama_for_paid_agents_when_available")]
    public async Task OrchestratorHealthInspectorRecommendsOllamaForPaidAgentsWhenAvailable()
{
    var port = GetAvailablePort();
    using var listener = new HttpListener();
    listener.Prefixes.Add($"http://localhost:{port}/");
    listener.Start();
    var server = Task.Run(async () =>
    {
        var context = await listener.GetContextAsync();
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"version\":\"test\"}");
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    });
    var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        ["OPENAI_API_KEY"] = "set",
        ["OLLAMA_BASE_URL"] = $"http://localhost:{port}"
    };

    var report = OrchestratorHealthInspector.Inspect(environment, AgentCatalog.Default(), WorkerProfileCatalog.Default(), _ => false);

    var developer = report.Agents.Single(agent => agent.Role == AgentRole.Developer);
    Assert.Contains(developer.Detail, text => text.Contains("local Ollama is available", StringComparison.Ordinal));
    Assert.Contains(developer.Detail, text => text.Contains("switching this role to Ollama before paid work", StringComparison.Ordinal));
    await server;
}
    [Xunit.Fact(DisplayName = "OrchestratorHealthInspector_validates_subscription_only_agents_by_worker_profile")]
    public void OrchestratorHealthInspectorValidatesSubscriptionOnlyAgentsByWorkerProfile()
{
    var agent = new AgentDefinition(
        new AgentId("subscription-developer"),
        "Subscription Developer",
        AgentRole.Developer,
        new ModelProfile("Unknown", "unused", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("local-subscription"));
    var catalog = new AgentCatalog([agent]);
    var profiles = new WorkerProfileCatalog([new WorkerProfile("local-subscription", "agent-cli --model {subscriptionModelName} {promptPath}")]);

    var report = OrchestratorHealthInspector.Inspect(
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
        catalog,
        profiles,
        command => command == "agent-cli");

    var developer = report.Agents.Single(agent => agent.Role == AgentRole.Developer);
    Assert.True(developer.IsValid);
    Assert.Equal(AgentExecutionPolicy.SubscriptionOnly, developer.ExecutionPolicy);
    Assert.Equal("local-subscription", developer.SubscriptionProfileName);
}
    [Xunit.Fact(DisplayName = "OrchestratorHealthInspector_rejects_subscription_profiles_without_model_pinning")]
    public void OrchestratorHealthInspectorRejectsSubscriptionProfilesWithoutModelPinning()
{
    var agent = new AgentDefinition(
        new AgentId("subscription-reviewer"),
        "Subscription Reviewer",
        AgentRole.Reviewer,
        new ModelProfile("OpenAI", "gpt", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("local-subscription"));
    var catalog = new AgentCatalog([agent]);
    var profiles = new WorkerProfileCatalog([new WorkerProfile("local-subscription", "agent-cli {promptPath}")]);

    var report = OrchestratorHealthInspector.Inspect(
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
        catalog,
        profiles,
        command => command == "agent-cli");

    var reviewer = report.Agents.Single(agent => agent.Role == AgentRole.Reviewer);
    Assert.False(reviewer.IsValid);
    Assert.Contains(reviewer.Detail, text => text.Contains("does not pin the selected model", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "OrchestratorHealthInspector_rejects_subscription_profiles_without_reasoning_pinning")]
    public void OrchestratorHealthInspectorRejectsSubscriptionProfilesWithoutReasoningPinning()
{
    var agent = new AgentDefinition(
        new AgentId("subscription-reviewer"),
        "Subscription Reviewer",
        AgentRole.Reviewer,
        new ModelProfile("OpenAI", "gpt", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("local-subscription", "gpt-codex", "low"));
    var catalog = new AgentCatalog([agent]);
    var profiles = new WorkerProfileCatalog([new WorkerProfile("local-subscription", "agent-cli --model {subscriptionModelName} {promptPath}")]);

    var report = OrchestratorHealthInspector.Inspect(
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
        catalog,
        profiles,
        command => command == "agent-cli");

    var reviewer = report.Agents.Single(agent => agent.Role == AgentRole.Reviewer);
    Assert.False(reviewer.IsValid);
    Assert.Contains(reviewer.Detail, text => text.Contains("does not pin the selected reasoning effort", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "OrchestratorHealthInspector_reports_local_bridge_provider_status")]
    public void OrchestratorHealthInspectorReportsLocalBridgeProviderStatus()
{
    var report = OrchestratorHealthInspector.Inspect(
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
        AgentCatalog.Default(),
        WorkerProfileCatalog.Default(),
        command => command is "codex");

    var openAi = report.Providers.Single(provider => provider.ProviderName == "OpenAI");
    var anthropic = report.Providers.Single(provider => provider.ProviderName == "Anthropic");

    Assert.True(openAi.IsConfigured);
    Assert.Equal("LocalBridge", openAi.Mode);
    Assert.Contains(openAi.Detail, text => text.Contains("codex", StringComparison.Ordinal));
    Assert.False(anthropic.IsConfigured);
}
    [Xunit.Fact(DisplayName = "OrchestratorHealthInspector_treats_default_cli_bridges_as_optional_profiles")]
    public void OrchestratorHealthInspectorTreatsDefaultCliBridgesAsOptionalProfiles()
{
    var report = OrchestratorHealthInspector.Inspect(
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["OPENAI_API_KEY"] = "set" },
        AgentCatalog.Default(),
        WorkerProfileCatalog.Default(),
        command => command == "Write-Output");

    Assert.True(report.IsReady);
    Assert.False(report.WorkerProfiles.Single(profile => profile.Name == "codex-cli").IsResolvable);
    Assert.True(report.WorkerProfiles.Single(profile => profile.Name == "codex-cli").IsOptional);
    Assert.False(report.WorkerProfiles.Single(profile => profile.Name == "claude-cli").IsResolvable);
    Assert.True(report.WorkerProfiles.Single(profile => profile.Name == "claude-cli").IsOptional);
}
    [Xunit.Fact(DisplayName = "OrchestratorHealthInspector_validates_agent_providers_and_roles")]
    public void OrchestratorHealthInspectorValidatesAgentProvidersAndRoles()
{
    var catalog = new AgentCatalog(
    [
        new AgentDefinition(
            new AgentId("openai-planner"),
            "OpenAI planner",
            AgentRole.Planner,
            new ModelProfile("OpenAI", "gpt", ModelCapability.Text, SubscriptionMode.ApiKey)),
        new AgentDefinition(
            new AgentId("bad-developer"),
            "Bad developer",
            AgentRole.Developer,
            new ModelProfile("Unknown", "unknown", ModelCapability.Text, SubscriptionMode.ApiKey))
    ]);

    var report = OrchestratorHealthInspector.Inspect(
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["OPENAI_API_KEY"] = "set" },
        catalog,
        WorkerProfileCatalog.Default(),
        command => true);

    Assert.True(report.Agents.Single(agent => agent.Role == AgentRole.Planner).IsValid);
    Assert.False(report.Agents.Single(agent => agent.Role == AgentRole.Developer).IsValid);
    Assert.False(report.Agents.Single(agent => agent.Role == AgentRole.Reviewer).IsValid);
    Assert.Contains(report.Agents.Single(agent => agent.Role == AgentRole.Developer).Detail, text => text.Contains("not registered", StringComparison.Ordinal));
    Assert.Contains(report.Agents.Single(agent => agent.Role == AgentRole.Reviewer).Detail, text => text.Contains("No agent", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "OrchestratorHealthInspector_rejects_local_bridge_for_api_only_agent")]
    public void OrchestratorHealthInspectorRejectsLocalBridgeForApiOnlyAgent()
{
    var catalog = new AgentCatalog(
    [
        new AgentDefinition(
            new AgentId("openai-planner"),
            "OpenAI planner",
            AgentRole.Planner,
            new ModelProfile("OpenAI", "gpt", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly)
    ]);

    var report = OrchestratorHealthInspector.Inspect(
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
        catalog,
        WorkerProfileCatalog.Default(),
        command => command == "codex");

    var planner = report.Agents.Single(agent => agent.Role == AgentRole.Planner);
    Assert.False(planner.IsValid);
    Assert.Contains(planner.Detail, text => text.Contains("local subscription bridge", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "OrchestratorHealthInspector_rejects_echo_only_subscription_routes")]
    public void OrchestratorHealthInspectorRejectsEchoOnlySubscriptionRoutes()
{
    var agent = new AgentDefinition(
        new AgentId("subscription-tester"),
        "Subscription Tester",
        AgentRole.Tester,
        new ModelProfile("OpenAI", "gpt", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    var catalog = new AgentCatalog([agent]);
    var profiles = WorkerProfileCatalog.Default().Upsert(new WorkerProfile("codex-cli", "Write-Output {promptPath}"));

    var report = OrchestratorHealthInspector.Inspect(
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
        catalog,
        profiles,
        command => command == "Write-Output");

    var tester = report.Agents.Single(agent => agent.Role == AgentRole.Tester);
    Assert.False(tester.IsValid);
    Assert.Contains(tester.Detail, text => text.Contains("only echoes", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "OrchestratorHealthInspector_validates_worker_profile_commands")]
    public void OrchestratorHealthInspectorValidatesWorkerProfileCommands()
{
    var catalog = new WorkerProfileCatalog(
    [
        new WorkerProfile("ok", "agent-cli --prompt {promptPath}"),
        new WorkerProfile("missing", "missing-cli {promptPath}")
    ]);

    var report = OrchestratorHealthInspector.Inspect(
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
        AgentCatalog.Default(),
        catalog,
        command => command == "agent-cli");

    Assert.True(report.WorkerProfiles.Single(profile => profile.Name == "ok").IsResolvable);
    Assert.False(report.WorkerProfiles.Single(profile => profile.Name == "missing").IsResolvable);
    Assert.Equal("missing-cli", report.WorkerProfiles.Single(profile => profile.Name == "missing").Executable);
}
    [Xunit.Fact(DisplayName = "OrchestratorHealthInspector_warns_on_echo_only_worker_profiles")]
    public void OrchestratorHealthInspectorWarnsOnEchoOnlyWorkerProfiles()
{
    var catalog = WorkerProfileCatalog.Default().Upsert(new WorkerProfile("codex-cli", "Write-Output {promptPath}"));

    var report = OrchestratorHealthInspector.Inspect(
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
        AgentCatalog.Default(),
        catalog,
        command => command == "Write-Output");

    var codex = report.WorkerProfiles.Single(profile => profile.Name == "codex-cli");
    Assert.True(codex.IsResolvable);
    Assert.True(codex.IsEchoOnly);
    Assert.False(codex.IsPatchCapable);
    Assert.Contains(codex.Detail, text => text.Contains("only echoes the prompt path", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "OrchestratorHealthInspector_reports_codex_patch_capability")]
    public void OrchestratorHealthInspectorReportsCodexPatchCapability()
{
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-ok", "codex exec --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})"),
        new WorkerProfile("codex-readonly", "codex exec (Get-Content -Raw {promptPath})")
    ]);

    var report = OrchestratorHealthInspector.Inspect(
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
        AgentCatalog.Default(),
        profiles,
        command => command == "codex");

    var ok = report.WorkerProfiles.Single(profile => profile.Name == "codex-ok");
    var readOnly = report.WorkerProfiles.Single(profile => profile.Name == "codex-readonly");
    Assert.True(ok.IsPatchCapable);
    Assert.Contains(ok.Detail, text => text.Contains("workspace-write", StringComparison.Ordinal));
    Assert.False(readOnly.IsPatchCapable);
    Assert.Contains(readOnly.Detail, text => text.Contains("missing --sandbox workspace-write", StringComparison.Ordinal));
}
}
