using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private static ConductorAdvanceOutcome.Held PreReviewEvidenceHold(
        GoalLifecycleState fromState,
        string goalId,
        string condition,
        string attemptId,
        string attemptOutcome,
        string reason,
        string? stableIdentity = null,
        ConductorHoldOwner owner = ConductorHoldOwner.None)
    {
        var decision = PreReviewEvidenceHoldPolicy.Evaluate(new PreReviewEvidenceHoldFacts(goalId)
        {
            Condition = condition,
            AttemptId = attemptId,
            AttemptOutcome = attemptOutcome,
            HoldReason = reason
        });
        return new ConductorAdvanceOutcome.Held(fromState, decision.Reason, stableIdentity)
        {
            Owner = owner,
            Decision = decision.ToRecord()
        };
    }
}
