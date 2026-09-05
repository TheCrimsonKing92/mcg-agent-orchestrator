namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceOverlappedCheckRunner
{
    internal static async Task<(T Independent, T Lanes)> RunAsync<T>(
        Func<Task<T>> runIndependent,
        Func<Task<T>> runLanes)
    {
        var independentTask = runIndependent();
        var laneTask = runLanes();
        await Task.WhenAll(independentTask, laneTask).ConfigureAwait(false);
        return (await independentTask.ConfigureAwait(false), await laneTask.ConfigureAwait(false));
    }
}
