namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed partial class GateShardPermitPool
{
    private GateShardProcessFacts _processFacts = GateShardProcessFacts.System;
    private TimeProvider _clock = TimeProvider.System;

    internal static GateShardPermitPool ForRoot(string rootPath, int budget,
        GateShardProcessFacts processFacts, TimeProvider clock) => new(rootPath, budget)
    {
        _processFacts = processFacts,
        _clock = clock
    };

    internal GateShardPermitWait BeginWait(GateShardLaneClass laneClass) =>
        new(this, laneClass, new GateShardWaiterMarkers(_directory, _processFacts), _clock);

    internal async Task<GateShardPermit> AcquireAsync(GateShardLaneClass laneClass,
        Action<GateShardPollOutcome> onPoll, TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        if (pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        using var wait = BeginWait(laneClass);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = wait.Poll(out var permit);
            if (outcome is GateShardPollOutcome.Acquired or GateShardPollOutcome.AcquiredAfterForcedYield)
            {
                if (outcome == GateShardPollOutcome.AcquiredAfterForcedYield)
                {
                    try { onPoll(outcome); }
                    catch { permit?.Dispose(); throw; }
                }
                return permit!;
            }
            onPoll(outcome);
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed class GateShardPermitWait(
    GateShardPermitPool pool, GateShardLaneClass laneClass, GateShardWaiterMarkers markers, TimeProvider clock) : IDisposable
{
    private static readonly TimeSpan MaximumContinuousYield = TimeSpan.FromMinutes(10);
    private string? _marker;
    private DateTimeOffset? _firstYield;
    private bool _disposed;
    private bool _completed;

    internal GateShardPollOutcome Poll(out GateShardPermit? permit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            throw new InvalidOperationException("A completed gate shard permit wait cannot be polled again.");
        permit = null;
        if (laneClass != GateShardLaneClass.Evidence)
        {
            if (pool.TryAcquire(out permit))
            {
                RemoveMarker();
                _completed = true;
                return GateShardPollOutcome.Acquired;
            }
            if (_marker is null || !markers.Exists(_marker))
                _marker = markers.Publish();
            return GateShardPollOutcome.Waiting;
        }

        var now = clock.GetUtcNow();
        if (markers.HasLiveWaiter())
        {
            _firstYield ??= now;
            if (now - _firstYield.Value < MaximumContinuousYield)
                return GateShardPollOutcome.YieldingToGate;
            if (pool.TryAcquire(out permit))
            {
                _completed = true;
                return GateShardPollOutcome.AcquiredAfterForcedYield;
            }
            return GateShardPollOutcome.Waiting;
        }

        if (!pool.TryAcquire(out permit)) return GateShardPollOutcome.Waiting;
        _completed = true;
        return GateShardPollOutcome.Acquired;
    }

    private void RemoveMarker()
    {
        if (_marker is not null)
        {
            GateShardWaiterMarkers.TryDelete(_marker);
            _marker = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveMarker();
    }
}
