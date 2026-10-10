using System.Collections.ObjectModel;
using System.Data;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Timeout = System.Threading.Timeout;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleFullScreenView : IDisposable
{
    private readonly OwnerConsoleScreenController _controller;
    private readonly IApplication _app;
    private readonly IKeyboard? _keyboard;
    private readonly Func<Task> _refresh;
    private readonly CancellationToken _token;
    private readonly CancellationTokenSource _lifetime;
    private readonly ITimer _noticeExpiryTimer;
    private readonly Label _status = new() { Width = Dim.Fill(), Height = 1 };
    private readonly Label _notice = new() { Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 1 };
    private readonly ListView _decisions = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly Label _emptyDecisions = new() { Text = "Nothing needs you right now.", Width = Dim.Fill(), Height = 1, Visible = false };
    private readonly TableView _board = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly ListView _activity = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly TextField _command = new() { Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
    private readonly Label _hints = new() { Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 };
    private readonly Label _epicHint = new() { Y = Pos.AnchorEnd(2), Text = OwnerConsoleEpicFormatter.KeyHint, Height = 1 };
    internal OwnerConsoleEpicDialog EpicView { get; }
    internal string EpicHintText => _epicHint.Text;
    private readonly Dictionary<string, string> _working = new();
    private readonly OwnerConsoleScreenOperation _operation;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _refreshInterval;
    private DateTimeOffset? _refreshedAt;
    private bool _rendering;
    private bool _editingCommand;
    private bool _acting;
    private OwnerConsolePane _focusedPane = OwnerConsolePane.Decisions;
    private int _selectedBoardIndex = -1;
    private int _activityWidth;
    private int _boardTitleWidth;
    private IReadOnlyList<string> _fullActivityLines = [];
    private View? _commandReturnFocus;
    private bool ActionRunning => _acting || _operation.IsRunning;

    internal Window Window { get; } = new() { Title = "Owner console", Width = Dim.Fill(), Height = Dim.Fill() };
    internal DataTable BoardTable { get; } = CreateBoardTable();
    internal string StatusText => _status.Text;
    internal OwnerConsoleNoticeStrip NoticeStrip { get; }
    internal string NoticeText => _notice.Text;
    internal TextField CommandLine => _command;
    internal TableView BoardPane => _board;
    internal ListView DecisionsPane => _decisions;
    internal ListView ActivityPane => _activity;
    internal string HintText => _hints.Text;
    internal IReadOnlyList<string> DecisionLines { get; private set; } = [];
    internal IReadOnlyList<string> ActivityLines { get; private set; } = [];
    internal string? SelectedGoalId { get; private set; }
    internal OwnerConsolePane FocusedPane => _decisions.HasFocus ? OwnerConsolePane.Decisions :
        _board.HasFocus ? OwnerConsolePane.Board : _activity.HasFocus ? OwnerConsolePane.Activity : _focusedPane;

    internal OwnerConsoleFullScreenView(IApplication app, OwnerConsoleScreenController controller,
        Func<Task> refresh, CancellationToken token = default, TimeProvider? clock = null,
        OwnerConsoleLoopOptions? options = null)
    {
        _app = app;
        _controller = controller;
        _refresh = refresh;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _token = _lifetime.Token;
        _clock = clock ?? TimeProvider.System;
        NoticeStrip = new(_clock);
        _noticeExpiryTimer = _clock.CreateTimer(_ =>
        {
            if (!_token.IsCancellationRequested)
                Invoke(() => { if (!_token.IsCancellationRequested) RenderNotices(); });
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _refreshInterval = (options ?? OwnerConsoleLoopOptions.Default).RefreshInterval;
        if (_refreshInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "Refresh interval must be positive.");
        _operation = new(_clock,
            label => Invoke(() => { if (!_token.IsCancellationRequested) SetWorking("command", label); }),
            message => Invoke(() => { if (!_token.IsCancellationRequested) ShowNotice(message, OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action); }), options,
            message => Invoke(() => { if (!_token.IsCancellationRequested) ShowNotice(message, OwnerConsoleNoticeSeverity.Success, OwnerConsoleNoticeSource.Action); }));
        var decisions = new FrameView { Title = "DECISIONS", Y = 1, Width = Dim.Fill(), Height = Dim.Percent(25) };
        var board = new FrameView { Title = "BOARD", Y = Pos.Bottom(decisions), Width = Dim.Fill(), Height = Dim.Percent(40) };
        var activity = new FrameView { Title = "ACTIVITY", Y = Pos.Bottom(board), Width = Dim.Fill(), Height = Dim.Fill(3) };
        decisions.Add(_decisions, _emptyDecisions);
        board.Add(_board);
        activity.Add(_activity);
        _board.Table = new DataTableSource(BoardTable);
        _board.Style.AlwaysShowHeaders = true;
        _board.Style.ShowHeaders = true;
        _board.FullRowSelect = true;
        _epicHint.Width = OwnerConsoleEpicFormatter.KeyHint.Length;
        _epicHint.X = Pos.AnchorEnd(OwnerConsoleEpicFormatter.KeyHint.Length);
        _hints.Width = Dim.Fill(OwnerConsoleEpicFormatter.KeyHint.Length + 2);
        EpicView = new(controller, Invoke, token);
        Window.Add(_status, decisions, board, activity, _notice, _hints, _epicHint, _command, EpicView);
        _notice.ViewportChanged += (_, _) => RenderNotices();
        _decisions.ValueChanged += (_, _) =>
        {
            if (!_rendering && _decisions.SelectedItem is { } index) _controller.SelectIndex(index);
            if (!_rendering) RenderHints();
        };
        _board.ValueChanged += (_, args) =>
        {
            if (!_rendering && args.NewValue?.SelectedCell is { } cell) SelectBoard(cell.Y);
        };
        _decisions.HasFocusChanged += (_, _) => PaneFocusChanged(_decisions, OwnerConsolePane.Decisions);
        _board.HasFocusChanged += (_, _) => PaneFocusChanged(_board, OwnerConsolePane.Board);
        _activity.HasFocusChanged += (_, _) => PaneFocusChanged(_activity, OwnerConsolePane.Activity);
        _command.HasFocusChanged += (_, _) => RenderHints();
        _activity.ViewportChanged += (_, _) => Fit(_activity.Viewport.Width, _boardTitleWidth);
        _board.ViewportChanged += (_, _) => Fit(_activityWidth, 0);
        _decisions.ViewportChanged += (_, _) =>
        {
            if (_rendering) return;
            _rendering = true;
            try { RenderDecisions(); }
            finally { _rendering = false; }
        };
        RenderHints();
        _keyboard = app.Initialized ? app.Keyboard : null;
        if (_keyboard is not null) _keyboard.KeyDown += OnKeyDown;
    }

    private static DataTable CreateBoardTable()
    {
        var table = new DataTable();
        foreach (var name in new[] { "GOAL", "EPIC", "TITLE", "STATE", "STAGE", "AGE" }) table.Columns.Add(name);
        return table;
    }

    internal void Render(OwnerConsoleViewModel model)
    {
        _rendering = true;
        try
        {
            _controller.Apply(model);
            _refreshedAt = _clock.GetUtcNow();
            RenderStatus();
            RenderDecisions();
            RenderBoard();
            var index = Array.FindIndex(model.Board.ToArray(), row => row.GoalId == SelectedGoalId);
            SelectBoard(index < 0 ? _selectedBoardIndex : index);
            RenderActivity();
            RenderHints();
            NoticeStrip.RefreshSucceeded();
            RenderNotices();
        }
        finally { _rendering = false; }
    }

    internal void Fit(int activityWidth, int boardTitleWidth)
    {
        if (_rendering) return;
        _activityWidth = activityWidth;
        _boardTitleWidth = boardTitleWidth;
        _rendering = true;
        try { RenderActivity(); RenderBoard(); }
        finally { _rendering = false; }
    }

    private void RenderBoard()
    {
        if (_controller.Model is not { } model) return;
        OwnerConsoleBoardRenderer.Render(BoardTable, _board, model.Board, _board.Viewport.Width, _boardTitleWidth);
    }

    private void RenderDecisions()
    {
        if (_controller.Model is not { } model) return;
        var rows = model.Decisions.Select(item => OwnerConsoleLineFitter.Fit(
            $"[{item.Number}] {item.GoalPrefix} {item.Kind}: {item.Summary}", OwnerConsoleLineSpans.None, _decisions.Viewport.Width)).ToArray();
        _emptyDecisions.Text = model.DecisionsState is { Loading: true } ? "loading..." :
            model.DecisionsState?.Error is { } error ? "DECISIONS unavailable: " + error : "Nothing needs you right now.";
        _emptyDecisions.Visible = rows.Length == 0;
        DecisionLines = _emptyDecisions.Visible ? [_emptyDecisions.Text] : rows;
        _decisions.SetSource(new ObservableCollection<string>(rows));
        _decisions.SelectedItem = _controller.SelectedIndex < 0 ? null : _controller.SelectedIndex;
    }

    internal void SetWorking(string source, string? label)
    {
        if (label is null) _working.Remove(source);
        else _working[source] = label;
        RenderStatus();
    }

    internal void ShowNotice(string message, OwnerConsoleNoticeSeverity severity, OwnerConsoleNoticeSource source)
    {
        NoticeStrip.Raise(message, severity, source);
        RenderNotices();
    }

    private void RenderNotices()
    {
        if (_token.IsCancellationRequested) return;
        _notice.Text = NoticeStrip.Format(_notice.Viewport.Width);
        // An uninitialized real application has no UI dispatcher. Injected clocks let
        // headless callers drive callbacks themselves rather than mutating UI on a timer thread.
        if (!_app.Initialized && ReferenceEquals(_clock, TimeProvider.System)) return;
        // Recheck the clock at each callback: an early timer must rearm for the remainder.
        _noticeExpiryTimer.Change(NoticeStrip.UntilNextSuccessExpiry ?? Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    private void RenderStatus()
    {
        if (_controller.Model is not { } model) return;
        var status = model.Status;
        _status.Text = $"conductor: {(status.ConductorRunning ? "running" : "stopped")} | active: {status.ActiveGoals} | decisions: {status.LiveDecisions} | last event: {Age(status.LastEventAge)} | landed today: {status.LandedToday} | bell: {(_controller.BellEnabled ? "on" : "off")}";
        if (status.HiddenQuestions > 0) _status.Text += $" | hidden: {status.HiddenQuestions}";
        if (_refreshedAt is { } refreshed)
        {
            var age = _clock.GetUtcNow() - refreshed;
            _status.Text += age.TotalSeconds > 3 * _refreshInterval.TotalSeconds
                ? $" | stale {Math.Max(1, (int)age.TotalMinutes)}m"
                : $" | refreshed {TimeZoneInfo.ConvertTime(refreshed, _clock.LocalTimeZone):HH:mm:ss}";
        }
        if (status.FailedToday > 0) _status.Text += $" | failed today: {status.FailedToday}";
        if (_working.Count > 0) _status.Text += $" | working: {string.Join(", ", _working.Values)}";
    }

    // Host ticks repaint freshness even when the state refresh is blocked or has failed.
    internal void RefreshStatus() { RenderStatus(); RenderNotices(); }

    private void RenderActivity()
    {
        var selected = _activity.SelectedItem;
        var selectedLine = selected is { } index && index >= 0 && index < _fullActivityLines.Count
            ? _fullActivityLines[index] : null;
        var state = _controller.Model?.ActivityState;
        IEnumerable<string> pane = state is { Loading: true } ? ["loading..."] : state?.Error is { } error
            ? ["ACTIVITY unavailable: " + error] : _controller.Model?.Activity.Select(OwnerActivityNarrator.Line) ?? [];
        _fullActivityLines = pane.Take(OwnerConsoleViewModelBuilder.MaxActivityItems).ToArray();
        ActivityLines = _fullActivityLines.Select((line, row) => OwnerConsoleLineFitter.Fit(line,
            state is not { Loading: true } && state?.Error is null &&
                _controller.Model is { } model && row < model.Activity.Length
                ? OwnerActivityNarrator.LineSpans(model.Activity[row]) : OwnerConsoleLineSpans.None, _activityWidth)).ToArray();
        _activity.SetSource(new ObservableCollection<string>(ActivityLines));
        var preserved = selectedLine is null ? -1 : Array.IndexOf(_fullActivityLines.ToArray(), selectedLine);
        _activity.SelectedItem = ActivityLines.Count == 0 ? null :
            preserved >= 0 ? preserved : Math.Clamp(selected ?? 0, 0, ActivityLines.Count - 1);
    }

    private async void OnKeyDown(object? sender, Key key)
    {
        if (_app.TopRunnableView != Window) return;
        try { await HandleKeyAsync(key); }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception ex) { ShowNotice($"key action failed: {ex.Message}", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action); }
    }

    // The production keyboard callback and headless tests use this same routing path.
    internal async Task HandleKeyAsync(Key key)
    {
        if (EpicView.IsOpen)
        {
            if (await EpicView.HandleKeyAsync(key)) FocusPane(EpicView.ReturnPane);
            return;
        }
        if (_editingCommand || _command.HasFocus)
        {
            if (key == Key.Esc) { key.Handled = true; FinishCommand(); return; }
            if (key != Key.Enter || ActionRunning) return;
            key.Handled = true;
            var line = _command.Text;
            FinishCommand();
            if (line.Trim().TrimStart(':').Trim().Equals("epics", StringComparison.OrdinalIgnoreCase))
            {
                StartAction();
                await EpicView.OpenAsync(FocusedPane);
                return;
            }
            await ActAsync(ct => _controller.RunCommandAsync(line, _operation, ct));
            return;
        }
        if (key.AsRune.Value == ':' && !ActionRunning)
        {
            key.Handled = true;
            _commandReturnFocus = PaneView(FocusedPane);
            _editingCommand = true;
            _command.SetFocus();
            RenderHints();
            return;
        }
        var character = char.ToLowerInvariant((char)key.AsRune.Value);
        if (character == 'q')
        {
            key.Handled = true;
            await _controller.HandleKeyAsync(0, 'q', cancellationToken: _token);
            _app.RequestStop(Window);
            return;
        }
        if (key == Key.Tab.WithShift)
        {
            key.Handled = true;
            if (!ActionRunning) FocusPane(OwnerConsoleKeyHints.Previous(FocusedPane));
            return;
        }
        if (key == Key.Tab)
        {
            key.Handled = true;
            if (!ActionRunning) FocusPane(OwnerConsoleKeyHints.Next(FocusedPane));
            return;
        }
        if (character == '?')
        {
            key.Handled = true;
            if (!ActionRunning) await ActAsync(_ => _controller.ShowHelpAsync());
            return;
        }
        if (character == 'e')
        {
            key.Handled = true;
            if (!ActionRunning) { StartAction(); await EpicView.OpenAsync(FocusedPane); }
            return;
        }
        var pane = FocusedPane;
        var count = pane == OwnerConsolePane.Activity ? ActivityLines.Count : pane == OwnerConsolePane.Board ?
            _controller.Model?.Board.Length ?? 0 : _controller.Model?.Decisions.Length ?? 0;
        var current = pane == OwnerConsolePane.Activity ? _activity.SelectedItem ?? 0 : pane == OwnerConsolePane.Board ?
            _selectedBoardIndex : _controller.SelectedIndex;
        var pageHeight = PaneView(pane).Viewport.Height;
        if (pane == OwnerConsolePane.Board) pageHeight -= 1 + (_board.Style.ShowHorizontalHeaderOverline ? 1 : 0) +
            (_board.Style.ShowHorizontalHeaderUnderline ? 1 : 0);
        if (OwnerConsolePaneNavigator.Target(key, current, count, pageHeight) is { } target)
        {
            key.Handled = true;
            if (ActionRunning || target < 0) return;
            if (pane == OwnerConsolePane.Board) SelectBoard(target);
            else if (pane == OwnerConsolePane.Activity) { _activity.SelectedItem = target; _activity.EnsureSelectedItemVisible(); }
            else { _controller.SelectIndex(target); _decisions.SelectedItem = target; _decisions.EnsureSelectedItemVisible(); }
            RenderHints();
            return;
        }
        if (key == Key.CursorUp || key == Key.CursorDown)
        {
            key.Handled = true;
            // Production modal keys are excluded by OnKeyDown's top-window guard. Keep the
            // existing headless decision-navigation contract while a fake dialog is pending.
            if (ActionRunning && pane != OwnerConsolePane.Decisions) return;
            var delta = key == Key.CursorUp ? -1 : 1;
            if (pane == OwnerConsolePane.Board) SelectBoard(_selectedBoardIndex + delta);
            else if (pane == OwnerConsolePane.Activity)
            {
                if (ActivityLines.Count > 0)
                {
                    _activity.SelectedItem = Math.Clamp((_activity.SelectedItem ?? 0) + delta, 0, ActivityLines.Count - 1);
                    _activity.EnsureSelectedItemVisible();
                }
            }
            else
            {
                await _controller.HandleKeyAsync(delta < 0 ? ConsoleKey.UpArrow : ConsoleKey.DownArrow, cancellationToken: _token);
                _decisions.SelectedItem = _controller.SelectedIndex < 0 ? null : _controller.SelectedIndex;
                RenderHints();
            }
            return;
        }
        if (key == Key.Enter)
        {
            key.Handled = true;
            if (ActionRunning) return;
            if (pane == OwnerConsolePane.Board && SelectedGoalId is { } goalId)
                await ActAsync(ct => _controller.ShowGoalDetailAsync(goalId, _operation, ct));
            else if (pane == OwnerConsolePane.Decisions && _controller.SelectedIndex >= 0)
                await ActAsync(ct => _controller.HandleKeyAsync(ConsoleKey.Enter, operation: _operation, cancellationToken: ct));
            else if (pane == OwnerConsolePane.Activity && _controller.Model?.ActivityState is null &&
                _activity.SelectedItem is { } selected && selected >= 0 &&
                selected < _controller.Model!.Activity.Length)
                await ActAsync(ct => _controller.ShowActivityMeaningAsync(_controller.Model.Activity[selected], _operation, ct));
            return;
        }
        if (character is 'a' or 'r')
        {
            key.Handled = true;
            if (ActionRunning) return;
            if (pane == OwnerConsolePane.Decisions && _controller.SelectedIndex >= 0)
                await ActAsync(ct => _controller.HandleKeyAsync(0, character, _operation, ct));
            else if (pane != OwnerConsolePane.Decisions)
            {
                StartAction();
                ShowNotice(OwnerConsoleKeyHints.UnavailableKey(character, pane), OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
            }
        }
    }

    private void FinishCommand()
    {
        _editingCommand = false;
        _command.Text = string.Empty;
        (_commandReturnFocus ?? _decisions).SetFocus();
        RenderHints();
    }

    private async Task ActAsync(Func<CancellationToken, Task> action)
    {
        // Modal waits belong to the owner; only the controller's dependency work is bounded.
        StartAction();
        _acting = true;
        try
        {
            await action(_token);
            if (!_token.IsCancellationRequested) await _refresh();
        }
        finally { _acting = false; }
        if (_controller.TakeRequestedEpicId() is { } epicId && !_token.IsCancellationRequested)
            await EpicView.OpenAsync(FocusedPane, epicId);
    }

    private void StartAction() { NoticeStrip.ActionStarted(); RenderNotices(); }

    internal void FocusDecisions() => FocusPane(OwnerConsolePane.Decisions);

    private View PaneView(OwnerConsolePane pane) => pane switch
    {
        OwnerConsolePane.Decisions => _decisions,
        OwnerConsolePane.Board => _board,
        OwnerConsolePane.Activity => _activity,
        _ => throw new ArgumentOutOfRangeException(nameof(pane))
    };

    private void FocusPane(OwnerConsolePane pane)
    {
        _focusedPane = pane;
        PaneView(pane).SetFocus();
        RenderHints();
    }

    private void PaneFocusChanged(View view, OwnerConsolePane pane)
    {
        if (view.HasFocus) _focusedPane = pane;
        RenderHints();
    }

    private void RenderHints() => _hints.Text = _editingCommand || _command.HasFocus
        ? OwnerConsoleKeyHints.CommandLineHint
        : OwnerConsoleKeyHints.Hint(FocusedPane,
            _controller.SelectedDecision is { } decision && OwnerConsoleScreenController.AnswersInConsole(decision));

    private void SelectBoard(int index)
    {
        if (_controller.Model is not { Board.Length: > 0 } model)
        { _selectedBoardIndex = -1; SelectedGoalId = null; return; }
        _selectedBoardIndex = Math.Clamp(index, 0, model.Board.Length - 1);
        SelectedGoalId = model.Board[_selectedBoardIndex].GoalId;
        if (_board.Value?.SelectedCell.Y != _selectedBoardIndex)
            _board.SetSelection(0, _selectedBoardIndex, false);
        _board.EnsureCursorIsVisible();
    }

    private void Invoke(Action action)
    {
        if (_app.Initialized) _app.Invoke(action);
        else action();
    }

    private static string Age(TimeSpan? span) => span is null ? "unknown" :
        span.Value.TotalMinutes < 1 ? $"{span.Value.TotalSeconds:0}s" : span.Value.TotalHours < 1 ? $"{span.Value.TotalMinutes:0}m" : $"{span.Value.TotalHours:0}h";

    public void Dispose()
    {
        _lifetime.Cancel();
        _noticeExpiryTimer.Dispose();
        _controller.Dispose();
        if (_keyboard is not null) _keyboard.KeyDown -= OnKeyDown;
        Window.Dispose();
        BoardTable.Dispose();
        _lifetime.Dispose();
    }
}
