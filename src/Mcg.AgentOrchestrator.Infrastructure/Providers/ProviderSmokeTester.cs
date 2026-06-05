using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record ProviderSmokeResult(
    string ProviderName,
    string ResponseText,
    ModelUsage? Usage,
    string StopReason);

public sealed class ProviderSmokeTester
{
    public async Task<ProviderSmokeResult> RunAsync(IModelProvider provider, CancellationToken cancellationToken = default)
    {
        var request = new ModelRequest(
            "You are a connectivity smoke test for an agent orchestrator. Reply with exactly OK.",
            [new ModelMessage("user", "Confirm the model provider is reachable by replying OK.")],
            new ModelOptions(Temperature: 0, MaxOutputTokens: 32));

        var response = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

        return new ProviderSmokeResult(
            provider.ProviderName,
            response.Text,
            response.Usage,
            response.StopReason);
    }
}
