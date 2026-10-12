using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each invocation owns immutable in-memory model facts and answers.
public sealed class LearnedAcceptanceManifestCheckNameTests
{
    [Fact]
    public void Build_SharedFileNames_EmitsDistinctNamesAndPreservesProjects()
    {
        var model = Model("tests/C/Other.csproj", "tests/B/Tests.csproj", "tests/A/Tests.csproj");
        var result = LearnedAcceptanceManifest.Build(model, Answers());
        Assert.Empty(result.Blockers);
        Assert.Empty(result.OwnerQuestions);
        Assert.NotNull(result.Json);
        using var document = JsonDocument.Parse(result.Json);
        var checks = document.RootElement.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Equal(new[] { "tests/A/Tests.csproj", "tests/B/Tests.csproj", "tests/C/Other.csproj" },
            checks.Select(check => check.GetProperty("project").GetString()));
        var names = checks.Select(check => check.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(new[] { "tests.A.Tests", "tests.B.Tests", "Other" }, names);
        Assert.Equal(3, names.Select(AcceptanceArtifactFileNames.Slug).Distinct(StringComparer.Ordinal).Count());
        var settings = AcceptanceGateEngineSettings.Parse(result.Json);
        Assert.Equal(TimeSpan.FromMinutes(17), settings.ResolveCheckTimeout(null));
        Assert.Equal(TimeSpan.FromMinutes(3), settings.ResolveBuildServerShutdownTimeout());
        Assert.Equal(TimeSpan.FromMinutes(4), settings.ResolveDiscoveryTimeout());
    }

    [Fact]
    public void Build_UnresolvedNameCollision_RefusesUntilOneUnitIsRemoved()
    {
        var result = LearnedAcceptanceManifest.Build(Model("tests/a/b/X.csproj", "tests/a-b/X.csproj"), Answers());
        Assert.Null(result.Json);
        Assert.Empty(result.OwnerQuestions);
        var blocker = Assert.Single(result.Blockers);
        Assert.Equal("checks/names", blocker.FactKey);
        Assert.Contains("tests/a-b/X.csproj", blocker.Reason);
        Assert.Contains("tests/a/b/X.csproj", blocker.Reason);
        Assert.True(blocker.Reason.IndexOf("tests/a-b/X.csproj", StringComparison.Ordinal) <
            blocker.Reason.IndexOf("tests/a/b/X.csproj", StringComparison.Ordinal));

        var reduced = LearnedAcceptanceManifest.Build(Model("tests/a/b/X.csproj"), Answers());
        Assert.NotNull(reduced.Json);
        Assert.Empty(reduced.Blockers);
        using var document = JsonDocument.Parse(reduced.Json);
        var check = Assert.Single(document.RootElement.GetProperty("checks").EnumerateArray());
        Assert.Equal("X", check.GetProperty("name").GetString());
        Assert.Equal("tests/a/b/X.csproj", check.GetProperty("project").GetString());
    }

    [Fact]
    public void Build_NameCollisionAndMissingTimeouts_PreservesExistingBlockerOrder()
    {
        var result = LearnedAcceptanceManifest.Build(Model("tests/a-b/X.csproj", "tests/a/b/X.csproj"), []);
        Assert.Null(result.Json);
        Assert.Equal(new[] { "engine/timeouts/defaultMinutes", "engine/timeouts/buildServerShutdownMinutes",
            "engine/timeouts/discoveryMinutes", "checks/names" }, result.Blockers.Select(blocker => blocker.FactKey));
    }

    [Fact]
    public void Build_UniqueFileNames_MatchesPreMtpJsonBytes()
    {
        // Expected text copied unchanged from LearnedAcceptanceManifestTests' pre-MTP baseline.
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
        const string a = "tests\\A.Tests\\A.Tests.csproj";
        const string b = "tests\\B.Tests\\B.Tests.csproj";
        var model = new ProjectModel(ProjectModel.CurrentSchemaVersion, ".",
            new[] { b, a }.Select(id => new ProjectUnit(id, Fact(id), Fact(id), Fact<bool?>(true))).ToArray(), [],
            [new(b, Fact("xunit"), Fact("VSTest")), new(a, Fact("xunit"), Fact("VSTest"))], [],
            new[] { b, a }.Select(id => new UnitCommands(id, Fact("build"), Fact("test"))).ToArray(), [],
            [new(b, null, Fact(200d)), new(a, null, Fact(100d))], []);
        var result = LearnedAcceptanceManifest.Build(model, Answers());
        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(expected.Replace("\r\n", "\n") + "\n"),
            System.Text.Encoding.UTF8.GetBytes(result.Json));
    }

    private static ProjectModel Model(params string[] paths) => new(ProjectModel.CurrentSchemaVersion, ".",
        paths.Select(id => new ProjectUnit(id, Fact(id), Fact(id), Fact<bool?>(true))).ToArray(), [],
        paths.Select(id => new UnitTestSetup(id, Fact("xunit"), Fact("VSTest"))).ToArray(), [],
        paths.Select(id => new UnitCommands(id, Fact("build"), Fact("test"))).ToArray(), [], [], []);

    private static FactSource Source() => new(measurementReference: "fixture");
    private static ProjectFact<T> Fact<T>(T value) => new(value, Source(), FactConfidence.High);
    private static OwnerAnswer Answer(string key, string value) =>
        new(key, value, "owner choice", new DateTimeOffset(2026, 10, 11, 12, 34, 56, TimeSpan.Zero));
    private static OwnerAnswer[] Answers() => [Answer("engine/timeouts/defaultMinutes", "17"),
        Answer("engine/timeouts/buildServerShutdownMinutes", "3"), Answer("engine/timeouts/discoveryMinutes", "4")];
}
