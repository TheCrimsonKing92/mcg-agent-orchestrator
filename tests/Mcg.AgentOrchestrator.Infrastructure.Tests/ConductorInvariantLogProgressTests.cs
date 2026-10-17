using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure log classification with no shared state.
public sealed class ConductorInvariantLogProgressTests
{
    [Theory]
    [InlineData("ACCEPTANCE_COHORT_INFLIGHT tick=3 members=a+b outcome=inflight elapsed_ms=60000", true)]
    [InlineData("ACCEPTANCE_COHORT_INFLIGHT tick=3\toutcome=inflight\towner=a+b", true)]
    [InlineData("LOOP_RELAUNCH_DRAIN tick=9 active=1 admitting=false liveGates=1", true)]
    [InlineData("LOOP_RELAUNCH_DRAIN liveGates=12", true)]
    [InlineData("LOOP_RELAUNCH_DRAIN liveGates=0", false)]
    [InlineData("LOOP_RELAUNCH_DRAIN liveGates=-1", false)]
    [InlineData("LOOP_RELAUNCH_DRAIN liveGates=invalid", false)]
    [InlineData("LOOP_RELAUNCH_DRAIN liveGates=2147483648", false)]
    [InlineData("LOOP_RELAUNCH_DRAIN liveGates=1suffix", false)]
    [InlineData("LOOP_RELAUNCH_DRAIN tick=9 active=1", false)]
    [InlineData("LOOP_RELAUNCH_DRAIN otherliveGates=1", false)]
    [InlineData("ACCEPTANCE_COHORT_INFLIGHT tick=3 owner=a+b", false)]
    [InlineData("ACCEPTANCE_COHORT_INFLIGHT outcome=inflight-extra", false)]
    [InlineData("ACCEPTANCE_COHORT_INFLIGHT otheroutcome=inflight", false)]
    [InlineData("ACCEPTANCE_COHORT_INFLIGHTX outcome=inflight", false)]
    [InlineData("ACCEPTANCE_COHORT_ADOPTION_REFUSED members=a+b reason=identity-changed field=main", false)]
    [InlineData("", false)]
    public void TryGetProgress_LogDetail_RequiresExplicitLiveGateEvidence(string detail, bool expected)
    {
        Assert.Equal(expected, ConductorInvariantLogProgress.TryGetProgress(detail));
    }
}
