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
    private readonly TableView _board = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly ListView _activity = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly TextField _command = new() { Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
    private bool _rendering;
    private bool _busy;
    private bool _editingCommand;
    private View? _commandReturnFocus;

    internal Window Window { get; } = new() { Title = "Owner console", Width = Dim.Fill(), Height = Dim.Fill() };
    internal DataTable BoardTable { get; } = CreateBoardTable();

    internal OwnerConsoleFullScreenView(IApplication app, OwnerConsoleScreenController controller,
        Func<Task> refresh, CancellationToken token = default)
    {
        _app = app;
        _controller = controller;
        _refresh = refresh;
        _token = token;
        var decisions = new FrameView { Title = "DECISIONS", Y = 1, Width = Dim.Fill(), Height = Dim.Percent(25) };
        var board = new FrameView { Title = "BOARD", Y = Pos.Bottom(decisions), Width = Dim.Fill(), Height = Dim.Percent(40) };
        var activity = new FrameView { Title = "ACTIVITY", Y = Pos.Bottom(board), Width = Dim.Fill(), Height = Dim.Fill(2) };
        decisions.Add(_decisions);
        board.Add(_board);
        activity.Add(_activity);
        _board.Table = new DataTableSource(BoardTable);
        _board.Style.AlwaysShowHeaders = true;
        _board.Style.ShowHeaders = true;
        Window.Add(_status, decisions, board, activity,
            new Label { Text = "Enter detail  a accept  r answer  : command  q quit", Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 }, _command);
        _decisions.ValueChanged += (_, _) =>
        {
            if (!_rendering && _decisions.SelectedItem is { } index) _controller.SelectIndex(index);
        };
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
            var status = model.Status;
            _status.Text = $"conductor: {(status.ConductorRunning ? "running" : "stopped")} | active: {status.ActiveGoals} | decisions: {status.LiveDecisions} | hidden: {status.HiddenQuestions} | last event: {Age(status.LastEventAge)} | landings: {status.LandingsSinceOpen} | bell: {(_controller.BellEnabled ? "on" : "off")}";
            _decisions.SetSource(new ObservableCollection<string>(model.Decisions.Select(item => $"[{item.Number}] {item.GoalPrefix} {item.Kind}: {item.Summary}")));
            _decisions.SelectedItem = _controller.SelectedIndex < 0 ? null : _controller.SelectedIndex;
            BoardTable.Rows.Clear();
            foreach (var row in model.Board) BoardTable.Rows.Add(row.GoalPrefix, row.Epic, row.Title, row.State, row.Stage, Age(row.Age));
            _board.Update();
            _activity.SetSource(new ObservableCollection<string>(model.Activity.Select(item => $"{item.Timestamp:HH:mm:ss} {item.Tag} {item.GoalPrefix} {item.Kind}: {item.Detail}")));
        }
        finally { _rendering = false; }
    }

    internal void ShowRefreshFailure(string message) => _status.Text = $"refresh failed: {message}";

    private async void OnKeyDown(object? sender, Key key)
    {
        if (_app.TopRunnableView != Window) return;
        if (_editingCommand || _command.HasFocus)
        {
            if (key == Key.Esc) { key.Handled = true; FinishCommand(); return; }
            if (key != Key.Enter || _busy) return;
            key.Handled = true;
            var line = _command.Text;
            FinishCommand();
            await ActAsync(() => _controller.RunCommandAsync(line, _token));
            return;
        }
        if (key.AsRune.Value == ':' && !_busy)
        {
            key.Handled = true;
            _commandReturnFocus = _decisions.HasFocus ? _decisions : _board.HasFocus ? _board : _activity;
            _editingCommand = true;
            _command.SetFocus();
            return;
        }
        ConsoleKey mapped = key == Key.CursorUp ? ConsoleKey.UpArrow : key == Key.CursorDown ? ConsoleKey.DownArrow :
            key == Key.Enter ? ConsoleKey.Enter : 0;
        var character = char.ToLowerInvariant((char)key.AsRune.Value);
        if (mapped != 0 && !_decisions.HasFocus || mapped == 0 && character is not ('a' or 'r' or 'q')) return;
        key.Handled = true;
        if (character == 'q')
        {
            await _controller.HandleKeyAsync(0, 'q', _token);
            _app.RequestStop(Window);
            return;
        }
        if (_busy) return;
        await ActAsync(() => _controller.HandleKeyAsync(mapped, character, _token));
    }

    private void FinishCommand()
    {
        _editingCommand = false;
        _command.Text = string.Empty;
        (_commandReturnFocus ?? _decisions).SetFocus();
    }

    private async Task ActAsync(Func<Task> action)
    {
        _busy = true;
        try
        {
            await action();
            if (_controller.QuitRequested) _app.Invoke(() => _app.RequestStop(Window));
            else await _refresh();
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        { _app.Invoke(() => ShowRefreshFailure(ex.Message)); }
        finally { _busy = false; }
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
