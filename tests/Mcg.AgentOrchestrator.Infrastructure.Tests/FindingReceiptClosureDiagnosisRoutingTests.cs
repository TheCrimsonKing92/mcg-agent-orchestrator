using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;
using static ConductorDriverTestsGreenTesterReceiptClosure;

// Parallel-safe: each fixture owns its temp directory and kernel; execution uses inline seams.
public sealed class FindingReceiptClosureDiagnosisRoutingTests
{
    private const string CandidateSha = "abc1234abcdef1234abcdef1234abcdef1234abcd12";
    private const string OlderSha = "def5678abcdef1234abcdef1234abcdef1234abcd12";

    private static ReviewFinding Finding(string id = "closure-finding", string description = "Focused evidence missing") =>
        EvidenceFindingWithRequest(description, id, classes: ["ConductorDriverTests"]);

    [Fact]
    public void ReviewedOlderCommitRoutesTesterWithReceiptAndDiagnosis()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([Finding()], reviewedSha: OlderSha);
        fixture.AdvanceToDecision();

        Assert.Equal([fixture.Requester.Id], fixture.Retried);
        Assert.DoesNotContain(fixture.Developer!.Id, fixture.Retried);
        var receipt = Assert.Single(fixture.Requester.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.True(receipt.Accepted && receipt.Passed);
        Assert.Equal(CandidateSha, receipt.CandidateSha);
        var message = Assert.Single(fixture.RetryMessages);
        Assert.Contains(receipt.ReceiptId, message, StringComparison.Ordinal);
        Assert.Contains(CandidateSha, message, StringComparison.Ordinal);
        Assert.Contains(OlderSha, message, StringComparison.Ordinal);
        Assert.Contains("closure-finding", message, StringComparison.Ordinal);
        Assert.Contains(FindingReceiptClosureDiagnosis.ReviewedCommitDiffers, message, StringComparison.Ordinal);
        Assert.Contains("The supplied receipts passed on the current candidate. Resolve each finding they satisfy, or keep it open and name the source or test location that still needs a Developer change.", message, StringComparison.Ordinal);
        Assert.Equal(0, FindingEvidenceExecutionClassifier.CountEvidenceDeliveryRetries(
            fixture.Goal.Timeline, fixture.Requester.Id, CandidateSha, "closure-finding"));
        Assert.DoesNotContain(fixture.Goal.Timeline, item => item.Message.StartsWith("FINDING_RECEIPT_CLOSURE", StringComparison.Ordinal));
        fixture.AdvanceOnce();
        Assert.Single(fixture.Retried); // Merely observing another tick is not a Tester re-raise.
    }

    [Fact]
    public void AllConditionsHoldClosesByReceiptWithNoWorkerRouted()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([Finding()]);
        fixture.AdvanceToDecision();

