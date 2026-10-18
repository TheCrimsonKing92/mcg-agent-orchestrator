using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// No external artifacts or shared state; parallel-safe.
public sealed class PreTesterApparatusReceiptClassifierTests
{
    [Fact]
    public void MissingEvidenceDoesNotAuthorizeRefresh() =>
        Assert.False(PreTesterApparatusReceiptClassifier.IsApparatusUnusable(null));

    [Theory]
    [InlineData(FindingEvidenceArmDisposition.Inconclusive, 0, false, true)]
    [InlineData(FindingEvidenceArmDisposition.Inconclusive, null, false, true)]
    [InlineData(FindingEvidenceArmDisposition.Green, 0, false, false)]
    [InlineData(FindingEvidenceArmDisposition.Red, 1, true, false)]
    [InlineData(FindingEvidenceArmDisposition.Inconclusive, 1, false, false)]
    public void EmptyNonGreenCandidateIsUnusable(FindingEvidenceArmDisposition disposition,
        int? executed, bool failing, bool expected)
    {
        var check = new AcceptanceCheckResult("selection", disposition == FindingEvidenceArmDisposition.Green,
            0, "fixture", ExecutedTestCount: executed, FailingTestIdentities: failing ? ["Tests.Fails"] : []);
        var evidence = new FocusedEvidenceRunResult("request", true, check.Passed, "fixture", [],
            Arms: [new(FindingEvidenceArm.Candidate, "abc1234", disposition, true, check.Passed, "fixture", [check])]);
        Assert.Equal(expected, PreTesterApparatusReceiptClassifier.IsApparatusUnusable(evidence));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public void ApparatusCheckIsPositiveEvidence(bool inArm, bool passed, bool expected)
    {
        var check = new AcceptanceCheckResult("build", passed, -1, "no tests",
            FailureClassification: AcceptanceFailureClassifications.BuildPhaseTimeout);
        var evidence = new FocusedEvidenceRunResult("request", true, false, "fixture", inArm ? [] : [check],
            Arms: inArm ? [new(FindingEvidenceArm.Baseline, "aaa1111", FindingEvidenceArmDisposition.Inconclusive,
                true, false, "fixture", [check])] : null);
        Assert.Equal(expected, PreTesterApparatusReceiptClassifier.IsApparatusUnusable(evidence));
    }

    [Fact]
    public void MissingCandidateWithoutApparatusCheckDoesNotAuthorizeRefresh() =>
        Assert.False(PreTesterApparatusReceiptClassifier.IsApparatusUnusable(
            new FocusedEvidenceRunResult("request", true, false, "incomplete", [])));
}
