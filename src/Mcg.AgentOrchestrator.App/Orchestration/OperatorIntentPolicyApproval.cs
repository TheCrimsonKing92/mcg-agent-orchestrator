using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class OperatorIntentPolicyApproval
{
    internal static void Apply(
        ICollaborationItemStore decisions,
        Goal goal,
        OperatorIntentRecord intent,
        ApprovePolicyChangeOperatorIntentPayload payload,
        DateTimeOffset now,
        string? protectedChangeFingerprint = null)
    {
        if (intent.ActorKind != OperatorActorKind.Human)
            throw new InvalidOperationException("Acceptance policy approval requires a human actor kind.");
        if (intent.AuthenticationAssurance != "local-process")
            throw new InvalidOperationException("Acceptance policy approval requires local operator authentication.");
        if (!AcceptancePolicyChangeDecision.IsFullSha(payload.CandidateSha))
            throw new ArgumentException("A full candidate commit SHA is required.");
        if (string.IsNullOrWhiteSpace(payload.Reason))
            throw new ArgumentException("An approval reason is required.");

        var goalId = goal.Id.Value;
        var sha = payload.CandidateSha.ToLowerInvariant();
        var actionRef = new DecisionActionRef("approve-acceptance-policy-change");
        var requestId = AcceptancePolicyChangeDecision.RequestId(goalId, sha);
        var request = new DecisionRequest(
            requestId, DecisionRequestKind.RiskApproval, goalId,
            AcceptancePolicyChangeDecision.Subject(goalId, sha), payload.Reason.Trim(),
            "acceptance-policy-change-v1", EvidenceManifest.Create(
                protectedChangeFingerprint is null ? [] :
                [new EvidenceManifestEntry(AcceptancePolicyChangeDecision.FingerprintEvidenceId, protectedChangeFingerprint)]), now.AddHours(24),
            DecisionDefaultDisposition.Deny,
            new DecisionBlockingImpact("Acceptance policy change remains blocked without human approval.", []),
            [DecisionReuseScope.ThisOccurrence],
            [new DecisionAllowedAction(actionRef, "Approve candidate policy change",
                DecisionActionKind.AcceptancePolicyChangeApproved, AuthorizationTier.AttestLand,
                now.AddHours(24), null)], now);
        decisions.RaiseDecisionRequestAsync(request).GetAwaiter().GetResult();
        decisions.RecordDecisionAsync(
            requestId, OperatorActorIdentity.Format(intent.Actor, intent.ActorKind), intent.Channel,
            AuthorizationTier.AttestLand, null,
            new DecisionResponse(actionRef, payload.Reason.Trim(), DecisionReuseScope.ThisOccurrence, false),
            now).GetAwaiter().GetResult();
        if (!AcceptancePolicyChangeDecision.IsApprovedAsync(decisions, goalId, sha).GetAwaiter().GetResult())
            throw new InvalidOperationException("The candidate approval decision was not recorded as a human approval.");
    }
}
