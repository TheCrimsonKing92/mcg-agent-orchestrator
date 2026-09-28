using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    // This projection judges pair compatibility only. It never makes the in-review goal gate-ready.
    internal GateReadyCandidateProjectionResult ProjectInReviewCohortPartner(
        Goal goal, ConductorAutonomyPolicy policy)
    {
        if (HasActiveApparatusHold(goal, out _))
            return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.ApparatusHold);

        try
        {
            var risk = _classifyChangeRisk(goal);
            if (risk is null)
                return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.RiskUnknown);

            return _gateReadyCandidateProjector?.Project(new GateReadyCandidateInput(
                goal.Id,
                GoalLifecycleState.Verified,
                true,
                risk,
                policy.GetTransitionDecision(GoalLifecycleState.Merged, risk.Value)))
                ?? ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.RevisionUnknown);
        }
        catch
        {
            return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.RevisionUnknown);
        }
    }
}
