using System.Diagnostics.CodeAnalysis;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Retirement applies to newly effective assignments, never to historical dispatch receipts.
public static class EffectiveModelAssignment
{
    [return: NotNullIfNotNull(nameof(modelName))]
    public static string? Normalize(string? providerName, string? modelName) =>
        string.Equals(providerName, "OpenAI", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(modelName, AgentCatalog.OpenAiGpt6SolSubscriptionModelAlias, StringComparison.OrdinalIgnoreCase)
            ? AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias
            : modelName;
}
