using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class GateHeartbeatProgressFailsafe
{
    internal static async Task<AcceptanceGateProgress> WaitForTargetAsync(
        Task<AcceptanceGateProgress> targetSignal,
        string target,
        CancellationTokenSource cancellation,
        Task run,
        Task failsafe)
    {
        var completed = await Task.WhenAny(targetSignal, failsafe);
        if (completed == targetSignal)
            return await targetSignal;

        cancellation.Cancel();
        try
        {
            await run;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // RunOwnedAsync completes cancellation only after its owned child has exited.
        }

        throw new Xunit.Sdk.XunitException(
            $"Missing progress target '{target}' before the gate heartbeat failsafe; " +
            "the run was cancelled and child exit was awaited.");
    }
}
