namespace Mcg.AgentOrchestrator.Core;

// A receipt is an immutable observation for one candidate and scope. Rebinding
// its obligation must retain the observation without applying it to new code.
public sealed record CriterionEvidenceReceipt(
    CriterionEvidenceOwner Owner,
    string CandidateSha,
    string ReceiptId,
    string Scope,
    bool Passed,
    string Detail,
    string Provenance,
    DateTimeOffset RecordedAt)
{
    internal bool IsWellFormed =>
        Owner is CriterionEvidenceOwner.Operator or CriterionEvidenceOwner.Acceptance &&
        !string.IsNullOrWhiteSpace(CandidateSha) && !string.IsNullOrWhiteSpace(ReceiptId) &&
        !string.IsNullOrWhiteSpace(Scope) && !string.IsNullOrWhiteSpace(Detail) &&
        !string.IsNullOrWhiteSpace(Provenance) &&
        (Owner != CriterionEvidenceOwner.Acceptance || Scope == CriterionEvidenceScopes.FullAcceptanceGate);

    internal bool HasSamePayload(CriterionEvidenceReceipt other) =>
        Owner == other.Owner && Passed == other.Passed &&
        string.Equals(CandidateSha, other.CandidateSha, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ReceiptId, other.ReceiptId, StringComparison.Ordinal) &&
        string.Equals(Scope, other.Scope, StringComparison.Ordinal) &&
        string.Equals(Detail, other.Detail, StringComparison.Ordinal);
}
