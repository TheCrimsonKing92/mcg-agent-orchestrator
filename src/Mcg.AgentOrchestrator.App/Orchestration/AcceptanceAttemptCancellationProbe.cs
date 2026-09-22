namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class AcceptanceAttemptCancellationProbe
{
    private readonly object _sync = new();
    private readonly Func<AcceptanceAttemptCancellationDecision> _readDecision;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _refreshInterval;
    private AcceptanceAttemptCancellationDecision? _cachedDecision;
    private AcceptanceAttemptCancellationDecision? _latchedCancellation;
    private DateTimeOffset _nextRefreshAt = DateTimeOffset.MinValue;

    internal AcceptanceAttemptCancellationProbe(
        Func<AcceptanceAttemptCancellationDecision> readDecision,
        TimeSpan? refreshInterval = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(readDecision);
        _readDecision = readDecision;
        _refreshInterval = refreshInterval ?? TimeSpan.FromSeconds(2);
        if (_refreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal AcceptanceAttemptCancellationDecision? CancellationDecision
    {
        get
        {
            lock (_sync)
            {
                return _latchedCancellation;
            }
        }
    }

    internal bool ShouldCancel() => Observe().ShouldCancel;

    internal bool ShouldCancelNow() => Observe(forceRefresh: true).ShouldCancel;

    internal AcceptanceAttemptCancellationDecision Observe(bool forceRefresh = false)
    {
        lock (_sync)
        {
            if (_latchedCancellation is not null)
            {
                return _latchedCancellation;
            }

            var now = _timeProvider.GetUtcNow();
            if (!forceRefresh && _cachedDecision is not null && now < _nextRefreshAt)
            {
                return _cachedDecision;
            }

            var observed = _readDecision();
            _cachedDecision = observed;
            _nextRefreshAt = now + _refreshInterval;
            if (observed.ShouldCancel)
            {
                _latchedCancellation = observed;
            }

            return observed;
        }
    }
}
