using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// One open goal owns its viewport and refresh session. No action writes orchestrator state.
internal sealed class OwnerConsoleGoalDialog : IDisposable
{
    internal sealed record Content(string Text, IReadOnlyList<int> ChoiceLines,
        IReadOnlyList<OwnerQuestionResolution> Resolved, OwnerConsoleDecision? Question,
        string? Failure, string? EpicId, string? GateGoalId = null);

    private readonly OwnerConsoleScreenController _controller;
    private readonly Func<CancellationToken, Task<Content?>> _load;
    private readonly Func<OwnerConsoleDecision, Task> _question;
    private readonly Func<string, string, Task> _text;
    private readonly CancellationTokenSource _session;
    private readonly object _gate = new();
    private readonly bool _loadOnStart;
    private bool _contentLoaded;
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
    internal string? RequestedGateGoalId { get; private set; }
    internal event Action? Changed;
    internal event Action? CloseRequested;

    // Shared production construction path for Terminal.Gui layout/input verification.
    internal (OwnerConsoleTextDialog Dialog, Dictionary<char, Button> Buttons) CreateView(
        Action close, Func<char, Task> action)
    {
        var dialog = new OwnerConsoleTextDialog(Title, Current.Text, Current.ChoiceLines, Page);
        var closeButton = new Button { Text = "Close", IsDefault = true };
        closeButton.Accepting += (_, args) => { args.Handled = true; close(); };
        dialog.AddButton(closeButton);
        var buttons = new Dictionary<char, Button>();
        foreach (var row in OwnerConsoleDialogBindings.GoalDetail.Where(row => row.Command != "quit"))
        {
            var key = row.Key!.Value;
            var binding = OwnerConsoleBindings.ForDialogKey(OwnerConsoleScope.GoalDetail, key, row.Ctrl)!;
            var button = new Button { Text = OwnerConsoleDialogBindings.Label(binding), Visible = Actions.Contains(OwnerConsoleDialogBindings.Label(binding)) };
            button.Accepting += async (_, args) => { args.Handled = true; await action(key); };
            buttons.Add(key, button); dialog.AddConditionalButton(button);
        }
        return (dialog, buttons);
    }
    internal IReadOnlyList<string> Actions => OwnerConsoleDialogBindings.GoalDetail
        .Where(row => row.Command switch
        {
            "question" => Current.Question is not null,
            "failure" => Current.Failure is not null,
            "epics" => Current.EpicId is not null,
            "gate" => Current.GateGoalId is not null,
            _ => false
        }).Select(row => OwnerConsoleDialogBindings.Label(
            OwnerConsoleBindings.ForDialogKey(OwnerConsoleScope.GoalDetail, row.Key!.Value, row.Ctrl)!)).ToArray();

    internal OwnerConsoleGoalDialog(OwnerConsoleScreenController controller, Content initial,
        Func<CancellationToken, Task<Content?>> load, Func<OwnerConsoleDecision, Task> question,
        Func<string, string, Task> text, CancellationToken token, bool loadOnStart = false)
    {
        _controller = controller; Current = initial; _load = load; _question = question; _text = text;
        _session = CancellationTokenSource.CreateLinkedTokenSource(token);
        _loadOnStart = loadOnStart; _contentLoaded = !loadOnStart;
        Page = new(initial.Text, 60, 15, initial.ChoiceLines);
    }

    internal void Start(Action<Action> invoke)
    {
        if (_started || _disposed) return;
        _invoke = invoke; _started = true;
        _controller.ModelApplied += ModelApplied;
        if (_loadOnStart) ModelApplied();
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
                    if (content is null && _contentLoaded)
                    { Page.Replace(StaleSummaryText(Current.Text), Current.ChoiceLines); Changed?.Invoke(); return; }
                    if (content is not null) _contentLoaded = true;
                    content ??= OwnerConsoleGoalDetailLoading.Unavailable(null);
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
                    if (_contentLoaded)
                        Page.Replace(StaleSummaryText(Current.Text) + Environment.NewLine + "Refresh failed: " + ex.Message, Current.ChoiceLines);
                    else
                    {
                        Current = OwnerConsoleGoalDetailLoading.Unavailable(ex);
                        Page.Replace(Current.Text, Current.ChoiceLines);
                    }
                    Changed?.Invoke();
                }, revision, token).ConfigureAwait(false);
            }
        }
    }

    private static string StaleSummaryText(string text)
    {
        foreach (var heading in new[] { "Planned:", "Changed:", "Reported:", "Verified:", "Candidate:", "Promotion:" })
            text = text.Replace(Environment.NewLine + heading, Environment.NewLine + heading + " [stale — refresh unavailable]", StringComparison.Ordinal);
        return text;
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
        switch (OwnerConsoleBindings.ForDialogKey(OwnerConsoleScope.GoalDetail, key)?.Command)
        {
            case "question" when Current.Question is { } question: await _question(question); return true;
            case "failure" when Current.Failure is { } failure: await _text("Last failure", failure); return true;
            case "epics" when Current.EpicId is { } epic:
                RequestedEpicId = epic; CloseRequested?.Invoke(); return true;
            case "gate" when Current.GateGoalId is { } goalId:
                RequestedGateGoalId = goalId; CloseRequested?.Invoke(); return true;
            case "quit": CloseRequested?.Invoke(); return true;
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
