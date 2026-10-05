using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorParallelAcceptanceAttemptCoordinator
{
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
