using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record BoardFillVerifierBinding(string? Profile, string? Alias, string? Effort, string? Error)
{
    internal static BoardFillVerifierBinding Resolve(ModelFunctionCatalog catalog)
    {
        if (catalog.Bindings is null || catalog.Bindings.Any(binding => binding is null))
            return new(null, null, null, "malformed-catalog");
        var bindings = catalog.ForPurpose(ModelFunctionPurposes.BoardFillVerifier);
        var subscription = bindings.Count == 1 ? bindings[0].Subscription : null;
        var cause = bindings.Count != 1 ? "expected-one-binding" : subscription is null ? "missing-subscription" :
            !string.Equals(subscription.WorkerProfileName, WorkerProfileDispatcher.OpenAiSubscriptionProfileName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(subscription.WorkerProfileName, WorkerProfileDispatcher.AnthropicSubscriptionProfileName, StringComparison.OrdinalIgnoreCase)
                ? "wrong-profile" :
            string.IsNullOrWhiteSpace(subscription.ModelAlias) ? "empty-model-alias" :
            subscription.ModelAlias.Any(char.IsControl) ? "malformed-model-alias" :
            subscription.ReasoningEffort is { Length: > 0 } effort && effort is not
                ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max") ? "invalid-effort" : null;
        return cause is null ? new(subscription!.WorkerProfileName, subscription.ModelAlias!.Trim(), subscription.ReasoningEffort, null)
            : new(null, null, null, cause);
    }
}
