namespace Mcg.AgentOrchestrator.Core;

public static class AcceptancePolicyChangeDecision
{
    public static string RequestId(string goalId, string candidateSha) =>
        $"acceptance-policy-change:{goalId}:{candidateSha.ToLowerInvariant()}";

    public static string Subject(string goalId, string candidateSha) =>
        $"Acceptance policy change goal={goalId} candidate={candidateSha.ToLowerInvariant()}";

    public static bool IsFullSha(string value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    public static async Task<bool> IsApprovedAsync(
        ICollaborationItemStore store, string goalId, string candidateSha,
        CancellationToken cancellationToken = default)
    {
        if (!IsFullSha(candidateSha)) return false;
        var state = await store.GetDecisionStateAsync(RequestId(goalId, candidateSha), cancellationToken);
        if (state?.Receipt is not { } receipt ||
            state.Request.GoalId != goalId ||
            state.Request.Subject != Subject(goalId, candidateSha) ||
            !OperatorActorIdentity.TryParse(receipt.ActorId, out _, out var actorKind) ||
            actorKind != OperatorActorKind.Human ||
            !DecisionAuthorization.Meets(receipt.AuthenticationAssurance, AuthorizationTier.AttestLand))
            return false;
        return state.Request.AllowedActions.Any(action =>
            action.Kind == DecisionActionKind.AcceptancePolicyChangeApproved &&
            action.ActionRef == receipt.Response.ActionRef);
    }
}
