using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceFailureAttributionPlannerCandidateRerunTests
{
    private const string Identity = "Sample.Tests.UnchangedTests.FailsOnce";
    private const string BaselineSha = "main-green";
    private const string SourcePath = "tests/Sample.Tests/UnchangedTests.cs";

    [Fact]
    public async Task GreenBaselineAndPassingCandidateRerunIsUnconfirmedWithReceipt()
    {
        var baseline = GreenBaseline(Identity);
        var original = Classify(Identity, baseline);
        Assert.Equal(AcceptanceTestFailureOrigin.Introduced, original.Single().Origin);
        var rerunner = new FakeRerunner(new CandidateFailureRerunResult(true, "candidate-rerun.trx"));

        var result = await Apply(original, rerunner);

        var attribution = Assert.Single(result);
        Assert.Equal(AcceptanceTestFailureOrigin.UnconfirmedIntroduced, attribution.Origin);
        Assert.Equal("Passed", attribution.CandidateRerun?.Outcome);
        Assert.Equal("candidate-rerun.trx", attribution.CandidateRerun?.ReceiptPointer);
        Assert.Equal([Identity], Assert.Single(rerunner.Calls));
    }

    [Fact]
    public async Task RepeatedCandidateFailureStaysIntroducedWithOriginalEvidence()
    {
        var original = Classify(Identity, GreenBaseline(Identity));
        var result = await Apply(original,
            new FakeRerunner(new CandidateFailureRerunResult(false, "repeat-failure.trx")));

        var attribution = Assert.Single(result);
        Assert.Equal(AcceptanceTestFailureOrigin.Introduced, attribution.Origin);
        Assert.Equal(original.Single().Evidence, attribution.Evidence);
        Assert.Equal("Failed", attribution.CandidateRerun?.Outcome);
        Assert.Equal("repeat-failure.trx", attribution.CandidateRerun?.ReceiptPointer);
    }

    [Fact]
    public async Task RerunsOnlyEligibleIdentitiesOncePerAttempt()
    {
        var changedIdentity = "Sample.Tests.ChangedTests.Fails";
        var inheritedIdentity = "Sample.Tests.UnchangedTests.Inherited";
        var original = new[]
        {
            Assert.Single(Classify(Identity, GreenBaseline(Identity))),
            Assert.Single(Classify(changedIdentity, GreenBaseline(changedIdentity))),
            Assert.Single(Classify(inheritedIdentity, RedBaseline(inheritedIdentity)))
        };
        var rerunner = new FakeRerunner(new CandidateFailureRerunResult(true, "once.trx"));
        var budget = new AcceptanceFailureAttributionPlanner.FocusedInvocationBudget(4);
        var first = await AcceptanceFailureAttributionPlanner.ApplyCandidateRerunAsync(
            original, BaselineSha,
            identity => [identity == changedIdentity ? "tests/Sample.Tests/ChangedTests.cs" : SourcePath],
            ["tests/Sample.Tests/ChangedTests.cs"], budget, rerunner, default);
        var second = await AcceptanceFailureAttributionPlanner.ApplyCandidateRerunAsync(
            original, BaselineSha,
            identity => [identity == changedIdentity ? "tests/Sample.Tests/ChangedTests.cs" : SourcePath],
            ["tests/Sample.Tests/ChangedTests.cs"], budget, rerunner, default);

        Assert.Equal([Identity], Assert.Single(rerunner.Calls));
        Assert.Single(rerunner.Calls);
        Assert.Equal(AcceptanceTestFailureOrigin.UnconfirmedIntroduced, first[0].Origin);
        Assert.Equal(AcceptanceTestFailureOrigin.Introduced, first[1].Origin);
        Assert.Null(first[1].CandidateRerun);
        Assert.Equal(AcceptanceTestFailureOrigin.Inherited, first[2].Origin);
        Assert.Null(first[2].CandidateRerun);
        Assert.Equal(first[0], second[0]);
    }

    [Fact]
    public async Task MoreThanTenEligibleIdentitiesKeepsTodayAttribution()
    {
        var attributions = Enumerable.Range(0, 11)
            .Select(index => new AcceptanceTestFailureAttribution(
                $"Sample.Tests.UnchangedTests.Failure{index}",
                AcceptanceTestFailureOrigin.Introduced,
                $"focused identity was green at merge-base {BaselineSha}"))
            .ToArray();
        var rerunner = new FakeRerunner(new CandidateFailureRerunResult(true, "unexpected.trx"));
        var result = await AcceptanceFailureAttributionPlanner.ApplyCandidateRerunAsync(
            attributions, BaselineSha, _ => [SourcePath], ["src/Changed.cs"],
            new AcceptanceFailureAttributionPlanner.FocusedInvocationBudget(11), rerunner, default);

        Assert.Equal(attributions, result);
        Assert.Empty(rerunner.Calls);
    }

    [Fact]
    public async Task RerunErrorFailsClosedAndKeepsItsReason()
    {
        var original = Classify(Identity, GreenBaseline(Identity));
        var result = await Apply(original, new FakeRerunner(
            new CandidateFailureRerunResult(null, "rerun-log.txt", "harness timeout")));

        var attribution = Assert.Single(result);
        Assert.Equal(AcceptanceTestFailureOrigin.Introduced, attribution.Origin);
        Assert.Equal("NotExecuted", attribution.CandidateRerun?.Outcome);
        Assert.Equal("harness timeout", attribution.CandidateRerun?.Error);
        Assert.Equal("rerun-log.txt", attribution.CandidateRerun?.ReceiptPointer);
    }

    [Fact]
    public async Task MissingRerunnerAndMissingIdentityFailClosed()
    {
        var original = Classify(Identity, GreenBaseline(Identity));
        var withoutRerunner = await Apply(original, null);
        Assert.Equal(AcceptanceTestFailureOrigin.Introduced, Assert.Single(withoutRerunner).Origin);
        Assert.Equal("candidate rerunner unavailable", withoutRerunner[0].CandidateRerun?.Error);

        var withoutIdentity = await Apply(original, new EmptyRerunner());
        Assert.Equal(AcceptanceTestFailureOrigin.Introduced, Assert.Single(withoutIdentity).Origin);
        Assert.Equal("identity absent from candidate rerun result", withoutIdentity[0].CandidateRerun?.Error);
    }

    [Fact]
    public void HarnessFailureDoesNotProveRepeatedTestFailure()
    {
        var arm = new FocusedEvidenceArmRunResult(
            FindingEvidenceArm.Candidate, "candidate", FindingEvidenceArmDisposition.Red,
            Accepted: true, Passed: false, "harness failed", []);
        var check = new AcceptanceCheckResult("focused", false, 1, "harness failed",
            FailureClassification: AcceptanceFailureClassifications.FocusedSelectionApparatusFailure,
            FailingTestIdentities: []);

        var result = GoalAcceptanceVerifier.ClassifyCandidateRerunCheck(Identity, arm, check, "harness.trx");

        Assert.Null(result.Passed);
        Assert.Equal(AcceptanceFailureClassifications.FocusedSelectionApparatusFailure, result.Error);
        Assert.Equal("harness.trx", result.ReceiptPointer);
        Assert.Null(GoalAcceptanceVerifier.ClassifyCandidateRerunCheck(
            Identity, arm, check with { FailureClassification = null }, "harness.trx").Passed);
        Assert.False(GoalAcceptanceVerifier.ClassifyCandidateRerunCheck(
            Identity, arm, check with { FailureClassification = null, FailingTestIdentities = [Identity] },
            "failure.trx").Passed);
        var apparatusArm = arm with { Disposition = FindingEvidenceArmDisposition.ApparatusFailure };
        Assert.Null(GoalAcceptanceVerifier.ClassifyCandidateRerunCheck(
            Identity, apparatusArm, check with { FailureClassification = null, FailingTestIdentities = [Identity] },
            "apparatus.trx").Passed);
    }

    private static Task<IReadOnlyList<AcceptanceTestFailureAttribution>> Apply(
        IReadOnlyList<AcceptanceTestFailureAttribution> original,
        ICandidateFailureRerunner? rerunner) =>
        AcceptanceFailureAttributionPlanner.ApplyCandidateRerunAsync(
            original, BaselineSha, _ => [SourcePath], ["src/Changed.cs"],
            new AcceptanceFailureAttributionPlanner.FocusedInvocationBudget(4), rerunner, default);

    private static IReadOnlyList<AcceptanceTestFailureAttribution> Classify(
        string identity,
        FocusedEvidenceArmRunResult baseline) =>
        AcceptanceFailureAttributionPlanner.ClassifyBaselineFailures(
            [identity], [identity], new Dictionary<string, string> { [identity] = "focused" }, baseline);

    private static FocusedEvidenceArmRunResult GreenBaseline(string _) => new(
        FindingEvidenceArm.Baseline, BaselineSha, FindingEvidenceArmDisposition.Green,
        Accepted: true, Passed: true, "green", [new AcceptanceCheckResult("focused", true, 0, "passed")]);

    private static FocusedEvidenceArmRunResult RedBaseline(string identity) => new(
        FindingEvidenceArm.Baseline, BaselineSha, FindingEvidenceArmDisposition.Red,
        Accepted: true, Passed: false, "red",
        [new AcceptanceCheckResult("focused", false, 1, "failed", FailingTestIdentities: [identity])]);

    private sealed class FakeRerunner(CandidateFailureRerunResult outcome) : ICandidateFailureRerunner
    {
        internal List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<IReadOnlyDictionary<string, CandidateFailureRerunResult>> RerunAsync(
            IReadOnlyList<string> identities,
            CancellationToken cancellationToken)
        {
            Calls.Add(identities.ToArray());
            return Task.FromResult<IReadOnlyDictionary<string, CandidateFailureRerunResult>>(
                identities.ToDictionary(identity => identity, _ => outcome, StringComparer.Ordinal));
        }
    }

    private sealed class EmptyRerunner : ICandidateFailureRerunner
    {
        public Task<IReadOnlyDictionary<string, CandidateFailureRerunResult>> RerunAsync(
            IReadOnlyList<string> identities,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, CandidateFailureRerunResult>>(
                new Dictionary<string, CandidateFailureRerunResult>());
    }
}
