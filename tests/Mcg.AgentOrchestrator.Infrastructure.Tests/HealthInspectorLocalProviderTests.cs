using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: environments and command resolution are supplied per call; no server is started.
public sealed class HealthInspectorLocalProviderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("http://127.0.0.1:1")]
    public void Inspect_LocalProviderUrls_ReportsUnprobedProviders(string? baseUrl)
    {
        var environment = new Dictionary<string, string?>();
        if (baseUrl is not null)
        {
            environment["LLAMA_CPP_BASE_URL"] = baseUrl;
            environment["OLLAMA_BASE_URL"] = baseUrl;
        }

        var report = OrchestratorHealthInspector.Inspect(
            environment, AgentCatalog.Default(), WorkerProfileCatalog.Default(), _ => false);

        Assert.Equal(new[] { "OpenAI", "Anthropic", "LlamaCpp", "Ollama" },
            report.Providers.Select(provider => provider.ProviderName));
        foreach (var (name, expectedUrl) in new[]
        {
            ("LlamaCpp", baseUrl ?? LlamaCppDefaults.BaseUrl),
            ("Ollama", baseUrl ?? OllamaDefaults.BaseUrl)
        })
        {
            var provider = report.Providers.Single(provider => provider.ProviderName == name);
            Assert.False(provider.IsConfigured);
            Assert.Equal("NotProbed", provider.Mode);
            Assert.Contains(expectedUrl, provider.Detail, StringComparison.Ordinal);
            Assert.Contains("not health-checked", provider.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Inspect_LocalApiOnlyAgents_PreservesRouteSupportWithoutHealthChecks()
    {
        var catalog = new AgentCatalog(
        [
            new AgentDefinition(new AgentId("ollama-planner"), "Ollama planner", AgentRole.Planner,
                new ModelProfile("Ollama", "local", ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.ApiOnly),
            new AgentDefinition(new AgentId("llamacpp-developer"), "LlamaCpp developer", AgentRole.Developer,
                new ModelProfile("LlamaCpp", "local", ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.ApiOnly)
        ]);

        var report = OrchestratorHealthInspector.Inspect(
            new Dictionary<string, string?>(), catalog, WorkerProfileCatalog.Default(), _ => false);

        var ollama = report.Agents.Single(agent => agent.Role == AgentRole.Planner);
        Assert.True(ollama.IsValid);
        Assert.Contains("API provider 'Ollama' is not health-checked", ollama.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("is reachable locally", ollama.Detail, StringComparison.Ordinal);
        var llama = report.Agents.Single(agent => agent.Role == AgentRole.Developer);
        Assert.False(llama.IsValid);
        Assert.Contains("API provider 'LlamaCpp' is not health-checked", llama.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("registered but offline", llama.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_DefaultAgentsWithOpenAiKey_RemainsReadyWithoutLocalRecommendations()
    {
        var report = OrchestratorHealthInspector.Inspect(
            new Dictionary<string, string?> { ["OPENAI_API_KEY"] = "set" },
            AgentCatalog.Default(), WorkerProfileCatalog.Default(), command => command == "Write-Output");

        Assert.True(report.IsReady);
        Assert.NotEmpty(report.Agents);
        Assert.All(report.Agents, agent =>
        {
            Assert.DoesNotContain("LlamaCpp is available", agent.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("switching this role to LlamaCpp", agent.Detail, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Inspect_OnlyUnprobedProviders_DoesNotCountAsReady()
    {
        var catalog = new AgentCatalog(Enum.GetValues<AgentRole>().Select(role =>
            new AgentDefinition(new AgentId($"ollama-{role}"), $"Ollama {role}", role,
                new ModelProfile("Ollama", "local", ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.ApiOnly)).ToList());
        var report = OrchestratorHealthInspector.Inspect(
            new Dictionary<string, string?>(), catalog, new WorkerProfileCatalog([]), _ => false);

        Assert.NotEmpty(report.Agents);
        Assert.All(report.Agents, agent => Assert.True(agent.IsValid));
        Assert.Empty(report.WorkerProfiles);
        Assert.False(report.IsReady);
    }
}
