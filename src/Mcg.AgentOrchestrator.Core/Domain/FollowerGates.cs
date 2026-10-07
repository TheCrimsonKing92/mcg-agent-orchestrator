namespace Mcg.AgentOrchestrator.Core;

/// <summary>Binds a follower's tested content to its leader, original branch and plan.</summary>
public sealed record FollowerGateReceipt
{
    public FollowerGateReceipt(
        GoalId leaderGoalId,
        string leaderCandidateRevision,
        string leaderCandidateTree,
        string baseMainRevision,
        GoalId followerGoalId,
        string followerBranchHead,
        string followerTestedTree,
        string followerPlanIdentity)
    {
        ArgumentNullException.ThrowIfNull(leaderGoalId);
        ArgumentNullException.ThrowIfNull(followerGoalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaderGoalId.Value, nameof(leaderGoalId));
        ArgumentException.ThrowIfNullOrWhiteSpace(followerGoalId.Value, nameof(followerGoalId));
        ArgumentException.ThrowIfNullOrWhiteSpace(followerPlanIdentity);
        LeaderGoalId = leaderGoalId;
        LeaderCandidateRevision = AcceptanceCohortMemberBinding.NormalizeRevision(leaderCandidateRevision, nameof(leaderCandidateRevision));
        LeaderCandidateTree = AcceptanceCohortMemberBinding.NormalizeRevision(leaderCandidateTree, nameof(leaderCandidateTree));
        BaseMainRevision = AcceptanceCohortMemberBinding.NormalizeRevision(baseMainRevision, nameof(baseMainRevision));
        FollowerGoalId = followerGoalId;
        FollowerBranchHead = AcceptanceCohortMemberBinding.NormalizeRevision(followerBranchHead, nameof(followerBranchHead));
        FollowerTestedTree = AcceptanceCohortMemberBinding.NormalizeRevision(followerTestedTree, nameof(followerTestedTree));
        FollowerPlanIdentity = followerPlanIdentity;
    }

    public GoalId LeaderGoalId { get; }
    public string LeaderCandidateRevision { get; }
    public string LeaderCandidateTree { get; }
    public string BaseMainRevision { get; }
    public GoalId FollowerGoalId { get; }
    public string FollowerBranchHead { get; }
    public string FollowerTestedTree { get; }
    public string FollowerPlanIdentity { get; }
}

public enum FollowerLeaderOutcome
{
    Landed,
    Failed,
    Cancelled,
    Stale
}

public sealed record FollowerGateLandingObservation(
    FollowerLeaderOutcome LeaderOutcome,
    string? LandedFirstParent,
    string? LandedTree,
    string? CurrentFollowerBranchHead,
    string? RebasedFollowerTree,
    string? CurrentFollowerPlanIdentity);

public enum FollowerGateInvalidReason
{
    LeaderFailed,
    BaseMoved,
    LeaderTreeDiffers,
    FollowerBranchMoved,
    RebaseTreeDiffers,
    PlanChanged
}

/// <summary>A valid binding has no reason; an invalid binding has exactly one typed reason.</summary>
public sealed record FollowerGateBindingVerdict
{
    private FollowerGateBindingVerdict(FollowerGateInvalidReason? reason) => Reason = reason;

    public bool IsValid => Reason is null;
    public FollowerGateInvalidReason? Reason { get; }
    public static FollowerGateBindingVerdict Valid { get; } = new(reason: null);

    public static FollowerGateBindingVerdict Invalid(FollowerGateInvalidReason reason)
    {
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown follower gate invalid reason.");
        return new(reason);
    }
}

public enum FollowerGateDisposition
{
    LandFollower,
    ChargeFollower,
    Discard
}

public static partial class FollowerGateBindingRule
{
    public static FollowerGateBindingVerdict Evaluate(
        FollowerGateReceipt receipt,
        FollowerGateLandingObservation observation)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.LeaderOutcome != FollowerLeaderOutcome.Landed)
            return FollowerGateBindingVerdict.Invalid(FollowerGateInvalidReason.LeaderFailed);
        if (!MatchesRevision(observation.LandedFirstParent, receipt.BaseMainRevision))
            return FollowerGateBindingVerdict.Invalid(FollowerGateInvalidReason.BaseMoved);
        if (!MatchesRevision(observation.LandedTree, receipt.LeaderCandidateTree))
            return FollowerGateBindingVerdict.Invalid(FollowerGateInvalidReason.LeaderTreeDiffers);
        if (!MatchesRevision(observation.CurrentFollowerBranchHead, receipt.FollowerBranchHead))
            return FollowerGateBindingVerdict.Invalid(FollowerGateInvalidReason.FollowerBranchMoved);
        if (!MatchesRevision(observation.RebasedFollowerTree, receipt.FollowerTestedTree))
            return FollowerGateBindingVerdict.Invalid(FollowerGateInvalidReason.RebaseTreeDiffers);
        if (!string.Equals(observation.CurrentFollowerPlanIdentity, receipt.FollowerPlanIdentity, StringComparison.Ordinal))
            return FollowerGateBindingVerdict.Invalid(FollowerGateInvalidReason.PlanChanged);
        return FollowerGateBindingVerdict.Valid;
    }

    public static FollowerGateDisposition Disposition(FollowerGateBindingVerdict verdict, bool followerPassed)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        if (!verdict.IsValid)
            return FollowerGateDisposition.Discard;
        return followerPassed ? FollowerGateDisposition.LandFollower : FollowerGateDisposition.ChargeFollower;
    }

    public static string WireName(FollowerGateInvalidReason reason) => reason switch
    {
        FollowerGateInvalidReason.LeaderFailed => "leader-failed",
        FollowerGateInvalidReason.BaseMoved => "base-moved",
        FollowerGateInvalidReason.LeaderTreeDiffers => "leader-tree-differs",
        FollowerGateInvalidReason.FollowerBranchMoved => "follower-branch-moved",
        FollowerGateInvalidReason.RebaseTreeDiffers => "rebase-tree-differs",
        FollowerGateInvalidReason.PlanChanged => "plan-changed",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown follower gate invalid reason.")
    };

    private static bool MatchesRevision(string? observed, string expected)
    {
        if (observed is null)
            return false;
        var trimmed = observed.Trim();
        return trimmed.Length == 40 && trimmed.All(Uri.IsHexDigit) &&
            string.Equals(trimmed, expected, StringComparison.OrdinalIgnoreCase);
    }
}
