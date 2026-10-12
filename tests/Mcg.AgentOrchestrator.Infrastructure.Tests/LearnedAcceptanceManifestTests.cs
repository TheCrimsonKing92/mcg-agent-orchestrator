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
        Assert.Equal(new[] { $"commands/{B}/test", ShutdownKey, DiscoveryKey },
            result.Blockers.Select(blocker => blocker.FactKey));
        Assert.DoesNotContain(result.OwnerQuestions, question => question.FactKey == "engine/mtpInvocations");
    }

    [Fact]
    public void Build_MultipleMtpChecks_EmitsInvocationsInProjectOrder()
    {
        var model = Model() with { TestSetups = [new(B, Fact("xunit"), Fact("MTP")), new(A, Fact("xunit"), Fact("MTP"))] };
        var result = LearnedAcceptanceManifest.Build(model, Answers());
        Assert.NotNull(result.Json);
        Assert.Empty(result.Blockers);
        Assert.Empty(result.OwnerQuestions);
        using var document = JsonDocument.Parse(result.Json);
        var projects = document.RootElement.GetProperty("engine").GetProperty("mtpInvocations")
            .EnumerateArray().Select(invocation => invocation.GetProperty("project").GetString()).ToArray();
        Assert.Equal(new[] { A.Replace('\\', '/'), B.Replace('\\', '/') }, projects);
        Assert.Equal(document.RootElement.GetProperty("checks").EnumerateArray()
            .Select(check => check.GetProperty("project").GetString()), projects);
    }

    [Fact]
    public void Build_OneMtpCheck_EmitsOneInvocation()
    {
        var model = Model() with { TestSetups = [new(B, Fact("xunit"), Fact("VSTest")), new(A, Fact("xunit"), Fact("MTP"))] };
        var result = LearnedAcceptanceManifest.Build(model, Answers());
        Assert.NotNull(result.Json);
        Assert.Empty(result.Blockers);
        using var document = JsonDocument.Parse(result.Json);
        var invocation = Assert.Single(document.RootElement.GetProperty("engine").GetProperty("mtpInvocations").EnumerateArray());
        Assert.Equal(A.Replace('\\', '/'), invocation.GetProperty("project").GetString());
    }

    [Fact]
    public void Build_RefusedMtpLayout_BlocksWithSuppliedReason()
    {
        var model = Model() with { TestSetups = [new(B, Fact("xunit"), Fact("MTP")), new(A, Fact("xunit"), Fact("MTP"))] };
        const string reason = "MTP project tests/A.Tests/A.Tests.csproj: file Directory.Build.props declares layout element OutputPath";
        var result = LearnedAcceptanceManifest.Build(model, Answers(),
            new Dictionary<string, string> { [A.Replace('\\', '/')] = reason });
        Assert.Null(result.Json);
        var blocker = Assert.Single(result.Blockers);
        Assert.Equal("engine/mtpInvocations", blocker.FactKey);
        Assert.Equal(reason, blocker.Reason);
        Assert.Empty(result.OwnerQuestions);
    }

    [Fact]
    public void Build_VstestOnly_MatchesPreMtpJsonBytes()
    {
        // Baseline from the existing VSTest composition: two shards and a measured ten-minute timeout.
        const string expected = """
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "enforceStructuralCoverage": false,
                "timeouts": {
                  "defaultMinutes": 10,
                  "buildServerShutdownMinutes": 3,
                  "discoveryMinutes": 4
                }
              },
              "checks": [
                {
                  "name": "A.Tests",
                  "type": "dotnet-test",
                  "runner": "vstest",
                  "project": "tests/A.Tests/A.Tests.csproj",
                  "arguments": []
                },
                {
                  "name": "B.Tests",
                  "type": "dotnet-test",
                  "runner": "vstest",
                  "project": "tests/B.Tests/B.Tests.csproj",
                  "arguments": []
                }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """;
        var result = LearnedAcceptanceManifest.Build(Model(), Answers());
        Assert.NotNull(result.Json);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(expected.Replace("\r\n", "\n") + "\n"),
            System.Text.Encoding.UTF8.GetBytes(result.Json));
        Assert.Equal(result.Json, LearnedAcceptanceManifest.Build(Model(), Answers().Reverse().ToArray()).Json);
        using var document = JsonDocument.Parse(result.Json);
        Assert.False(document.RootElement.GetProperty("engine").TryGetProperty("mtpInvocations", out _));
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

    [Fact]
    public void Build_OneUnitTimeout_EmitsNumberOnlyOnAnsweredCheck()
    {
        var result = LearnedAcceptanceManifest.Build(Model(),
            [.. Answers(), Answer($"commands/{A}/timeoutMinutes", "025")]);
        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        using var document = JsonDocument.Parse(result.Json);
        var checks = document.RootElement.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Equal(2, checks.Length);
        var answered = Assert.Single(checks.Where(check => check.GetProperty("project").GetString() == A.Replace('\\', '/')));
        Assert.Equal(25, answered.GetProperty("timeoutMinutes").GetInt32());
        Assert.Equal(new[] { "name", "type", "runner", "project", "timeoutMinutes", "arguments" }, Keys(answered));
        var unanswered = Assert.Single(checks.Where(check => check.GetProperty("project").GetString() == B.Replace('\\', '/')));
        Assert.False(unanswered.TryGetProperty("timeoutMinutes", out _));
        Assert.Equal(new[] { "name", "type", "runner", "project", "arguments" }, Keys(unanswered));
    }

    [Fact]
    public void Build_NoUnitTimeout_PreservesDeterministicBaselineBytes()
    {
        const string expected = """
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "enforceStructuralCoverage": false,
                "timeouts": {
                  "defaultMinutes": 10,
                  "buildServerShutdownMinutes": 3,
                  "discoveryMinutes": 4
                }
              },
              "checks": [
                {
                  "name": "A.Tests",
                  "type": "dotnet-test",
                  "runner": "vstest",
                  "project": "tests/A.Tests/A.Tests.csproj",
                  "arguments": []
                },
                {
                  "name": "B.Tests",
                  "type": "dotnet-test",
                  "runner": "vstest",
                  "project": "tests/B.Tests/B.Tests.csproj",
                  "arguments": []
                }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """;
        var result = LearnedAcceptanceManifest.Build(Model(), Answers());
        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(expected.Replace("\r\n", "\n") + "\n"),
            System.Text.Encoding.UTF8.GetBytes(result.Json));
        var reordered = LearnedAcceptanceManifest.Build(Model(), Answers().Reverse().ToArray());
        Assert.NotNull(reordered.Json);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(result.Json), System.Text.Encoding.UTF8.GetBytes(reordered.Json));
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
