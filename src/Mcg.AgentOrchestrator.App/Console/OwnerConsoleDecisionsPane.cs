using System.Collections.ObjectModel;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleDecisionsPane
{
    internal ListView List { get; } = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    internal Label EmptyLabel { get; } = new()
    {
        Text = "Nothing needs you right now.", Width = Dim.Fill(), Height = 1, Visible = false
    };
    internal IReadOnlyList<string> Lines { get; private set; } = [];

    internal void Render(OwnerConsoleViewModel model, int selectedIndex, int width)
    {
        var rows = model.Decisions.Select(item => OwnerConsoleLineFitter.Fit(
            $"[{item.Number}] {item.GoalPrefix} {item.Kind}: {item.Summary}", OwnerConsoleLineSpans.None, width)).ToArray();
        EmptyLabel.Text = model.DecisionsState is { Loading: true } ? "loading..." :
            model.DecisionsState?.Error is { } error ? "DECISIONS unavailable: " + error : "Nothing needs you right now.";
        EmptyLabel.Visible = rows.Length == 0;
        Lines = EmptyLabel.Visible ? [EmptyLabel.Text] : rows;
        List.SetSource(new ObservableCollection<string>(rows));
        List.SelectedItem = selectedIndex < 0 ? null : selectedIndex;
    }
}
