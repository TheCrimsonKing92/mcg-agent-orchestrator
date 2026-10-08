using System.Collections.ObjectModel;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// An overlay inside the owner window keeps production and headless input on the same route.
internal sealed class OwnerConsoleEpicDialog : FrameView
{
    private readonly OwnerConsoleScreenController _controller;
    private readonly Action<Action> _invoke;
    private readonly CancellationToken _hostToken;
    private readonly Label _header = new() { Width = Dim.Fill(), Height = 1 };
    private readonly ListView _body = new() { Y = 1, Width = Dim.Fill(), Height = Dim.Fill(2) };
    private readonly Label _hint = new() { Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 2 };
    private readonly object _gate = new();
    private CancellationTokenSource? _session;
    private OwnerConsoleEpicViewModel? _model;
    private string? _error;
    private bool _disposed;
    private bool _dirty;
    private bool _formatting;
    private int _revision;
    private int _scroll;
    private int _selectedIndex;
    private TaskCompletionSource? _loading;

    internal bool IsOpen { get; private set; }
    internal bool ShowingDetail { get; private set; }
    internal OwnerConsoleEpicWindow TimeWindow { get; private set; } = OwnerConsoleEpicWindow.Day;
    internal string? SelectedEpicId { get; private set; }
    internal OwnerConsolePane ReturnPane { get; private set; }
    internal IReadOnlyList<string> Lines { get; private set; } = [];
    internal string HeaderText => OwnerConsoleEpicFormatter.Header(TimeWindow);
    internal ListView BodyPane => _body;
    internal Task LastLoad { get; private set; } = Task.CompletedTask;

    internal OwnerConsoleEpicDialog(OwnerConsoleScreenController controller, Action<Action> invoke, CancellationToken token)
    {
        _controller = controller;
        _invoke = invoke;
        _hostToken = token;
        Title = "Epics";
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        Visible = false;
        Add(_header, _body, _hint);
        ViewportChanged += (_, _) => Format();
        _body.ViewportChanged += (_, _) => Format();
    }

    internal async Task OpenAsync(OwnerConsolePane returnPane)
    {
        if (_disposed || IsOpen) return;
        ReturnPane = returnPane;
        _session = CancellationTokenSource.CreateLinkedTokenSource(_hostToken);
        IsOpen = true;
        ShowingDetail = false;
        _scroll = 0;
        _revision++;
        Visible = true;
        _body.SetFocus();
        _controller.ModelApplied += ModelApplied;
        await ReloadAsync();
    }

    internal async Task<bool> HandleKeyAsync(Key key)
    {
        key.Handled = true;
        if (key == Key.Esc)
        {
            if (ShowingDetail)
            {
                ShowingDetail = false;
                _scroll = 0;
                _revision++;
                Format();
                await ReloadAsync();
                return false;
            }
            Close();
            return true;
        }
        if (char.ToLowerInvariant((char)key.AsRune.Value) == 'w')
        {
            TimeWindow = TimeWindow switch
            {
                OwnerConsoleEpicWindow.Day => OwnerConsoleEpicWindow.Week,
                OwnerConsoleEpicWindow.Week => OwnerConsoleEpicWindow.AllTime,
                _ => OwnerConsoleEpicWindow.Day
            };
            _revision++;
            await ReloadAsync();
        }
        else if (ShowingDetail)
        {
            var delta = key == Key.CursorUp ? -1 : key == Key.CursorDown ? 1 :
                key == Key.PageUp ? -Math.Max(1, _body.Viewport.Height) :
                key == Key.PageDown ? Math.Max(1, _body.Viewport.Height) : 0;
            _scroll = Math.Clamp(_scroll + delta, 0, Math.Max(0, Lines.Count - 1));
            Format();
        }
        else if (_model is { Epics.Count: > 0 })
        {
            if (key == Key.CursorUp || key == Key.CursorDown)
            {
                _selectedIndex = Math.Clamp(_selectedIndex + (key == Key.CursorUp ? -1 : 1), 0, _model.Epics.Count - 1);
                SelectedEpicId = _model.Epics[_selectedIndex].Epic.Id;
                Format();
            }
            else if (key == Key.Enter)
            {
                ShowingDetail = true;
                _scroll = 0;
                _revision++;
                await ReloadAsync();
            }
        }
        return false;
    }

