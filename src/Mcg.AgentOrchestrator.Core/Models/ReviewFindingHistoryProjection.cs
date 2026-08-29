using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

public enum ReviewFindingHistoryProjectionMode
{
    FullInspection,
    ContractRepair
}

public sealed record ReviewFindingHistoryProjectionMetrics(
    ReviewFindingHistoryProjectionMode Mode,
    int UniqueRoundCount,
    int DuplicateRoundCount,
    int UniqueReceiptCount,
    int DuplicateReceiptCount,
    string? FallbackReason = null);

public sealed record ReviewFindingRepairCheckpoint(
    string? CandidateSha,
    string ContractViolationHash,
    IReadOnlyList<string> EvidenceContentHashes,
    IReadOnlyList<string> StableFindingIds)
{
    public static ReviewFindingRepairCheckpoint Create(TaskVerificationRecord verification)
    {
        ArgumentNullException.ThrowIfNull(verification);
        var violation = verification.ReviewFindingContractViolation ??
            throw new ArgumentException("A contract-repair checkpoint requires a contract violation.", nameof(verification));
        return new ReviewFindingRepairCheckpoint(
            verification.ReviewedCommit,
            WorkerContextArtifact.Hash(JsonSerializer.SerializeToUtf8Bytes(violation)),
            (verification.FindingEvidenceReceipts ?? [])
                .Select(receipt => WorkerContextArtifact.Hash(JsonSerializer.SerializeToUtf8Bytes(receipt)))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(hash => hash, StringComparer.Ordinal)
                .ToArray(),
            (verification.MergedReviewFindings ?? [])
                .Select(finding => finding.StableId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(stableId => stableId, StringComparer.Ordinal)
                .ToArray());
    }
}

public sealed record ReviewFindingContentReference(
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("logical_identity")] string LogicalIdentity);

public sealed record CanonicalReviewFindingEntry(
    [property: JsonPropertyName("stable_id")] string StableId,
    [property: JsonPropertyName("role")] AgentRole Role,
    [property: JsonPropertyName("state")] ReviewFindingState State,
    [property: JsonPropertyName("severity")] FindingSeverity Severity,
    [property: JsonPropertyName("category")] FindingCategory Category,
    [property: JsonPropertyName("location")] ReviewFindingLocation Location,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("candidate_sha")] string? CandidateSha,
    [property: JsonPropertyName("verdict_identity")] string VerdictIdentity,
    [property: JsonPropertyName("evidence_identity")] string? EvidenceIdentity,
    [property: JsonPropertyName("evidence_request")] FindingEvidenceRequest? EvidenceRequest,
    [property: JsonPropertyName("evidence_outcome")] FindingEvidenceOutcome? EvidenceOutcome,
    [property: JsonPropertyName("round")] ReviewFindingContentReference Round,
    [property: JsonPropertyName("receipt_bodies")] IReadOnlyList<ReviewFindingContentReference> ReceiptBodies,
    [property: JsonPropertyName("resolved_anchor_proof")] IReadOnlyList<ReviewFindingLocation> ResolvedAnchorProof);

public sealed record ReviewFindingRoundIndexEntry(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("role")] AgentRole Role,
    [property: JsonPropertyName("completed_at")] DateTimeOffset CompletedAt,
    [property: JsonPropertyName("candidate_sha")] string? CandidateSha,
    [property: JsonPropertyName("verdict_identity")] string VerdictIdentity,
    [property: JsonPropertyName("body")] ReviewFindingContentReference Body);

public sealed record ReviewFindingHistoryLedger(
    [property: JsonPropertyName("contract_version")] int ContractVersion,
    [property: JsonPropertyName("projection_mode")] ReviewFindingHistoryProjectionMode ProjectionMode,
    [property: JsonPropertyName("fallback_reason")] string? FallbackReason,
    [property: JsonPropertyName("findings")] IReadOnlyList<CanonicalReviewFindingEntry> Findings,
    [property: JsonPropertyName("rounds")] IReadOnlyList<ReviewFindingRoundIndexEntry> Rounds,
    [property: JsonPropertyName("receipt_bodies")] IReadOnlyList<ReviewFindingContentReference> ReceiptBodies);

public sealed record ReviewFindingContractRepairEnvelope(
    [property: JsonPropertyName("contract_version")] int ContractVersion,
    [property: JsonPropertyName("candidate_sha")] string CandidateSha,
    [property: JsonPropertyName("prior_substantive_verdict")] string PriorSubstantiveVerdict,
    [property: JsonPropertyName("findings")] IReadOnlyList<CanonicalReviewFindingEntry> Findings,
    [property: JsonPropertyName("touched_anchor_proof")] IReadOnlyList<ReviewFindingLocation> TouchedAnchorProof,
    [property: JsonPropertyName("contract_violation")] ReviewFindingContractViolation ContractViolation,
    [property: JsonPropertyName("required_output_schema")] IReadOnlyList<string> RequiredOutputSchema,
    [property: JsonPropertyName("immutable_references")] IReadOnlyList<ReviewFindingContentReference> ImmutableReferences);
