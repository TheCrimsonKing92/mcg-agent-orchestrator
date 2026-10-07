using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class AcceptanceFailureCauseAdjudicatorTests
{
    [Fact]
    public void PassedCheck_ClearsSuppliedEvidence_AndMatchesVerifier()
    {
        static AcceptanceCheckResult CreateCheck() => new(
            "passed-check", true, 0, null,
            FailureCauseEvidence: new AcceptanceFailureCauseEvidence(
                AcceptanceFailureCause.EnvironmentalApparatus,
                "supplied evidence",
                "passed-check"));

        var result = AcceptanceFailureCauseAdjudicator.Attach(CreateCheck());

        Assert.Null(result.FailureCauseEvidence);
        Assert.Equal(GoalAcceptanceVerifier.AttachFailureCauseEvidence(CreateCheck()), result);
    }

    [Fact]
    public void EnvironmentInterference_AttachesExactEvidence_AndMatchesVerifier()
    {
        static AcceptanceCheckResult CreateCheck() => new(
            "environment \"check\"", false, 1, null,
            FailureClassification: AcceptanceFailureClassifications.GateEnvironmentInterference);

        var check = CreateCheck();
        var result = AcceptanceFailureCauseAdjudicator.Attach(check);

        var cause = Assert.IsType<AcceptanceFailureCauseEvidence>(result.FailureCauseEvidence);
        Assert.Equal(AcceptanceFailureCause.EnvironmentalApparatus, cause.Cause);
        Assert.Equal(check.Name, cause.CheckName);
        Assert.Equal("gate-environment-interference", cause.SourceClassification);
        Assert.Equal(
            $"check={JsonSerializer.Serialize(check.Name)}; failureClassification={JsonSerializer.Serialize(check.FailureClassification)}",
            cause.Evidence);
        Assert.Equal(GoalAcceptanceVerifier.AttachFailureCauseEvidence(CreateCheck()), result);
    }

    [Fact]
    public void EvidenceForAnotherCheck_IsRejected_AndMatchesVerifier()
    {
        static AcceptanceCheckResult CreateCheck() => new(
            "failed-check", false, 1, null,
            FailureClassification: AcceptanceFailureClassifications.GateEnvironmentInterference,
            FailureCauseEvidence: new AcceptanceFailureCauseEvidence(
                AcceptanceFailureCause.EnvironmentalApparatus,
                "valid nonblank evidence",
                "different-check",
                AcceptanceFailureClassifications.GateEnvironmentInterference));

        var result = AcceptanceFailureCauseAdjudicator.Attach(CreateCheck());

        Assert.Null(result.FailureCauseEvidence);
        Assert.Equal(GoalAcceptanceVerifier.AttachFailureCauseEvidence(CreateCheck()), result);
    }
}
