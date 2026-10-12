using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: discovery owns its fixture copy; constructed models and answers are case-local.
public sealed class ManifestCheckComparerAnswersTests
{
    private const string Unit = "tests/Loose.UndeterminedTests/Loose.UndeterminedTests.csproj";
    private const string Key = "commands/" + Unit + "/test";

    [Fact]
    public void Compare_AnsweredVstest_MatchesManifest()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root, measuredKinds: UnitCommandKinds.None);
        var manifest = ManifestFor([Unit]);
        Assert.Equal(ManifestCheckResultKind.Unresolved,
            Assert.Single(ManifestCheckComparer.Compare(model, manifest)).Kind);
        var applied = OwnerAnswerApplier.Apply(model, [Answer(Key, "VSTest")]);
        Assert.Equal(Key, Assert.Single(applied.AnsweredFacts).FactKey);

        var result = Assert.Single(ManifestCheckComparer.Compare(model, manifest, applied));
        Assert.Equal(new ManifestCheckComparison(Unit, ManifestCheckResultKind.Match, "vstest", "vstest", ""), result);
    }

    [Fact]
    public void Compare_EmptyAnswers_EqualsExistingOverload()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root, measuredKinds: UnitCommandKinds.None);
        var manifest = ManifestFor([Unit]);
        var expected = ManifestCheckComparer.Compare(model, manifest);
        Assert.Equal(new ManifestCheckComparison(Unit, ManifestCheckResultKind.Unresolved, null, "vstest",
            $"Runner requires owner answer: {Key}"), Assert.Single(expected));

        Assert.Equal(expected, ManifestCheckComparer.Compare(model, manifest, new AppliedOwnerAnswers([], [], [])));
    }

    [Fact]
    public void Compare_LiveConflictingAnswer_PreservesConfidentRunner()
    {
        const string coreUnit = "tests/AnswerKey.Core.Tests/AnswerKey.Core.Tests.csproj";
        const string appUnit = "tests/AnswerKey.App.Tests/AnswerKey.App.Tests.csproj";
        const string key = "commands/" + coreUnit + "/test";
        var paths = new[] { coreUnit, appUnit };
        var source = new FactSource(measurementReference: "confident discovery");
        var units = paths.Select(path => new ProjectUnit(path,
            new ProjectFact<string>(Path.GetFileNameWithoutExtension(path), source, FactConfidence.High),
            new ProjectFact<string>(path, source, FactConfidence.High),
            new ProjectFact<bool?>(true, source, FactConfidence.High))).ToArray();
        var setups = paths.Select(path => new UnitTestSetup(path,
            new ProjectFact<string>("xunit", source, FactConfidence.High),
            new ProjectFact<string>("VSTest", source, FactConfidence.High))).ToArray();
        var model = new ProjectModel(ProjectModel.CurrentSchemaVersion, ".", units, [], setups,
            [new ProjectOwnerQuestion(key, "Which runner?", source)], [], [], [], []);
        var applied = OwnerAnswerApplier.Apply(model, [Answer(key, "MTP")]);
        Assert.Empty(applied.StaleAnswers);
        var answered = Assert.Single(applied.AnsweredFacts);
        Assert.Equal(key, answered.FactKey);
        Assert.Equal("MTP", answered.Fact.Value);
        var manifest = ManifestFor(paths);
        var expected = ManifestCheckComparer.Compare(model, manifest);
        Assert.Equal(2, expected.Count);
        Assert.All(expected, comparison => Assert.Equal(new ManifestCheckComparison(comparison.ProjectPath,
            ManifestCheckResultKind.Match, "vstest", "vstest", ""), comparison));

        Assert.Equal(expected, ManifestCheckComparer.Compare(model, manifest, applied));
    }

    private static OwnerAnswer Answer(string key, string value) =>
        new(key, value, "runner control", new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));

    private static string ManifestFor(string[] paths) => JsonSerializer.Serialize(new
    {
        checks = paths.Select(path => new { type = "dotnet-test", project = path, runner = "vstest" }).ToArray()
    });
}
