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
        var output =
            $"HUMAN_INPUT: {ProviderName} is configured with the offline adapter. Configure a live provider, choose a reachable local provider, or use subscription handoff before running this task.";

        return Task.FromResult(new ModelResponse(output, new ModelUsage(null, null), "offline-scripted"));
    }
}


