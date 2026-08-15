namespace Mcg.AgentOrchestrator.Core;

public static class SourceBacklogClaimEligibility
{
    public static GoalReplacementOutcome Evaluate(
        GoalReplacementDisposition disposition,
        GoalReplacementEligibilityFacts facts)
    {
        if (facts.IsProtected || facts.Status is not (GoalStatus.Cancelled or GoalStatus.Failed or GoalStatus.Superseded))
            return GoalReplacementOutcome.ProtectedOwner;

        return disposition switch
        {
            GoalReplacementDisposition.ZeroWorkCorrection
                when facts.Status == GoalStatus.Cancelled && !facts.HasWork => GoalReplacementOutcome.Succeeded,
            GoalReplacementDisposition.AbandonFailedAttempt
                when facts.Status == GoalStatus.Failed => GoalReplacementOutcome.Succeeded,
            GoalReplacementDisposition.SupersedeUnlandedAttempt
                when facts.Status == GoalStatus.Superseded ||
                     (facts.Status == GoalStatus.Cancelled && facts.HasWork) => GoalReplacementOutcome.Succeeded,
            _ => GoalReplacementOutcome.IneligibleDisposition
        };
    }
}
