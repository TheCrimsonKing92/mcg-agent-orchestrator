using Mcg.AgentOrchestrator.App.OwnerConsole;
using Fixture = OwnerLandingsQueryTests.Fixture;

// Parallel-safe: fixed clock, in-memory state and a unique filesystem root per fixture.
public sealed class OwnerLandingsPageTests
{
    [Fact]
    public void From_TwoLandings_MapsBothRowLinesToGoalIdentity()
    {
        using var fixture = new Fixture();
        var newer = fixture.Add("10000001", fixture.Clock.Now);
        var older = fixture.Add("20000002", fixture.Clock.Now.AddMinutes(-1));

        var page = OwnerLandingsPage.From(fixture.Load());
        var lines = page.Text.Split('\n');

        Assert.Equal("Enter on a row opens its goal detail.", lines[0]);
        Assert.Equal(OwnerLandingsQuery.Render(fixture.Load()), lines.Skip(1));
        Assert.Equal(new[] { 1, 2, 3, 4 }, page.ChoiceLines);
        Assert.Equal(new[] { newer.Id, newer.Id, older.Id, older.Id }, page.ChoiceGoalIds);
        for (var i = 0; i < page.ChoiceLines.Count; i++)
        {
            var line = lines[page.ChoiceLines[i]];
            Assert.True(line.Contains(page.ChoiceGoalIds[i][..8], StringComparison.Ordinal) ||
                line.StartsWith("  ", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void From_EmptyWindow_MatchesQueryWithoutChoices()
    {
        using var fixture = new Fixture();
        var result = fixture.Load();

        AssertPlain(OwnerLandingsPage.From(result), string.Join("\n", OwnerLandingsQuery.Render(result)));
    }

    [Fact]
    public void Load_InvalidWindow_MatchesQueryUsageWithoutChoices()
    {
        using var fixture = new Fixture();
        var expected = string.Join("\n", OwnerLandingsQuery.Render(fixture.Load("bogus")));
        Assert.Equal("usage: landings [today|30]", expected);

        AssertPlain(OwnerLandingsPage.From(fixture.Load("bogus")), expected);
        AssertPlain(OwnerLandingsPage.Load(fixture, fixture.DirectoryPath, fixture.Clock, "bogus"), expected);
    }

    [Fact]
    public void Load_ReadFailure_MatchesQueryFailureWithoutChoices()
    {
        using var fixture = new Fixture { ReadError = new IOException("receipt unavailable") };
        var expected = OwnerLandingsQuery.Text(fixture, fixture.DirectoryPath, fixture.Clock, "");

        AssertPlain(OwnerLandingsPage.Load(fixture, fixture.DirectoryPath, fixture.Clock, ""), expected);
    }

    [Fact]
    public void Load_Cancellation_PropagatesToCaller()
    {
        using var fixture = new Fixture { ReadError = new OperationCanceledException() };

        Assert.Throws<OperationCanceledException>(() =>
            OwnerLandingsPage.Load(fixture, fixture.DirectoryPath, fixture.Clock, ""));
    }

    [Fact]
    public void Plain_Text_HasNoChoices() => AssertPlain(OwnerLandingsPage.Plain("x"), "x");

    private static void AssertPlain(OwnerLandingsPage page, string text)
    {
        Assert.Equal(text, page.Text);
        Assert.Empty(page.ChoiceLines);
        Assert.Empty(page.ChoiceGoalIds);
    }
}
