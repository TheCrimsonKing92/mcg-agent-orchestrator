using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Providers;

internal static class StartupProviderComposition
{
    internal static (IModelProviderRegistry Providers, AgentCatalog? AgentFallback) Create(
        bool execution,
        Func<string, bool>? probe = null,
        string? ollamaBaseUrl = null,
        string? llamaBaseUrl = null)
    {
        if (!execution)
        {
            return (new InMemoryModelProviderRegistry([]), null);
        }

        var providers = ProviderRegistryFactory.CreateDefaultProviders(
            probe ?? LocalInferenceProbe.IsReachable,
            ollamaBaseUrl ?? OllamaDefaults.ResolveBaseUrl(),
            llamaBaseUrl ?? LlamaCppDefaults.ResolveBaseUrl(),
            out var llamaCppReachable);

        return (providers, llamaCppReachable ? AgentCatalog.LlamaCppDefault() : null);
    }
}
