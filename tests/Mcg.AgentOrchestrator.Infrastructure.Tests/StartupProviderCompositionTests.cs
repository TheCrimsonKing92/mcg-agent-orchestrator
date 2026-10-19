using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: explicit fixture URLs and recording probes perform no network I/O.
public sealed class StartupProviderCompositionTests
{
    [Fact]
    public void Create_RefusedEndpoints_ProbesEachOnceAndUsesScriptedProviders()
    {
        var calls = new List<string>();

        var result = StartupProviderComposition.Create(
            execution: true,
            probe: url => { calls.Add(url); return false; },
            ollamaBaseUrl: "http://ollama.fixture:1",
            llamaBaseUrl: "http://llama.fixture:2");

        Assert.Equal(new[] { "http://ollama.fixture:1/v1/models", "http://llama.fixture:2/v1/models" }, calls);
        Assert.IsType<ScriptedModelProvider>(result.Providers.GetRequired("Ollama"));
        Assert.IsType<ScriptedModelProvider>(result.Providers.GetRequired("LlamaCpp"));
        Assert.Null(result.AgentFallback);
    }

    [Fact]
    public void Create_ReachableLlamaCpp_ReusesProbeForProviderAndDefaultAgents()
    {
        var calls = new List<string>();

        var result = StartupProviderComposition.Create(
            execution: true,
            probe: url => { calls.Add(url); return url == "http://llama.fixture:2/v1/models"; },
            ollamaBaseUrl: "http://ollama.fixture:1",
            llamaBaseUrl: "http://llama.fixture:2");

        Assert.Equal(new[] { "http://ollama.fixture:1/v1/models", "http://llama.fixture:2/v1/models" }, calls);
        Assert.IsType<ChatCompletionsModelProvider>(result.Providers.GetRequired("LlamaCpp"));
        Assert.IsType<ScriptedModelProvider>(result.Providers.GetRequired("Ollama"));
        var fallback = Assert.IsType<AgentCatalog>(result.AgentFallback);
        Assert.Equal(
            AgentCatalog.LlamaCppDefault().Agents.Select(agent => (agent.Id, agent.Name, agent.Role)),
            fallback.Agents.Select(agent => (agent.Id, agent.Name, agent.Role)));
    }

    [Fact]
    public void Create_QueryOnly_SkipsProbesAndReturnsEmptyRegistryWithoutFallback()
    {
        var calls = new List<string>();

        var result = StartupProviderComposition.Create(
            execution: false,
            probe: url => { calls.Add(url); return true; },
            ollamaBaseUrl: "http://ollama.fixture:1",
            llamaBaseUrl: "http://llama.fixture:2");

        Assert.Empty(calls);
        Assert.Throws<KeyNotFoundException>(() => result.Providers.GetRequired("Ollama"));
        Assert.Null(result.AgentFallback);
    }
}
