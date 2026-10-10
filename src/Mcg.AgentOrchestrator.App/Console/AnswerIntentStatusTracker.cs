using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Tracks queue receipts, never inferring an outcome from the live question list.
internal sealed class AnswerIntentStatusTracker : IDisposable
{
    private readonly Func<string, CancellationToken, Task<OwnerAnswerIntentStatus?>> _read;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _bound;
    private readonly TimeSpan _pollInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _inFlight = [];
    private bool _disposed;

    internal AnswerIntentStatusTracker(
        Func<string, CancellationToken, Task<OwnerAnswerIntentStatus?>> read, TimeProvider clock,
        TimeSpan? bound = null, TimeSpan? pollInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _read = read;
        _clock = clock;
        _bound = bound ?? OwnerConsoleLoopOptions.Default.OperationBound;
        _pollInterval = pollInterval ?? OwnerConsoleLoopOptions.Default.PollBound;
        if (_bound <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(bound));
        if (_pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _delay = delay ?? ((span, token) => Task.Delay(span, clock, token));
    }

    internal static string Queued(int number, string prefix) =>
        $"Answer queued for question {number} ({prefix}); the conductor applies it on its next tick.";
    internal static string Applied(int number, string prefix) =>
        $"Answer applied for question {number} ({prefix})";
    internal static string Rejected(int number, string prefix, string? outcome) =>
        $"Answer rejected for question {number} ({prefix})" +
        (string.IsNullOrEmpty(outcome) ? string.Empty : $": {outcome}");
    internal static string StillQueued(int number) =>
        $"Answer still queued for question {number}; the conductor has not applied it yet";

    internal void Track(string? intentId, int number, string prefix, Action<string> notify,
        CancellationToken lifetime = default, Action<string>? failure = null)
    {
        if (string.IsNullOrWhiteSpace(intentId)) return;
        lock (_gate)
        {
            if (_disposed || lifetime.IsCancellationRequested) return;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, lifetime);
            var deadline = _clock.GetUtcNow() + _bound;
            var task = Task.Run(async () =>
            {
                using (cancellation)
                {
                    try { await WatchAsync(intentId, number, prefix, notify, failure ?? notify, deadline, cancellation.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                }
            });
            _inFlight.Add(task);
            _ = task.ContinueWith(completed =>
            {
                // Observe faults from a torn-down notice sink as well as removing completed work.
                _ = completed.Exception;
                lock (_gate) _inFlight.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    internal Task WhenIdle()
    {
        lock (_gate) return Task.WhenAll(_inFlight.ToArray());
    }

    private async Task WatchAsync(string intentId, int number, string prefix, Action<string> notify, Action<string> failure,
        DateTimeOffset deadline, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var remaining = deadline - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) break;
            OwnerAnswerIntentStatus? status = null;
            using var readStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var readToken = readStop.Token;
            try
            {
                // A slow reader cannot outlive the operation bound or console lifetime.
                var read = Task.Run(() => _read(intentId, readToken), readToken);
                status = await read.WaitAsync(remaining, _clock, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { /* A failed status read is transient, not a rejected answer. */ }
            finally { readStop.Cancel(); }
            if (status is { Status: OperatorIntentStatus.Applied })
            { Publish(Applied(number, prefix), notify); return; }
            if (status is not null && status.Status is not (OperatorIntentStatus.Pending or OperatorIntentStatus.Claimed))
            { Publish(Rejected(number, prefix, status.Outcome), failure); return; }
            remaining = deadline - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) break;
            await _delay(remaining < _pollInterval ? remaining : _pollInterval, token).ConfigureAwait(false);
        }
        Publish(StillQueued(number), notify);

        void Publish(string message, Action<string> sink)
        {
            lock (_gate)
                if (!_disposed && !token.IsCancellationRequested) sink(message);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _stop.Cancel();
        _stop.Dispose();
    }
}
