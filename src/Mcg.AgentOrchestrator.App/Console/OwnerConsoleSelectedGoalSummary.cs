namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// A session owns one selected request. Refreshes coalesce; late results cannot replace a newer selection.
internal sealed class OwnerConsoleSelectedGoalSummary(
    Func<string, CancellationToken, Task<OwnerGoalChangeSummary?>> load,
    Action<Action> invoke, Action<OwnerConsoleSelectedGoalPreview> apply, CancellationToken token) : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _request;
    private string? _goalId;
    private int _revision;
    private bool _dirty;
    private bool _running;
    private bool _disposed;
    internal Task LastLoad { get; private set; } = Task.CompletedTask;

    internal void Select(string? id, bool refresh = false)
    {
        lock (_gate)
        {
            if (_disposed || id == _goalId && !refresh) return;
            var selectionChanged = id != _goalId;
            _goalId = id; _revision++; _dirty = true;
            if (selectionChanged) _request?.Cancel();
            apply(new(id, null));
            if (_running) return;
            _running = true;
            LastLoad = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            string? id;
            int revision;
            CancellationToken current;
            lock (_gate)
            {
                if (_disposed || !_dirty) { _running = false; return; }
                _dirty = false; id = _goalId; revision = _revision;
                _request?.Dispose();
                _request = CancellationTokenSource.CreateLinkedTokenSource(token);
                current = _request.Token;
            }
            OwnerGoalChangeSummary? summary = null;
            string? failure = null;
            try { if (id is not null) summary = await load(id, current).WaitAsync(current).ConfigureAwait(false); }
            catch (OperationCanceledException) when (current.IsCancellationRequested) { continue; }
            catch (Exception ex) { failure = ex.GetType().Name; }
            if (summary is not null && summary.GoalId != id)
            { summary = null; failure = "mismatched goal identity"; }
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            invoke(() =>
            {
                try
                {
                    lock (_gate)
                        if (!_disposed && !current.IsCancellationRequested && id == _goalId &&
                            (summary is null || summary.GoalId == id))
                            apply(new(id, revision == _revision ? summary : summary?.AsStale(),
                                failure ?? (id is not null && summary is null ? "goal unavailable" : null)));
                    applied.TrySetResult();
                }
                catch (Exception ex) { applied.TrySetException(ex); }
            });
            try { await applied.Task.WaitAsync(current).ConfigureAwait(false); }
            catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true; _revision++;
            _request?.Cancel(); _request?.Dispose(); _request = null;
        }
    }
}
