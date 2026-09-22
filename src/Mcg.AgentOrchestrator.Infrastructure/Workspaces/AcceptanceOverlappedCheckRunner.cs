namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceOverlappedCheckRunner
{
    internal static async Task<(T Independent, T Lanes)> RunAsync<T>(
        Func<Task<T>> runIndependent,
        Func<Task<T>> runLanes,
        IAcceptanceRunExecutionContext? execution = null)
    {
        var independentTask = Track(runIndependent(), execution);
        Task<T>? laneTask = null;
        try
        {
            laneTask = Track(runLanes(), execution);
            await Task.WhenAll(independentTask, laneTask).ConfigureAwait(false);
            return (await independentTask.ConfigureAwait(false), await laneTask.ConfigureAwait(false));
        }
        catch (Exception executionFailure)
        {
            execution?.Cancel();
            if (execution is not null)
            {
                try
                {
                    await execution.DrainAsync().ConfigureAwait(false);
                }
                catch (Exception drainFailure)
                {
                    throw new AggregateException(
                        "Acceptance overlap failed and teardown could not observe every started task.",
                        executionFailure,
                        drainFailure);
                }
            }
            else
                try { await independentTask.ConfigureAwait(false); } catch { }

            if (laneTask is not null && laneTask.IsFaulted)
            {
                _ = laneTask.Exception;
            }

            throw;
        }
    }

    private static TTask Track<TTask>(TTask task, IAcceptanceRunExecutionContext? execution)
        where TTask : Task => execution is null ? task : execution.TrackStarted(task);
}
