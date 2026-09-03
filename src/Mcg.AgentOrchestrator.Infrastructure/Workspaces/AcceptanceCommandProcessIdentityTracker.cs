namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class AcceptanceCommandProcessIdentityTracker(
    RegisteredOwnedProcess process,
    Action<SpawnProcessIdentity>? identityObserver = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private SpawnProcessIdentity? _identity;
    private Task? _tracking;

    internal SpawnProcessIdentity? Identity => Volatile.Read(ref _identity);

    internal void Start() => _tracking = TrackAsync(_stop.Token);

    private async Task TrackAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && Identity is null)
        {
            var observed = process.TryReadDirectChildIdentity();
            if (observed is not null && Interlocked.CompareExchange(ref _identity, observed, null) is null)
            {
                identityObserver?.Invoke(observed);
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _stop.CancelAsync().ConfigureAwait(false); } catch { }
        if (_tracking is not null)
        {
            try { await _tracking.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
