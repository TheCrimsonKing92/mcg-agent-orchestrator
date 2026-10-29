using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleTextDialog : Dialog
{
    internal ListView Body { get; } = new() { Width = Dim.Fill(), Height = Dim.Fill(1) };
    internal OwnerConsoleTextPage Page { get; }
    internal event Action<int>? ChoiceAccepted;
    private readonly string _baseTitle;
    private readonly OwnerConsoleListRenderer _source = new();
    internal string? TitleStatus { get; set; }
    private bool _formatting;

    internal OwnerConsoleTextDialog(string title, string text, IReadOnlyList<int>? choiceLines = null,
        OwnerConsoleTextPage? page = null)
    {
        _baseTitle = title;
        Title = title; Width = Dim.Percent(OwnerConsoleDialogWidth.Percent); Height = Dim.Percent(80);
        Page = page ?? new(text, 60, 15, choiceLines);
        Add(Body);
        Body.ViewportChanged += (_, _) => Format(Body.Viewport.Width, Body.Viewport.Height);
        Initialized += (_, _) => Body.SetFocus();
        Format(60, 15);
    }

    internal void AddConditionalButton(Button button)
    {
        AddButton(button);
        var alignment = button.X;
        // Terminal.Gui includes invisible Pos.Align views in the button row's width.
        button.VisibleChanged += (_, _) => button.X = button.Visible ? alignment : Pos.Absolute(0);
        if (!button.Visible) button.X = Pos.Absolute(0);
    }

    internal void Format(int width, int height)
    {
        if (_formatting) return;
        _formatting = true;
        try { Page.Resize(width, height); Render(); }
        finally { _formatting = false; }
    }

    internal bool HandleKey(Key key)
    {
        if (key == Key.Enter && Page.Choice is { } choice)
        { ChoiceAccepted?.Invoke(choice); key.Handled = true; return true; }
        if (!Page.HandleKey(key)) return false;
        Render(); key.Handled = true; return true;
    }

    internal void Render()
    {
        var wasFormatting = _formatting;
        _formatting = true;
        try
        {
            var changed = _source.Apply(Body, Page.Visible);
            var selected = Page.Selected - Page.Offset;
            if (changed || Body.SelectedItem != selected) Body.SelectedItem = selected;
            var title = OwnerConsoleOverflow.Title(_baseTitle + (TitleStatus is null ? "" : $" ({TitleStatus})"),
                Page.Overflow, Frame.Width - OwnerConsoleOverflow.TitleChrome);
            if (Title != title) Title = title;
        }
        finally { _formatting = wasFormatting; }
    }
}
