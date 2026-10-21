using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure controlled results; no provider, process or shared store.
public sealed class AcceptanceVerificationSummaryTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeResult_ConversionPreservesBindingAndOmitsPassingFailureDetail(bool passed)
    {
        var check = new AcceptanceCheckResult("candidate tests", false, 1, "executed RED");
        var attributions = new[] { new AcceptanceCheckAttribution(check.Name, AcceptanceFailureOrigin.Inherited,
            "bound baseline receipt", AcceptanceFailureCause.EnvironmentalApparatus) };
        var native = new AcceptanceVerificationResult(passed, false, passed ? 0 : 1, "native output tail")
            { AttemptId = "native-attempt" };
        var summary = AcceptanceVerificationSummary.FromVerification(native, passed ? [] : [check],
            passed ? [] : [check.Name], "candidate-sha", "main-sha", ["candidate.trx"], attributions, "baseline attestation");

        Assert.Equal(passed, summary.Passed);
        Assert.Equal(passed ? null : native.OutputTail, summary.FailureDetail);
        AssertTransport(summary, attributions);
        Assert.Equal(passed ? 0 : 1, summary.RequiredUnmetCriteria.Count);
        var classified = ConductorDriver.ClassifyInheritedBaselineApparatus(summary);
        Assert.Equal(passed ? null : native.OutputTail, classified.FailureDetail);
        AssertTransport(classified, attributions);
        if (passed)
        {
            Assert.Same(summary, classified);
            Assert.Empty(classified.UnmetCriteria);
        }
        else
        {
            Assert.Equal(AcceptanceFailureClassifications.InheritedBaselineApparatus,
                Assert.Single(classified.RequiredUnmetCriteria).FailureClassification);
            Assert.Null(check.FailureClassification);
        }
    }

    [Fact]
    public void NamedFailure_NormalizationPreservesAttemptAndCandidateTransport()
    {
        var attributions = new[] { new AcceptanceCheckAttribution("candidate tests", AcceptanceFailureOrigin.Inherited,
            "bound baseline receipt", AcceptanceFailureCause.EnvironmentalApparatus) };
        var native = new AcceptanceVerificationResult(false, false, 1, "native output tail")
            { AttemptId = "native-attempt" };
        var summary = AcceptanceVerificationSummary.FromVerification(native, [], ["candidate tests"],
            "candidate-sha", "main-sha", ["candidate.trx"], attributions, "baseline attestation");
        Assert.Empty(summary.RequiredUnmetCriteria);
        var normalized = ConductorDriver.NormalizeNamedFailedChecksForRetry(summary);
        var failure = Assert.Single(normalized.RequiredUnmetCriteria);
        Assert.Equal("acceptance failed checks", failure.Name);
        Assert.Equal(AcceptanceFailureClassifications.InheritedBaselineApparatus, failure.FailureClassification);
        Assert.Equal(native.OutputTail, normalized.FailureDetail);
        AssertTransport(normalized, attributions);
        Assert.Equal(new[] { "candidate tests" }, normalized.FailedChecks);
    }

    private static void AssertTransport(AcceptanceVerificationSummary summary,
        IReadOnlyList<AcceptanceCheckAttribution> attributions)
    {
        Assert.Equal("native-attempt", summary.AttemptId);
        Assert.Equal("candidate-sha", summary.BranchHeadSha);
        Assert.Equal("main-sha", summary.MainHeadSha);
        Assert.Equal(new[] { "candidate.trx" }, summary.TestResultPaths);
        Assert.Equal(attributions, summary.CheckAttributions);
        Assert.Equal("baseline attestation", summary.BaselineAttestation);
    }
}
