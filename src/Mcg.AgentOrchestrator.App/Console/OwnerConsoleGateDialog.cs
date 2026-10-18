using System.Collections.ObjectModel;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleGateDialog : FrameView
{
    private readonly ListView _body = new() { Width = Dim.Fill(), Height = Dim.Fill(1) };
    private OwnerConsoleGateProgress? _gate;
    internal bool Expanded { get; private set; }
    internal bool IsOpen { get; private set; }
    internal OwnerConsolePane ReturnPane { get; private set; }
    internal IReadOnlyList<string> Lines { get; private set; } = [];

    internal OwnerConsoleGateDialog()
    {
        Title = "GATE";
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        Visible = false;
        Add(_body, new Label { Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1,
            Text = "Up/Down PgUp/PgDn scroll  Enter expand passed and baseline  Esc close" });
    }

    internal void Open(OwnerConsolePane returnPane, OwnerConsoleGateProgress? gate)
    {
        ReturnPane = returnPane;
        Expanded = false;
        IsOpen = Visible = true;
        Render(gate);
        _body.SetFocus();
    }

    internal void Render(OwnerConsoleGateProgress? gate)
    {
        _gate = gate;
        if (!IsOpen) return;
        var selected = _body.SelectedItem ?? 0;
        Lines = OwnerConsoleGateLayout.Lines(gate, OwnerConsoleEpicFormatter.Width(_body.Viewport.Width), Expanded);
        _body.SetSource(new ObservableCollection<string>(Lines));
        _body.SelectedItem = Math.Clamp(selected, 0, Math.Max(0, Lines.Count - 1));
    }

    internal bool HandleKey(Key key)
    {
        key.Handled = true;
        if (key == Key.Enter || key == Key.Esc && Expanded)
        {
            Expanded = key == Key.Enter && !Expanded;
            Render(_gate);
            return false;
        }
        if (key == Key.Esc) { IsOpen = Visible = false; return true; }
        if (OwnerConsolePaneNavigator.Target(key, _body.SelectedItem ?? 0, Lines.Count, _body.Viewport.Height) is { } target)
        {
            _body.SelectedItem = target;
            _body.EnsureSelectedItemVisible();
        }
        return false;
    }
}
