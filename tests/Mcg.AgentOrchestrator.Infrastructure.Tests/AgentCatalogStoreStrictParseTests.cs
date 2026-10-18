using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Scenario = ProviderHoldAgentSubstitutionTests.Scenario;

// Parallel-safe: each test owns its catalog directory and never changes process state.
public sealed class AgentCatalogStoreStrictParseTests
{
    [Xunit.Fact]
    public void ValidCatalog_StrictParse_EqualsLoadIncludingNormalization()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "agents.json");
            var agent = Scenario.Agent("custom-tester", "OpenAI", "codex-cli", "gpt-6.1-sol")
                with { IsProviderRoutingConstrained = null };
            AgentCatalogStore.Save(path, new AgentCatalog([agent]));

            Assert.True(AgentCatalogStore.TryParseStrict(File.ReadAllText(path), out var catalog, out var error));

            Assert.Null(error);
            Assert.NotNull(catalog);
            Assert.Equal(AgentCatalogStore.Load(path).Agents.ToArray(), catalog.Agents.ToArray());
            Assert.NotNull(catalog.Agents.Single().IsProviderRoutingConstrained);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Xunit.Theory]
    [Xunit.InlineData("{\"Agents\": [", "LineNumber:")]
    [Xunit.InlineData("", "LineNumber:")]
    [Xunit.InlineData("   ", "LineNumber:")]
    [Xunit.InlineData("{\"Agents\":[]}", "no agents")]
    [Xunit.InlineData("null", "no agents")]
    [Xunit.InlineData("{}", "no agents")]
    [Xunit.InlineData("{\"Agents\":null}", "no agents")]
    public void InvalidCatalog_ReturnsDocumentedError(string json, string expected)
    {
        Assert.False(AgentCatalogStore.TryParseStrict(json, out var catalog, out var error));
        Assert.Null(catalog);
        Assert.NotNull(error);
        if (expected == "no agents") Assert.Equal(expected, error);
        else
        {
            Assert.Contains(expected, error, StringComparison.Ordinal);
            var exception = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentCatalog>(json));
            Assert.Equal(exception.Message, error);
        }
    }

    [Xunit.Fact]
    public void CaseDuplicate_ReturnsSecondIdInError()
    {
        var first = Scenario.Agent("xai-tester", "xAI", "grok-cli", "grok-4.6");
        var json = JsonSerializer.Serialize(new AgentCatalog([first, first with { Id = new AgentId("XAI-TESTER") }]));

        Assert.False(AgentCatalogStore.TryParseStrict(json, out var catalog, out var error));

        Assert.Null(catalog);
        Assert.Equal("duplicate agent id 'XAI-TESTER'", error);
    }

    [Xunit.Fact]
    public void CorruptFile_LoadStillRecoversBackup()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "agents.json");
            var agents = new[] { Scenario.Agent("xai-tester", "xAI", "grok-cli", "grok-4.6") };
            AgentCatalogStore.Save(path, new AgentCatalog(agents));
            File.Copy(path, path + ".bak");
            File.WriteAllText(path, "{");

            Assert.Equal(agents, AgentCatalogStore.Load(path).Agents.ToArray());
            Assert.False(AgentCatalogStore.TryParseStrict(File.ReadAllText(path), out _, out var error));
            Assert.Contains("LineNumber:", error!, StringComparison.Ordinal);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
