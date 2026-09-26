namespace Mcg.AgentOrchestrator.Core;

public static class AcceptancePolicyChangeDecision
{
    public const string FingerprintEvidenceId = "owner-protected-change-fingerprint";

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

    public static async Task<bool> IsApprovedForFingerprintAsync(
        ICollaborationItemStore store, string goalId, string fingerprint,
        CancellationToken cancellationToken = default)
    {
        if (!IsVersionOneFingerprint(fingerprint)) return false;
        var prefix = $"acceptance-policy-change:{goalId}:";
        foreach (var request in await store.ListDecisionRequestsAsync(goalId, cancellationToken))
        {
            if (!request.Id.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var sha = request.Id[prefix.Length..];
            if (!await IsApprovedAsync(store, goalId, sha, cancellationToken)) continue;
            var state = await store.GetDecisionStateAsync(request.Id, cancellationToken);
            if (state?.Receipt?.EvidenceHashes.Any(entry =>
                    entry.ReceiptId == FingerprintEvidenceId &&
                    IsVersionOneFingerprint(entry.ContentHash) &&
                    string.Equals(entry.ContentHash, fingerprint, StringComparison.Ordinal)) == true)
                return true;
        }
        return false;
    }

    private static bool IsVersionOneFingerprint(string? value) =>
        value is { Length: 67 } && value.StartsWith("v1:", StringComparison.Ordinal) &&
        value.AsSpan(3).ToArray().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
