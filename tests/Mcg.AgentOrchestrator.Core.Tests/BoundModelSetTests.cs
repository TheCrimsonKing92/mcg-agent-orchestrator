using Mcg.AgentOrchestrator.Core;

// Parallel-safe: catalogs are in-memory fixtures only.
public sealed class BoundModelSetTests
{
    [Fact]
    public void CatalogMembershipUsesExactlyTheFourSpecifiedSources()
    {
        var agents = new[]
        {
            new AgentDefinition(new AgentId("fixture"), "fixture", AgentRole.Developer,
                Model("routine"), Subscription: new("codex-cli", "excluded-agent-alias"),
                ComplexModel: Model("complex")),
            new AgentDefinition(new AgentId("duplicate"), "duplicate", AgentRole.Tester, Model("ROUTINE"))
        };
        var functions = new ModelFunctionCatalog(
        [
            new("purpose", ModelLane.Local, Model("function"), Subscription: new("cli", "alias")),
            new("other-purpose", ModelLane.Capable, Model("COMPLEX"), Subscription: new("cli", "ALIAS"))
        ]);

        var bound = BoundModelSet.FromCatalogs(agents, functions);

        Assert.True(bound.IsAvailable);
        Assert.Null(bound.UnavailableReason);
        Assert.Equal(4, bound.ModelNames.Count);
        Assert.True(bound.ModelNames.SetEquals(new[] { "routine", "complex", "function", "alias" }));
        Assert.False(bound.Contains("excluded-agent-alias"));
    }

    [Fact]
    public void MembershipIgnoresCaseAndProviderPrefixesOnEitherSide()
    {
        var bound = BoundModelSet.Available(["OpenAI/GPT-6.1-SOL", "gpt-6.1-sol", "qwen3:8b"]);

        Assert.Equal(2, bound.ModelNames.Count);
        Assert.True(bound.Contains("Anthropic/gpt-6.1-sol"));
        Assert.True(bound.Contains("GPT-6.1-SOL"));
        Assert.True(bound.Contains("Ollama/qwen3:8b"));
        Assert.False(bound.Contains("gpt-5.5")); // Deliberate unbound model alias verifies membership rejects names absent from the fixture bound set.
    }

    [Fact]
    public void EmptyCatalogsAreAvailableAndUnavailableKeepsItsReason()
    {
        var empty = BoundModelSet.FromCatalogs([], ModelFunctionCatalog.Empty);
        Assert.True(empty.IsAvailable);
        Assert.Empty(empty.ModelNames);

        var unavailable = BoundModelSet.Unavailable("agents.json missing");
        Assert.False(unavailable.IsAvailable);
        Assert.Equal("agents.json missing", unavailable.UnavailableReason);
        Assert.Empty(unavailable.ModelNames);
    }

    private static ModelProfile Model(string name) => new("fixture-provider", name,
        ModelCapability.Text, SubscriptionMode.ApiKey);
}
