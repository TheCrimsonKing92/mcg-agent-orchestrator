using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleActivityPane
{
    private readonly OwnerConsoleActivityFollow _follow = new();
    private readonly OwnerConsoleLastGood<string, OwnerConsoleActivityItem[]> _lastGood = new();
    internal OwnerConsoleListRenderer Source { get; } = new();
    internal string? TitleStatus { get; private set; }

    internal ListView List { get; } = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    internal IReadOnlyList<string> Lines { get; private set; } = [];
    internal IReadOnlyList<OwnerConsoleActivityRow> Rows { get; private set; } = [];
    internal IReadOnlyList<OwnerConsoleActivityKey> Keys { get; private set; } = [];
    internal bool Following => _follow.Following;
    internal string? Filter { get; private set; }

    internal void Render(OwnerConsoleViewModel? model, int width, bool refilter = false, bool follow = false)
    {
        var selected = refilter ? null : List.SelectedItem;
        var top = List.Viewport.Y;
        var state = model?.ActivityState;
        if (state is { Loading: false, Error: null }) state = null;
        var items = model?.Activity.Take(OwnerConsoleViewModelBuilder.MaxActivityItems).ToArray() ?? [];
        if (model is not null && state is null) _lastGood.Remember("activity", items);
        var retained = false;
        if (model is not null && state is not null && _lastGood.TryGet("activity", out var prior))
        { items = prior; retained = true; }
        var showContent = state is null || retained;
        TitleStatus = retained ? state is { Loading: true } ? "refreshing" : "refresh failed: " + state!.Error : null;
        Rows = showContent ? OwnerConsoleActivityRows.Build(items, Filter) : [];
        Keys = showContent ? Rows.Select(row => row.Key).ToArray() : OwnerConsoleActivityFollow.Keys(items);
        IEnumerable<string> pane = !retained && state is { Loading: true } ? ["loading..."] : !retained && state?.Error is { } error
            ? ["ACTIVITY unavailable: " + error] : Rows.Select(row => row.Text);
        Lines = pane.Select((line, row) => OwnerConsoleLineFitter.Fit(line,
            showContent && row < Rows.Count && Rows[row].ItemIndex is { } itemIndex
                ? OwnerActivityNarrator.LineSpans(items[itemIndex]) : OwnerConsoleLineSpans.None, width)).ToArray();
        if (!Source.Apply(List, Lines) && !refilter && !follow) return;
        var placement = showContent ? _follow.Place(Keys, selected, top, List.Viewport.Height) : ((int?)0, 0);
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
        Render(model, width, follow: true);
    }

    internal int? SelectedRow(OwnerConsoleViewModel? model) =>
        model?.ActivityState is null && List.SelectedItem is { } row && row >= 0 && row < Keys.Count ? row : null;

    internal void Inspect(int row) => _follow.Inspect(Keys[row]);

    internal void Scrolled(int row) => _follow.Scrolled(Keys[row], row, List.Viewport.Y, List.Viewport.Height);
}
