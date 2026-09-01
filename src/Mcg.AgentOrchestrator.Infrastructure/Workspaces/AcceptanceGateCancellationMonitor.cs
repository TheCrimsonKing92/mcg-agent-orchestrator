namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceGateCancellationMonitor
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(250);

    internal static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> run,
        Func<bool>? shouldCancel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (shouldCancel is null)
        {
            return await run(cancellationToken).ConfigureAwait(false);
        }

        using var activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var monitor = MonitorAsync(shouldCancel, activeCancellation);
        try
        {
            return await run(activeCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            activeCancellation.Cancel();
            await monitor.ConfigureAwait(false);
        }
    }

    private static async Task MonitorAsync(
        Func<bool> shouldCancel,
        CancellationTokenSource activeCancellation)
    {
        try
        {
            while (!activeCancellation.IsCancellationRequested)
            {
                await Task.Delay(ProbeInterval, activeCancellation.Token).ConfigureAwait(false);
                if (shouldCancel())
                {
                    activeCancellation.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (activeCancellation.IsCancellationRequested)
        {
            // The check completed or another cancellation source already stopped it.
        }
        catch
        {
            // The existing boundary probe remains authoritative for probe failures.
        }
    }
}
