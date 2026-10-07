using Mcg.AgentOrchestrator.Core;

public sealed class FollowerGateStopRuleTests
{
    public static IEnumerable<object[]> StopCases()
    {
        foreach (var leader in Enum.GetValues<FollowerLeaderGateStatus>())
        foreach (var state in Enum.GetValues<FollowerLiveBaseState>())
        foreach (var firstParentIsBase in new[] { false, true })
        {
            FollowerGateInvalidReason? reason = leader switch
            {
                FollowerLeaderGateStatus.Failed or FollowerLeaderGateStatus.Cancelled or FollowerLeaderGateStatus.Stale
                    => FollowerGateInvalidReason.LeaderFailed,
                FollowerLeaderGateStatus.Pending or FollowerLeaderGateStatus.Landed when state == FollowerLiveBaseState.Moved
                    => firstParentIsBase ? FollowerGateInvalidReason.LeaderTreeDiffers : FollowerGateInvalidReason.BaseMoved,
                _ => null
            };
            yield return [leader, state, firstParentIsBase, reason!];
        }
    }

    [Theory]
    [MemberData(nameof(StopCases))]
    public void OnlyLeaderFailureOrKnownBaseMovementStopsFollower(FollowerLeaderGateStatus leader,
        FollowerLiveBaseState state, bool firstParentIsBase, FollowerGateInvalidReason? expected) =>
        Assert.Equal(expected, FollowerGateBindingRule.StopReason(leader, state, firstParentIsBase));
}
