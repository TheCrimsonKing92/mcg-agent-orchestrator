using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// One open goal owns its viewport and refresh session. No action writes orchestrator state.
internal sealed class OwnerConsoleGoalDialog : IDisposable
{
    internal sealed record Content(string Text, IReadOnlyList<int> ChoiceLines,
        IReadOnlyList<OwnerQuestionResolution> Resolved, OwnerConsoleDecision? Question,
        string? Failure, string? EpicId);

    private readonly OwnerConsoleScreenController _controller;
    private readonly Func<CancellationToken, Task<Content?>> _load;
    private readonly Func<OwnerConsoleDecision, Task> _question;
    private readonly Func<string, string, Task> _text;
    private readonly CancellationTokenSource _session;
    private readonly object _gate = new();
    private Action<Action> _invoke = action => action();
    private TaskCompletionSource? _loading;
    private bool _dirty;
    private bool _started;
    private bool _disposed;
    private int _revision;

    internal Content Current { get; private set; }
    internal string Title => "Goal";
    internal OwnerConsoleTextPage Page { get; }
    internal Task LastLoad { get; private set; } = Task.CompletedTask;
    internal string? RequestedEpicId { get; private set; }
    internal event Action? Changed;
    internal event Action? CloseRequested;
    internal IReadOnlyList<string> Actions => new[]
    {
        Current.Question is not null ? "Open question (q)" : null,
        Current.Failure is not null ? "Last failure (f)" : null,
        Current.EpicId is not null ? "Epic (e)" : null
    }.OfType<string>().ToArray();

    internal OwnerConsoleGoalDialog(OwnerConsoleScreenController controller, Content initial,
        Func<CancellationToken, Task<Content?>> load, Func<OwnerConsoleDecision, Task> question,
        Func<string, string, Task> text, CancellationToken token)
    {
        _controller = controller; Current = initial; _load = load; _question = question; _text = text;
        _session = CancellationTokenSource.CreateLinkedTokenSource(token);
        Page = new(initial.Text, 60, 15, initial.ChoiceLines);
    }

    internal void Start(Action<Action> invoke)
    {
        if (_started || _disposed) return;
        _invoke = invoke; _started = true;
        _controller.ModelApplied += ModelApplied;
    }

    private void ModelApplied()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _dirty = true; _revision++;
            if (_loading is not null) return;
            _loading = new(TaskCreationOptions.RunContinuationsAsynchronously);
            LastLoad = _loading.Task;
            _ = Task.Run(async () =>
            {
                try { await ReloadLoopAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    lock (_gate) { _loading?.TrySetException(ex); _loading = null; }
                }
            });
        }
    }

    private async Task ReloadLoopAsync()
    {
        while (true)
        {
            int revision;
            CancellationToken token;
            lock (_gate)
            {
                if (_disposed || !_dirty)
                { _loading!.TrySetResult(); _loading = null; return; }
                _dirty = false; revision = _revision; token = _session.Token;
            }
            try
            {
                var content = await _load(token).WaitAsync(token).ConfigureAwait(false);
                await ApplyAsync(() =>
                {
                    if (content is null) return; // The goal disappeared; retain the last known page.
                    Current = content;
                    Page.Replace(content.Text, content.ChoiceLines);
                    Changed?.Invoke();
                }, revision, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                await ApplyAsync(() =>
                {
                    Page.Replace(Current.Text + Environment.NewLine + "Refresh failed: " + ex.Message, Current.ChoiceLines);
                    Changed?.Invoke();
                }, revision, token).ConfigureAwait(false);
            }
        }
    }

    private async Task ApplyAsync(Action apply, int revision, CancellationToken token)
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _invoke(() =>
        {
            try
            {
                lock (_gate)
                    if (!_disposed && !token.IsCancellationRequested && revision == _revision) apply();
                applied.TrySetResult();
            }
            catch (Exception ex) { applied.TrySetException(ex); }
        });
        try { await applied.Task.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    internal async Task<bool> HandleKeyAsync(char key)
    {
        if (_disposed) return false;
        switch (char.ToLowerInvariant(key))
        {
            case 'q' when Current.Question is { } question: await _question(question); return true;
            case 'f' when Current.Failure is { } failure: await _text("Last failure", failure); return true;
            case 'e' when Current.EpicId is { } epic:
                RequestedEpicId = epic; CloseRequested?.Invoke(); return true;
            default: return false;
        }
    }

    internal Task OpenResolutionAsync(int index) => index >= 0 && index < Current.Resolved.Count
        ? _text("Question resolution", _controller.ResolutionText(Current.Resolved[index])) : Task.CompletedTask;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _revision++;
            _controller.ModelApplied -= ModelApplied;
            _session.Cancel(); _session.Dispose();
        }
    }
}
