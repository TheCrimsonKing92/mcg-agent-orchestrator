using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all fixtures and rerun evidence stay in memory.
public sealed class CandidateRerunCheckClassifierTests
{
    private const string Identity = "Sample.Tests.UnchangedTests.Fails";

    [Fact]
    public void FailingTrxRerunListingTheIdentityIsAConfirmedFailure()
    {
        var check = FailedCheck();

        var result = CandidateRerunCheckClassifier.Classify(Identity, RedArm(check), check, "rerun.trx");

        Assert.False(result.Passed);
        Assert.Null(result.Error);
        Assert.Equal("rerun.trx", result.ReceiptPointer);
    }

    [Fact]
    public async Task FailingTrxRerunThroughAttributionRecordsFailed()
    {
        const string baselineSha = "main-green";
        var original = new AcceptanceTestFailureAttribution(
            Identity, AcceptanceTestFailureOrigin.Introduced,
            $"focused identity was green at merge-base {baselineSha}");
        var check = FailedCheck();
        var rerunner = new CandidateFailureRerunner((identities, _) =>
        {
            Assert.Equal(Identity, Assert.Single(identities));
            return Task.FromResult<IReadOnlyDictionary<string, CandidateFailureRerunResult>>(
                new Dictionary<string, CandidateFailureRerunResult>
                {
                    [Identity] = CandidateRerunCheckClassifier.Classify(
                        Identity, RedArm(check), check, "rerun.trx")
                });
        });

        var result = await AcceptanceFailureAttributionPlanner.ApplyCandidateRerunAsync(
            [original], baselineSha, _ => ["tests/Sample.Tests/UnchangedTests.cs"], ["src/Changed.cs"],
            new AcceptanceFailureAttributionPlanner.FocusedInvocationBudget(4), rerunner, default);

        var attribution = Assert.Single(result);
        var evidence = attribution.CandidateRerun;
        Assert.NotNull(evidence);
        Assert.Equal("Failed", evidence.Outcome);
        Assert.Null(evidence.Error);
        Assert.Equal("rerun.trx", evidence.ReceiptPointer);
        Assert.Equal(AcceptanceTestFailureOrigin.Introduced, attribution.Origin);
    }

    [Fact]
    public void FailingTrxRerunWithoutTheIdentityStaysUnconfirmed()
    {
        var check = FailedCheck() with { FailingTestIdentities = ["Sample.Tests.OtherTests.Fails"] };

        var result = CandidateRerunCheckClassifier.Classify(Identity, RedArm(check), check, "rerun.trx");

        Assert.Null(result.Passed);
        Assert.Equal("candidate rerun did not report this identity as failing", result.Error);
        Assert.Equal("rerun.trx", result.ReceiptPointer);
    }

    [Theory]
    [InlineData(false, FindingEvidenceArmDisposition.Red, AcceptanceShardCompletionPredicates.FailingTrx)]
    [InlineData(true, FindingEvidenceArmDisposition.ApparatusFailure, AcceptanceShardCompletionPredicates.FailingTrx)]
    [InlineData(true, FindingEvidenceArmDisposition.Inconclusive, AcceptanceShardCompletionPredicates.FailingTrx)]
    [InlineData(true, FindingEvidenceArmDisposition.Red, AcceptanceFailureClassifications.FocusedSelectionApparatusFailure)]
    public void UnusableArmStillWinsOverFailingTrx(
        bool accepted, FindingEvidenceArmDisposition disposition, string classification)
    {
        var check = FailedCheck() with { FailureClassification = classification };
        var arm = RedArm(check) with { Accepted = accepted, Disposition = disposition };

        var result = CandidateRerunCheckClassifier.Classify(Identity, arm, check, "rerun.trx");

        Assert.Null(result.Passed);
        Assert.Equal(classification, result.Error);
        Assert.Equal("rerun.trx", result.ReceiptPointer);
    }

    private static AcceptanceCheckResult FailedCheck() => new(
        "focused", false, 1, "failed",
        FailureClassification: AcceptanceShardCompletionPredicates.FailingTrx,
        FailingTestIdentities: [Identity]);

    private static FocusedEvidenceArmRunResult RedArm(AcceptanceCheckResult check) => new(
        FindingEvidenceArm.Candidate, "candidate", FindingEvidenceArmDisposition.Red,
        Accepted: true, Passed: false, "failed", [check]);
}
