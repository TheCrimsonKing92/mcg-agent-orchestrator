using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A read-only, per-tick reservation of a current receipt, never a new gate or durable hold.
internal static class PassedCohortReceiptApprovalHold
{
    private const string IdentityPrefix = "cohort-approval:";

    internal sealed record Decision(AcceptanceCohortReceipt Receipt, string Reason);

    internal static Decision? Evaluate(
        AcceptanceCohortReceipt receipt,
        IReadOnlyDictionary<GoalId, Goal> goals,
        IReadOnlyDictionary<GoalId, GateReadyCandidateProjectionResult> projections,
        IReadOnlySet<GoalId> otherwiseEligible,
        Func<GoalId, bool> isLanded,
        Func<AcceptanceCohortMemberBinding, LandingDenylistOwnerGate.Result> evaluateApproval)
    {
        if (!receipt.HasAuthoritativeLandingEvidence ||
            receipt.Identity.Members.Count != ConductorAcceptanceCohortSelector.CohortSize)
            return null;

        foreach (var member in receipt.Identity.Members)
        {
            if (!goals.TryGetValue(member.GoalId, out var goal) ||
                !otherwiseEligible.Contains(member.GoalId) ||
                ConductorWorkingSet.IsPreWalkExcludedGoal(goal) ||
                goal.Status is not (GoalStatus.Verified or GoalStatus.Verifying) ||
                goal.OutstandingCriterionEvidenceObligations.Any(item => item.Owner != CriterionEvidenceOwner.Acceptance) ||
                isLanded(member.GoalId) ||
                !projections.TryGetValue(member.GoalId, out var projection) ||
                projection is not GateReadyCandidateProjectionResult.Ready ready ||
                !string.Equals(ready.Projection.MainRevision, receipt.Identity.ObservedMainRevision, StringComparison.Ordinal) ||
                !string.Equals(ready.Projection.CandidateRevision, member.CandidateRevision, StringComparison.Ordinal))
                return null;
        }

        var missing = receipt.Identity.Members.OrderBy(member => member.GoalId.Value, StringComparer.Ordinal)
            .Select(member => (Member: member, Approval: evaluateApproval(member)))
            .Where(item => !item.Approval.Approved).ToArray();
        // An observation failure is an independent blocker, not proof of missing approval.
        if (missing.Length == 0 || missing.Any(item => item.Approval.Fingerprint == "unavailable" ||
                item.Approval.HoldReason.Contains(" diagnostic=", StringComparison.Ordinal)))
            return null;

        var waiting = string.Join(',', missing.Select(item => item.Member.GoalId.Value[..8]));
        return new(receipt, $"reason=PassedCohortReceiptApprovalHeld cohort={receipt.Identity.Value} receipt={receipt.ReceiptId} " +
            $"waiting={waiting}; cohort waits on landing-denylist approval for {waiting}, not on a gate; " +
            string.Join("; ", missing.Select(item => item.Approval.HoldReason)));
    }

    internal static ConductorAdvanceResult Held(Goal goal, ConductorAutonomyPolicy policy, Decision decision) =>
        new(goal.Id.Value, goal.Id.Value[..8], policy.Name,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, decision.Reason,
                IdentityPrefix + decision.Receipt.ReceiptId)
            {
                Owner = ConductorHoldOwner.AcceptanceQueue,
                Decision = new PolicyDecisionRecord("cohort-receipt-admission", "Hold", 1,
                    "passed-cohort-awaiting-landing-denylist-approval", decision.Reason,
                    Array.AsReadOnly<PolicyDecisionFact>(
                    [
                        new("goalId", goal.Id.Value),
                        new("receiptId", decision.Receipt.ReceiptId),
                        new("cohortIdentity", decision.Receipt.Identity.Value),
                        new("observedMainRevision", decision.Receipt.Identity.ObservedMainRevision)
                    ]))
            });

    internal static bool IsReadOnlyReservation(ConductorAdvanceResult result) =>
        result.Outcome is ConductorAdvanceOutcome.Held { Owner: ConductorHoldOwner.AcceptanceQueue, StableIdentity: { } identity } &&
        identity.StartsWith(IdentityPrefix, StringComparison.Ordinal);
}
