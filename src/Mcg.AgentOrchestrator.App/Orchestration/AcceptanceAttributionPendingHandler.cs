using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns retention and presentation of pending attribution; the driver supplies its existing sinks.
internal static class AcceptanceAttributionPendingHandler
{
    internal static ConductorAdvanceResult Hold(Goal goal, string goalPrefix,
        ConductorAutonomyPolicy policy, AcceptanceVerificationSummary acceptance,
        ApparatusRedDisposition.AttributionPending disposition,
        Action<Goal, IReadOnlyList<string>, string?, string?, IReadOnlyList<AcceptanceCheckAttribution>?, string?> recordFailure,
        Action<Goal, GoalLifecycleState, string> recordEscalation)
    {
        var proof = disposition.PendingTests[0].ConfirmedLaneFailure!.Attribution;
        var pending = new AcceptanceAttributionPending(proof.CandidateSha, proof.MainSha, proof.AttemptId,
            disposition.PendingTests.Select(test =>
            {
                var attribution = test.ConfirmedLaneFailure!.Attribution;
                AcceptanceAttributionPendingReceipt ConvertReceipt(Infrastructure.AcceptanceLaneRerunReceipt receipt) =>
                    new(receipt.Path, receipt.Sha256, receipt.RunId);
                return new AcceptanceAttributionPendingTest(test.CheckName, test.TestIdentity,
                    test.BaselineAttribution!.Evidence,
                    attribution.FirstReceipts.Select(ConvertReceipt).ToArray(),
                    attribution.RerunReceipts.Select(ConvertReceipt).ToArray());
            }).ToArray(), disposition.OtherEvidenceKind);

        // Native reconciliation may already have recorded this occurrence. Keep its timestamp so
        // the existing acceptance-retry command can resolve precisely its landing escalation.
        if (goal.LatestAcceptanceFailure is not { } failure || failure.BranchHeadSha != proof.CandidateSha ||
            failure.MainHeadSha != proof.MainSha)
            recordFailure(goal, acceptance.FailedChecks is { Count: > 0 } failedChecks
                ? failedChecks : acceptance.RequiredUnmetCriteria.Select(check => check.Name).ToArray(),
                proof.CandidateSha, proof.MainSha, acceptance.CheckAttributions, acceptance.BaselineAttestation);
        goal.RetainAcceptanceAttributionPending(pending);
        return RetainedHold(goal, goalPrefix, policy, pending, recordEscalation);
    }

    internal static ConductorAdvanceResult? TryHoldRetained(Goal goal, string goalPrefix,
        ConductorAutonomyPolicy policy, Action<Goal, GoalLifecycleState, string> recordEscalation) => goal.Status == GoalStatus.AcceptanceFailed &&
        goal.LatestAcceptanceFailure?.AttributionPending is { } pending
            ? RetainedHold(goal, goalPrefix, policy, pending, recordEscalation) : null;

    private static ConductorAdvanceResult RetainedHold(Goal goal, string goalPrefix,
        ConductorAutonomyPolicy policy, AcceptanceAttributionPending pending,
        Action<Goal, GoalLifecycleState, string> recordEscalation)
    {
        var evidence = AcceptanceApparatusDispositionPolicy.AttributionPendingHold(goal.Id.Value, "pending", pending).Facts;
        var reason = $"{AcceptanceAttributionPending.EvidenceKind}: candidate={pending.CandidateSha} main={pending.MainSha} " +
            $"attempt={pending.AttemptId}; executed candidate failures remain RED: {evidence.TestIdentities}. " +
            $"Baseline attribution is unavailable/unexecuted: {evidence.BaselineEvidence}. " +
            $"Receipts: {evidence.ReceiptReferences}. Other apparatus evidence: {pending.OtherEvidenceKind}. " +
            "Supply attributable baseline evidence or a relevant verified repair, then run " +
            $"acceptance-retry {goalPrefix} <reason> --confirm-acceptance-retry. No automatic gate or Developer retry is admitted.";
        recordEscalation(goal, GoalLifecycleState.AcceptanceFailed, reason);
        return new ConductorAdvanceResult(goal.Id.Value, goalPrefix, policy.Name,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.AcceptanceFailed, reason, StableIdentity: pending.Identity)
            { Decision = AcceptanceApparatusDispositionPolicy.AttributionPendingHold(goal.Id.Value, reason, pending).ToRecord() });
    }
}
