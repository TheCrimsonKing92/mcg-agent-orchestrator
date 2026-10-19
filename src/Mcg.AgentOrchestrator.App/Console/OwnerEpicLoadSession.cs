namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Owns one reopenable dialog lifetime and all eligibility decisions for its reads.
internal sealed class OwnerEpicLoadSession(
    Func<OwnerConsoleEpicWindow, string?, CancellationToken, Task<OwnerConsoleEpicViewModel?>> read,
    Action<Action> dispatch, Action<OwnerEpicLoadResult> publish, CancellationToken hostToken) : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _session;
    private CancellationTokenSource? _request;
    private bool _disposed;
    private bool _dirty;
    private int _revision;
    private long _generation;
    private long _refreshVersion;
    private long _publishedRefreshVersion;
    private OwnerConsoleEpicWindow _window;
    private string? _detailId;
    private TaskCompletionSource? _loading;

    internal Task LastLoad { get; private set; } = Task.CompletedTask;

    internal void Open()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null) throw new InvalidOperationException("Epic load session is already open.");
            _session = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        }
    }

    internal void Select(OwnerConsoleEpicWindow window, string? detailId)
    {
        lock (_gate)
        {
            _revision++;
            _window = window;
            _detailId = detailId;
            _request?.Cancel();
        }
    }

    internal Task RefreshAsync()
    {
        lock (_gate)
        {
            if (_disposed || _session is null) return LastLoad;
            _dirty = true;
            _refreshVersion++;
            if (_loading is not null) return LastLoad;
            // Notifications in a cycle request another read without invalidating completed content.
            _generation++;
            _loading = new(TaskCreationOptions.RunContinuationsAsynchronously);
            LastLoad = _loading.Task;
            _ = ReadLoopAsync();
            return LastLoad;
        }
    }

    private async Task ReadLoopAsync()
    {
        while (true)
        {
            PendingRead pending;
            lock (_gate)
            {
                if (_disposed || _session is null || !_dirty)
                {
                    _loading!.TrySetResult();
                    _loading = null;
                    return;
                }
                _dirty = false;
                _request = CancellationTokenSource.CreateLinkedTokenSource(_session.Token);
                pending = new(_revision, _generation, _refreshVersion, _window, _detailId, _request, _request.Token);
            }
            await ReadAsync(pending).ConfigureAwait(false);
        }
    }

    private async Task ReadAsync(PendingRead pending)
    {
        try
        {
            var model = await read(pending.Window, pending.DetailId, pending.Token)
                .WaitAsync(pending.Token).ConfigureAwait(false);
            QueuePublication(pending, model, null);
        }
        catch (OperationCanceledException) when (pending.Token.IsCancellationRequested) { }
        catch (Exception ex) { QueuePublication(pending, null, ex.Message); }
        finally
        {
            lock (_gate)
                if (ReferenceEquals(_request, pending.Cancellation)) _request = null;
            pending.Cancellation.Dispose();
        }
    }

    private void QueuePublication(PendingRead pending, OwnerConsoleEpicViewModel? model, string? failure)
    {
        dispatch(() =>
        {
            lock (_gate)
            {
                if (_disposed || _session is null || hostToken.IsCancellationRequested ||
                    pending.Token.IsCancellationRequested || pending.Revision != _revision ||
                    pending.Generation != _generation || pending.RefreshVersion <= _publishedRefreshVersion) return;
                _publishedRefreshVersion = pending.RefreshVersion;
                // Delivery shares the lifetime lock so disposal cannot pass the check then dispose widgets.
                publish(new(model, failure, failure is null && pending.RefreshVersion != _refreshVersion));
            }
        });
    }

    internal void Close()
    {
        lock (_gate)
        {
            _dirty = false;
            _revision++;
            var session = _session;
            _session = null;
            session?.Cancel();
            session?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Close();
        }
    }

    private sealed record PendingRead(int Revision, long Generation, long RefreshVersion,
        OwnerConsoleEpicWindow Window, string? DetailId, CancellationTokenSource Cancellation, CancellationToken Token);
}