        Assert.Empty(fixture.Retried);
        Assert.Empty(fixture.Escalations);
        Assert.Equal(FindingReceiptClosureDiagnosis.Closable, fixture.ReceiptDiagnosis!.Code);
        Assert.Equal(WorkTaskStatus.Completed, fixture.Requester.Status);
        var receipt = Assert.Single(fixture.Requester.LastVerification!.FindingEvidenceReceipts!);
        Assert.Equal(ReviewFindingState.Resolved, Assert.Single(fixture.Requester.LastVerification.MergedReviewFindings!).State);
        Assert.Contains(fixture.Goal.Timeline, item => item.Message ==
            $"FINDING_RECEIPT_CLOSURE task={fixture.Requester.Id.Value[..8]} findings=closure-finding receipts={receipt.ReceiptId} candidate={CandidateSha}; no worker dispatched");
    }

    [Fact]
    public void TesterReRaiseAtSameCandidateRoutesDeveloperWithReasonAndDiagnosis()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([Finding()], reviewedSha: OlderSha);
        fixture.AdvanceToDecision();
        Assert.Equal([fixture.Requester.Id], fixture.Retried);
        var receipt = Assert.Single(fixture.Requester.VerificationHistory.Last().FindingEvidenceReceipts!);

        const string reason = "src/Synthetic.cs:42 still discards the result";
        fixture.Report([Finding(description: reason)], reviewedSha: OlderSha);
        fixture.AdvanceToDecision();

        Assert.Equal([fixture.Requester.Id, fixture.Developer!.Id], fixture.Retried);
        Assert.Equal(RetryCause.NewSourceFinding, fixture.RetryCauses.Last());
        var message = fixture.RetryMessages.Last();
        Assert.Contains("route=developer", message, StringComparison.Ordinal);
        Assert.Contains(reason, message, StringComparison.Ordinal);
        Assert.Contains(receipt.ReceiptId, message, StringComparison.Ordinal);
        Assert.Contains(CandidateSha, message, StringComparison.Ordinal);
        Assert.Contains(FindingReceiptClosureDiagnosis.ReviewedCommitDiffers, message, StringComparison.Ordinal);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public void NewlyRaisedFindingGetsTesterReceiptDeliveryBeforeDeveloperRoute()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([Finding()], reviewedSha: OlderSha);
        fixture.AdvanceToDecision();
        fixture.Report([Finding(), Finding("new-finding")], reviewedSha: OlderSha);
        fixture.AdvanceToDecision();

        Assert.Equal([fixture.Requester.Id, fixture.Requester.Id], fixture.Retried);
        Assert.DoesNotContain(fixture.Developer!.Id, fixture.Retried);
        Assert.Contains("new-finding", fixture.RetryMessages.Last(), StringComparison.Ordinal);
        fixture.Report([Finding(), Finding("new-finding")], reviewedSha: OlderSha);
        fixture.AdvanceToDecision();
        Assert.Equal(fixture.Developer.Id, fixture.Retried.Last());
    }

    [Fact]
    public void TesterReRaiseWithoutDeveloperEscalatesWithDiagnosis()
    {
        using var fixture = new ClosureFixture(includeDeveloper: false);
        fixture.Report([Finding()], reviewedSha: OlderSha);
        fixture.AdvanceToDecision();
        Assert.Equal([fixture.Requester.Id], fixture.Retried);
        Assert.Empty(fixture.Escalations);
        fixture.Report([Finding(description: "tests/SyntheticTests.cs:17 requires repair")], reviewedSha: OlderSha);
        fixture.AdvanceToDecision();

        var message = Assert.Single(fixture.Escalations);
        Assert.Contains("No upstream Developer", message, StringComparison.Ordinal);
        Assert.Contains(FindingReceiptClosureDiagnosis.ReviewedCommitDiffers, message, StringComparison.Ordinal);
        Assert.Contains("tests/SyntheticTests.cs:17 requires repair", message, StringComparison.Ordinal);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public void FailingReceiptKeepsActionableRedDeveloperRouteAndMessage()
    {
        // Contrast the two evidence verdicts at the same seam: only RED routes Developer immediately.
        using var passing = new ClosureFixture();
        passing.Report([Finding()], reviewedSha: OlderSha);
        passing.AdvanceToDecision();
        Assert.Equal([passing.Requester.Id], passing.Retried);

        using var fixture = new ClosureFixture(evidenceFailure: "candidate-red");
        fixture.Report([Finding()]);
        fixture.AdvanceToDecision();

        Assert.Equal([fixture.Developer!.Id], fixture.Retried);
        var receipt = Assert.Single(fixture.Requester.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.False(receipt.Passed);
        var candidate = receipt.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
        var failingTests = candidate.FailingTestIdentities!.Distinct(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(failingTests);
        var message = Assert.Single(fixture.RetryMessages);
        Assert.StartsWith($"ACTIONABLE_CANDIDATE_RED candidate_sha={CandidateSha}; receipt_id={receipt.ReceiptId}; " +
            $"finding_ids=closure-finding; failing_tests={string.Join(',', failingTests)}.  " +
            "Repair the Developer-owned source/test anchor before any remaining focused evidence or downstream verification runs.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnosis=", message, StringComparison.Ordinal);
        Assert.DoesNotContain("route=", message, StringComparison.Ordinal);
    }
}
