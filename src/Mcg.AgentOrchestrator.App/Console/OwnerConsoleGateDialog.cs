using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleGateDialog : FrameView
{
    private readonly ListView _body = new() { Width = Dim.Fill(), Height = Dim.Fill(1) };
    private readonly Label _footer = new() { Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1,
        Text = OwnerConsoleDialogBindings.GateHelp.HintLabel! };
    private OwnerConsoleGateProgress? _gate;
    internal OwnerConsoleListRenderer SourceRenderer { get; } = new();
    private string? _goalId;
    internal bool Expanded { get; private set; }
    internal bool IsOpen { get; private set; }
    internal OwnerConsolePane ReturnPane { get; private set; }
    internal IReadOnlyList<string> Lines { get; private set; } = [];
    internal string FooterText => _footer.Text;
    internal int SelectedLine => _body.SelectedItem ?? 0;
    internal ListView BodyPane => _body;

    internal OwnerConsoleGateDialog()
    {
        Title = "GATE";
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        Visible = false;
        Add(_body, _footer);
        _body.ViewportChanged += (_, _) => { if (IsOpen) RenderTitle(); };
    }

    internal void Open(OwnerConsolePane returnPane, OwnerConsoleGateProgress? gate, string? goalId = null)
    {
        ReturnPane = returnPane;
        _goalId = goalId;
        Expanded = false;
        IsOpen = Visible = true;
        Render(gate);
        _body.SetFocus();
    }

    internal void Render(OwnerConsoleGateProgress? gate)
    {
        _gate = gate;
        if (!IsOpen) return;
        if (_goalId is not null)
        {
            if (gate?.ForMember(_goalId) is { } memberGate) gate = memberGate;
            else _goalId = null;
        }
        var selected = _body.SelectedItem ?? 0;
        Lines = OwnerConsoleGateLayout.Lines(gate, OwnerConsoleEpicFormatter.Width(_body.Viewport.Width), Expanded);
        if (SourceRenderer.Apply(_body, Lines))
            _body.SelectedItem = Math.Clamp(selected, 0, Math.Max(0, Lines.Count - 1));
        RenderTitle();
    }

    private void RenderTitle()
    {
        var title = OwnerConsoleOverflow.Title("GATE",
            OwnerConsoleOverflow.Of(Lines.Count, _body.Viewport.Y, _body.Viewport.Height),
            Frame.Width - OwnerConsoleOverflow.TitleChrome);
        if (Title != title) Title = title;
    }

    internal bool HandleKey(Key key)
    {
        key.Handled = true;
        var binding = key.IsAlt ? null : OwnerConsoleBindings.ForDialogKey(
            OwnerConsoleScope.GateView, (char)key.NoCtrl.AsRune.Value, key.IsCtrl);
        var back = key == Key.Esc || binding?.Command == "quit";
        if (key == Key.Enter || key == Key.Esc && Expanded)
        {
            Expanded = key == Key.Enter && !Expanded;
            Render(_gate);
            return false;
        }
        if (back) { IsOpen = Visible = false; return true; }
        if (OwnerConsolePaneNavigator.Target(key, _body.SelectedItem ?? 0, Lines.Count, _body.Viewport.Height) is { } target)
        {
            _body.SelectedItem = target;
            _body.EnsureSelectedItemVisible();
            RenderTitle();
        }
        return false;
    }
}
