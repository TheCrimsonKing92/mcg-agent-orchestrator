using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json.Nodes;
using Xunit;

// Parallel-safe: every discovery and manifest variant belongs to an isolated fixture copy.
public sealed class ManifestCheckComparerTests
{
    private const string AppTest = "tests/AnswerKey.App.Tests/AnswerKey.App.Tests.csproj";
    private const string CoreTest = "tests/AnswerKey.Core.Tests/AnswerKey.Core.Tests.csproj";

    [Fact]
    public void AnswerKey_NormalizedPaths_ReturnSameOrderedMatches()
    {
        using var fixture = new ProjectOnboardingFixture("answer-key-repository");
        var model = Discover(fixture);
        Assert.Equal(4, model.Units.Count);
        Assert.Equal(2, model.Units.Count(unit => unit.IsTest.Value == true));
        var derived = LearnedTestCheckDeriver.Derive(model);
        Assert.Empty(derived.Unresolved);
        Assert.Equal(new[] { AppTest, CoreTest }, derived.Checks.Select(check => check.ProjectPath));
        foreach (var check in derived.Checks)
        {
            var fact = model.TestSetups.Single(setup => setup.UnitId == check.ProjectPath).Runner;
            Assert.Equal("vstest", check.Runner);
            Assert.Equal(FactConfidence.High, check.Confidence);
            Assert.Same(fact.Source, check.Source);
        }

        var manifest = ReadManifest(fixture);
        var expected = new[]
        {
            new ManifestCheckComparison(AppTest, ManifestCheckResultKind.Match, "vstest", "vstest", ""),
            new ManifestCheckComparison(CoreTest, ManifestCheckResultKind.Match, "vstest", "vstest", "")
        };
        Assert.Equal(expected, ManifestCheckComparer.Compare(model, WriteManifest(fixture, manifest)));
        foreach (var check in manifest["checks"]!.AsArray())
        {
            check!["project"] = " ./" + check["project"]!.GetValue<string>().Replace('/', '\\').ToUpperInvariant() + " ";
            check["type"] = "DOTNET-TEST";
        }
        manifest["checks"]![0]!["runner"] = " VSTEST ";
        Assert.Equal(expected, ManifestCheckComparer.Compare(model, WriteManifest(fixture, manifest)));
        manifest["checks"]![0]!["name"] = "app-tests";
        manifest["checks"]![1]!["name"] = "core-tests";
        Assert.Equal(expected, ManifestCheckComparer.Compare(model, WriteManifest(fixture, manifest)));
    }

    [Fact]
    public void AnswerKey_ChangedRunner_ReturnsRunnerDiffers()
    {
        using var fixture = new ProjectOnboardingFixture("answer-key-repository");
        var manifest = ReadManifest(fixture);
        manifest["checks"]![0]!["runner"] = "mtp";
        var results = ManifestCheckComparer.Compare(Discover(fixture), WriteManifest(fixture, manifest));
        Assert.Equal(2, results.Count);
        Assert.Equal(new ManifestCheckComparison(CoreTest, ManifestCheckResultKind.RunnerDiffers,
            "vstest", "mtp", "The learned and manifest runners differ."), results[1]);
        Assert.Equal(ManifestCheckResultKind.Match, results[0].Kind);
    }