    private void ModelApplied() => _ = ReloadAsync();

    internal Task ReloadAsync()
    {
        lock (_gate)
        {
            if (_disposed || !IsOpen) return LastLoad;
            _dirty = true;
            if (_loading is not null) return LastLoad;
            _loading = new(TaskCreationOptions.RunContinuationsAsynchronously);
            LastLoad = _loading.Task;
            _ = ReloadLoopAsync();
            return LastLoad;
        }
    }

    private async Task ReloadLoopAsync()
    {
        while (true)
        {
            int revision;
            OwnerConsoleEpicWindow window;
            string? detailId;
            CancellationToken token;
            lock (_gate)
            {
                if (_disposed || !IsOpen || !_dirty)
                {
                    _loading!.TrySetResult();
                    _loading = null;
                    return;
                }
                _dirty = false;
                revision = _revision;
                window = TimeWindow;
                detailId = ShowingDetail ? SelectedEpicId : null;
                token = _session!.Token;
            }
            try
            {
                var model = await _controller.LoadEpicViewAsync(window, detailId, token).WaitAsync(token).ConfigureAwait(false);
                _invoke(() =>
                {
                    if (_disposed || !IsOpen || token.IsCancellationRequested || revision != _revision) return;
                    _error = null;
                    _model = model;
                    var index = Array.FindIndex(model.Epics.ToArray(), row => row.Epic.Id == SelectedEpicId);
                    _selectedIndex = model.Epics.Count == 0 ? 0 : index >= 0 ? index : Math.Clamp(_selectedIndex, 0, model.Epics.Count - 1);
                    SelectedEpicId = model.Epics.Count == 0 ? null : model.Epics[_selectedIndex].Epic.Id;
                    if (ShowingDetail && model.SelectedDetail is null) ShowingDetail = false;
                    Format();
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _invoke(() =>
                {
                    if (_disposed || !IsOpen || token.IsCancellationRequested || revision != _revision) return;
                    _error = "EPICS unavailable: " + ex.Message;
                    Format();
                });
            }
        }
    }

    private void Format()
    {
        if (_disposed || !IsOpen || _formatting) return;
        _formatting = true;
        try
        {
            var width = OwnerConsoleEpicFormatter.Width(_body.Viewport.Width);
            var headerLines = OwnerConsoleEpicFormatter.Wrap(HeaderText, width).ToArray();
            _header.Text = string.Join("\n", headerLines);
            _header.Height = headerLines.Length;
            _body.Y = Pos.Bottom(_header);
            var hint = ShowingDetail ? OwnerConsoleEpicFormatter.DetailHint : OwnerConsoleEpicFormatter.ListHint;
            var hintLines = OwnerConsoleEpicFormatter.Wrap(hint, width).ToArray();
            _hint.Text = string.Join("\n", hintLines);
            _hint.Y = Pos.AnchorEnd(hintLines.Length);
            _hint.Height = hintLines.Length;
            _body.Height = Dim.Fill(hintLines.Length);
            Lines = _error is not null ? OwnerConsoleEpicFormatter.Wrap(_error, width).ToArray() :
                _model is null ? ["loading..."] : ShowingDetail && _model.SelectedDetail is { } detail
                    ? OwnerConsoleEpicFormatter.DetailLines(detail, width)
                    : OwnerConsoleEpicFormatter.ListLines(_model, width, SelectedEpicId);
            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, Lines.Count - 1));
            _body.SetSource(new ObservableCollection<string>(Lines));
            var row = ShowingDetail ? _scroll : _model?.Epics.Take(_selectedIndex)
                .Sum(epic => 2 + OwnerConsoleEpicFormatter.Wrap(OwnerConsoleEpicFormatter.Summary(epic), width).Count()) ?? 0;
            _body.SelectedItem = Lines.Count == 0 ? null : Math.Clamp(row, 0, Lines.Count - 1);
            // Scrolling can raise ViewportChanged; suppress reentrant formatting here.
            _body.EnsureSelectedItemVisible();
        }
        finally { _formatting = false; }
    }

    private void Close()
    {
        CancellationTokenSource? session;
        lock (_gate)
        {
            IsOpen = false;
            _dirty = false;
            _revision++;
            session = _session;
            _session = null;
        }
        Visible = false;
        _controller.ModelApplied -= ModelApplied;
        session?.Cancel();
        session?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
