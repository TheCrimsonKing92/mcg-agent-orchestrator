namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns the probe-to-token bridge for waits that do not invoke a local check.
internal sealed class AcceptanceGateProbeCancellationSource : IAsyncDisposable
{
    private readonly CancellationTokenSource _active;
    private readonly CancellationTokenSource _monitorLifetime = new();
    private readonly Task _monitor;

    internal AcceptanceGateProbeCancellationSource(Func<bool>? shouldCancel, CancellationToken cancellationToken)
    {
        _active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitor = shouldCancel is null ? Task.CompletedTask : MonitorAsync(shouldCancel);
    }

    internal CancellationToken Token => _active.Token;

    private async Task MonitorAsync(Func<bool> shouldCancel)
    {
        try
        {
            while (!_monitorLifetime.IsCancellationRequested && !_active.IsCancellationRequested)
            {
                // Match the local-check monitor's polling interval; the probe owns refresh/coalescing.
                if (shouldCancel())
                {
                    await _active.CancelAsync().ConfigureAwait(false);
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), _monitorLifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_monitorLifetime.IsCancellationRequested) { }
        catch
        {
            // As with the local-check monitor, boundary probes own probe-read failures.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _monitorLifetime.CancelAsync().ConfigureAwait(false);
        await _monitor.ConfigureAwait(false);
        _active.Dispose();
        _monitorLifetime.Dispose();
    }
}
