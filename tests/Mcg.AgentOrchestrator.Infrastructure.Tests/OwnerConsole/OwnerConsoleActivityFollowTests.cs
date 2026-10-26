using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: fixed inputs and view-local state, with no Terminal.Gui or external resources.
public sealed class OwnerConsoleActivityFollowTests
{
    private static readonly DateTimeOffset Time = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static OwnerConsoleActivityKey[] Keys(int count) => Enumerable.Range(0, count)
        .Select(index => new OwnerConsoleActivityKey(Time.AddSeconds(-index), "acceptance", "11111111", "raw", null, 0)).ToArray();

    [Fact]
    public void Place_Following_SelectsNewestOrNothingAndResetsTop()
    {
        var follow = new OwnerConsoleActivityFollow();
        Assert.True(follow.Following);
        Assert.Equal(((int?)0, 0), follow.Place(Keys(30), 4, 3, 5));
        Assert.Equal(((int?)null, 0), follow.Place([], 4, 3, 5));
        follow.Inspect(Keys(30)[4]);
        follow.Follow();
        Assert.True(follow.Following);
        Assert.Equal(((int?)0, 0), follow.Place(Keys(30), 4, 3, 5));
    }

    [Fact]
    public void Place_InsertedRows_ShiftsSelectionAndViewportTogether()
    {
        var follow = new OwnerConsoleActivityFollow();
        var before = Keys(20);
        follow.Inspect(before[2]);
        OwnerConsoleActivityKey[] current = [.. Keys(3).Select(key => key with { Detail = "new" }), .. before];
        Assert.False(follow.Following);
        Assert.Equal(((int?)5, 4), follow.Place(current, 2, 1, 5));
    }

    [Theory]
    [InlineData(8, 90, 5, 5)]
    [InlineData(1, -4, 5, 0)]
    [InlineData(7, 2, 3, 5)]
    [InlineData(2, 6, 3, 2)]
    [InlineData(7, 2, 0, 7)]
    public void Place_ChangedVisibleRows_ClampsAndMovesTopMinimally(
        int selected, int top, int visibleRows, int expectedTop)
    {
        var follow = new OwnerConsoleActivityFollow();
        var keys = Keys(10);
        follow.Inspect(keys[selected]);
        Assert.Equal(((int?)selected, expectedTop), follow.Place(keys, selected, top, visibleRows));
    }

    [Fact]
    public void Place_MissingKey_ClampsIndexAndRemembersFallback()
    {
        var follow = new OwnerConsoleActivityFollow();
        follow.Inspect(Keys(20)[19]);
        var current = Keys(5);
        Assert.Equal(((int?)4, 2), follow.Place(current, 19, 18, 3));
        OwnerConsoleActivityKey[] appended = [Keys(1)[0] with { Detail = "new" }, .. current];
        Assert.Equal(((int?)5, 3), follow.Place(appended, 4, 2, 3));
        Assert.False(follow.Following);
    }

    [Fact]
    public void Place_EmptyInspection_KeepsIdentityUntilItReturns()
    {
        var follow = new OwnerConsoleActivityFollow();
        var keys = Keys(10);
        follow.Inspect(keys[7]);
        Assert.Equal(((int?)null, 0), follow.Place([], 7, 4, 3));
        Assert.False(follow.Following);
        Assert.Equal(((int?)7, 7), follow.Place(keys, 0, 0, 3));
    }

    [Fact]
    public void Scrolled_SelectionOffScreen_PreservesViewportUntilSelectionMoves()
    {
        var follow = new OwnerConsoleActivityFollow();
        var keys = Keys(30);
        follow.Scrolled(keys[0], 0, 3, 5);
        OwnerConsoleActivityKey[] appended = [.. Keys(2).Select(key => key with { Detail = "new" }), .. keys];
        Assert.Equal(((int?)2, 5), follow.Place(appended, 0, 3, 5));
        follow.Inspect(appended[2]);
        Assert.Equal(((int?)2, 2), follow.Place(appended, 2, 5, 5));
        follow.Scrolled(appended[2], 2, 5, 5);
        follow.Follow();
        Assert.Equal(((int?)0, 0), follow.Place(appended, 2, 5, 5));
    }

    [Fact]
    public void Scrolled_SelectionVisible_StillKeepsItVisibleAfterResize()
    {
        var follow = new OwnerConsoleActivityFollow();
        var keys = Keys(30);
        follow.Scrolled(keys[4], 4, 2, 5);
        Assert.Equal(((int?)4, 4), follow.Place(keys, 4, 2, 1));
    }

    [Fact]
    public void Key_DisplayChanges_DoNotChangeRawIdentity()
    {
        var item = new OwnerConsoleActivityItem(Time, "acceptance", "tag", "11111111", "raw", OwnerQuestionId: "q1");
        var changed = item with { GoalTitle = "Renamed", Phrase = "New narration", Spans = new([]) };
        Assert.Equal(OwnerConsoleActivityFollow.Key([item], 0), OwnerConsoleActivityFollow.Key([changed], 0));
        foreach (var distinct in new[] { item with { Timestamp = Time.AddSeconds(1) }, item with { Kind = "other" },
            item with { GoalPrefix = "22222222" }, item with { Detail = "other" }, item with { OwnerQuestionId = "q2" } })
            Assert.NotEqual(OwnerConsoleActivityFollow.Key([item], 0), OwnerConsoleActivityFollow.Key([distinct], 0));
    }

    [Fact]
    public void Keys_Duplicates_CountFromOldestAndStayStableWhenNewerRowsArrive()
    {
        var item = new OwnerConsoleActivityItem(Time, "acceptance", "tag", "11111111", "raw");
        var before = OwnerConsoleActivityFollow.Keys([item, item]);
        var after = OwnerConsoleActivityFollow.Keys([item, item, item]);
        Assert.Equal(1, before[0].Occurrence);
        Assert.Equal(0, before[1].Occurrence);
        Assert.NotEqual(before[0], before[1]);
        Assert.Equal(before[0], after[1]);
        Assert.Equal(before[1], after[2]);
    }
}
