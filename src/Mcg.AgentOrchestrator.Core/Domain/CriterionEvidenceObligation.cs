namespace Mcg.AgentOrchestrator.Core;

// An obligation is the durable, authoritative statement of who can close a
// criterion. It is deliberately separate from a worker's criterion verdict:
// a verdict is an observation; this record controls whether that observation
// is sufficient to move the goal.
public enum CriterionEvidenceOwner
{
    Worker,
    Acceptance,
    Operator,
    Unknown
}

public enum CriterionEvidenceState
{
    Pending,
    Failed,
    Satisfied
}

public sealed record CriterionEvidenceObligation(
    string Id,
    int CriterionIndex,
    int CriterionVersion,
    string Criterion,
    CriterionEvidenceOwner Owner,
    CriterionEvidenceState State,
    string RequiredScope,
    string Provenance,
    DateTimeOffset RecordedAt,
    string? CandidateSha = null,
    string? ReceiptId = null,
    string? Detail = null)
{
    public bool IsPending => State == CriterionEvidenceState.Pending;

    public static string BuildId(int criterionVersion, int criterionIndex) =>
        $"criterion-v{criterionVersion}-{criterionIndex}";
}
