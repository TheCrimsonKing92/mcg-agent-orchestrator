using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorRoundModelResolver
{
    internal const string DefaultModelAlias = "sonnet";

    internal static ConductorRoundModel Resolve(ModelFunctionCatalog? catalog, string purpose, bool fallback = false)
    {
        var bindings = (catalog ?? ModelFunctionCatalog.Empty).ForPurpose(purpose);
        var author = string.Equals(purpose, ModelFunctionPurposes.ConductorAuthor, StringComparison.OrdinalIgnoreCase);
        if (bindings.Count == 0) return fallback ? Invalid("missing-subscription-fallback") : new(purpose, DefaultModelAlias, null, null);
        if (bindings.Count > 1) return Invalid("multiple-bindings");
        var subscription = fallback ? bindings[0].SubscriptionFallback : bindings[0].Subscription;
        if (fallback && !author) return Invalid("fallback-unsupported-purpose");
        if (fallback && subscription is null) return Invalid("missing-subscription-fallback");
        if (subscription is null) return Invalid("missing-subscription");
        var codex = author && string.Equals(subscription.WorkerProfileName, "codex-cli", StringComparison.OrdinalIgnoreCase);
        if (!codex && !string.Equals(subscription.WorkerProfileName,
                WorkerProfileDispatcher.AnthropicSubscriptionProfileName, StringComparison.OrdinalIgnoreCase))
            return Invalid($"non-claude-profile:{subscription.WorkerProfileName}");
        if (string.IsNullOrWhiteSpace(subscription.ModelAlias)) return Invalid("empty-model-alias");
        var alias = EffectiveModelAssignment.Normalize(codex ? "OpenAI" : "Anthropic", subscription.ModelAlias.Trim());
        var effort = string.IsNullOrWhiteSpace(subscription.ReasoningEffort)
            ? codex ? AgentCatalog.DefaultSubscriptionReasoningEffort("OpenAI", alias) : null
            : subscription.ReasoningEffort;
        return new(purpose, alias, effort, null, codex ? "codex-cli" : "claude-cli");

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

internal sealed record ConductorRoundModel(string Purpose, string? Alias, string? ReasoningEffort, string? InvalidReason,
    string WorkerProfileName = "claude-cli")
{
    internal bool IsValid => InvalidReason is null;
    internal string ModelArguments => $"--model {Alias}" +
        (ReasoningEffort is null ? "" : $" {ClaudeCliEffortPolicy.EffortFlag} {ReasoningEffort}");
}

internal sealed class ConductorModelRoundException(string message, string? modelAlias, string? terminalReason = null) : InvalidOperationException(message)
{
    internal string? ModelAlias { get; } = modelAlias;
    internal string? TerminalReason { get; } = terminalReason;
}
