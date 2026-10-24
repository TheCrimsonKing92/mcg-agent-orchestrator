namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleRefreshCoalescer(
    Func<IReadOnlyList<OwnerConductEvent>, OwnerConsoleRefreshScope, Task> refresh)
{
    private readonly object _sync = new();
    private readonly List<OwnerConductEvent> _pendingItems = [];
    private OwnerConsoleRefreshScope? _pendingScope;
    private TaskCompletionSource? _completion;

    internal Task Completion { get { lock (_sync) return _completion?.Task ?? Task.CompletedTask; } }

    internal void Request(OwnerConductEvent? item, OwnerConsoleRefreshScope scope)
    {
        lock (_sync)
        {
            if (_completion is not null)
            {
                if (item is not null) _pendingItems.Add(item);
                _pendingScope = _pendingScope?.Union(scope) ?? scope;
                return;
            }
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        // Invoke directly so an idle trigger starts before Request returns.
        _ = DrainAsync(item is null ? [] : [item], scope);
    }

    private async Task DrainAsync(IReadOnlyList<OwnerConductEvent> items, OwnerConsoleRefreshScope scope)
    {
        while (true)
        {
            try { await refresh(items, scope).ConfigureAwait(false); }
            // Refresh reports its own errors. A failed/cancelled batch must still drain pending work.
            catch (Exception) { }
            lock (_sync)
            {
                if (_pendingScope is null)
                {
                    _completion!.SetResult();
                    _completion = null;
                    return;
                }
                items = _pendingItems.ToArray();
                scope = _pendingScope;
                _pendingItems.Clear();
                _pendingScope = null;
            }
        }
    }
}
