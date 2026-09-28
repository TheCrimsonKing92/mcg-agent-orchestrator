using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorParallelAcceptanceAttemptCoordinator
{
    private const string RefusedCarryHoldOperation = "conductor:criterion-evidence-refused-carry-hold";

    private bool TryHoldRefusedCarryRelaunch(
        ConductorParallelAcceptanceAttempt current,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        string dispatchKind,
        out ConductorParallelAcceptanceAttemptDecision decision)
    {
        decision = null!;
        if (current.Outcome != ConductorParallelAcceptanceAttemptOutcome.Passed ||
            !string.Equals(dispatchKind, GateDispatchKind, StringComparison.Ordinal) ||
            !string.Equals(current.Kind, GateDispatchKind, StringComparison.Ordinal) ||
            !string.Equals(current.BranchHeadSha, candidate.BranchHeadSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.MainHeadSha, candidate.MainHeadSha, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(candidate.BranchHeadSha) ||
            string.IsNullOrWhiteSpace(candidate.MainHeadSha))
            return false;

        var hold = AcceptanceCriterionEvidence.DescribeRefusedCarryHold(
            candidate.Goal, candidate.BranchHeadSha, _executionDirectory);
        if (hold is null) return false;

        RecordRefusedCarryTimelineNoteOnce(candidate, hold.Reason);

        decision = ConductorParallelAcceptanceAttemptDecision.Completed(
            current,
            ConductorParallelAcceptanceRunResult.Early(
                candidate,
                new ConductorAdvanceResult(candidate.Goal.Id.Value, candidate.GoalPrefix, policy.Name, hold)));
        return true;
    }

    private void RecordRefusedCarryTimelineNoteOnce(
        ConductorParallelAcceptanceCandidate candidate, string reason)
    {
        if (string.IsNullOrWhiteSpace(_executionDirectory)) return;

        var instanceId = $"{candidate.BranchHeadSha!.ToLowerInvariant()}:{candidate.MainHeadSha!.ToLowerInvariant()}";
        if (GoalOperationJournal.ReadActive(_executionDirectory, candidate.Goal.Id).Entries.Any(entry =>
                entry.Operation == RefusedCarryHoldOperation &&
                string.Equals(entry.OperationInstanceId, instanceId, StringComparison.Ordinal)))
            return;

        var obligationIds = candidate.Goal.GetOutstandingCriterionEvidenceObligations(candidate.BranchHeadSha)
            .Where(item => item.Owner == CriterionEvidenceOwner.Acceptance &&
                !string.IsNullOrWhiteSpace(item.ExpectedCandidateSha) &&
                !string.Equals(item.ExpectedCandidateSha, candidate.BranchHeadSha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.RequiredScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal))
            .Select(item => item.Id);
        GoalOperationJournal.Completed(
            _executionDirectory,
            candidate.Goal,
            RefusedCarryHoldOperation,
            $"obligations={string.Join(',', obligationIds)}; candidate={candidate.BranchHeadSha}; main={candidate.MainHeadSha}; {reason}",
            mainHeadSha: candidate.MainHeadSha,
            operationInstanceId: instanceId);
    }
}
