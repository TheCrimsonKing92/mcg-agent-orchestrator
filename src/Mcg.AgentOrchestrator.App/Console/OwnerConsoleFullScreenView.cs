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
    private readonly OwnerConsoleInputDiagnostics? _diagnostics;
    private readonly Func<Task> _refresh;
    private readonly OwnerConsoleBoardSource? _boardSource;
    private readonly CancellationToken _token;
    private readonly CancellationTokenSource _lifetime;
    private ITimer? _noticeExpiryTimer;
    private readonly OwnerStatusPresenter _statusPresenter = new();
    private readonly Label _status = new() { Width = Dim.Fill(), Height = 1 };
    private readonly Label _notice = new() { Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 1 };
    private readonly OwnerConsoleDecisionsPane _decisionsPane = new();
    private readonly OwnerConsoleActivityPane _activityPane = new();
    private readonly ListView _decisions;
    private readonly TableView _board = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly Label _preview = new() { Width = Dim.Fill(), Height = 3 };
    private readonly OwnerConsoleSelectedGoalSummary _summary;
    private OwnerConsoleSelectedGoalPreview _selectedPreview = new(null, null);
    private readonly OwnerConsoleLastGood<string, (OwnerConsoleSelectedGoalPreview Preview, OwnerConsoleBoardRow? Row)> _previewLastGood = new();
    private OwnerConsoleBoardRow? _previewRow;
    private string? _previewStatus;
    internal string PreviewText => _preview.Text;
    internal Task PreviewLoad => _summary.LastLoad;
    private readonly ListView _activity;
    private readonly FrameView _decisionsFrame;
    private readonly FrameView _boardFrame;
    private readonly FrameView _previewFrame;
    private readonly FrameView _activityFrame;
    private readonly TextField _command = new() { Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
    private readonly Label _hints = new() { Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 };
    private readonly Label _epicHint = new() { Y = Pos.AnchorEnd(2), Text = OwnerConsoleEpicFormatter.KeyHint, Height = 1 };
    internal OwnerConsoleEpicDialog EpicView { get; }
    internal OwnerConsoleGateDialog GateView { get; } = new();
    internal string EpicHintText => _epicHint.Text;
    private readonly Dictionary<string, string> _working = new();
    private readonly OwnerConsoleScreenOperation _operation;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _refreshInterval;
    private DateTimeOffset? _refreshedAt;
    private bool _rendering;
    private bool _editingCommand;
    private string? _completionHint;
    private bool _acting;
    private OwnerConsolePane _focusedPane = OwnerConsolePane.Decisions;
    private int _selectedBoardIndex = -1;
    private int _activityWidth;
    private int _boardTitleWidth;
    internal bool ActivityFollowing => _activityPane.Following;
    internal string? ActivityFilter => _activityPane.Filter;
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
    internal IReadOnlyList<string> DecisionLines => _decisionsPane.Lines;
    internal IReadOnlyList<string> ActivityLines => _activityPane.Lines;
    internal string? SelectedGoalId { get; private set; }
    internal string? SelectedProject { get; private set; }
    private OwnerConsoleBoardRow? SelectedBoardRow => _controller.Model is { } model &&
        _selectedBoardIndex >= 0 && _selectedBoardIndex < model.Board.Length ? model.Board[_selectedBoardIndex] : null;
    internal OwnerConsolePane FocusedPane => _decisions.HasFocus ? OwnerConsolePane.Decisions :
        _board.HasFocus ? OwnerConsolePane.Board : _activity.HasFocus ? OwnerConsolePane.Activity : _focusedPane;

    internal OwnerConsoleFullScreenView(IApplication app, OwnerConsoleScreenController controller,
        Func<Task> refresh, CancellationToken token = default, TimeProvider? clock = null,
        OwnerConsoleLoopOptions? options = null, OwnerConsoleInputDiagnostics? diagnostics = null, string? projectLabel = null,
        OwnerConsoleBoardSource? boardSource = null)
    {
        _decisions = _decisionsPane.List;
        _activity = _activityPane.List;
        if (projectLabel is not null) Window.Title = $"Owner console - {projectLabel}";
        _app = app;
        _diagnostics = diagnostics;
        _controller = controller;
        _refresh = refresh;
        _boardSource = boardSource;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _token = _lifetime.Token;
        _clock = clock ?? TimeProvider.System;
        NoticeStrip = new(_clock);
        _refreshInterval = (options ?? OwnerConsoleLoopOptions.Default).RefreshInterval;
        if (_refreshInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "Refresh interval must be positive.");
        _operation = new(_clock,
            label => Invoke(() => { if (!_token.IsCancellationRequested) SetWorking("command", label); }),
            message => Invoke(() => { if (!_token.IsCancellationRequested) ShowNotice(message, OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action); }), options,
            message => Invoke(() => { if (!_token.IsCancellationRequested) ShowNotice(message, OwnerConsoleNoticeSeverity.Success, OwnerConsoleNoticeSource.Action); }));
        _summary = new(controller.LoadGoalSummaryAsync, Invoke, preview =>
        {
            _previewStatus = null;
            _previewRow = _controller.Model?.Board.FirstOrDefault(row =>
                row.Project == SelectedProject && row.GoalId == preview.GoalId);
            if (preview.GoalId is { } id)
            {
                if (preview.Unavailable is null && preview.Summary?.Preview is not null)
                    _previewLastGood.Remember(id, (preview, _previewRow));
                else if (_previewLastGood.TryGet(id, out var prior))
                {
                    _previewStatus = preview.Unavailable is { } error ? "refresh failed: " + error :
                        preview.Summary is not null ? "refresh failed: could not load the goal" : "refreshing";
                    preview = prior.Preview;
                    _previewRow = prior.Row;
                }
            }
            _selectedPreview = preview;
            RenderPreview();
        }, _token);
        _decisionsFrame = new FrameView { Title = "DECISIONS", Y = 1, Width = Dim.Fill(), Height = Dim.Percent(20) };
        _boardFrame = new FrameView { Title = "BOARD", Y = Pos.Bottom(_decisionsFrame), Width = Dim.Fill(), Height = Dim.Percent(33) };
        _previewFrame = new FrameView { Title = "SELECTED", Y = Pos.Bottom(_boardFrame), Width = Dim.Fill(), Height = 5 };
        _activityFrame = new FrameView { Title = "ACTIVITY", Y = Pos.Bottom(_previewFrame), Width = Dim.Fill(), Height = Dim.Fill(3) };
        _decisionsFrame.Add(_decisions, _decisionsPane.EmptyLabel);
        _boardFrame.Add(_board);
        _previewFrame.Add(_preview);
        _activityFrame.Add(_activity);
        _board.Table = new DataTableSource(BoardTable);
        _board.Style.AlwaysShowHeaders = true;
        _board.Style.ShowHeaders = true;
        _board.FullRowSelect = true;
        _epicHint.Width = OwnerConsoleEpicFormatter.KeyHint.Length;
        _epicHint.X = Pos.AnchorEnd(OwnerConsoleEpicFormatter.KeyHint.Length);
        _hints.Width = Dim.Fill(OwnerConsoleEpicFormatter.KeyHint.Length + 2);
        EpicView = new(controller, Invoke, token);
        Window.Add(_status, _decisionsFrame, _boardFrame, _previewFrame, _activityFrame, _notice, _hints, _epicHint, _command, EpicView, GateView);
        _preview.ViewportChanged += (_, _) => RenderPreview();
        RenderPreview();
        _notice.ViewportChanged += (_, _) => RenderNotices();
        _decisions.ValueChanged += (_, _) =>
        {
            if (!_rendering && _decisions.SelectedItem is { } index) _controller.SelectIndex(index);
            if (!_rendering) RenderHints();
            _diagnostics?.Mark("decision-selection", TraceState);
        };
        _board.ValueChanged += (_, args) =>
        {
            if (!_rendering && args.NewValue?.SelectedCell is { } cell) SelectBoard(cell.Y);
            _diagnostics?.Mark("board-selection", TraceState);
        };
        _activity.ValueChanged += (_, _) =>
        {
            if (!_rendering && ActivityRow() is { } row) { _activityPane.Inspect(row); RenderHints(); RenderOverflow(); }
            _diagnostics?.Mark("activity-selection", TraceState);
        };
        _activity.ViewportChanged += (_, args) =>
        {
            var viewport = _activity.Viewport; // NewViewport is empty before Terminal.Gui initialization.
            if (!_rendering && args.OldViewport.Size == viewport.Size && args.OldViewport.X == viewport.X && args.OldViewport.Y != viewport.Y && ActivityRow() is { } row)
            { _activityPane.Scrolled(row); RenderHints(); RenderOverflow(); }
        };
        _decisions.HasFocusChanged += (_, _) => PaneFocusChanged(_decisions, OwnerConsolePane.Decisions);
        _board.HasFocusChanged += (_, _) => PaneFocusChanged(_board, OwnerConsolePane.Board);
        _activity.HasFocusChanged += (_, _) => PaneFocusChanged(_activity, OwnerConsolePane.Activity);
        _command.HasFocusChanged += (_, _) => RenderHints();
        _command.TextChanged += (_, _) =>
        {
            if (_completionHint is null) return;
            _completionHint = null;
            RenderHints();
        };
        _activity.ViewportChanged += (_, _) =>
        {
            using var trace = _diagnostics?.Begin("activity-viewport", TraceState);
            Fit(_activity.Viewport.Width, _boardTitleWidth);
            RenderOverflow();
        };
        _board.ViewportChanged += (_, _) =>
        {
            using var trace = _diagnostics?.Begin("board-viewport", TraceState);
            Fit(_activityWidth, 0);
            RenderOverflow();
        };
        _decisions.ViewportChanged += (_, _) =>
        {
            using var trace = _diagnostics?.Begin("decisions-viewport", TraceState);
            if (_rendering) return;
            _rendering = true;
            try { RenderDecisions(); }
            finally { _rendering = false; }
            RenderOverflow();
        };
        RenderHints();
        _keyboard = app.Initialized ? app.Keyboard : null;
        if (_keyboard is not null) _keyboard.KeyDown += OnKeyDown;
        if (_diagnostics is not null) _app.LayoutAndDrawComplete += OnLayoutAndDrawComplete;
    }

    private static DataTable CreateBoardTable()
    {
        var table = new DataTable();
        foreach (var name in new[] { "GOAL", "EPIC", "TITLE", "PRIORITY", "STATUS", "AGE" }) table.Columns.Add(name);
        return table;
    }

    internal void Render(OwnerConsoleViewModel model)
    {
        GateView.Render(model.Gate);
        using var trace = _diagnostics?.Begin("render", TraceState);
        _rendering = true;
        try
        {
            _controller.Apply(model);
            if (model.BoardScope is { } scope) Window.Title = $"Owner console - {OwnerConsoleProjectLabel.ForScope(scope)}";
            _refreshedAt = _clock.GetUtcNow();
            RenderStatus();
            RenderDecisions();
            RenderBoard();
            var index = Array.FindIndex(model.Board.ToArray(), row => row.GoalId == SelectedGoalId && row.Project == SelectedProject);
            SelectBoard(index < 0 ? _selectedBoardIndex : index, refresh: true);
            RenderActivity();
            RenderHints();
            NoticeStrip.RefreshSucceeded();
            RenderNotices();
            RenderOverflow();
        }
        finally { _rendering = false; }
    }

    internal void Fit(int activityWidth, int boardTitleWidth)
    {
        using var trace = _diagnostics?.Begin("fit", TraceState);
        if (_rendering) { if (trace is not null) trace.Outcome = "skipped-rendering"; return; }
        _activityWidth = activityWidth;
        _boardTitleWidth = boardTitleWidth;
        _rendering = true;
        try { RenderActivity(); RenderBoard(); }
        finally { _rendering = false; }
        RenderOverflow();
    }

    private void RenderBoard()
    {
        using var trace = _diagnostics?.Begin("board-source-replacement", TraceState);
        if (_controller.Model is not { } model) return;
        OwnerConsoleBoardRenderer.Render(BoardTable, _board, model.Board, _board.Viewport.Width, _boardTitleWidth,
            model.BoardScope is { ProjectCount: > 1 });
        RenderPreview();
    }

    private void RenderDecisions()
    {
        using var trace = _diagnostics?.Begin("decisions-source-replacement", TraceState);
        if (_controller.Model is not { } model) return;
        _decisionsPane.Render(model, _controller.SelectedIndex, _decisions.Viewport.Width);
        RenderOverflow();
    }

    private int BoardBodyRows() => _board.Viewport.Height - 1 -
        (_board.Style.ShowHorizontalHeaderOverline ? 1 : 0) -
        (_board.Style.ShowHorizontalHeaderUnderline ? 1 : 0);

    private void RenderOverflow()
    {
        var model = _controller.Model;
        SetTitle(_decisionsFrame, "DECISIONS" + (_decisionsPane.TitleStatus is { } status ? $" ({status})" : ""), OwnerConsoleOverflow.Of(
            _decisions.Source?.Count ?? 0, _decisions.Viewport.Y, _decisions.Viewport.Height));
        SetTitle(_boardFrame, "BOARD", OwnerConsoleOverflow.Of(
            model?.Board.Length ?? 0, _board.RowOffset, BoardBodyRows()));
        var activityTitle = OwnerConsoleActivityTitle.Title(OwnerConsoleOverflow.Of(
            ActivityLines.Count, _activity.Viewport.Y, _activity.Viewport.Height),
            _activityPane.Following, _activityFrame.Frame.Width - OwnerConsoleOverflow.TitleChrome,
            _activityPane.Filter, _activityPane.TitleStatus);
        if (_activityFrame.Title != activityTitle) _activityFrame.Title = activityTitle;

        static void SetTitle(FrameView frame, string baseTitle, OwnerConsoleOverflow overflow)
        {
            var title = OwnerConsoleOverflow.Title(baseTitle, overflow, frame.Frame.Width - OwnerConsoleOverflow.TitleChrome);
            if (frame.Title != title) frame.Title = title;
        }
    }

    internal string PaneTitle(OwnerConsolePane pane) => pane switch
    {
        OwnerConsolePane.Decisions => _decisionsFrame.Title,
        OwnerConsolePane.Board => _boardFrame.Title,
        OwnerConsolePane.Activity => _activityFrame.Title,
        _ => throw new ArgumentOutOfRangeException(nameof(pane))
    };

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
        var remaining = NoticeStrip.UntilNextSuccessExpiry;
        if (remaining is null)
        {
            _noticeExpiryTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }
        // Create only when a success needs expiry; rendering failures or waiting for
        // owner input must not introduce a timer.
        _noticeExpiryTimer ??= _clock.CreateTimer(_ =>
        {
            if (!_token.IsCancellationRequested)
                Invoke(() => { if (!_token.IsCancellationRequested) RenderNotices(); });
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        // Recheck the clock at each callback: an early timer must rearm for the remainder.
        _noticeExpiryTimer.Change(remaining.Value, Timeout.InfiniteTimeSpan);
    }

    private void RenderStatus()
    {
        if (_controller.Model is not { } model) return;
        _status.Text = _statusPresenter.Compose(model.Status, model.Gate, _controller.BellStatusText,
            _refreshedAt, _clock.GetUtcNow(), _clock.LocalTimeZone, _refreshInterval, _working.Values);
    }

    // Host ticks repaint freshness even when the state refresh is blocked or has failed.
    internal void RefreshStatus() { RenderStatus(); RenderNotices(); }

    private void RenderActivity()
    {
        using var trace = _diagnostics?.Begin("activity-source-replacement", TraceState);
        _activityPane.Render(_controller.Model, _activityWidth);
    }
    private int? ActivityRow() => _activityPane.SelectedRow(_controller.Model);
    private void FollowLatest()
    {
        _rendering = true;
        try
        {
            using var trace = _diagnostics?.Begin("activity-source-replacement", TraceState);
            _activityPane.FollowLatest(_controller.Model, _activityWidth);
        }
        finally { _rendering = false; }
        RenderHints();
        RenderOverflow();
    }

    private async void OnKeyDown(object? sender, Key key)
    {
        if (_diagnostics is null && _app.TopRunnableView != Window) return;
        try
        {
            await (_diagnostics is null ? HandleKeyCoreAsync(key) :
                ObserveKeyAsync(key, _app.TopRunnableView == Window, "keyboard-callback"));
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception ex) { ShowNotice($"key action failed: {ex.Message}", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action); }
    }

    // The production callback and headless tests share routing; their input boundaries remain distinct.
    internal Task HandleKeyAsync(Key key) => _diagnostics is null ? HandleKeyCoreAsync(key) :
        ObserveKeyAsync(key, true, "headless-input");

    internal async Task ObserveKeyAsync(Key key, bool routeToWindow, string boundary)
    {
        using var input = _diagnostics?.Begin(boundary, TraceState, key.ToString(), input: true);
        if (!routeToWindow)
        {
            if (input is not null) input.Outcome = "excluded-top-window";
            return;
        }
        using var handler = _diagnostics?.Begin("handler", TraceState);
        try
        {
            await HandleKeyCoreAsync(key);
            if (handler is not null) handler.Outcome = key.Handled ? "handled" : "unhandled";
            if (input is not null) input.Outcome = key.Handled ? "handled" : "unhandled";
        }
        catch (Exception ex)
        {
            if (handler is not null) handler.Outcome = ex is OperationCanceledException ? "cancelled" : "faulted";
            if (input is not null) input.Outcome = handler?.Outcome ?? "faulted";
            throw;
        }
    }

    private async Task HandleKeyCoreAsync(Key key)
    {
        if (GateView.IsOpen)
        {
            if (GateView.HandleKey(key)) FocusPane(GateView.ReturnPane);
            return;
        }
        if (EpicView.IsOpen)
        {
            if (await EpicView.HandleKeyAsync(key)) FocusPane(EpicView.ReturnPane);
            return;
        }
        if (_editingCommand || _command.HasFocus)
        {
            if (key == Key.Esc) { key.Handled = true; FinishCommand(); return; }
            if (key == Key.Tab)
            {
                key.Handled = true;
                var completion = OwnerConsoleCommandCompletion.Complete(_command.Text, OwnerConsoleBindings.Commands,
                    _hints.Viewport.Width > 0 ? _hints.Viewport.Width : 80);
                _command.Text = completion.Text;
                _command.InsertionPoint = _command.Text.Length;
                _completionHint = completion.Hint;
                RenderHints();
                return;
            }
            if (key != Key.Enter || ActionRunning) return;
            key.Handled = true;
            var line = _command.Text;
            FinishCommand();
            if (OwnerConsoleBindings.ForLine(line) is { Owner: OwnerConsoleCommandOwner.View } binding)
                await RunBindingAsync(binding, line.Trim().TrimStart(':').TrimStart()
                    .Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1));
            else if (!(OwnerConsoleBindings.ForLine(line)?.Command == "goal" && RefuseSelectedBoardRow()))
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
        var character = (char)key.AsRune.Value;
        if (!key.IsCtrl && !key.IsAlt && OwnerConsoleBindings.ForKey(character, FocusedPane) is { } keyBinding)
        {
            key.Handled = true;
            await RunBindingAsync(keyBinding, fromKey: true);
            return;
        }
        if (!key.IsCtrl && !key.IsAlt && OwnerConsoleBindings.IsTopLevelKey(character))
        {
            key.Handled = true;
            if (!ActionRunning)
            {
                StartAction();
                ShowNotice(OwnerConsoleKeyHints.UnavailableKey(character, FocusedPane), OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
            }
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
        if (key == Key.Home && FocusedPane == OwnerConsolePane.Activity)
        { key.Handled = true; if (!ActionRunning) FollowLatest(); return; }
        var pane = FocusedPane;
        var count = pane == OwnerConsolePane.Activity ? ActivityLines.Count : pane == OwnerConsolePane.Board ?
            _controller.Model?.Board.Length ?? 0 : _controller.Model?.Decisions.Length ?? 0;
        var current = pane == OwnerConsolePane.Activity ? _activity.SelectedItem ?? 0 : pane == OwnerConsolePane.Board ?
            _selectedBoardIndex : _controller.SelectedIndex;
        var pageHeight = pane == OwnerConsolePane.Board ? BoardBodyRows() : PaneView(pane).Viewport.Height;
        if (OwnerConsolePaneNavigator.Target(key, current, count, pageHeight) is { } target)
        {
            key.Handled = true;
            if (ActionRunning || target < 0) return;
            if (pane == OwnerConsolePane.Board) SelectBoard(target);
            else if (pane == OwnerConsolePane.Activity) { _activity.SelectedItem = target; _activity.EnsureSelectedItemVisible(); }
            else { _controller.SelectIndex(target); _decisions.SelectedItem = target; _decisions.EnsureSelectedItemVisible(); }
            RenderHints();
            RenderOverflow();
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
            RenderOverflow();
            return;
        }
        if (key == Key.Enter)
        {
            key.Handled = true;
            if (ActionRunning) return;
            if (pane == OwnerConsolePane.Board && RefuseSelectedBoardRow()) return;
            if (pane == OwnerConsolePane.Board && SelectedGoalId is { } goalId)
                await ActAsync(ct => _controller.ShowGoalDetailAsync(goalId, _operation, ct));
            else if (pane == OwnerConsolePane.Decisions && _controller.SelectedIndex >= 0)
                await ActAsync(ct => _controller.HandleKeyAsync(ConsoleKey.Enter, operation: _operation, cancellationToken: ct));
            else if (pane == OwnerConsolePane.Activity && _controller.Model?.ActivityState is null &&
                _activity.SelectedItem is { } selected && selected >= 0 &&
                selected < _activityPane.Rows.Count)
            {
                if (_activityPane.Rows[selected].ItemIndex is { } itemIndex)
                    await ActAsync(ct => _controller.ShowActivityMeaningAsync(_controller.Model!.Activity[itemIndex], _operation, ct));
                else if (_activityPane.Rows[selected].Key.Kind == OwnerConsoleActivityFilter.EmptyKind)
                {
                    StartAction();
                    ShowNotice(OwnerConsoleActivityFilter.EmptyEnterNotice(_activityPane.Filter!), OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
                }
                else
                {
                    StartAction();
                    ShowNotice("Enter: a day separator has no detail; select an event row", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
                }
            }
            return;
        }
        if (!ActionRunning && !key.IsCtrl && !key.IsAlt && !char.IsControl(character) && !char.IsWhiteSpace(character))
        {
            StartAction();
            ShowNotice($"{character}: not bound; press : for commands or ? for help", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
        }
    }

    private async Task RunBindingAsync(OwnerConsoleBinding binding, string? argument = null, bool fromKey = false)
    {
        if (binding == OwnerConsoleBindings.Quit)
        {
            await _controller.HandleKeyAsync(0, binding.Key!.Value, cancellationToken: _token);
            _app.RequestStop(Window);
            return;
        }
        if (ActionRunning) return;
        if ((binding.Command is "goal" or "gate" or "accept" or "answer") && RefuseSelectedBoardRow()) return;
        if (binding == OwnerConsoleBindings.ProjectScope)
        {
            StartAction();
            var result = _boardSource?.HandleScope(argument) ?? new OwnerConsoleScopeCommandResult(
                "scope: project registry unavailable in this session", false, false);
            ShowNotice(result.Message, result.Success ? OwnerConsoleNoticeSeverity.Success : OwnerConsoleNoticeSeverity.Failure,
                OwnerConsoleNoticeSource.Action);
            if (result.Changed) await _refresh();
        }
        else if (binding.Owner == OwnerConsoleCommandOwner.Controller)
            await ActAsync(ct => _controller.RunCommandAsync(binding.Command, _operation, ct));
        else if (binding == OwnerConsoleBindings.Gate)
        { StartAction(); OpenGate(argument); }
        else if (binding == OwnerConsoleBindings.Epics)
        { StartAction(); await EpicView.OpenAsync(FocusedPane); }
        else if (binding == OwnerConsoleBindings.Help)
            await ActAsync(_ => _controller.ShowHelpAsync());
        else if (binding.Scope == OwnerConsoleScope.Decisions)
        {
            if (FocusedPane == OwnerConsolePane.Decisions && _controller.SelectedIndex >= 0)
                await ActAsync(ct => _controller.HandleKeyAsync(0, binding.Key!.Value, _operation, ct));
            else if (FocusedPane != OwnerConsolePane.Decisions)
            {
                StartAction();
                ShowNotice(OwnerConsoleKeyHints.UnavailableKey(binding.Key!.Value, FocusedPane), OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
            }
        }
        else if (binding == OwnerConsoleBindings.Follow)
        { StartAction(); FocusPane(OwnerConsolePane.Activity); FollowLatest(); }
        else if (binding == OwnerConsoleBindings.Filter)
        {
            if (fromKey)
            {
                _commandReturnFocus = _activity;
                _editingCommand = true;
                _command.Text = OwnerConsoleActivityFilter.CommandText(_activityPane.Filter);
                _command.InsertionPoint = _command.Text.Length;
                _command.SetFocus();
                RenderHints();
                return;
            }
            StartAction();
            FocusPane(OwnerConsolePane.Activity);
            _rendering = true;
            try
            {
                using var trace = _diagnostics?.Begin("activity-source-replacement", TraceState);
                _activityPane.Refilter(_controller.Model, argument, _activityWidth);
            }
            finally { _rendering = false; }
            RenderHints();
            RenderOverflow();
            ShowNotice(_activityPane.Filter is { } filter ? OwnerConsoleActivityFilter.AppliedNotice(filter,
                _activityPane.Rows.Count(row => row.ItemIndex is not null),
                Math.Min(_controller.Model?.Activity.Length ?? 0, OwnerConsoleViewModelBuilder.MaxActivityItems))
                : OwnerConsoleActivityFilter.ClearedNotice, OwnerConsoleNoticeSeverity.Success, OwnerConsoleNoticeSource.Action);
        }
        else throw new InvalidOperationException($"No view action for binding {binding.Command}.");
    }

    private void OpenGate(string? goalId = null)
    {
        var gate = _controller.Model?.Gate;
        if (goalId is not null && gate?.ForMember(goalId) is null)
            ShowNotice($"gate {goalId}: not in a live gate", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
        else GateView.Open(FocusedPane, gate, goalId);
    }

    private void FinishCommand()
    {
        _editingCommand = false;
        _completionHint = null;
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
        if (_controller.TakeRequestedGateGoalId() is { } gateGoalId && !_token.IsCancellationRequested)
            OpenGate(gateGoalId);
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
        ? _completionHint ?? OwnerConsoleKeyHints.CommandLineHint
        : OwnerConsoleKeyHints.Hint(FocusedPane,
            _controller.SelectedDecision is { } decision && OwnerConsoleScreenController.AnswersInConsole(decision), _activityPane.Following);

    private void SelectBoard(int index, bool refresh = false)
    {
        using var trace = _diagnostics?.Begin("select-board", TraceState);
        if (_controller.Model is not { Board.Length: > 0 } model)
        { _selectedBoardIndex = -1; SelectedGoalId = null; SelectedProject = null; _summary.Select(null); RenderPreview(); return; }
        _selectedBoardIndex = Math.Clamp(index, 0, model.Board.Length - 1);
        var row = model.Board[_selectedBoardIndex];
        SelectedGoalId = row.IsDiagnostic ? null : row.GoalId;
        SelectedProject = row.Project;
        _summary.Select(row.IsForeign || row.IsDiagnostic ? null : SelectedGoalId, refresh);
        RenderPreview();
        if (_board.Value?.SelectedCell.Y != _selectedBoardIndex)
            _board.SetSelection(0, _selectedBoardIndex, false);
        _board.EnsureCursorIsVisible();
        RenderOverflow();
    }

    private void RenderPreview()
    {
        if (SelectedBoardRow is { } selected && (selected.IsForeign || selected.IsDiagnostic))
        {
            var foreignTitle = OwnerConsoleSelectedPreview.Title(selected.IsDiagnostic ? null : selected.GoalId);
            if (_previewFrame.Title != foreignTitle) _previewFrame.Title = foreignTitle;
            var foreignText = selected.IsDiagnostic ? selected.Title : OwnerConsoleBoardSource.ForeignRowMessage(selected);
            if (_preview.Text != foreignText) _preview.Text = foreignText;
            return;
        }
        var title = OwnerConsoleSelectedPreview.Title(_selectedPreview.GoalId) +
            (_previewStatus is null ? "" : $" ({_previewStatus})");
        if (_previewFrame.Title != title) _previewFrame.Title = title;
        var row = _previewRow;
        var text = string.Join('\n', OwnerConsoleSelectedPreview.Lines(_selectedPreview, row?.Stage,
            _clock.LocalTimeZone, _preview.Viewport.Width, row));
        if (_preview.Text != text) _preview.Text = text;
    }

    private bool RefuseSelectedBoardRow()
    {
        if (FocusedPane != OwnerConsolePane.Board || SelectedBoardRow is not { } row ||
            !row.IsForeign && !row.IsDiagnostic) return false;
        StartAction();
        ShowNotice(row.IsDiagnostic ? row.Title : OwnerConsoleBoardSource.ForeignRowMessage(row),
            OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
        return true;
    }

    private void Invoke(Action action)
    {
        if (_app.Initialized && _diagnostics is not null)
        {
            _app.Invoke(_diagnostics.ObservePost("view-invoke", action, TraceState));
        }
        else if (_app.Initialized) _app.Invoke(action);
        else action();
    }

    private void OnLayoutAndDrawComplete(object? sender, EventArgs args) =>
        _diagnostics?.Mark("layout-draw-complete", TraceState);

    private OwnerConsoleTraceState TraceState()
    {
        static OwnerConsoleTracePane Pane(View view, int index, string? identity, int count) =>
            new(index, identity, count, view.Viewport.X, view.Viewport.Y, view.Viewport.Width, view.Viewport.Height);
        return new(_editingCommand || _command.HasFocus ? "Command" : FocusedPane.ToString(),
            _editingCommand, EpicView.IsOpen || GateView.IsOpen || (_app.Initialized && _app.TopRunnableView != Window),
            Pane(_decisions, _decisions.SelectedItem ?? -1, _controller.SelectedDecisionId, _controller.Model?.Decisions.Length ?? 0),
            Pane(_board, _selectedBoardIndex, SelectedGoalId, _controller.Model?.Board.Length ?? 0) with
                { RowOffset = _board.RowOffset, ColumnOffset = _board.ColumnOffset },
            Pane(_activity, _activity.SelectedItem ?? -1, null, ActivityLines.Count));
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _summary.Dispose();
        _noticeExpiryTimer?.Dispose();
        _controller.Dispose();
        if (_keyboard is not null) _keyboard.KeyDown -= OnKeyDown;
        if (_diagnostics is not null) _app.LayoutAndDrawComplete -= OnLayoutAndDrawComplete;
        Window.Dispose();
        BoardTable.Dispose();
        _lifetime.Dispose();
    }
}
