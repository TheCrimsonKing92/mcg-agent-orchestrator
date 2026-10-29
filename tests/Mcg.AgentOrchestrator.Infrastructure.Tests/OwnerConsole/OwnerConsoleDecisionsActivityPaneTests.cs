using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each test owns and disposes its controls; no application, driver or shared state.
public sealed class OwnerConsoleDecisionsActivityPaneTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 30, TimeSpan.Zero);

    [Theory]
    [InlineData(true, null, "loading...")]
    [InlineData(false, "offline", "DECISIONS unavailable: offline")]
    [InlineData(false, null, "Nothing needs you right now.")]
    [InlineData(true, "offline", "loading...")]
    public void DecisionsRender_NoRows_ReplacesPreviousRowsWithStateText(bool loading, string? error, string expected)
    {
        var pane = new OwnerConsoleDecisionsPane();
        using var list = pane.List;
        using var label = pane.EmptyLabel;
        pane.Render(Model() with { Decisions = [Decision(1, "Ship?")] }, 0, 80);
        Assert.Equal(1, list.Source!.Count);

        pane.Render(Model() with { DecisionsState = new(loading, error) }, -1, 80);

        Assert.Equal(expected, Assert.Single(pane.Lines));
        Assert.Equal(expected, label.Text);
        Assert.True(label.Visible);
        Assert.Equal(0, list.Source!.Count);
        Assert.Null(list.SelectedItem);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "offline")]
    public void DecisionsRender_RowsPresent_HidesStateLabelAndAppliesSelection(bool loading, string? error)
    {
        var pane = new OwnerConsoleDecisionsPane();
        using var list = pane.List;
        using var label = pane.EmptyLabel;
        var model = Model() with
        {
            Decisions = [Decision(1, "Ship?"), Decision(2, "Retry?")],
            DecisionsState = new(loading, error)
        };

        pane.Render(model, 1, 80);

        Assert.Equal(new[] { "[1] 11111111 HumanInput: Ship?", "[2] 11111111 HumanInput: Retry?" }, pane.Lines);
        Assert.False(label.Visible);
        Assert.Equal(2, list.Source!.Count);
        Assert.Equal(1, list.SelectedItem);
        pane.Render(model, -1, 80);
        Assert.Null(list.SelectedItem);
    }

    [Theory]
    [InlineData(true, null, "loading...")]
    [InlineData(false, "offline", "ACTIVITY unavailable: offline")]
    [InlineData(true, "offline", "loading...")]
    public void ActivityRender_LoadingOrError_ReplacesPreviousRowsWithStateText(bool loading, string? error, string expected)
    {
        var pane = new OwnerConsoleActivityPane();
        using var list = pane.List;
        var model = Model() with { Activity = [Activity("Older", -1), Activity("Oldest", -2)] };
        pane.Render(model, 80);
        Assert.Equal(2, list.Source!.Count);
        list.SelectedItem = 1;

        pane.Render(model with { ActivityState = new(loading, error) }, 80);

        Assert.Equal(expected, Assert.Single(pane.Lines));
        Assert.Equal(1, list.Source!.Count);
        Assert.Equal(expected, list.Source.ToList()![0]);
        Assert.Empty(pane.Rows);
        Assert.Equal(0, list.SelectedItem);
        Assert.Equal(0, list.Viewport.Y);
        Assert.Null(pane.SelectedRow(model with { ActivityState = new(loading, error) }));
    }

    [Fact]
    public void ActivityFollowLatest_InspectingOlderRow_SelectsNewestAndKeepsFollowingOnRefresh()
    {
        var pane = new OwnerConsoleActivityPane();
        using var list = pane.List;
        list.Frame = new Rectangle(0, 0, 80, 2);
        var model = Model() with
        {
            Activity = [Activity("Recent", 0), Activity("Older", -1), Activity("Oldest", -2)]
        };
        pane.Render(model, 80);
        list.SelectedItem = 2;
        list.Viewport = list.Viewport with { Y = 1 };
        pane.Inspect(2);
        Assert.False(pane.Following);
        Assert.Equal(2, list.SelectedItem);
        var newest = Activity("Newest arrival", 1);
        var refreshed = model with { Activity = [newest, .. model.Activity] };

        pane.FollowLatest(refreshed, 80);

        Assert.True(pane.Following);
        Assert.Equal(0, list.SelectedItem);
        Assert.Equal(0, list.Viewport.Y);
        Assert.Equal(0, pane.SelectedRow(refreshed));
        Assert.Equal(0, pane.Rows[0].ItemIndex);
        Assert.Contains("Newest arrival", pane.Lines[list.SelectedItem!.Value]);
        Assert.Equal(newest.Timestamp, pane.Keys[0].Timestamp);
        var next = refreshed with { Activity = [Activity("Next arrival", 2), .. refreshed.Activity] };

        pane.Render(next, 80);

        Assert.True(pane.Following);
        Assert.Equal(0, list.SelectedItem);
        Assert.Equal(0, list.Viewport.Y);
        Assert.Contains("Next arrival", pane.Lines[list.SelectedItem!.Value]);
    }

    [Fact]
    public void ActivityRender_NullModel_ClearsRowsAndSelection()
    {
        var pane = new OwnerConsoleActivityPane();
        using var list = pane.List;
        pane.Render(Model() with { Activity = [Activity("Previous", 0)] }, 80);
        Assert.Equal(0, list.SelectedItem);

        pane.Render(null, 80);

        Assert.Empty(pane.Lines);
        Assert.Empty(pane.Rows);
        Assert.Empty(pane.Keys);
        Assert.Equal(0, list.Source!.Count);
        Assert.Null(list.SelectedItem);
        Assert.Equal(0, list.Viewport.Y);
        Assert.Null(pane.SelectedRow(null));
    }

    private static OwnerConsoleViewModel Model() => new(new(false, 0, 0, 0, null, 0), [], [], []);

    private static OwnerConsoleDecision Decision(int number, string summary) =>
        new("q" + number, number, "11111111-goal", "11111111", OwnerQuestionKind.HumanInput,
            summary, summary, null, null, null);

    private static OwnerConsoleActivityItem Activity(string detail, int seconds) =>
        new(Now.AddSeconds(seconds), "goal-lifecycle", "goal", "11111111", detail, Phrase: detail);
}