    [Theory]
    [InlineData("src/AnswerKey.Core/AnswerKey.Core.csproj")]
    [InlineData("tests/Nope/Nope.csproj")]
    public void AnswerKey_ExtraProject_ReturnsMissingFromLearned(string path)
    {
        using var fixture = new ProjectOnboardingFixture("answer-key-repository");
        var manifest = ReadManifest(fixture);
        manifest["checks"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "core-tests", ["type"] = "dotnet-test", ["project"] = path
        });
        var results = ManifestCheckComparer.Compare(Discover(fixture), WriteManifest(fixture, manifest));
        Assert.Equal(3, results.Count);
        var missing = Assert.Single(results, result => result.ProjectPath == path);
        Assert.Equal(ManifestCheckResultKind.MissingFromLearned, missing.Kind);
        Assert.Null(missing.LearnedRunner);
        Assert.Equal("vstest", missing.ManifestRunner);
        Assert.Equal(2, results.Count(result => result.Kind == ManifestCheckResultKind.Match));
    }

    [Fact]
    public void AnswerKey_RemovedCheck_ReturnsMissingFromManifest()
    {
        using var fixture = new ProjectOnboardingFixture("answer-key-repository");
        var manifest = ReadManifest(fixture);
        manifest["checks"]!.AsArray().RemoveAt(1);
        var results = ManifestCheckComparer.Compare(Discover(fixture), WriteManifest(fixture, manifest));
        Assert.Equal(2, results.Count);
        Assert.Equal(AppTest, results[0].ProjectPath);
        Assert.Equal(ManifestCheckResultKind.MissingFromManifest, results[0].Kind);
        Assert.Equal("vstest", results[0].LearnedRunner);
        Assert.Null(results[0].ManifestRunner);
        Assert.Equal(ManifestCheckResultKind.Match, results[1].Kind);
    }

    [Fact]
    public void NoSolution_UndeterminedRunner_ReturnsOwnerQuestionInsteadOfCheck()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        var model = Discover(fixture);
        var derived = LearnedTestCheckDeriver.Derive(model);
        Assert.Empty(derived.Checks);
        var unresolved = Assert.Single(derived.Unresolved);
        Assert.Equal("tests/Loose.UndeterminedTests/Loose.UndeterminedTests.csproj", unresolved.UnitId);
        Assert.Equal($"commands/{unresolved.UnitId}/test", unresolved.FactKey);
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == unresolved.FactKey);
        using var answerKey = new ProjectOnboardingFixture("answer-key-repository");
        var manifest = ReadManifest(answerKey);
        var checks = manifest["checks"]!.AsArray();
        checks[0]!["project"] = unresolved.UnitId;
        checks[1]!["project"] = "src/Loose.Library/Loose.Library.csproj";
        var results = ManifestCheckComparer.Compare(model, WriteManifest(answerKey, manifest));
        Assert.Equal(2, results.Count);
        Assert.Equal(ManifestCheckResultKind.MissingFromLearned, results[0].Kind);
        Assert.Equal(ManifestCheckResultKind.Unresolved, results[1].Kind);
        Assert.Contains(unresolved.FactKey, results[1].Reason);
        Assert.Null(results[1].LearnedRunner);
        Assert.Equal("vstest", results[1].ManifestRunner);
        Assert.DoesNotContain(results, result => result.Kind == ManifestCheckResultKind.Match);
        checks.Clear();
        Assert.Empty(ManifestCheckComparer.Compare(model, WriteManifest(answerKey, manifest)));
    }

    [Theory]
    [InlineData(FactConfidence.High, "VSTest", true)]
    [InlineData(FactConfidence.Medium, "VSTest", true)]
    [InlineData(FactConfidence.Medium, "MTP", true)]
    [InlineData(FactConfidence.High, "MTP", true)]
    [InlineData(FactConfidence.Low, "VSTest", false)]
    [InlineData(FactConfidence.High, "undetermined", false)]
    public void Deriver_ConfidenceAndRunner_UsesOnlySupportedConfidentFacts(
        FactConfidence confidence, string runner, bool resolved)
    {
        using var fixture = new ProjectOnboardingFixture("answer-key-repository");
        var model = Discover(fixture);
        var setup = model.TestSetups.Single(setup => setup.UnitId == CoreTest);
        var fact = new ProjectFact<string>(runner, setup.Runner.Source, confidence);
        model = model with
        {
            Units = model.Units.Where(unit => unit.Id == CoreTest).ToArray(),
            TestSetups = [setup with { Runner = fact }], OwnerQuestions = []
        };
        // Break the declarations after discovery: derivation must use only the snapshot.
        File.WriteAllText(Path.Combine(fixture.Root, CoreTest), "not a project anymore");
        var derived = LearnedTestCheckDeriver.Derive(model);
        if (resolved)
        {
            var check = Assert.Single(derived.Checks);
            Assert.Equal(runner.ToLowerInvariant(), check.Runner);
            Assert.Equal(confidence, check.Confidence);
            Assert.Same(fact.Source, check.Source);
            Assert.Empty(derived.Unresolved);
        }
        else
        {
            Assert.Empty(derived.Checks);
            Assert.Equal(new UnresolvedTestUnit(CoreTest, $"commands/{CoreTest}/test"), Assert.Single(derived.Unresolved));
        }
    }

    [Fact]
    public void Deriver_MissingSetupOrUnknownTestStatus_DoesNotInventCheck()
    {
        using var fixture = new ProjectOnboardingFixture("answer-key-repository");
        var model = Discover(fixture);
        var unit = model.Units.Single(unit => unit.Id == CoreTest);
        model = model with { Units = [unit], TestSetups = [] };
        Assert.Empty(LearnedTestCheckDeriver.Derive(model).Checks);
        Assert.Equal(new UnresolvedTestUnit(CoreTest, $"commands/{CoreTest}/test"),
            Assert.Single(LearnedTestCheckDeriver.Derive(model).Unresolved));
        model = model with { Units = [unit with { IsTest = new(null, unit.IsTest.Source, FactConfidence.Low) }] };
        var derived = LearnedTestCheckDeriver.Derive(model);
        Assert.Empty(derived.Checks);
        Assert.Empty(derived.Unresolved);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("absent")]
    [InlineData("command")]
    [InlineData("no-project")]
    public void NoTests_IrrelevantOrEmptyManifest_ReturnsNoComparisons(string variant)
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        Directory.Delete(Path.Combine(fixture.Root, "tests"), recursive: true);
        var model = Discover(fixture);
        Assert.Single(model.Units);
        Assert.All(model.Units, unit => Assert.False(unit.IsTest.Value));
        using var answerKey = new ProjectOnboardingFixture("answer-key-repository");
        var manifest = ReadManifest(answerKey);
        var checks = manifest["checks"]!.AsArray();
        if (variant is "empty" or "absent") checks.Clear();
        if (variant == "absent") manifest.Remove("checks");
        foreach (var check in checks)
        {
            if (variant == "command") check!["type"] = "command";
            if (variant == "no-project") check!.AsObject().Remove("project");
        }
        Assert.Empty(ManifestCheckComparer.Compare(model, WriteManifest(answerKey, manifest)));
    }

    [Fact]
    public void AnswerKey_DuplicateProject_UsesFirstRunnerAndNotesConflict()
    {
        using var fixture = new ProjectOnboardingFixture("answer-key-repository");
        var manifest = ReadManifest(fixture);
        var duplicate = manifest["checks"]![0]!.DeepClone();
        duplicate["project"] = CoreTest.ToUpperInvariant().Replace('/', '\\');
        duplicate["runner"] = "mtp";
        manifest["checks"]!.AsArray().Add(duplicate);
        var results = ManifestCheckComparer.Compare(Discover(fixture), WriteManifest(fixture, manifest));
        Assert.Equal(2, results.Count);
        Assert.Equal(ManifestCheckResultKind.Match, results[1].Kind);
        Assert.Equal("vstest", results[1].ManifestRunner);
        Assert.Contains("Later duplicate", results[1].Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AnswerKey_BlankRunner_DefaultsToVstest(string? runner)
    {
        using var fixture = new ProjectOnboardingFixture("answer-key-repository");
        var manifest = ReadManifest(fixture);
        manifest["checks"]![0]!["runner"] = runner;
        var results = ManifestCheckComparer.Compare(Discover(fixture), WriteManifest(fixture, manifest));
        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(ManifestCheckResultKind.Match, result.Kind);
            Assert.Equal("vstest", result.ManifestRunner);
        });
    }

    private static ProjectModel Discover(ProjectOnboardingFixture fixture) =>
        new DotnetProjectDiscoveryAdapter().Discover(fixture.Root, measuredKinds: UnitCommandKinds.None);

    private static JsonObject ReadManifest(ProjectOnboardingFixture fixture) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Root, "hand-acceptance-manifest.json")))!.AsObject();

    private static string WriteManifest(ProjectOnboardingFixture fixture, JsonObject manifest)
    {
        var path = Path.Combine(fixture.Root, "hand-acceptance-manifest.json");
        File.WriteAllText(path, manifest.ToJsonString());
        return File.ReadAllText(path);
    }
}
