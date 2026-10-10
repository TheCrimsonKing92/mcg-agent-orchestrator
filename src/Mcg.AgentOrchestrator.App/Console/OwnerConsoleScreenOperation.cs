namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// A timed-out operation keeps ownership until its underlying task has really ended.
internal sealed class OwnerConsoleScreenOperation(TimeProvider clock, Action<string?> working,
    Action<string> report, OwnerConsoleLoopOptions? options = null, Action<string>? notify = null)
{
    private readonly object _gate = new();
    private Task? _operation;
    private readonly OwnerConsoleLoopOptions _options = options ?? OwnerConsoleLoopOptions.Default;

    internal bool IsRunning { get { lock (_gate) return _operation is { IsCompleted: false }; } }
    internal Task Completion { get { lock (_gate) return _operation ?? Task.CompletedTask; } }
    internal void Notify(string message) => (notify ?? report)(message);

    internal async Task<bool> RunAsync(string label, Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        using var step = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stepToken = step.Token;
        Task operation;
        lock (_gate)
        {
            if (_operation is { IsCompleted: false }) return false;
            operation = _operation = Task.Run(() => action(stepToken), CancellationToken.None);
        }
        using var timers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var busy = Task.Delay(_options.BusyNoticeAfter, clock, timers.Token);
        var bound = Task.Delay(_options.OperationBound, clock, timers.Token);
        var hitBound = false;
        try
        {
            if (await Task.WhenAny(operation, busy, bound).WaitAsync(cancellationToken) == busy && !operation.IsCompleted)
                working(label);
            if (await Task.WhenAny(operation, bound).WaitAsync(cancellationToken) != operation)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hitBound = true;
                throw new TimeoutException();
            }
            await operation;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { step.Cancel(); _ = ObserveAsync(operation, label); return false; }
        catch (TimeoutException) when (hitBound)
        {
            report($"{label} did not finish within {_options.OperationBound.TotalSeconds:0.###}s; the console is still running");
            step.Cancel();
            _ = ObserveAsync(operation, label);
            return false;
        }
        catch (Exception ex) { report($"{label} failed: {ex.Message}"); return false; }
        finally { timers.Cancel(); working(null); }
    }

    private async Task ObserveAsync(Task operation, string label)
    {
        try { await operation; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { report($"{label} failed after its bound: {ex.Message}"); }
    }
}
