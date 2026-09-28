using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorParallelAcceptanceAttemptCoordinator
{
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
            string.IsNullOrWhiteSpace(candidate.BranchHeadSha))
            return false;

        var hold = AcceptanceCriterionEvidence.DescribeRefusedCarryHold(
            candidate.Goal, candidate.BranchHeadSha, _executionDirectory);
        if (hold is null) return false;

        decision = ConductorParallelAcceptanceAttemptDecision.Completed(
            current,
            ConductorParallelAcceptanceRunResult.Early(
                candidate,
                new ConductorAdvanceResult(candidate.Goal.Id.Value, candidate.GoalPrefix, policy.Name, hold)));
        return true;
    }
}
