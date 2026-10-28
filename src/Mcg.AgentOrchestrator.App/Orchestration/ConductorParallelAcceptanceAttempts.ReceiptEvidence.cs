using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorParallelAcceptanceAttemptCoordinator
{
    // Original authority is captured by the execution owner, never reconstructed by a reader.
    private static FindingEvidenceReceipt? CaptureOriginalFocusedReceipt(
        ConductorParallelAcceptanceAttempt attempt, ConductorParallelAcceptanceRunResult run)
    {
        if (attempt.FindingRoundFingerprint != "pre-tester-deferred" ||
            string.IsNullOrWhiteSpace(attempt.FocusedExecutionBasisIdentity) ||
            string.IsNullOrWhiteSpace(attempt.FocusedEvidenceReceiptId) ||
            run.FocusedEvidence is not { } evidence ||
            !string.Equals(attempt.BranchHeadSha, run.Candidate.BranchHeadSha, StringComparison.OrdinalIgnoreCase)) return null;
        var selections = (attempt.FocusedEvidenceMemberRequests ?? []).SelectMany(member =>
            ConductorDriver.ParseDispositionSelections(member)).ToArray();
        if (selections.Length == 0) return null;
        var request = new FindingEvidenceRequest(selections);
        var identity = ConductorDriver.BuildFindingEvidenceIdentity(request);
        return new(attempt.FocusedEvidenceReceiptId, attempt.BranchHeadSha!, request,
            evidence.Accepted, evidence.IsValidEvidence, evidence.Summary,
            (evidence.Arms ?? []).Select(ConductorDriver.CreateFindingEvidenceArmReceipt).ToArray(),
            [new("pre-tester-deferred", identity, "executed-pre-tester", "pre-tester-deferred",
                FindingEvidenceRequestDecision.Executed, attempt.BranchHeadSha, attempt.FindingRoundFingerprint,
                SourceReceiptIds: [attempt.FocusedEvidenceReceiptId],
                ExecutionBasisIdentity: attempt.FocusedExecutionBasisIdentity)],
            attempt.FindingRoundFingerprint, attempt.FocusedExecutionBasisIdentity);
    }

    internal IReadOnlyList<ConductorParallelAcceptanceAttempt> ReadFocusedCoverageAttempts(GoalId goalId)
    {
        var directory = Path.Combine(_rootDirectory, goalId.Value);
        if (!Directory.Exists(directory)) return [];
        // A corrupt canonical record invalidates this optimization as a whole: silently skipping
        // it could expose an older GREEN hidden by a newer failed attempt.
        try
        {
            return Directory.EnumerateFiles(directory, "*.attempt.json")
                .Select(path => ReadCanonicalAttempt(path, goalId.Value))
                .Where(attempt => attempt.Kind == PreReviewEvidenceDispatchKind)
                .OrderBy(attempt => attempt.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return [];
        }
    }

    internal FindingEvidenceReceipt? ReadOriginalFocusedReceipt(ConductorParallelAcceptanceAttempt attempt)
    {
        try
        {
            var current = ReadCanonicalAttempt(attempt.MetadataPath, attempt.GoalId);
            if (current.AttemptId != attempt.AttemptId || current.Outcome != ConductorParallelAcceptanceAttemptOutcome.Passed ||
                current.SupersededBy is not null || current.CompletedAt is null ||
                current.OriginalFocusedReceipt is not { } receipt ||
                receipt.ReceiptId != current.FocusedEvidenceReceiptId ||
                receipt.ExecutionBasisIdentity != current.FocusedExecutionBasisIdentity ||
                receipt.FindingRoundFingerprint != "pre-tester-deferred" ||
                !string.Equals(receipt.CandidateSha, current.BranchHeadSha, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(current.ReplayResultFingerprint)) return null;
            var artifact = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(
                File.ReadAllText(current.ResultPath), JsonOptions);
            if (artifact is not { Kind: "focused-evidence", FocusedEvidence: { Accepted: true, IsValidEvidence: true } } ||
                !string.Equals(artifact.BranchHeadSha, current.BranchHeadSha, StringComparison.OrdinalIgnoreCase) ||
                HashReplayResult(JsonSerializer.Serialize(artifact, JsonOptions)) != current.ReplayResultFingerprint) return null;
            return receipt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    // A reconciled capacity deferral still owns its batch, even though it has no evidence receipt.
    internal ConductorParallelAcceptanceAttempt? ReadLatestFocusedEvidenceAttempt(
        ConductorParallelAcceptanceCandidate candidate, string batchId)
    {
        var directory = Path.Combine(_rootDirectory, candidate.Goal.Id.Value);
        if (!Directory.Exists(directory)) return null;
        return Directory.EnumerateFiles(directory, "*.attempt.json")
            .Select(path => ReadCanonicalAttempt(path, candidate.Goal.Id.Value))
            .Where(attempt => attempt.Kind == PreReviewEvidenceDispatchKind &&
                attempt.CandidateKey == candidate.CandidateKey && attempt.FocusedEvidenceBatchId == batchId)
            .OrderByDescending(attempt => attempt.Ordinal)
            .FirstOrDefault();
    }

    // Include reconciled attempts: reconciliation does not revoke a receipt's evidence.
    internal FocusedEvidenceRunResult? ReadFocusedReceiptEvidence(GoalId goalId, string receiptId, string candidateSha)
    {
        var directory = Path.Combine(_rootDirectory, goalId.Value);
        if (!Directory.Exists(directory)) return null;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.attempt.json"))
            {
                var attempt = ReadCanonicalAttempt(path, goalId.Value);
                if (attempt.FocusedEvidenceReceiptId != receiptId ||
                    !string.Equals(attempt.BranchHeadSha, candidateSha, StringComparison.OrdinalIgnoreCase)) continue;
                var artifact = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(
                    File.ReadAllText(attempt.ResultPath), JsonOptions);
                return artifact is { Kind: "focused-evidence", FocusedEvidence: { } evidence } &&
                    string.Equals(artifact.BranchHeadSha, candidateSha, StringComparison.OrdinalIgnoreCase)
                    ? evidence : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Missing or unreadable provenance cannot authorize a recovery rerun.
            return null;
        }
        return null;
    }
}
