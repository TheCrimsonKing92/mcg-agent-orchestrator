using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorParallelAcceptanceAttemptCoordinator
{
    private static string HashReplayResult(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    internal bool HasPassedReconciledReceipt(string goalId) =>
        TryReadLatest(goalId) is { Outcome: ConductorParallelAcceptanceAttemptOutcome.Passed, ReconciledAt: not null };

    private static bool HasSameReplayScope(ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate) =>
        attempt.ScopePaths is not null && attempt.ScopePaths.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
            candidate.ScopePaths.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    // Evidence satisfaction is deliberately absent: answering an operator wait must not
    // invalidate the gate that created it. Changes to the obligations themselves do.
    internal static string? BuildReplayContextFingerprint(Goal goal)
    {
        if (goal.AuthoritativeRefinedSpecVersion is not { } version)
            return null;
        var context = new
        {
            goal.Id, Brief = goal.AuthoritativeBrief, version.Version, version.BriefVersion,
            version.Spec.BehavioralContract, version.Spec.AcceptanceCriteria,
            version.Spec.VerificationClass, version.Spec.Decisions,
            version.Spec.OperatorOwnedAcceptanceCriteria, version.Spec.AcceptanceGateOwnedAcceptanceCriteria,
            Tasks = goal.Tasks.OrderBy(task => task.Id.Value, StringComparer.Ordinal).Select(task => new
            {
                task.Id, task.Description, task.RequiredRole, task.Status, task.VerificationPlan,
                task.LatestRetryAt, task.CriterionRetryCount, task.LastVerification,
                task.LastExecution, task.LastDispatch
            }),
            Obligations = goal.CriterionEvidenceObligations.OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => new { item.Id, item.CriterionVersion, item.CriterionIndex,
                    item.Criterion, item.Owner, item.RequiredScope, item.Provenance,
                    item.FindingStableId, item.ReplacementObligationId, item.ReplacementOwner })
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(context)))).ToLowerInvariant();
    }

    // This operation cannot launch. Keep writer custody until the consumer has applied
    // the receipt, so another tick cannot supersede or apply it between read and landing.
    internal ConductorAdvanceResult? ReplayPassedReconciledReceipt(
        ConductorParallelAcceptanceCandidate currentCandidate,
        Func<ConductorParallelAcceptanceAttemptDecision, ConductorAdvanceResult?> apply)
    {
        var goalId = currentCandidate.Goal.Id.Value;
        if (TryReadLatest(goalId) is not { Outcome: ConductorParallelAcceptanceAttemptOutcome.Passed,
                ReconciledAt: not null } observed)
            return null;
        var directory = Path.Combine(_rootDirectory, goalId);
        using var custody = _tryAcquireAttemptWriterLease(directory, TimeSpan.Zero);
        if (custody is null)
            throw new AcceptanceArtifactWriterLeaseBusyException(directory, observed.AttemptId);
        var latest = TryReadLatest(goalId);
        if (latest is null || latest.AttemptId != observed.AttemptId)
            return null;
        var attempt = ReadCanonicalAttempt(latest.MetadataPath, goalId);
        if (attempt.ReplayLandedAt is not null)
        {
            var reason = $"Receipt {attempt.AttemptId} already landed; awaiting lifecycle reconciliation.";
            return new ConductorAdvanceResult(goalId, currentCandidate.GoalPrefix, attempt.PolicyName ?? "replay",
                new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, reason)
                {
                    Decision = new PolicyDecisionRecord("acceptance-receipt-replay", "Hold", 3,
                        "receipt-already-landed", reason, Array.AsReadOnly<PolicyDecisionFact>(
                        [new("goalId", goalId), new("attemptId", attempt.AttemptId),
                            new("attemptOutcome", attempt.Outcome.ToString()),
                            new("replayLandedAt", attempt.ReplayLandedAt.Value.ToString("O")),
                            new("branchHeadSha", attempt.BranchHeadSha ?? string.Empty),
                            new("mainHeadSha", attempt.MainHeadSha ?? string.Empty)]))
                });
        }
        if (!CanReplay(attempt, currentCandidate.Goal) || attempt.ScopePaths is null)
            return null;
        var original = ConductorParallelAcceptanceCandidate.Create(currentCandidate.Goal, attempt.SlotIndex,
            attempt.ScopePaths, attempt.BranchHeadSha, attempt.MainHeadSha);
        if (!HasSameReplayScope(attempt, currentCandidate))
            return null;
        // A branch-only change is new work, not a main-advance carry candidate.
        if (string.Equals(attempt.MainHeadSha, currentCandidate.MainHeadSha, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(attempt.BranchHeadSha, currentCandidate.BranchHeadSha, StringComparison.OrdinalIgnoreCase))
            return null;
        var run = ReadPassedReplayRun(attempt, original);
        if (run is null)
            return null;
        var result = apply(ConductorParallelAcceptanceAttemptDecision.Completed(attempt, run));
        if (result?.Outcome is ConductorAdvanceOutcome.Executed { Description: var message } &&
            message.StartsWith("Landed:", StringComparison.Ordinal))
            WriteAttemptFile(ReadCanonicalAttempt(attempt.MetadataPath, goalId) with { ReplayLandedAt = _utcNow() });
        return result;
    }

    private ConductorParallelAcceptanceRunResult? ReadPassedReplayRun(
        ConductorParallelAcceptanceAttempt attempt, ConductorParallelAcceptanceCandidate original)
    {
        var directory = Path.Combine(_rootDirectory, attempt.GoalId);
        ConductorParallelAcceptanceRunArtifact? artifact;
        try
        {
            // Registration, not serialized paths, owns the receipt location.
            var resultText = ReadAllTextSharedWithRetry(Path.Combine(directory, attempt.AttemptId + ".result.json"));
            if (attempt.ReplayResultFingerprint is null || HashReplayResult(resultText) != attempt.ReplayResultFingerprint)
                return null;
            artifact = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(resultText, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
        if (artifact is not { Kind: "accepted", Acceptance: { Passed: true } acceptance } ||
            acceptance.RequiredUnmetCriteria.Count > 0 ||
            !string.Equals(artifact.BranchHeadSha, attempt.BranchHeadSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(artifact.MainHeadSha, attempt.MainHeadSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(acceptance.BranchHeadSha, attempt.BranchHeadSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(acceptance.MainHeadSha, attempt.MainHeadSha, StringComparison.OrdinalIgnoreCase))
            return null;
        return ConductorParallelAcceptanceRunResult.Accepted(original, acceptance);
    }

    private static bool CanReplay(ConductorParallelAcceptanceAttempt attempt, Goal goal) =>
        attempt.GoalId == goal.Id.Value && attempt.Kind == GateDispatchKind &&
        attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Passed &&
        attempt.ReconciledAt is not null && attempt.SupersededBy is null &&
        attempt.ReplayLandedAt is null &&
        !string.IsNullOrWhiteSpace(attempt.BranchHeadSha) && !string.IsNullOrWhiteSpace(attempt.MainHeadSha) &&
        attempt.ReplayContextFingerprint is not null &&
        attempt.ReplayContextFingerprint == BuildReplayContextFingerprint(goal) &&
        !goal.GetOutstandingCriterionEvidenceObligations(attempt.BranchHeadSha).Any(obligation =>
            obligation.Owner is not (CriterionEvidenceOwner.Operator or CriterionEvidenceOwner.Worker) ||
            obligation.RequiredScope == CriterionEvidenceScopes.FullAcceptanceGate);

    private ConductorParallelAcceptanceAttemptDecision? TryReusePassedReconciledAttempt(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        string dispatchKind,
        string? focusedEvidenceRequest,
        ConductorFocusedEvidenceRequestContext? requestContext)
    {
        if (!CanReplay(attempt, candidate.Goal) || !HasSameReplayScope(attempt, candidate) ||
            !string.Equals(dispatchKind, GateDispatchKind, StringComparison.Ordinal) ||
            focusedEvidenceRequest is not null || requestContext is not null ||
            string.IsNullOrWhiteSpace(candidate.BranchHeadSha) ||
            string.IsNullOrWhiteSpace(candidate.MainHeadSha) ||
            !MatchesCandidate(attempt, candidate, dispatchKind, focusedEvidenceRequest, requestContext))
        {
            return null;
        }

        var effectiveCandidate = ConductorParallelAcceptanceCandidate.Create(
            candidate.Goal,
            attempt.SlotIndex,
            attempt.ScopePaths ?? candidate.ScopePaths,
            attempt.BranchHeadSha,
            attempt.MainHeadSha);
        var run = ReadPassedReplayRun(attempt, effectiveCandidate);
        return run is null ? null : ConductorParallelAcceptanceAttemptDecision.Completed(attempt, run);
    }
}
