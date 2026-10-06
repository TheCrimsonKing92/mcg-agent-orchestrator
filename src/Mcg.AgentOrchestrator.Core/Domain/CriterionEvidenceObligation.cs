using System.Text.Json.Serialization;

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
    Satisfied,
    // A malformed, Unknown claim may only reach this terminal history state
    // through the attributed repair operation.  It is not evidence and it
    // always names the replacement obligation in Detail.
    Repaired
}

public static class CriterionEvidenceScopes
{
    // A successful normal acceptance run proves this exact, bounded scope. It
    // cannot be substituted for a manual/native observation or a narrower gate.
    public const string FullAcceptanceGate = "acceptance:full-gate";
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
    string? Detail = null,
    string? FindingStableId = null,
    string? ExpectedCandidateSha = null,
    IReadOnlyList<CriterionEvidenceReceipt>? PriorReceipts = null,
    string? ReplacementObligationId = null,
    CriterionEvidenceOwner? ReplacementOwner = null)
{
    [JsonIgnore]
    public int CriterionNumber => CriterionIndex + 1;

    [JsonIgnore]
    public string DisplayLabel => DescribeCriterion(CriterionVersion, CriterionIndex);

    public static string DescribeCriterion(int criterionVersion, int criterionIndex) =>
        $"criterion {criterionIndex + 1} (v{criterionVersion}) {BuildId(criterionVersion, criterionIndex)}";

    public bool IsPending => State == CriterionEvidenceState.Pending;

    internal bool HasValidEvidenceState =>
        Owner is CriterionEvidenceOwner.Operator or CriterionEvidenceOwner.Acceptance or CriterionEvidenceOwner.Unknown &&
        State is CriterionEvidenceState.Pending or CriterionEvidenceState.Failed or CriterionEvidenceState.Satisfied or CriterionEvidenceState.Repaired &&
        (Owner != CriterionEvidenceOwner.Unknown || State is CriterionEvidenceState.Pending or CriterionEvidenceState.Repaired) &&
        (Owner != CriterionEvidenceOwner.Acceptance ||
         (RequiredScope == CriterionEvidenceScopes.FullAcceptanceGate &&
          (State == CriterionEvidenceState.Pending || !string.IsNullOrWhiteSpace(ExpectedCandidateSha)))) &&
        (State is CriterionEvidenceState.Pending or CriterionEvidenceState.Repaired
            ? CandidateSha is null && ReceiptId is null && Detail is null
            : CurrentReceipt() is { IsWellFormed: true } &&
              (string.IsNullOrWhiteSpace(ExpectedCandidateSha) ||
               string.Equals(ExpectedCandidateSha, CandidateSha, StringComparison.OrdinalIgnoreCase))) &&
        (PriorReceipts is null ||
         (PriorReceipts.All(receipt => receipt is { IsWellFormed: true } && receipt.ReceiptId != ReceiptId) &&
          PriorReceipts.Select(receipt => receipt.ReceiptId).Distinct(StringComparer.Ordinal).Count() == PriorReceipts.Count));

    public bool HasSatisfiedEvidenceFor(string? candidateSha) =>
        State == CriterionEvidenceState.Satisfied && HasValidEvidenceState &&
        !string.IsNullOrWhiteSpace(candidateSha) &&
        string.Equals(CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(ExpectedCandidateSha) ||
         string.Equals(ExpectedCandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase));

    internal CriterionEvidenceReceipt? FindReceipt(string receiptId) =>
        CurrentReceipt() is { } current && current.ReceiptId == receiptId
            ? current
            : PriorReceipts?.SingleOrDefault(receipt => receipt.ReceiptId == receiptId);

    internal IReadOnlyList<CriterionEvidenceReceipt>? ArchiveCurrentReceipt() =>
        CurrentReceipt() is { } current ? [.. PriorReceipts ?? [], current] : PriorReceipts;

    private CriterionEvidenceReceipt? CurrentReceipt() =>
        State is not (CriterionEvidenceState.Pending or CriterionEvidenceState.Repaired) && CandidateSha is not null && ReceiptId is not null && Detail is not null
            ? new(Owner, CandidateSha, ReceiptId, RequiredScope, State == CriterionEvidenceState.Satisfied,
                Detail, Provenance, RecordedAt)
            : null;

    public static string BuildId(int criterionVersion, int criterionIndex) =>
        $"criterion-v{criterionVersion}-{criterionIndex}";
}
