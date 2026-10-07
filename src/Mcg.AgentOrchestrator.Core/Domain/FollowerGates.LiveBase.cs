namespace Mcg.AgentOrchestrator.Core;

public enum FollowerLiveBaseState
{
    Unresolved,
    LeaderPending,
    LeaderLandedExactly,
    Moved
}

public static partial class FollowerGateBindingRule
{
    public static FollowerLiveBaseState ClassifyLiveBase(
        string baseMainRevision,
        string leaderCandidateTree,
        string? liveMain,
        string? liveMainFirstParent,
        string? liveMainTree)
    {
        var main = AcceptanceCohortMemberBinding.NormalizeRevision(baseMainRevision, nameof(baseMainRevision));
        var tree = AcceptanceCohortMemberBinding.NormalizeRevision(leaderCandidateTree, nameof(leaderCandidateTree));
        if (liveMain is null || !MatchesRevision(liveMain, liveMain.Trim()))
            return FollowerLiveBaseState.Unresolved;
        if (MatchesRevision(liveMain, main))
            return FollowerLiveBaseState.LeaderPending;
        if (MatchesRevision(liveMainFirstParent, main) && MatchesRevision(liveMainTree, tree))
            return FollowerLiveBaseState.LeaderLandedExactly;
        return FollowerLiveBaseState.Moved;
    }
}
