namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal interface IOwnerConsoleActivityLoader
{
    Task<OwnerConsoleActivityLoad> LoadAsync(IReadOnlyCollection<string> boardGoalIds, CancellationToken token);
}

internal sealed record OwnerConsoleActivityLoad(IReadOnlyList<OwnerConductEvent> Recent, DateTimeOffset? LastActivity);

internal sealed class OwnerConsoleActivityLoader(string conductPath, string lifecycleDirectory, TimeProvider clock)
    : IOwnerConsoleActivityLoader, IAsyncDisposable
{
    private readonly OwnerGoalLifecycleTail _lifecycle = new(lifecycleDirectory);
    internal IConductEventSource? Events { get; private set; }

    public Task<OwnerConsoleActivityLoad> LoadAsync(IReadOnlyCollection<string> boardGoalIds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Capture the live offset before scanning history so startup never opens a gap.
        Events ??= new OwnerConsoleStartupEventSource(conductPath, clock);
        var recent = OwnerConsoleStartupActivity.ReadRecent(conductPath).ToList();
        foreach (var item in ReadNew(boardGoalIds)) OwnerConsoleStartupActivity.Append(recent, item);
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new OwnerConsoleActivityLoad(recent, Events.LastActivity));
    }

    internal IReadOnlyList<OwnerConductEvent> ReadNew(IReadOnlyCollection<string> goalIds) => _lifecycle.ReadNew(goalIds);
    public ValueTask DisposeAsync() => Events?.DisposeAsync() ?? ValueTask.CompletedTask;
}
