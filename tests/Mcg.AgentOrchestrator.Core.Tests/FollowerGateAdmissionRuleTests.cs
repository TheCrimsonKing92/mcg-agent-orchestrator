using Mcg.AgentOrchestrator.Core;

// Parallel-safe: pure decision tables.
public sealed class FollowerGateAdmissionRuleTests
{
    [Fact]
    public void AdmissionChecksInputsInContractOrder()
    {
        foreach (var enabled in new[] { false, true })
        foreach (var slot in new[] { false, true })
        foreach (var count in new[] { -1, 0, 1, 2, 3 })
        foreach (var hasFollower in new[] { false, true })
        foreach (var ready in new[] { false, true })
        {
            var expected = !enabled ? FollowerGateAdmissionDecision.Disabled :
                !slot ? FollowerGateAdmissionDecision.NoSecondSlot :
                count != 1 ? FollowerGateAdmissionDecision.NoSingleLeader :
                hasFollower ? FollowerGateAdmissionDecision.LeaderHasFollower :
                !ready ? FollowerGateAdmissionDecision.FollowerNotReady : FollowerGateAdmissionDecision.Start;
            Assert.Equal(expected, FollowerGateAdmissionRule.Decide(enabled, slot, count, hasFollower, ready));
        }
    }

    [Fact]
    public void OnlyCompletedVerdictsOnPendingLeaderAndBaseHold()
    {
        foreach (var outcome in Enum.GetValues<FollowerGateRunOutcome>().Select(value => (FollowerGateRunOutcome?)value).Prepend(null))
        foreach (var leader in Enum.GetValues<FollowerLeaderGateStatus>())
        foreach (var liveBase in Enum.GetValues<FollowerLiveBaseState>())
        {
            var expected = (outcome == FollowerGateRunOutcome.Passed || outcome == FollowerGateRunOutcome.Failed) &&
                leader == FollowerLeaderGateStatus.Pending && liveBase == FollowerLiveBaseState.LeaderPending;
            Assert.Equal(expected, FollowerGateAdmissionRule.HoldForLeader(outcome, leader, liveBase));
        }
    }

    [Fact]
    public void UnknownHoldStatesFailLoudly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FollowerGateAdmissionRule.HoldForLeader(
            (FollowerGateRunOutcome)99, FollowerLeaderGateStatus.Pending, FollowerLiveBaseState.LeaderPending));
        Assert.Throws<ArgumentOutOfRangeException>(() => FollowerGateAdmissionRule.HoldForLeader(
            FollowerGateRunOutcome.Passed, (FollowerLeaderGateStatus)99, FollowerLiveBaseState.LeaderPending));
        Assert.Throws<ArgumentOutOfRangeException>(() => FollowerGateAdmissionRule.HoldForLeader(
            FollowerGateRunOutcome.Passed, FollowerLeaderGateStatus.Pending, (FollowerLiveBaseState)99));
    }
}
