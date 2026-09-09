namespace Mcg.AgentOrchestrator.Core;

public static class OperatorIntentVerbs
{
    public const string Progress = "progress";
    public const string Retry = "retry";
    public const string VerifyManual = "verify-manual";
    public const string CriterionEvidenceMap = "criterion-evidence-map";
    public const string CriterionEvidenceRecord = "criterion-evidence-record";
}

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
