using System.Collections.ObjectModel;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleGateDialog : FrameView
{
    private readonly ListView _body = new() { Width = Dim.Fill(), Height = Dim.Fill(1) };
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
            Text = "Up/Down PgUp/PgDn scroll  Esc close" });
    }

    internal void Open(OwnerConsolePane returnPane, OwnerConsoleGateProgress? gate)
    {
        ReturnPane = returnPane;
        IsOpen = Visible = true;
        Render(gate);
        _body.SetFocus();
    }

    internal void Render(OwnerConsoleGateProgress? gate)
    {
        if (!IsOpen) return;
        var selected = _body.SelectedItem ?? 0;
        Lines = OwnerConsoleGateFormatter.Lines(gate);
        _body.SetSource(new ObservableCollection<string>(Lines));
        _body.SelectedItem = Math.Clamp(selected, 0, Math.Max(0, Lines.Count - 1));
    }

    internal bool HandleKey(Key key)
    {
        key.Handled = true;
        if (key == Key.Esc) { IsOpen = Visible = false; return true; }
        if (OwnerConsolePaneNavigator.Target(key, _body.SelectedItem ?? 0, Lines.Count, _body.Viewport.Height) is { } target)
        {
            _body.SelectedItem = target;
            _body.EnsureSelectedItemVisible();
        }
        return false;
    }
}
