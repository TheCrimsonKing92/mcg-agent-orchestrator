using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorRoundModelResolverTests
{
    [Fact]
    public void Absent_purpose_uses_named_default_without_effort()
    {
        var result = ConductorRoundModelResolver.Resolve(Catalog(ModelFunctionPurposes.ConductorSteward),
            ModelFunctionPurposes.ConductorAuthor);
        Assert.True(result.IsValid);
        Assert.Equal("sonnet", result.Alias);
        Assert.Null(result.ReasoningEffort);
        Assert.Equal("--model sonnet", result.ModelArguments);
    }

    [Theory]
    [InlineData(null, "--model claude-sonnet-5")]
    [InlineData("", "--model claude-sonnet-5")]
    [InlineData(" ", "--model claude-sonnet-5")]
    [InlineData("high", "--model claude-sonnet-5 --effort high")]
    public void Named_binding_matches_purpose_and_renders_Claude_effort(string? effort, string expected)
    {
        var binding = Binding("CONDUCTOR-AUTHOR", effort: effort) with { Name = "custom-name" };
        var result = ConductorRoundModelResolver.Resolve(new([binding]), ModelFunctionPurposes.ConductorAuthor);
        Assert.True(result.IsValid);
        Assert.Equal("claude-sonnet-5", result.Alias);
        Assert.Equal(expected, result.ModelArguments);
    }

    [Theory]
    [InlineData("duplicate", "multiple-bindings")]
    [InlineData("missing", "missing-subscription")]
    [InlineData("empty", "empty-model-alias")]
    [InlineData("null", "empty-model-alias")]
    [InlineData("whitespace", "empty-model-alias")]
    [InlineData("other-profile", "non-claude-profile:codex-cli")]
    public void Invalid_shapes_name_the_purpose_and_binding_without_defaulting(string shape, string cause)
    {
        var result = ConductorRoundModelResolver.Resolve(InvalidCatalog(ModelFunctionPurposes.ConductorAuthor, shape),
            ModelFunctionPurposes.ConductorAuthor);
        Assert.False(result.IsValid);
        Assert.Null(result.Alias);
        Assert.StartsWith("model-binding-invalid:conductor-author binding=", result.InvalidReason);
        Assert.Contains("conductor-author@0", result.InvalidReason);
        Assert.Contains("cause=" + cause, result.InvalidReason);
        if (shape == "duplicate") Assert.Contains("CONDUCTOR-AUTHOR@1", result.InvalidReason);
    }

    [Fact]
    public void Claude_profile_matching_is_case_insensitive()
    {
        var binding = Binding(ModelFunctionPurposes.ConductorAuthor) with
        {
            Subscription = new("CLAUDE-CLI", "claude-sonnet-5")
        };
        Assert.True(ConductorRoundModelResolver.Resolve(new([binding]), ModelFunctionPurposes.ConductorAuthor).IsValid);
    }

    [Fact]
    public void Conduct_reason_preserves_existing_tokens_and_exposes_binding_failures()
    {
        Assert.Equal("InvalidOperationException", ConductorRoundModelResolver.ConductReason(
            new InvalidOperationException("ordinary failure"), "InvalidOperationException"));
        Assert.Equal("model-failure model=claude-sonnet-5", ConductorRoundModelResolver.ConductReason(
            new ConductorModelRoundException("exit 1", "claude-sonnet-5"), "model-failure"));
        Assert.Equal("model-binding-invalid:conductor-author binding=x", ConductorRoundModelResolver.ConductReason(
            new ConductorModelRoundException("model-binding-invalid:conductor-author binding=x", null), "model-failure"));
    }

    internal static ModelFunctionBinding Binding(string purpose, string? alias = "claude-sonnet-5", string? effort = null) =>
        new(purpose, ModelLane.Capable,
            new ModelProfile("Anthropic", "catalog-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            Subscription: new(WorkerProfileDispatcher.AnthropicSubscriptionProfileName, alias, effort));

    internal static ModelFunctionCatalog Catalog(string purpose, string? effort = null) => new([Binding(purpose, effort: effort)]);

    internal static ModelFunctionCatalog InvalidCatalog(string purpose, string shape)
    {
        var binding = Binding(purpose);
        return shape switch
        {
            "duplicate" => new([binding, Binding(purpose.ToUpperInvariant())]),
            "missing" => new([binding with { Subscription = null }]),
            "empty" => new([Binding(purpose, "")]),
            "null" => new([Binding(purpose, null)]),
            "whitespace" => new([Binding(purpose, " ")]),
            "other-profile" => new([binding with { Subscription = new("codex-cli", "claude-sonnet-5") }]),
            _ => throw new ArgumentException("Unknown invalid shape", nameof(shape))
        };
    }
}
