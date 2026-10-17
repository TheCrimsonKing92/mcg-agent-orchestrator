using Mcg.AgentOrchestrator.App.OwnerConsole;
using Fixture = OwnerLandingsQueryTests.Fixture;

// Parallel-safe: each case owns its fixture and custom-zone clock.
public sealed class OwnerLandingsQueryWindowTests
{
    [Fact]
    public void Load_DefaultWindow_IncludesOnlySevenLocalDaysThroughNow()
    {
        using var fixture = new Fixture();
        // Local midnight six days ago is 06:00 UTC, not UTC midnight.
        var midnight = new DateTimeOffset(2026, 10, 10, 6, 0, 0, TimeSpan.Zero);
        var included = fixture.Add("10000001", midnight.AddMinutes(1));
        fixture.Add("20000002", midnight.AddMinutes(-1));
        fixture.Add("30000003", fixture.Clock.Now.AddMinutes(1));

        var result = fixture.Load();

        Assert.Equal(included.Id, Assert.Single(result.Rows).GoalId);
        Assert.Contains("2026-10-10 00:01 local", OwnerLandingsQuery.Render(result)[0]);
    }

    [Fact]
    public void Load_TodayWindow_UsesLocalMidnight()
    {
        using var fixture = new Fixture();
        var today = fixture.Add("10000001", new(2026, 10, 16, 6, 0, 0, TimeSpan.Zero));
        fixture.Add("20000002", new(2026, 10, 16, 5, 59, 0, TimeSpan.Zero));

        Assert.Equal(today.Id, Assert.Single(fixture.Load("today").Rows).GoalId);
    }

    [Fact]
    public void Load_ThirtyDays_IncludesDayTwentyNineAndExcludesDayThirty()
    {
        using var fixture = new Fixture();
        var included = fixture.Add("10000001", fixture.Clock.Now.AddDays(-29));
        fixture.Add("20000002", fixture.Clock.Now.AddDays(-30));

        Assert.Equal(included.Id, Assert.Single(fixture.Load("30").Rows).GoalId);
        Assert.Empty(fixture.Load().Rows);
    }

    [Fact]
    public void Load_TiedTimes_SortsByDescendingInstantThenAscendingGoalId()
    {
        using var fixture = new Fixture();
        var b = fixture.Add("bbbbbbbb", fixture.Clock.Now.AddHours(-1));
        var a = fixture.Add("aaaaaaaa", fixture.Clock.Now.AddHours(-1));
        var later = fixture.Add("cccccccc", fixture.Clock.Now);

        Assert.Equal(new[] { later.Id, a.Id, b.Id }, fixture.Load().Rows.Select(row => row.GoalId));
    }

    [Theory]
    [InlineData("7")]
    [InlineData("TODAY")]
    [InlineData("yesterday")]
    public void Load_InvalidArgument_ReturnsUsageWithoutReadingState(string argument)
    {
        using var fixture = new Fixture();
        var result = fixture.Load(argument);

        Assert.Empty(result.Rows);
        Assert.Equal(new[] { "usage: landings [today|30]" }, OwnerLandingsQuery.Render(result));
        Assert.Equal(0, fixture.Reads);
    }

    [Fact]
    public void Render_EmptyWindow_DisclosesNoLandingsAndZeroUndated()
    {
        using var fixture = new Fixture();
        Assert.Equal(new[] { "No landings recorded in today.",
            "Undated: 0 landed goals have no recorded landing time" }, OwnerLandingsQuery.Render(fixture.Load(" today ")));
    }
}
