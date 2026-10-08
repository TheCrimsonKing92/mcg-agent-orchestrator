using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class PlanSampleProviderBridge
{
    // Forward stdin verbatim to the existing API fake; never reconstruct a prompt in the adapter.
    internal static IDisposable Use(IModelProviderRegistry providers) =>
        PlanDecompositionSampleRound.PushProcessRunner(async (request, cancellationToken) =>
        {
            var response = await providers.GetRequired("Fake").CompleteAsync(
                new ModelRequest("", [new ModelMessage("user", request.StandardInput ?? "")], new ModelOptions()),
                cancellationToken);
            return new WorkerProcessRunResult(0, response.Text, "");
        });
}
