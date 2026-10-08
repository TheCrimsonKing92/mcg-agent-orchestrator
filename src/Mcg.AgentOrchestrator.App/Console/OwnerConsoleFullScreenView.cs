using System.Collections.ObjectModel;
using System.Data;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleFullScreenView : IDisposable
{
    private readonly OwnerConsoleScreenController _controller;
    private readonly IApplication _app;
    private readonly IKeyboard? _keyboard;
    private readonly Func<Task> _refresh;
    private readonly CancellationToken _token;
    private readonly Label _status = new() { Width = Dim.Fill(), Height = 1 };
    private readonly ListView _decisions = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly Label _emptyDecisions = new() { Text = "Nothing needs you right now.", Width = Dim.Fill(), Height = 1, Visible = false };
    private readonly TableView _board = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly ListView _activity = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly TextField _command = new() { Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
    private readonly Label _hints = new() { Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 };
    private readonly Dictionary<string, string> _working = new();
    private readonly List<string> _notices = [];
    private readonly OwnerConsoleScreenOperation _operation;
    private bool _rendering;
    private bool _editingCommand;
    private bool _acting;
    private OwnerConsolePane _focusedPane = OwnerConsolePane.Decisions;
    private int _selectedBoardIndex = -1;
    private View? _commandReturnFocus;
    private bool ActionRunning => _acting || _operation.IsRunning;

    internal Window Window { get; } = new() { Title = "Owner console", Width = Dim.Fill(), Height = Dim.Fill() };
    internal DataTable BoardTable { get; } = CreateBoardTable();
    internal string StatusText => _status.Text;
    internal IReadOnlyList<string> Notices => _notices;
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
        _token = token;
        _operation = new(clock ?? TimeProvider.System,
            label => Invoke(() => { if (!_token.IsCancellationRequested) SetWorking("command", label); }),
            message => Invoke(() => { if (!_token.IsCancellationRequested) ShowRefreshFailure(message); }), options);
        var decisions = new FrameView { Title = "DECISIONS", Y = 1, Width = Dim.Fill(), Height = Dim.Percent(25) };
        var board = new FrameView { Title = "BOARD", Y = Pos.Bottom(decisions), Width = Dim.Fill(), Height = Dim.Percent(40) };
        var activity = new FrameView { Title = "ACTIVITY", Y = Pos.Bottom(board), Width = Dim.Fill(), Height = Dim.Fill(2) };
        decisions.Add(_decisions, _emptyDecisions);
        board.Add(_board);
        activity.Add(_activity);
        _board.Table = new DataTableSource(BoardTable);
        _board.Style.AlwaysShowHeaders = true;
        _board.Style.ShowHeaders = true;
        _board.FullRowSelect = true;
        Window.Add(_status, decisions, board, activity, _hints, _command);
        _decisions.ValueChanged += (_, _) =>
        {
            if (!_rendering && _decisions.SelectedItem is { } index) _controller.SelectIndex(index);
        };
        _board.ValueChanged += (_, args) =>
        {
            if (!_rendering && args.NewValue?.SelectedCell is { } cell) SelectBoard(cell.Y);
        };
        _decisions.HasFocusChanged += (_, _) => PaneFocusChanged(_decisions, OwnerConsolePane.Decisions);
        _board.HasFocusChanged += (_, _) => PaneFocusChanged(_board, OwnerConsolePane.Board);
        _activity.HasFocusChanged += (_, _) => PaneFocusChanged(_activity, OwnerConsolePane.Activity);
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
            RenderStatus();
            var decisionRows = model.Decisions.Select(item => $"[{item.Number}] {item.GoalPrefix} {item.Kind}: {item.Summary}").ToArray();
            _emptyDecisions.Visible = decisionRows.Length == 0;
            DecisionLines = _emptyDecisions.Visible ? [_emptyDecisions.Text] : decisionRows;
            _decisions.SetSource(new ObservableCollection<string>(decisionRows));
            _decisions.SelectedItem = _controller.SelectedIndex < 0 ? null : _controller.SelectedIndex;
            BoardTable.Rows.Clear();
            foreach (var row in model.Board) BoardTable.Rows.Add(row.GoalPrefix, row.Epic, row.Title, row.State, row.Stage, Age(row.Age));
            _board.Update();
            var index = Array.FindIndex(model.Board.ToArray(), row => row.GoalId == SelectedGoalId);
            SelectBoard(index < 0 ? _selectedBoardIndex : index);
            RenderActivity();
            RenderHints();
        }
        finally { _rendering = false; }
    }

    internal void SetWorking(string source, string? label)
    {
        if (label is null) _working.Remove(source);
        else _working[source] = label;
        RenderStatus();
    }

    internal void ShowRefreshFailure(string message)
    {
        _notices.Insert(0, message);
        if (_notices.Count > OwnerConsoleViewModelBuilder.MaxActivityItems) _notices.RemoveAt(_notices.Count - 1);
        RenderActivity();
    }

    private void RenderStatus()
    {
        if (_controller.Model is not { } model) return;
        var status = model.Status;
        _status.Text = $"conductor: {(status.ConductorRunning ? "running" : "stopped")} | active: {status.ActiveGoals} | decisions: {status.LiveDecisions} | hidden: {status.HiddenQuestions} | last event: {Age(status.LastEventAge)} | landings: {status.LandingsSinceOpen} | bell: {(_controller.BellEnabled ? "on" : "off")}";
        if (_working.Count > 0) _status.Text += $" | working: {string.Join(", ", _working.Values)}";
    }

    private void RenderActivity()
    {
        var selected = _activity.SelectedItem;
        var selectedLine = selected is { } index && index >= 0 && index < ActivityLines.Count
            ? ActivityLines[index] : null;
        ActivityLines = _notices.Concat(_controller.Model?.Activity.Select(OwnerConsoleActivityPresentation.Line) ?? [])
            .Take(OwnerConsoleViewModelBuilder.MaxActivityItems).ToArray();
        _activity.SetSource(new ObservableCollection<string>(ActivityLines));
        var preserved = selectedLine is null ? -1 : Array.IndexOf(ActivityLines.ToArray(), selectedLine);
        _activity.SelectedItem = ActivityLines.Count == 0 ? null :
            preserved >= 0 ? preserved : Math.Clamp(selected ?? 0, 0, ActivityLines.Count - 1);
    }

    private async void OnKeyDown(object? sender, Key key)
    {
        if (_app.TopRunnableView != Window) return;
        try { await HandleKeyAsync(key); }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception ex) { ShowRefreshFailure($"key action failed: {ex.Message}"); }
    }

    // The production keyboard callback and headless tests use this same routing path.
    internal async Task HandleKeyAsync(Key key)
    {
        if (_editingCommand || _command.HasFocus)
        {
            if (key == Key.Esc) { key.Handled = true; FinishCommand(); return; }
            if (key != Key.Enter || ActionRunning) return;
            key.Handled = true;
            var line = _command.Text;
            FinishCommand();
            await ActAsync(ct => _controller.RunCommandAsync(line, _operation, ct));
            return;
        }
        if (key.AsRune.Value == ':' && !ActionRunning)
        {
            key.Handled = true;
            _commandReturnFocus = PaneView(FocusedPane);
            _editingCommand = true;
            _command.SetFocus();
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
        var pane = FocusedPane;
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
            return;
        }
        if (character is 'a' or 'r')
        {
            key.Handled = true;
            if (ActionRunning) return;
            if (pane == OwnerConsolePane.Decisions && _controller.SelectedIndex >= 0)
                await ActAsync(ct => _controller.HandleKeyAsync(0, character, _operation, ct));
        }
    }

    private void FinishCommand()
    {
        _editingCommand = false;
        _command.Text = string.Empty;
        (_commandReturnFocus ?? _decisions).SetFocus();
    }

    private async Task ActAsync(Func<CancellationToken, Task> action)
    {
        // Modal waits belong to the owner; only the controller's dependency work is bounded.
        _acting = true;
        try
        {
            await action(_token);
            if (!_token.IsCancellationRequested) await _refresh();
        }
        finally { _acting = false; }
    }

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

    private void RenderHints() => _hints.Text = OwnerConsoleKeyHints.Hint(FocusedPane);

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
        if (_keyboard is not null) _keyboard.KeyDown -= OnKeyDown;
        Window.Dispose();
        BoardTable.Dispose();
    }
}
