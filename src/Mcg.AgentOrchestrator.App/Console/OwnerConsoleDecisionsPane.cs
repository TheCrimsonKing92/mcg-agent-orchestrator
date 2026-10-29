using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleDecisionsPane
{
    private readonly OwnerConsoleLastGood<string, OwnerConsoleDecision[]> _lastGood = new();
    internal OwnerConsoleListRenderer Source { get; } = new();
    internal string? TitleStatus { get; private set; }
    internal ListView List { get; } = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    internal Label EmptyLabel { get; } = new()
    {
        Text = "Nothing needs you right now.", Width = Dim.Fill(), Height = 1, Visible = false
    };
    internal IReadOnlyList<string> Lines { get; private set; } = [];

    internal void Render(OwnerConsoleViewModel model, int selectedIndex, int width)
    {
        var state = model.DecisionsState;
        if (state is { Loading: false, Error: null }) state = null;
        var items = model.Decisions.ToArray();
        if (state is null) _lastGood.Remember("decisions", items);
        var retained = false;
        if (state is not null && _lastGood.TryGet("decisions", out var prior))
        { items = prior; retained = true; }
        TitleStatus = retained ? state is { Loading: true } ? "refreshing" : "refresh failed: " + state!.Error : null;
        var rows = items.Select(item => OwnerConsoleLineFitter.Fit(
            $"[{item.Number}] {item.GoalPrefix} {item.Kind}: {item.Summary}", OwnerConsoleLineSpans.None, width)).ToArray();
        EmptyLabel.Text = !retained && state is { Loading: true } ? "loading..." :
            !retained && state?.Error is { } error ? "DECISIONS unavailable: " + error : "Nothing needs you right now.";
        EmptyLabel.Visible = rows.Length == 0;
        Lines = EmptyLabel.Visible ? [EmptyLabel.Text] : rows;
        var selected = retained ? List.SelectedItem : selectedIndex < 0 ? (int?)null : selectedIndex;
        var changed = Source.Apply(List, rows);
        if (changed || List.SelectedItem != selected) List.SelectedItem = selected;
    }
}
