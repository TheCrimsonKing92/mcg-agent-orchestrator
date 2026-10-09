using System.Collections.ObjectModel;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleTextDialog : Dialog
{
    internal ListView Body { get; } = new() { Width = Dim.Fill(), Height = Dim.Fill(1) };
    internal OwnerConsoleTextPage Page { get; }
    internal event Action<int>? ChoiceAccepted;
    private bool _formatting;

    internal OwnerConsoleTextDialog(string title, string text, IReadOnlyList<int>? choiceLines = null)
    {
        Title = title; Width = Dim.Percent(85); Height = Dim.Percent(80);
        Page = new(text, 60, 15, choiceLines);
        Add(Body);
        Body.ViewportChanged += (_, _) => Format(Body.Viewport.Width, Body.Viewport.Height);
        Initialized += (_, _) => Body.SetFocus();
        Format(60, 15);
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

    private void Render()
    {
        var wasFormatting = _formatting;
        _formatting = true;
        try
        {
            Body.SetSource(new ObservableCollection<string>(Page.Visible));
            Body.SelectedItem = Page.Selected - Page.Offset;
        }
        finally { _formatting = wasFormatting; }
    }
}
