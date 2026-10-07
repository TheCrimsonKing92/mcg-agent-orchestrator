namespace Mcg.AgentOrchestrator.Core;

public enum FollowerGateAdmissionDecision
{
    Disabled,
    NoSecondSlot,
    NoSingleLeader,
    LeaderHasFollower,
    FollowerNotReady,
    Start
}

public static class FollowerGateAdmissionRule
{
    public static FollowerGateAdmissionDecision Decide(bool enabled, bool secondSlotAdmitted,
        int runningSoloGateCount, bool leaderHasFollower, bool followerReady)
    {
        if (!enabled) return FollowerGateAdmissionDecision.Disabled;
        if (!secondSlotAdmitted) return FollowerGateAdmissionDecision.NoSecondSlot;
        if (runningSoloGateCount != 1) return FollowerGateAdmissionDecision.NoSingleLeader;
        if (leaderHasFollower) return FollowerGateAdmissionDecision.LeaderHasFollower;
        if (!followerReady) return FollowerGateAdmissionDecision.FollowerNotReady;
        return FollowerGateAdmissionDecision.Start;
    }

    public static bool HoldForLeader(FollowerGateRunOutcome? newestOutcome,
        FollowerLeaderGateStatus leader, FollowerLiveBaseState liveBase)
    {
        if (newestOutcome.HasValue && !Enum.IsDefined(newestOutcome.Value))
            throw new ArgumentOutOfRangeException(nameof(newestOutcome));
        if (!Enum.IsDefined(leader)) throw new ArgumentOutOfRangeException(nameof(leader));
        if (!Enum.IsDefined(liveBase)) throw new ArgumentOutOfRangeException(nameof(liveBase));
        return newestOutcome is FollowerGateRunOutcome.Passed or FollowerGateRunOutcome.Failed &&
            leader == FollowerLeaderGateStatus.Pending && liveBase == FollowerLiveBaseState.LeaderPending;
    }
}
