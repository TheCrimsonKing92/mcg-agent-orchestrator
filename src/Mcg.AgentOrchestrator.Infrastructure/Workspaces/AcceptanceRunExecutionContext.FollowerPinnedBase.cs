using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record AcceptanceFollowerPinnedBase(
    string BaseMainRevision, string LeaderCandidateRevision, string LeaderCandidateTree)
{
    public string BaseMainRevision { get; init; } =
        AcceptanceCohortMemberBinding.NormalizeRevision(BaseMainRevision, nameof(BaseMainRevision));
    public string LeaderCandidateRevision { get; init; } =
        AcceptanceCohortMemberBinding.NormalizeRevision(LeaderCandidateRevision, nameof(LeaderCandidateRevision));
    public string LeaderCandidateTree { get; init; } =
        AcceptanceCohortMemberBinding.NormalizeRevision(LeaderCandidateTree, nameof(LeaderCandidateTree));
}

public static partial class AcceptanceExecutionOwners
{
    private static string? ResolveFollowerPinnedMain(string worktreePath, AcceptanceFollowerPinnedBase pinnedBase)
    {
        var live = GoalAcceptanceVerifier.ResolveGitScalarForExecutionOwner(worktreePath, "rev-parse", "refs/heads/main");
        // Resolve parent and tree from this immutable revision, never from a second read of main.
        var parent = live is null ? null :
            GoalAcceptanceVerifier.ResolveGitScalarForExecutionOwner(worktreePath, "rev-parse", $"{live}^1");
        var tree = live is null ? null :
            GoalAcceptanceVerifier.ResolveGitScalarForExecutionOwner(worktreePath, "rev-parse", $"{live}^{{tree}}");
        var state = FollowerGateBindingRule.ClassifyLiveBase(
            pinnedBase.BaseMainRevision, pinnedBase.LeaderCandidateTree, live, parent, tree);
        // A moved main can itself be C_A (e.g. a multi-commit fast-forward). Its SHA must
        // not masquerade as the accepted pinned identity when the landing rule rejects it.
        if (state == FollowerLiveBaseState.Moved &&
            string.Equals(live, pinnedBase.LeaderCandidateRevision, StringComparison.OrdinalIgnoreCase))
            throw new AcceptanceExecutionIdentityChangedException(
                "Follower main reached the candidate without an exact leader landing.", isChangedIdentity: true);
        return state switch
        {
            FollowerLiveBaseState.LeaderPending or FollowerLiveBaseState.LeaderLandedExactly => pinnedBase.LeaderCandidateRevision,
            FollowerLiveBaseState.Moved => live,
            FollowerLiveBaseState.Unresolved => null,
            _ => throw new InvalidOperationException($"Unknown follower live base state: {state}")
        };
    }
}
