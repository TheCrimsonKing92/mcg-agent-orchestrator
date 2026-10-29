using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = NodeProjectDiscoveryAdapterTests.Fixture;

// Parallel-safe: each case uses an owned fixture; comparisons operate only on snapshots and JSON.
public sealed class NodeManifestCheckComparerTests
{
    [Fact]
    public void Compare_EmittedManifest_MatchesAllThreeLearnedChecks()
    {
        using var fixture = new Fixture();
        var model = fixture.Discover();
        var manifest = Emit(model);

        var result = ManifestCheckComparer.Compare(model, manifest.ToJsonString());

        Assert.Equal(new[] { "crosswalk-test .", "npm ci", "npx tsc" }, result.Select(row => row.ProjectPath));
        Assert.Equal(new[] { "crosswalk-test .", "npm ci", "npx tsc --noEmit" }, result.Select(row => row.LearnedRunner));
        Assert.All(result, row =>
        {
            Assert.Equal(ManifestCheckResultKind.Match, row.Kind);
            Assert.Equal(row.LearnedRunner, row.ManifestRunner);
            Assert.Empty(row.Reason);
        });
        Assert.Equal(result, ManifestCheckComparer.Compare(model, manifest.ToJsonString(),
            OwnerAnswerApplier.Apply(model, Fixture.Answers())));
    }

    [Fact]
    public void Compare_RemovedTypeCheck_ReportsMissingWithoutShiftingOtherChecks()
    {
        using var fixture = new Fixture();
        var model = fixture.Discover();
        var manifest = Emit(model);
        manifest["checks"]!.AsArray().RemoveAt(1);

        var result = ManifestCheckComparer.Compare(model, manifest.ToJsonString());

        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { ManifestCheckResultKind.Match, ManifestCheckResultKind.Match, ManifestCheckResultKind.MissingFromManifest },
            result.Select(row => row.Kind));
        Assert.Equal("npx tsc", result[2].ProjectPath);
        Assert.Equal("npx tsc --noEmit", result[2].LearnedRunner);
        Assert.Null(result[2].ManifestRunner);
    }

    [Theory]
    [InlineData(0, "[\"ci\",\"--ignore-scripts\"]", "npm ci")]
    [InlineData(1, "[\"tsc\",\"--emitDeclarationOnly\"]", "npx tsc")]
    [InlineData(2, "[\"one-test-id\"]", "crosswalk-test .")]
    public void Compare_AlteredArguments_ReportsDifferingCheck(int index, string arguments, string key)
    {
        using var fixture = new Fixture();
        var model = fixture.Discover();
        var manifest = Emit(model);
        manifest["checks"]![index]!["arguments"] = JsonNode.Parse(arguments);

        var result = ManifestCheckComparer.Compare(model, manifest.ToJsonString());

        Assert.Equal(3, result.Count);
        var changed = Assert.Single(result, row => row.Kind == ManifestCheckResultKind.RunnerDiffers);
        Assert.Equal(key, changed.ProjectPath);
        Assert.NotEqual(changed.LearnedRunner, changed.ManifestRunner);
        Assert.All(result.Where(row => row.ProjectPath != key), row => Assert.Equal(ManifestCheckResultKind.Match, row.Kind));
    }

    [Theory]
    [InlineData(0, "command", "pnpm", "npm ci")]
    [InlineData(2, "project", "subdir", "crosswalk-test .")]
    public void Compare_IdentifiableChangedCheck_ReportsInvocationDifference(
        int index, string property, string value, string key)
    {
        using var fixture = new Fixture();
        var model = fixture.Discover();
        var manifest = Emit(model);
        manifest["checks"]![index]![property] = value;

        var result = ManifestCheckComparer.Compare(model, manifest.ToJsonString());

        Assert.Equal(3, result.Count);
        Assert.Equal(key, Assert.Single(result, row => row.Kind == ManifestCheckResultKind.RunnerDiffers).ProjectPath);
    }

    [Theory]
    [InlineData(0, "type", "null", "npm ci")]
    [InlineData(2, "project", "{}", "crosswalk-test .")]
    [InlineData(2, "arguments", "null", "crosswalk-test .")]
    public void Compare_InvalidExecutionFields_NeverReportsMatch(int index, string property, string json, string key)
    {
        using var fixture = new Fixture();
        var model = fixture.Discover();
        var manifest = Emit(model);
        manifest["checks"]![index]![property] = JsonNode.Parse(json);

        var result = ManifestCheckComparer.Compare(model, manifest.ToJsonString());

        Assert.Equal(3, result.Count);
        Assert.Equal(ManifestCheckResultKind.MissingFromManifest, Assert.Single(result, row => row.ProjectPath == key).Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("./")]
    public void Compare_EquivalentRootProjects_MatchWholeSuiteCrosswalk(string project)
    {
        using var fixture = new Fixture();
        var model = fixture.Discover();
        var manifest = Emit(model);
        manifest["checks"]![2]!["project"] = project;

        var result = ManifestCheckComparer.Compare(model, manifest.ToJsonString());

        Assert.Equal(3, result.Count);
        Assert.All(result, row => Assert.Equal(ManifestCheckResultKind.Match, row.Kind));
    }

    [Fact]
    public void Compare_NoTypeScriptConfig_MatchesTwoChecksAndFlagsExtraCompiler()
    {
        using var fixture = new Fixture(hasTsconfig: false);
        var model = fixture.Discover();
        var manifest = Emit(model);

        var result = ManifestCheckComparer.Compare(model, manifest.ToJsonString());

        Assert.Equal(new[] { "crosswalk-test .", "npm ci" }, result.Select(row => row.ProjectPath));
        Assert.All(result, row => Assert.Equal(ManifestCheckResultKind.Match, row.Kind));
        manifest["checks"]!.AsArray().Add(JsonNode.Parse("""{"type":"command","command":"npx","arguments":["tsc","--noEmit"]}"""));
        var extra = ManifestCheckComparer.Compare(model, manifest.ToJsonString());
        Assert.Equal(3, extra.Count);
        Assert.Equal("npx tsc", Assert.Single(extra, row => row.Kind == ManifestCheckResultKind.MissingFromLearned).ProjectPath);
    }

    [Fact]
    public void Compare_UnsupportedFramework_ReportsOwnerQuestionAsUnresolved()
    {
        using var fixture = new Fixture("""{"devDependencies":{"jest":"30"}}""");
        var model = fixture.Discover();
        var question = Assert.Single(model.OwnerQuestions, item => item.FactKey == "environment/node-test-framework");

        var result = ManifestCheckComparer.Compare(model, """{"checks":[]}""");

        var row = Assert.Single(result);
        Assert.Equal(ManifestCheckResultKind.Unresolved, row.Kind);
        Assert.Equal(question.FactKey, row.ProjectPath);
        Assert.Equal(question.Question, row.Reason);
    }

    [Theory]
    [InlineData("[]", "The manifest must be an object.")]
    [InlineData("{\"checks\":{}}", "Manifest checks must be an array.")]
    [InlineData("{\"checks\":[1]}", "Each manifest check must be an object.")]
    public void Compare_MalformedEnvelope_ReportsExistingContract(string json, string message)
    {
        using var fixture = new Fixture();

        var error = Assert.Throws<JsonException>(() => ManifestCheckComparer.Compare(fixture.Discover(), json));

        Assert.Equal(message, error.Message);
    }

    private static JsonNode Emit(ProjectModel model)
    {
        var result = LearnedAcceptanceManifest.Build(model, Fixture.Answers());
        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        return JsonNode.Parse(result.Json)!;
    }
}
