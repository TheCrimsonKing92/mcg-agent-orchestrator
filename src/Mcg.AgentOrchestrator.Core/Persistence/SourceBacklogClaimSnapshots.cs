namespace Mcg.AgentOrchestrator.Core;

public enum GoalReplacementDisposition
{
    ZeroWorkCorrection,
    AbandonFailedAttempt,
    SupersedeUnlandedAttempt,
    Unspecified
}

public enum GoalReplacementOutcome
{
    Succeeded,
    Replayed,
    ValidationRejected,
    IneligibleDisposition,
    ProtectedOwner,
    CurrentOwnerConflict,
    LegacyOwnerAmbiguous,
    PersistenceFailed
}

public sealed record SourceBacklogClaimSnapshot(
    string BacklogItemId,
    string OwnerGoalId,
    SourceBacklogCoverage Coverage,
    long Version,
    DateTimeOffset UpdatedAt);

public sealed record GoalReplacementLineageSnapshot(
    string BacklogItemId,
    string PredecessorGoalId,
    string SuccessorGoalId,
    Guid RequestId,
    DateTimeOffset ReplacedAt);

public sealed record GoalReplacementEligibilityFacts(
    GoalStatus Status,
    bool HasWorkspace,
    bool HasBranch,
    bool HasDispatch,
    bool HasRepositoryDelta,
    bool IsRunning,
    bool IsLanded,
    bool IsMerged,
    bool IsRecorded,
    bool IsGitEvidenceAvailable = true,
    string EvidenceToken = "")
{
    public bool HasWork => HasWorkspace || HasBranch || HasDispatch || HasRepositoryDelta;
    public bool IsProtected => !IsGitEvidenceAvailable || IsRunning || IsLanded || IsMerged || IsRecorded;
}

public sealed record GoalReplacementTransferAuthoritySnapshot(
    Guid AuthorityId,
    string BacklogItemId,
    string PredecessorGoalId,
    long ExpectedClaimVersion,
    string EvidenceToken,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public interface IGoalReplacementTransferAuthority
{
    GoalReplacementTransferAuthoritySnapshot Snapshot { get; }
    GoalReplacementEligibilityFacts RevalidateForTransfer();
}

public sealed class GoalReplacementTransferAuthorityException(
    string reasonCode,
    GoalReplacementEligibilityFacts? observedFacts = null)
    : InvalidOperationException($"GOAL_REPLACE_VALIDATION_REJECTED reason={reasonCode}")
{
    public string ReasonCode { get; } = reasonCode;
    public GoalReplacementEligibilityFacts? ObservedFacts { get; } = observedFacts;
}

public sealed record GoalReplacementAuditSnapshot(
    Guid RequestId,
    string Fingerprint,
    GoalReplacementOutcome Outcome,
    string BacklogItemId,
    string PredecessorGoalId,
    string? SuccessorGoalId,
    GoalReplacementDisposition Disposition,
    string Reason,
    GoalStatus OldStatus,
    GoalStatus? NewStatus,
    SourceBacklogCoverage Coverage,
    string Actor,
    string Channel,
    string AuthenticationAssurance,
    DateTimeOffset AttemptedAt,
    string ExpectedOwnerGoalId,
    long ExpectedClaimVersion,
    string? ObservedOwnerGoalId,
    long? ObservedClaimVersion,
    GoalReplacementEligibilityFacts EligibilityFacts,
    string? FailureCode = null,
    string ObjectiveHash = "",
    string OrderedRoles = "",
    string AssignedAgents = "");

public sealed record GoalReplacementReceipt(
    GoalReplacementOutcome Outcome,
    Guid RequestId,
    string BacklogItemId,
    string PredecessorGoalId,
    string? SuccessorGoalId,
    string? OwnerGoalId,
    long? ClaimVersion,
    string ReasonCode);
