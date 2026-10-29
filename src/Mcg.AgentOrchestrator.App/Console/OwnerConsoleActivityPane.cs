using System.Collections.ObjectModel;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleActivityPane
{
    private readonly OwnerConsoleActivityFollow _follow = new();

    internal ListView List { get; } = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    internal IReadOnlyList<string> Lines { get; private set; } = [];
    internal IReadOnlyList<OwnerConsoleActivityRow> Rows { get; private set; } = [];
    internal IReadOnlyList<OwnerConsoleActivityKey> Keys { get; private set; } = [];
    internal bool Following => _follow.Following;
    internal string? Filter { get; private set; }

    internal void Render(OwnerConsoleViewModel? model, int width, bool refilter = false)
    {
        var selected = refilter ? null : List.SelectedItem;
        var top = List.Viewport.Y;
        var state = model?.ActivityState;
        var items = model?.Activity.Take(OwnerConsoleViewModelBuilder.MaxActivityItems).ToArray() ?? [];
        Rows = state is null ? OwnerConsoleActivityRows.Build(items, Filter) : [];
        Keys = state is null ? Rows.Select(row => row.Key).ToArray() : OwnerConsoleActivityFollow.Keys(items);
        IEnumerable<string> pane = state is { Loading: true } ? ["loading..."] : state?.Error is { } error
            ? ["ACTIVITY unavailable: " + error] : Rows.Select(row => row.Text);
        Lines = pane.Select((line, row) => OwnerConsoleLineFitter.Fit(line,
            state is not { Loading: true } && state?.Error is null &&
                model is not null && row < Rows.Count && Rows[row].ItemIndex is { } itemIndex
                ? OwnerActivityNarrator.LineSpans(model.Activity[itemIndex]) : OwnerConsoleLineSpans.None, width)).ToArray();
        List.SetSource(new ObservableCollection<string>(Lines));
        var placement = state is null ? _follow.Place(Keys, selected, top, List.Viewport.Height) : ((int?)0, 0);
        List.SelectedItem = placement.Item1;
        List.Viewport = List.Viewport with { Y = placement.Item2 };
    }

    internal void Refilter(OwnerConsoleViewModel? model, string? filter, int width)
    {
        Filter = OwnerConsoleActivityFilter.Normalize(filter);
        Render(model, width, refilter: true);
    }

    internal void FollowLatest(OwnerConsoleViewModel? model, int width)
    {
        _follow.Follow();
        Render(model, width);
    }

    internal int? SelectedRow(OwnerConsoleViewModel? model) =>
        model?.ActivityState is null && List.SelectedItem is { } row && row >= 0 && row < Keys.Count ? row : null;

    internal void Inspect(int row) => _follow.Inspect(Keys[row]);

    internal void Scrolled(int row) => _follow.Scrolled(Keys[row], row, List.Viewport.Y, List.Viewport.Height);
}
