using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each invocation owns immutable in-memory inputs.
public sealed class LearnedAcceptanceManifestTests
{
    private const string DefaultKey = "engine/timeouts/defaultMinutes";
    private const string ShutdownKey = "engine/timeouts/buildServerShutdownMinutes";
    private const string DiscoveryKey = "engine/timeouts/discoveryMinutes";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_SettledFacts_EmitsOnlyConsumedKeysInProjectOrder(bool measured)
    {
        var model = Model(measured);
        var result = LearnedAcceptanceManifest.Build(model, Answers());
        Assert.Empty(result.Blockers);
        Assert.Empty(result.OwnerQuestions);
        Assert.NotNull(result.Json);
        using var document = JsonDocument.Parse(result.Json);
        var root = document.RootElement;
        Assert.Equal(new[] { "version", "engine", "checks", "forbiddenChangedPathGlobs" }, Keys(root));
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Empty(root.GetProperty("forbiddenChangedPathGlobs").EnumerateArray());
        var engine = root.GetProperty("engine");
        Assert.Equal(new[] { "maxConcurrentShards", "enforceStructuralCoverage", "timeouts" }, Keys(engine));
        Assert.Equal(LearnedEngineSettingsDeriver.Derive(model).MaxConcurrentShards,
            engine.GetProperty("maxConcurrentShards").GetInt32());
        Assert.False(engine.GetProperty("enforceStructuralCoverage").GetBoolean());
        var timeouts = engine.GetProperty("timeouts");
        Assert.Equal(new[] { "defaultMinutes", "buildServerShutdownMinutes", "discoveryMinutes" }, Keys(timeouts));
        Assert.Equal(measured ? LearnedEngineSettingsDeriver.Derive(model).DefaultTimeoutMinutes : 17,
            timeouts.GetProperty("defaultMinutes").GetInt32());
        Assert.Equal(3, timeouts.GetProperty("buildServerShutdownMinutes").GetInt32());
        Assert.Equal(4, timeouts.GetProperty("discoveryMinutes").GetInt32());
        var checks = root.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Equal(new[] { "tests/A.Tests/A.Tests.csproj", "tests/B.Tests/B.Tests.csproj" },
            checks.Select(check => check.GetProperty("project").GetString()));
        Assert.All(checks, check =>
        {
            Assert.Equal(new[] { "name", "type", "runner", "project", "arguments" }, Keys(check));
            var path = check.GetProperty("project").GetString()!;
            Assert.DoesNotContain('\\', path);
            Assert.Equal(Path.GetFileNameWithoutExtension(path), check.GetProperty("name").GetString());
            Assert.Equal("dotnet-test", check.GetProperty("type").GetString());
            Assert.Equal("vstest", check.GetProperty("runner").GetString());
            Assert.Empty(check.GetProperty("arguments").EnumerateArray());
        });
    }

    [Fact]
    public void Build_RepeatedAndReorderedAnswers_ProducesIdenticalLfJson()
    {
        var model = Model();
        var answers = Answers();
        var json = LearnedAcceptanceManifest.Build(model, answers).Json;
        Assert.NotNull(json);
        Assert.Equal(json, LearnedAcceptanceManifest.Build(model, answers).Json);
        Assert.Equal(json, LearnedAcceptanceManifest.Build(model, answers.Reverse().ToArray()).Json);
        Assert.EndsWith("\n", json);
        Assert.DoesNotContain('\r', json);
        Assert.False(json.EndsWith("\n\n", StringComparison.Ordinal));
        Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}|\d{2}:\d{2}:\d{2}", json);
        Assert.Contains("\n  \"version\": 1,\n", json);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_MissingAnswers_BlocksOnlyUnsettledEngineQuestions(bool measured)
    {
        var model = Model(measured);
        var expected = measured ? new[] { ShutdownKey, DiscoveryKey }
            : new[] { DefaultKey, ShutdownKey, DiscoveryKey };
        var result = LearnedAcceptanceManifest.Build(model, []);
        Assert.Null(result.Json);
        Assert.Equal(expected, result.Blockers.Select(blocker => blocker.FactKey));
        Assert.Equal(expected, result.OwnerQuestions.Select(question => question.FactKey));
        foreach (var key in expected)
        {
            result = LearnedAcceptanceManifest.Build(model, [Answer(key, "8")]);
            Assert.Null(result.Json);
            Assert.Equal(expected.Where(candidate => candidate != key), result.Blockers.Select(blocker => blocker.FactKey));
        }
    }

