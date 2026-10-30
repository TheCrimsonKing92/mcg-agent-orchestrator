using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = LuaProjectDiscoveryAdapterTests.Fixture;

// Parallel-safe: fixture files are isolated; answer application and emission are in memory.
public sealed class LuaVerificationManifestTests
{
    [Fact]
    public void Build_BustedEvidence_EmitsOnlyCrosswalkTest()
    {
        using var fixture = new Fixture();
        fixture.Write(".busted");
        fixture.Write("spec/a_spec.lua");
        var model = fixture.Discover();

        var result = LearnedAcceptanceManifest.Build(model, Fixture.Answers());

        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        var manifest = JsonNode.Parse(result.Json)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(CrosswalkChecks), manifest["checks"]), result.Json);
        Assert.False(manifest["engine"]!.AsObject().ContainsKey("mtpInvocations"));
        Assert.Empty(model.OwnerQuestions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("no")]
    [InlineData("busted")]
    public void Build_NoSpecsWithoutAffirmativeAnswer_BlocksTest(string? answer)
    {
        using var fixture = new Fixture();
        fixture.Write("scripts/a.lua");
        fixture.Write("scripts/b.lua");
        var model = fixture.Discover();
        var question = Assert.Single(model.OwnerQuestions);
        Assert.Equal("environment/lua-test-framework", question.FactKey);
        Assert.Equal("crosswalk-test .", Assert.Single(model.EnvironmentNeeds, need => need.Name == "lua-test-command").Value.Value);
        var answers = answer is null ? Fixture.Answers() : Fixture.Answers(Answer(question.FactKey, answer));

        var result = LearnedAcceptanceManifest.Build(model, answers);

        Assert.Null(result.Json);
        var blocker = Assert.Single(result.Blockers);
        Assert.Equal(question.FactKey, blocker.FactKey);
        Assert.Equal(question.Question, blocker.Reason);
    }

    [Fact]
    public void Build_FrameworkYes_EnablesCrosswalkWithoutChangingSnapshot()
    {
        using var fixture = new Fixture();
        fixture.Write("scripts/a.lua");
        var model = fixture.Discover();
        var snapshot = ProjectModelJson.Serialize(model);
        var answers = Fixture.Answers(Answer("environment/lua-test-framework", "YES"));
        var applied = OwnerAnswerApplier.Apply(model, answers);
        Assert.Empty(applied.StaleAnswers);
        Assert.Equal("yes", Assert.Single(applied.AnsweredFacts, fact => fact.FactKey == "environment/lua-test-framework").Fact.Value);

        var result = LearnedAcceptanceManifest.Build(model, answers);

        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(CrosswalkChecks), JsonNode.Parse(result.Json)!["checks"]), result.Json);
        Assert.Equal(snapshot, ProjectModelJson.Serialize(model));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("busted")]
    [InlineData(" yes")]
    public void Build_RockspecWithoutValidConsent_BlocksInstall(string? answer)
    {
        using var fixture = new Fixture();
        fixture.Write(".busted");
        fixture.Write("x.rockspec");
        var model = fixture.Discover();
        var answers = answer is null ? Fixture.Answers() : Fixture.Answers(Answer("environment/lua-luarocks-install", answer));

        var result = LearnedAcceptanceManifest.Build(model, answers);

        Assert.Null(result.Json);
        Assert.Equal("environment/lua-luarocks-install", Assert.Single(result.Blockers).FactKey);
    }

    [Theory]
    [InlineData("x.rockspec")]
    [InlineData("rockspecs/x.rockspec")]
    [InlineData("x.rockspec|y.rockspec|rockspecs/z.rockspec")]
    public void Build_ExplicitConsent_EmitsOneRootInstallBeforeCrosswalk(string paths)
    {
        using var fixture = new Fixture();
        fixture.Write(".busted");
        foreach (var path in paths.Split('|'))
            fixture.Write(path);
        var model = fixture.Discover();

        var result = LearnedAcceptanceManifest.Build(model,
            Fixture.Answers(Answer("environment/lua-luarocks-install", "yes")));

        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        var expected = JsonNode.Parse("""
            [
              {"name":"luarocks make --local","type":"command","command":"luarocks","arguments":["make","--local"]},
              {"name":"crosswalk-test .","type":"crosswalk-test","project":".","arguments":[]}
            ]
            """);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(result.Json)!["checks"]), result.Json);
        Assert.Empty(model.Commands);
        Assert.Empty(model.Measurements);
    }

    [Fact]
    public void Build_InstallDeclined_OmitsInstallAndEmitsTest()
    {
        using var fixture = new Fixture();
        fixture.Write(".busted");
        fixture.Write("x.rockspec");

        var result = LearnedAcceptanceManifest.Build(fixture.Discover(),
            Fixture.Answers(Answer("environment/lua-luarocks-install", "no")));

        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(CrosswalkChecks), JsonNode.Parse(result.Json)!["checks"]), result.Json);
    }

    [Theory]
    [InlineData("environment/lua-test-framework", "environment/lua-luarocks-install")]
    [InlineData("environment/lua-luarocks-install", "environment/lua-test-framework")]
    public void Build_OneAffirmativeAnswer_DoesNotAuthorizeOtherCheck(string answered, string pending)
    {
        using var fixture = new Fixture();
        fixture.Write("x.rockspec");
        var model = fixture.Discover();
        Assert.Equal(2, model.OwnerQuestions.Count);

        var result = LearnedAcceptanceManifest.Build(model, Fixture.Answers(Answer(answered, "yes")));

        Assert.Null(result.Json);
        Assert.Equal(pending, Assert.Single(result.Blockers).FactKey);
    }

    [Fact]
    public void Build_NoRockspec_StaleInstallConsentCannotAddInstall()
    {
        using var fixture = new Fixture();
        fixture.Write(".busted");
        var model = fixture.Discover();
        var answers = Fixture.Answers(Answer("environment/lua-luarocks-install", "yes"));
        var applied = OwnerAnswerApplier.Apply(model, answers);
        Assert.Equal(OwnerAnswerApplier.NotLiveReason, Assert.Single(applied.StaleAnswers).Reason);

        var result = LearnedAcceptanceManifest.Build(model, answers);

        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(CrosswalkChecks), JsonNode.Parse(result.Json)!["checks"]), result.Json);
    }

    private static OwnerAnswer Answer(string key, string value) => new(key, value, "fixture", DateTimeOffset.UnixEpoch);

    private const string CrosswalkChecks = """
        [{"name":"crosswalk-test .","type":"crosswalk-test","project":".","arguments":[]}]
        """;
}
