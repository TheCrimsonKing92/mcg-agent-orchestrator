namespace Mcg.AgentOrchestrator.Core;

/// <summary>Audit text captured from the original binding before a provider hold reassignment.</summary>
internal sealed record ProviderHoldSubstitutionProvenance(string ReassignmentReason, string Message)
{
    public static ProviderHoldSubstitutionProvenance Create(
        AgentId? heldAgentId,
        AgentId substituteId,
        string heldBindingKey,
        string evidenceReceipt,
        ProviderSubscriptionCooldown? subscriptionCooldown)
    {
        var reason = subscriptionCooldown is null
            ? "Provider budget hold substitution." : "Provider subscription cooldown substitution.";
        var message = $"held binding={heldBindingKey}; held agent={heldAgentId?.Value ?? "none"}; substitute={substituteId.Value}; receipt={(string.IsNullOrWhiteSpace(evidenceReceipt) ? "none" : evidenceReceipt)}" +
            (subscriptionCooldown is null ? "" :
                $"; reason=subscription-cooldown; source task={subscriptionCooldown.SourceTaskId.Value}; source dispatch={subscriptionCooldown.DispatchStartedAt:O}; reset={subscriptionCooldown.RetryAfter:O}");
        return new ProviderHoldSubstitutionProvenance(reason, message);
    }
}
