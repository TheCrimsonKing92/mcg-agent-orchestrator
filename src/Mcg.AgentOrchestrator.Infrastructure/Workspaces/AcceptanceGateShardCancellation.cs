using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns cancellation for the whole shard batch, including waits with no local check running.
internal sealed class AcceptanceGateShardCancellation : IAsyncDisposable
{
    private readonly AcceptanceGateProbeCancellationSource _probe;
    private readonly CancellationTokenSource _shared;
    private readonly CancellationTokenRegistration _remoteCancellation;
    private readonly CancellationToken _stopToken;
    private readonly RemoteLaneEarlyOffer? _offers;

    internal AcceptanceGateShardCancellation(Func<bool>? shouldCancel, CancellationToken callerToken,
        CancellationToken stopToken, RemoteLaneCoordinator? remote)
    {
        // Owned attempts supply None as the caller token. Metadata invalidation arrives via
        // the probe and must cancel remote offers, remote polls and local slot waits as well.
        _probe = new(shouldCancel, callerToken);
        _stopToken = stopToken;
        _offers = remote is null ? null : RemoteLaneEarlyOffer.For(remote);
        _shared = CancellationTokenSource.CreateLinkedTokenSource(_probe.Token, stopToken);
        _remoteCancellation = _shared.Token.Register(() => { _offers?.Cancel(); remote?.AbandonAll(); });
    }

    internal CancellationToken CallerToken => _probe.Token;
    internal CancellationToken Token => _shared.Token;
    internal bool IsCancellationRequested => _shared.IsCancellationRequested;
    internal Task CancelAsync() => _shared.CancelAsync();

    internal async Task WaitForOffersAsync()
    {
        if (_offers is null) return;
        try { await _offers.OfferPassCompleted.WaitAsync(CallerToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stopToken.IsCancellationRequested && !CallerToken.IsCancellationRequested) { }
    }

    internal async Task<(RemoteLaneEarlyOffer.Offered? Offer, bool Cancelled)> TryOfferAsync(
        AcceptanceManifestCheck check, RemoteLaneEarlyOffer.Context context)
    {
        try
        {
            var offered = _offers!.TryAdopt(check.Name, out var adopted) ? adopted :
                await _offers.TryOfferAsync(check, context, Token).ConfigureAwait(false);
            return (offered, false);
        }
        catch (OperationCanceledException) when (IsCancellationRequested)
        {
            return (null, true); // Drain active work before rethrowing caller/probe cancellation.
        }
    }

    internal void RecordFailure(ICollection<Exception> failures, Exception exception, bool stoppingEarly)
    {
        if (exception is not OperationCanceledException ||
            (!stoppingEarly && !_stopToken.IsCancellationRequested && !CallerToken.IsCancellationRequested))
            failures.Add(exception);
    }

    public async ValueTask DisposeAsync()
    {
        _remoteCancellation.Dispose();
        _shared.Dispose();
        await _probe.DisposeAsync().ConfigureAwait(false);
    }
}
