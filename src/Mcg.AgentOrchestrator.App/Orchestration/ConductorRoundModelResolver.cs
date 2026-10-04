using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorRoundModelResolver
{
    internal const string DefaultModelAlias = "sonnet";

    internal static ConductorRoundModel Resolve(ModelFunctionCatalog? catalog, string purpose)
    {
        var bindings = (catalog ?? ModelFunctionCatalog.Empty).ForPurpose(purpose);
        if (bindings.Count == 0) return new(purpose, DefaultModelAlias, null, null);
        if (bindings.Count > 1) return Invalid("multiple-bindings");
        var subscription = bindings[0].Subscription;
        if (subscription is null) return Invalid("missing-subscription");
        if (!string.Equals(subscription.WorkerProfileName,
                WorkerProfileDispatcher.AnthropicSubscriptionProfileName, StringComparison.OrdinalIgnoreCase))
            return Invalid($"non-claude-profile:{subscription.WorkerProfileName}");
        if (string.IsNullOrWhiteSpace(subscription.ModelAlias)) return Invalid("empty-model-alias");
        return new(purpose, subscription.ModelAlias.Trim(),
            string.IsNullOrWhiteSpace(subscription.ReasoningEffort) ? null : subscription.ReasoningEffort, null);

        ConductorRoundModel Invalid(string cause)
        {
            var identities = string.Join(",", (catalog ?? ModelFunctionCatalog.Empty).Bindings
                .Select((binding, index) => (binding, index))
                .Where(entry => string.Equals(entry.binding.Purpose, purpose, StringComparison.OrdinalIgnoreCase))
                .Select(entry => $"{entry.binding.Name ?? entry.binding.Purpose}@{entry.index}"));
            return new(purpose, null, null, $"model-binding-invalid:{purpose} binding={identities} cause={cause}");
        }
    }

    internal static string ConductReason(Exception? failure, string existing) => failure switch
    {
        ConductorModelRoundException { ModelAlias: null } => failure.Message,
        ConductorModelRoundException round => WithModel(existing, round.ModelAlias),
        _ => existing
    };

    internal static string WithModel(string existing, string? modelAlias) =>
        modelAlias is null ? existing : $"{existing} model={modelAlias}";
}

internal sealed record ConductorRoundModel(string Purpose, string? Alias, string? ReasoningEffort, string? InvalidReason)
{
    internal bool IsValid => InvalidReason is null;
    internal string ModelArguments => $"--model {Alias}" +
        (ReasoningEffort is null ? "" : $" {ClaudeCliEffortPolicy.EffortFlag} {ReasoningEffort}");
}

internal sealed class ConductorModelRoundException(string message, string? modelAlias) : InvalidOperationException(message)
{
    internal string? ModelAlias { get; } = modelAlias;
}
