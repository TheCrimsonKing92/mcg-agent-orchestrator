namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal interface IOwnerConsoleActivityLoader
{
    Task<OwnerConsoleActivityLoad> LoadAsync(IReadOnlyCollection<string> boardGoalIds, CancellationToken token);
}

internal sealed record OwnerConsoleActivityLoad(IReadOnlyList<OwnerConductEvent> Recent, DateTimeOffset? LastActivity, int LandedToday = 0);

internal sealed class OwnerConsoleActivityLoader(string conductPath, string lifecycleDirectory, TimeProvider clock)
    : IOwnerConsoleActivityLoader, IAsyncDisposable
{
    private readonly OwnerGoalLifecycleTail _lifecycle = new(lifecycleDirectory);
    private readonly object _gate = new();
    internal IConductEventSource? Events { get; private set; }

    public Task<OwnerConsoleActivityLoad> LoadAsync(IReadOnlyCollection<string> boardGoalIds, CancellationToken token)
    {
        lock (_gate)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                // Capture the live offset before scanning history so startup never opens a gap.
                Events ??= new OwnerConsoleStartupEventSource(conductPath, clock);
                var lifecycle = _lifecycle.ReadNew(boardGoalIds, token);
                var history = OwnerConsoleStartupActivity.ReadHistory(conductPath,
                    lifecycle.Count == 0 ? null : lifecycle.Min(item => item.Timestamp), clock, token);
                var recent = history.Recent.ToList();
                token.ThrowIfCancellationRequested();
                foreach (var item in lifecycle) OwnerConsoleStartupActivity.Append(recent, item);
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new OwnerConsoleActivityLoad(recent, history.LastActivity ?? Events.LastActivity, history.LandedToday));
            }
            catch (OperationCanceledException)
            {
                // The abandoned snapshot was never published. Rewind to a bounded
                // tail on the next refresh instead of silently consuming its events.
                _lifecycle.Reset();
                throw;
            }
        }
    }

    internal IReadOnlyList<OwnerConductEvent> ReadNew(IReadOnlyCollection<string> goalIds)
    {
        // A timed-out filesystem read can still be unwinding. Never overlap cursor
        // mutations or make a refresh wait for that read; the next refresh drains it.
        if (!Monitor.TryEnter(_gate)) return [];
        try { return _lifecycle.ReadNew(goalIds); }
        finally { Monitor.Exit(_gate); }
    }
    public ValueTask DisposeAsync() => Events?.DisposeAsync() ?? ValueTask.CompletedTask;
}
