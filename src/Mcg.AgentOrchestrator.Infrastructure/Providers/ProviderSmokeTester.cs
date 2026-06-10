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
            "Connectivity smoke test. Reply exactly OK.",
            [new ModelMessage("user", "Reply OK.")],
            new ModelOptions(Temperature: 0, MaxOutputTokens: 8));

        var response = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

        return new ProviderSmokeResult(
            provider.ProviderName,
            response.Text,
            response.Usage,
            response.StopReason);
    }
}