    [Theory]
    [InlineData("Unknown", FactConfidence.High)]
    [InlineData("VSTest", FactConfidence.Low)]
    public void Build_UnconfidentRunner_BlocksUnitBeforeEngineFacts(string runner, FactConfidence confidence)
    {
        var model = Model() with
        {
            TestSetups = [new(B, Fact("xunit"), new(runner, Source(), confidence)),
                new(A, Fact("xunit"), Fact("MTP"))]
        };
        var result = LearnedAcceptanceManifest.Build(model, []);
        Assert.Null(result.Json);
        Assert.Equal(new[] { $"commands/{B}/test", "engine/mtpInvocations", ShutdownKey, DiscoveryKey },
            result.Blockers.Select(blocker => blocker.FactKey));
        Assert.Contains(A.Replace('\\', '/'), result.Blockers[1].Reason);
        Assert.DoesNotContain(result.OwnerQuestions, question => question.FactKey == "engine/mtpInvocations");
    }

    [Fact]
    public void Build_MultipleMtpChecks_BlocksEachInvocationEvenWithAllAnswers()
    {
        var model = Model() with { TestSetups = [new(B, Fact("xunit"), Fact("MTP")), new(A, Fact("xunit"), Fact("MTP"))] };
        var result = LearnedAcceptanceManifest.Build(model, Answers());
        Assert.Null(result.Json);
        Assert.Equal(new[] { "engine/mtpInvocations", "engine/mtpInvocations" },
            result.Blockers.Select(blocker => blocker.FactKey));
        Assert.Contains(B.Replace('\\', '/'), result.Blockers[0].Reason);
        Assert.Contains(A.Replace('\\', '/'), result.Blockers[1].Reason);
        Assert.Empty(result.OwnerQuestions);
    }

    [Fact]
    public void Build_NonManifestQuestions_DoNotBlockEmission()
    {
        var model = Model() with { OwnerQuestions = [new("environment/dotnet-sdk", "SDK?", Source()),
            new("integration-branch", "Branch?", Source())] };
        var result = LearnedAcceptanceManifest.Build(model, Answers());
        Assert.NotNull(result.Json);
        Assert.Empty(result.Blockers);
        Assert.Empty(result.OwnerQuestions);
    }

    [Fact]
    public void Build_InvalidAndStaleAnswers_PreservesBlockerAndMeasuredDefault()
    {
        var answers = new[] { Answer(DefaultKey, "17"), Answer(ShutdownKey, "3"),
            Answer(DiscoveryKey, "latest"), Answer("integration-branch", "removed-branch") };
        var result = LearnedAcceptanceManifest.Build(Model(), answers);
        Assert.Null(result.Json);
        Assert.Equal(DiscoveryKey, Assert.Single(result.Blockers).FactKey);
        Assert.DoesNotContain("latest", result.Blockers[0].Reason);
        Assert.Equal(DiscoveryKey, Assert.Single(result.OwnerQuestions).FactKey);
        var settled = LearnedAcceptanceManifest.Build(Model(), [.. answers, Answer(DiscoveryKey, "4")]);
        Assert.NotNull(settled.Json);
        using var document = JsonDocument.Parse(settled.Json);
        Assert.Equal(LearnedEngineSettingsDeriver.Derive(Model()).DefaultTimeoutMinutes,
            document.RootElement.GetProperty("engine").GetProperty("timeouts").GetProperty("defaultMinutes").GetInt32());
        Assert.DoesNotContain("latest", settled.Json);
        Assert.DoesNotContain("removed-branch", settled.Json);
    }

    private const string A = "tests\\A.Tests\\A.Tests.csproj";
    private const string B = "tests\\B.Tests\\B.Tests.csproj";
    private static ProjectModel Model(bool measured = true) => new(ProjectModel.CurrentSchemaVersion, ".",
        new[] { B, A }.Select(id => new ProjectUnit(id, Fact(id), Fact(id), Fact<bool?>(true))).ToArray(), [],
        [new(B, Fact("xunit"), Fact("VSTest")), new(A, Fact("xunit"), Fact("VSTest"))], [],
        new[] { B, A }.Select(id => new UnitCommands(id, Fact("build"), Fact("test"))).ToArray(), [],
        measured ? [new(B, null, Fact(200d)), new(A, null, Fact(100d))] : [], []);
    private static FactSource Source() => new(measurementReference: "fixture");
    private static ProjectFact<T> Fact<T>(T value) => new(value, Source(), FactConfidence.High);
    private static OwnerAnswer Answer(string key, string value) =>
        new(key, value, "owner choice", new DateTimeOffset(2026, 10, 11, 12, 34, 56, TimeSpan.Zero));
    private static OwnerAnswer[] Answers() => [Answer(DefaultKey, "17"), Answer(ShutdownKey, "3"), Answer(DiscoveryKey, "4")];
    private static string[] Keys(JsonElement element) => element.EnumerateObject().Select(property => property.Name).ToArray();
}
