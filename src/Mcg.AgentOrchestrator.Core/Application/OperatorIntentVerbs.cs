namespace Mcg.AgentOrchestrator.Core;

public static class OperatorIntentVerbs
{
    public const string Progress = "progress";
    public const string Retry = "retry";
    public const string CancelDispatch = "cancel-dispatch";
    public const string Answer = "answer";
    public const string VerifyManual = "verify-manual";
    public const string Adjudicate = "adjudicate";
    public const string ApprovePolicyChange = "approve-policy-change";
    public const string CriterionEvidenceMap = "criterion-evidence-map";
    public const string CriterionEvidenceRecord = "criterion-evidence-record";
    public const string CriterionEvidenceRepair = "criterion-evidence-repair";
}

public enum OperatorActorKind
{
    Human = 1,
    Agent = 2
}

public enum OperatorAnswerTargetKind
{
    Clarification = 1,
    HumanInput = 2
}

public sealed record AnswerOperatorIntentPayload(
    OperatorAnswerTargetKind TargetKind,
    string TargetId,
    string GoalId,
    string Text,
    OperatorActorKind ActorKind,
    IReadOnlyList<string>? EvidenceReferences = null,
    string? Precedent = null);

public sealed record AdjudicateOperatorIntentPayload(
    string Shape,
    string Text,
    IReadOnlyList<string> EvidenceReferences,
    long ExpectedGoalStateVersion,
    string WorkingDirectory,
    string? Cause = null,
    string? Reversibility = null,
    string? Precedent = null,
    AdjudicationPreconditionFacts? Precondition = null);

public sealed record AdjudicationPreconditionFacts(
    string TaskStatus,
    long? TaskDispatchedAtUtcTicks,
    long? TaskLatestRetryAtUtcTicks,
    string GoalStatus,
    string? GoalCandidateCommit);

public sealed record ApprovePolicyChangeOperatorIntentPayload(string CandidateSha, string Reason);

public sealed record CancelDispatchOperatorIntentPayload(
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    string DispatchId);

// These payloads state an operator's attributed request. They do not mutate a
// goal until the conductor has durably applied the intent.
public sealed record CriterionEvidenceMappingOperatorIntentPayload(
    int CriterionIndex,
    int CriterionVersion,
    CriterionEvidenceOwner Owner,
    string RequiredScope,
    string FindingStableId,
    string CandidateSha);

public sealed record CriterionEvidenceReceiptOperatorIntentPayload(
    string ObligationId,
    CriterionEvidenceOwner Owner,
    string CandidateSha,
    string ReceiptId,
    string Scope,
    bool Passed,
    string Detail);

public sealed record CriterionEvidenceRepairOperatorIntentPayload(
    string MalformedObligationId,
    int CriterionIndex,
    int CriterionVersion,
    CriterionEvidenceOwner Owner,
    string RequiredScope,
    string FindingStableId,
    string CandidateSha,
    string Reason);
