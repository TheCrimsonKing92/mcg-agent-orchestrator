using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns all repositories, receipt files and attempt metadata.
public sealed class GateTestObligationGoPackageResolutionTests
{
    [Fact]
    public void OneMatchingChangedPackage_SatisfiesBareSelectorWithProvenance()
    {
        using var repo = ChangedRepository("a/run_test.go", "unrelated/run_test.go");
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, ["TestRun"]);
        var candidate = Head(repo);
        var receipt = WriteGoReceipt(fixture.Root);
        var (source, _, _) = fixture.WriteAttempt([], candidateSha: candidate, additionalPaths: [receipt]);
        var baseline = Evaluate(goal, fixture, candidate);
        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, baseline.Outcome);

        var result = Evaluate(goal, fixture, candidate, repo.Root);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(new[] { "TestRun" }, result.Selectors);
        Assert.Equal(source, result.SourceId);
        Assert.Equal("a", Assert.Single(result.PackageResolutions!).Directory);
        Assert.Equal($"GATE_TEST_OBLIGATION_PACKAGE_RESOLVED id={Assert.Single(goal.GateTestObligations).Id[..8]} " +
            $"selector=TestRun package=example.com/m/a directory=a source={source}",
            Assert.Single(result.PackageResolutionProvenance));
    }

    [Theory]
    [InlineData("both")]
    [InlineData("neither")]
    [InlineData("unavailable")]
    [InlineData("case-mismatch")]
    public void NoUniqueChangedPackage_PreservesAmbiguity(string scenario)
    {
        using var repo = ChangedRepository(scenario switch
        {
            "both" => ["a/run_test.go", "b/run_test.go"],
            "case-mismatch" => ["A/run_test.go"],
            _ => ["a/run.go", "other/run_test.go"]
        });
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, ["TestRun"]);
        var candidate = Head(repo);
        fixture.WriteAttempt([], candidateSha: candidate, additionalPaths: [WriteGoReceipt(fixture.Root)]);

        var result = Evaluate(goal, fixture, candidate, scenario == "unavailable" ? fixture.Root : repo.Root);

        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Equal(new[] { "TestRun (ambiguous: example.com/m/a, example.com/m/b)" }, result.Selectors);
        Assert.Empty(result.PackageResolutionProvenance);
        Assert.Equal(Evaluate(goal, fixture, candidate).SourceId, result.SourceId);
    }

    [Theory]
    [InlineData("Failed", "Passed", "Failed")]
    [InlineData("Passed", "Failed", "Satisfied")]
    [InlineData("NotExecuted", "Passed", "Failed")]
    public void SelectedPackage_IsGradedIndependentlyOfOtherPackage(
        string selectedOutcome, string otherOutcome, string expected)
    {
        using var repo = ChangedRepository("a/run_test.go");
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, ["TestRun"]);
        var candidate = Head(repo);
        var receipt = WriteGoReceipt(fixture.Root);
        ChangeMethod(receipt, "example.com/m/a", resultOutcome: selectedOutcome);
        ChangeMethod(receipt, "example.com/m/b", resultOutcome: otherOutcome);
        fixture.WriteAttempt([], candidateSha: candidate, additionalPaths: [receipt]);

        var result = Evaluate(goal, fixture, candidate, repo.Root);

        Assert.Equal(expected, result.Outcome.ToString());
        Assert.Equal(new[] { "TestRun" }, result.Selectors);
        Assert.Equal("example.com/m/a", Assert.Single(result.PackageResolutions!).Package);
        Assert.Single(result.PackageResolutionProvenance);
    }

    [Theory]
    [InlineData("a.TestRun")]
    [InlineData("example.com/m/a.TestRun")]
    [InlineData("ExistingTests")]
    public void QualifiedGoAndCSharpSelectors_KeepExistingGrading(string selector)
    {
        using var repo = ChangedRepository("a/run_test.go");
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, [selector]);
        var candidate = Head(repo);
        fixture.WriteAttempt([("ExistingTests.Check", "Passed")], candidateSha: candidate,
            additionalPaths: [WriteGoReceipt(fixture.Root)]);

        var result = Evaluate(goal, fixture, candidate, repo.Root);
        var baseline = Evaluate(goal, fixture, candidate);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(baseline.Outcome, result.Outcome);
        Assert.Equal(baseline.SourceId, result.SourceId);
        Assert.Equal(baseline.Selectors, result.Selectors);
        Assert.Empty(result.PackageResolutionProvenance);
    }

    [Theory]
    [InlineData("./a//nested/../")]
    [InlineData("a\\nested\\..\\")]
    [InlineData("absolute")]
    public void NormalizedCodeBase_SelectsExactDirectory(string codeBase)
    {
        using var repo = ChangedRepository("a/run_test.go");
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, ["TestRun"]);
        var receipt = WriteGoReceipt(fixture.Root);
        ChangeMethod(receipt, "example.com/m/a", codeBase == "absolute" ? Path.Combine(repo.Root, "a") : codeBase);
        var candidate = Head(repo);
        fixture.WriteAttempt([], candidateSha: candidate, additionalPaths: [receipt]);

        var result = Evaluate(goal, fixture, candidate, repo.Root);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal("a", Assert.Single(result.PackageResolutions!).Directory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../outside")]
    public void UnusableMatchingIdentity_LeavesSelectorAmbiguous(string codeBase)
    {
        using var repo = ChangedRepository("a/run_test.go");
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, ["TestRun"]);
        var receipt = WriteGoReceipt(fixture.Root);
        ChangeMethod(receipt, "example.com/m/b", codeBase);
        var candidate = Head(repo);
        fixture.WriteAttempt([], candidateSha: candidate, additionalPaths: [receipt]);

        var result = Evaluate(goal, fixture, candidate, repo.Root);

        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Contains("ambiguous: example.com/m/a, example.com/m/b", Assert.Single(result.Selectors), StringComparison.Ordinal);
        Assert.Empty(result.PackageResolutionProvenance);
    }

    [Fact]
    public void MultipleSelectors_RetainOrderedProvenanceAndUnresolvedDiagnostics()
    {
        using var repo = ChangedRepository("a/run_test.go");
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, ["TestTwo", "TestRun", "MissingTests"]);
        var receipt = ProspectiveEvidenceTrxRowsSelectorFormTests.Evidence.WriteGoReceipt(fixture.Root, "example.com/m",
            ("a", "a", "TestRun"), ("b", "b", "TestRun"), ("a", "a", "TestTwo"), ("b", "b", "TestTwo"));
        var candidate = Head(repo);
        fixture.WriteAttempt([], candidateSha: candidate, additionalPaths: [receipt]);

        var result = Evaluate(goal, fixture, candidate, repo.Root);

        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Equal(new[] { "MissingTests" }, result.Selectors);
        Assert.Equal(new[] { "TestTwo", "TestRun" }, result.PackageResolutions!.Select(resolution => resolution.Selector));
        Assert.Equal(2, result.PackageResolutionProvenance.Count);
    }

    [Fact]
    public void SplitSources_CannotPoolResolvedAndExistingSelectors()
    {
        using var repo = ChangedRepository("a/run_test.go");
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, ["TestRun", "ExistingTests"]);
        var candidate = Head(repo);
        fixture.WriteAttempt([], candidateSha: candidate, additionalPaths: [WriteGoReceipt(fixture.Root)],
            completedAt: DateTimeOffset.UnixEpoch.AddMinutes(1));
        fixture.WriteAttempt([("ExistingTests.Check", "Passed")], candidateSha: candidate,
            completedAt: DateTimeOffset.UnixEpoch.AddMinutes(2));

        var result = Evaluate(goal, fixture, candidate, repo.Root);

        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Equal(new[] { "TestRun" }, result.Selectors);
        Assert.Empty(result.PackageResolutionProvenance);
    }

    [Fact]
    public void OlderCompleteSource_SatisfiesWithoutPoolingSources()
    {
        using var repo = ChangedRepository("a/run_test.go");
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, ["TestRun", "ExistingTests"]);
        var candidate = Head(repo);
        var (source, _, _) = fixture.WriteAttempt([("ExistingTests.Check", "Passed")], candidateSha: candidate,
            additionalPaths: [WriteGoReceipt(fixture.Root)], completedAt: DateTimeOffset.UnixEpoch.AddMinutes(1));
        fixture.WriteAttempt([("ExistingTests.Check", "Passed")], candidateSha: candidate,
            completedAt: DateTimeOffset.UnixEpoch.AddMinutes(2));

        var result = Evaluate(goal, fixture, candidate, repo.Root);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(source, result.SourceId);
        Assert.Single(result.PackageResolutionProvenance);
    }

    [Fact]
    public void UnreadableReceipt_CannotSupplyPartialResolvedEvidence()
    {
        using var repo = ChangedRepository("a/run_test.go");
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, ["TestRun"]);
        var candidate = Head(repo);
        var broken = Path.Combine(fixture.Root, "broken.trx");
        File.WriteAllText(broken, "<broken");
        fixture.WriteAttempt([], candidateSha: candidate, additionalPaths: [WriteGoReceipt(fixture.Root), broken]);

        var result = Evaluate(goal, fixture, candidate, repo.Root);

        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Equal(new[] { "TestRun" }, result.Selectors);
        Assert.Empty(result.PackageResolutionProvenance);
    }

    [Fact]
    public void MixedGoAndCSharpEvidence_DoesNotAuthorizeResolution()
    {
        using var repo = ChangedRepository("a/run_test.go");
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, ["TestRun"]);
        var candidate = Head(repo);
        var csharp = GroupedGateTrxSamples.Write(fixture.Root, "mixed.trx",
            new GroupedGateTrxSamples.Row("csharp", "TestRun", "Check", "TestRun.Check", "Passed"));
        fixture.WriteAttempt([], candidateSha: candidate, additionalPaths: [WriteGoReceipt(fixture.Root), csharp]);

        var result = Evaluate(goal, fixture, candidate, repo.Root);

        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Contains("ambiguous:", Assert.Single(result.Selectors), StringComparison.Ordinal);
        Assert.Empty(result.PackageResolutionProvenance);
    }

    [Fact]
    public void PassingRerunInSameSource_PreservesSupersessionRule()
    {
        using var repo = ChangedRepository("a/run_test.go");
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, ["TestRun"]);
        var candidate = Head(repo);
        var failedReceipt = WriteGoReceipt(fixture.Root);
        ChangeMethod(failedReceipt, "example.com/m/a", resultOutcome: "Failed");
        var rerunRoot = Path.Combine(fixture.Root, "rerun");
        var passingReceipt = WriteGoReceipt(rerunRoot);
        fixture.WriteAttempt([], candidateSha: candidate, additionalPaths: [failedReceipt, passingReceipt]);

        var result = Evaluate(goal, fixture, candidate, repo.Root);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Single(result.PackageResolutionProvenance);
    }

    internal static string WriteGoReceipt(string root) =>
        ProspectiveEvidenceTrxRowsSelectorFormTests.Evidence.WriteGoReceipt(root, "example.com/m",
            ("a", "a", "TestRun"), ("b", "b", "TestRun"));

    internal static void ChangeMethod(string receipt, string package, string? codeBase = null, string? resultOutcome = null)
    {
        var document = XDocument.Load(receipt);
        var definition = document.Descendants().Single(element => element.Name.LocalName == "UnitTest" &&
            element.Descendants().Any(method => method.Name.LocalName == "TestMethod" && method.Attribute("className")?.Value == package));
        if (codeBase is not null)
            definition.Descendants().Single(element => element.Name.LocalName == "TestMethod").SetAttributeValue("codeBase", codeBase);
        if (resultOutcome is not null)
            document.Descendants().Single(element => element.Name.LocalName == "UnitTestResult" &&
                element.Attribute("testId")?.Value == definition.Attribute("id")!.Value).SetAttributeValue("outcome", resultOutcome);
        document.Save(receipt);
    }

    private static Goal Seed(ProspectiveEvidenceGateFixture fixture, IReadOnlyList<string> selectors) =>
        GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, selectors).GetGoal(fixture.Goal.Id);

    private static GateTestObligationEvaluator.Result Evaluate(Goal goal, ProspectiveEvidenceGateFixture fixture,
        string candidate, string? repository = null) => Assert.Single(GateTestObligationEvaluator.Evaluate(
            goal, fixture.OrchestratorDirectory, candidate, repository is null ? null : new(repository, "main")));

    private static string Head(TestClassRenameEvidenceTests.Repository repo) => TestClassRenameEvidenceTests.Git(repo.Root, "rev-parse", "HEAD");

    private static TestClassRenameEvidenceTests.Repository ChangedRepository(params string[] paths)
    {
        var repo = new TestClassRenameEvidenceTests.Repository();
        foreach (var path in paths) TestClassRenameEvidenceTests.Write(repo.Root, path, "package fixture\n");
        TestClassRenameEvidenceTests.Commit(repo.Root, "Change candidate tests", paths);
        return repo;
    }
}
