using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Providers;

internal sealed class ScriptedModelProvider : IModelProvider
{
    public ScriptedModelProvider(string providerName)
    {
        ProviderName = providerName;
    }

    public string ProviderName { get; }

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException(
            $"{ProviderName} is configured with the offline adapter. Configure a live provider, choose a reachable local provider, or use subscription handoff before running this task.");
    }
}


