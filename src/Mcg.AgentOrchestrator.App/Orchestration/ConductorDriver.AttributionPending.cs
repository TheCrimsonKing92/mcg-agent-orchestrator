using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private ConductorAdvanceResult HoldAttributionPending(Goal goal, string goalPrefix,
        ConductorAutonomyPolicy policy, AcceptanceVerificationSummary acceptance,
        ApparatusRedDisposition.AttributionPending disposition)
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
            _recordAcceptanceFailure(goal, acceptance.FailedChecks is { Count: > 0 } failedChecks
                ? failedChecks : acceptance.RequiredUnmetCriteria.Select(check => check.Name).ToArray(),
                proof.CandidateSha, proof.MainSha, acceptance.CheckAttributions, acceptance.BaselineAttestation);
        var kernel = _cohortKernel ?? _conductorTickKernel;
        if (kernel is not null) kernel.RetainAcceptanceAttributionPending(goal.Id, pending);
        else goal.RetainAcceptanceAttributionPending(pending);
        return RetainedAttributionPendingHold(goal, goalPrefix, policy, pending);
    }

    private ConductorAdvanceResult? TryHoldRetainedAttributionPending(Goal goal, string goalPrefix,
        ConductorAutonomyPolicy policy) => goal.Status == GoalStatus.AcceptanceFailed &&
        goal.LatestAcceptanceFailure?.AttributionPending is { } pending
            ? RetainedAttributionPendingHold(goal, goalPrefix, policy, pending) : null;

    private ConductorAdvanceResult RetainedAttributionPendingHold(Goal goal, string goalPrefix,
        ConductorAutonomyPolicy policy, AcceptanceAttributionPending pending)
    {
        var evidence = AcceptanceApparatusDispositionPolicy.AttributionPendingHold(goal.Id.Value, "pending", pending).Facts;
        var reason = $"{AcceptanceAttributionPending.EvidenceKind}: candidate={pending.CandidateSha} main={pending.MainSha} " +
            $"attempt={pending.AttemptId}; executed candidate failures remain RED: {evidence.TestIdentities}. " +
            $"Baseline attribution is unavailable/unexecuted: {evidence.BaselineEvidence}. " +
            $"Receipts: {evidence.ReceiptReferences}. Other apparatus evidence: {pending.OtherEvidenceKind}. " +
            "Supply attributable baseline evidence or a relevant verified repair, then run " +
            $"acceptance-retry {goalPrefix} <reason> --confirm-acceptance-retry. No automatic gate or Developer retry is admitted.";
        RecordEscalation(goal, GoalLifecycleState.AcceptanceFailed, reason);
        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.AcceptanceFailed, reason, StableIdentity: pending.Identity)
            { Decision = AcceptanceApparatusDispositionPolicy.AttributionPendingHold(goal.Id.Value, reason, pending).ToRecord() });
    }
}
