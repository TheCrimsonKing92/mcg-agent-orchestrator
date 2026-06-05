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
        var userMessage = request.Messages.LastOrDefault()?.Content ?? string.Empty;
        var output =
            $"[{ProviderName} offline adapter] Completed assigned orchestration task." + Environment.NewLine +
            "Evidence: provider registry resolved this adapter, generated a model request, and recorded this output on the task." + Environment.NewLine +
            "Next: replace this offline adapter with the live provider implementation when API credentials are configured." + Environment.NewLine +
            Environment.NewLine +
            userMessage;

        return Task.FromResult(new ModelResponse(output, new ModelUsage(null, null), "offline-scripted"));
    }
}


