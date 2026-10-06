using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: the harness owns a unique SQLite/event root and a fixed clock.
public sealed class ConductorBoardFillHostHeldRoundTests
{
    [Fact]
    public async Task Held_round_is_reported_once_and_retried_after_main_moves()
    {
        using var h = new BoardFillTestHarness();
        h.Reply = () => new("held", 1, null, null, null, [], "head-not-main");
        var item = Assert.Single(h.Items);

        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "held draft finished");
        h.Host.ServiceTick(h.Kernel);

        var round = Assert.Single(h.Store.ReadAll());
        Assert.Equal("held", round.Outcome);
        Assert.Equal(item.Id, round.BacklogItemId);
        Assert.Equal("head-not-main", round.Failure);
        Assert.True(round.Reported);
        Assert.Null(h.Host.CurrentRound);
        Assert.Equal(1, h.Calls);
        using var reported = BoardFillTestHarness.Event(Assert.Single(h.Events()));
        Assert.Equal("board-fill-draft", reported.RootElement.GetProperty("eventKind").GetString());
        Assert.Contains("BOARD_FILL_DRAFT", reported.RootElement.GetProperty("detail").GetString());
        Assert.Contains("outcome=held", reported.RootElement.GetProperty("detail").GetString());

        h.MoveMain();
        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "retry draft finished");
        var retry = Assert.Single(h.Store.ReadAll().Where(candidate => candidate.Outcome is null));
        Assert.Equal(item.Id, retry.BacklogItemId);
        Assert.NotEqual(round.Id, retry.Id);
        Assert.Equal(2, h.Calls);
        Assert.Single(h.Events());
    }

    [Theory]
    [InlineData("held", true)]
    [InlineData("failed", false)]
    public async Task Daily_cap_excludes_held_rounds_but_counts_failed_rounds(string outcome, bool fourthStarts)
    {
        using var h = new BoardFillTestHarness();
        h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = 3 };
        h.Items = [BoardFillReadyItemSelectorTests.Item('a'), BoardFillReadyItemSelectorTests.Item('b')];
        h.Reply = () => new(outcome, 1, null, null, null, [], "fixture");

        h.Host.ServiceTick(h.Kernel);
        for (var finished = 0; finished < 3; finished++)
        {
            await PanelTestHarness.Signal(h.Host.CurrentRound!, "daily-cap draft finished");
            h.Host.ServiceTick(h.Kernel);
            if (outcome == "held" && finished < 2) h.MoveMain();
            if (outcome == "held" && finished < 2) h.Host.ServiceTick(h.Kernel);
        }

        var rounds = h.Store.ReadAll();
        Assert.Equal(3, rounds.Count);
        Assert.All(rounds, round =>
        {
            Assert.Equal(outcome, round.Outcome);
            Assert.Equal(h.Clock.UtcNow.UtcDateTime.Date, round.StartedAt.UtcDateTime.Date);
        });
        Assert.Equal(3, h.Calls);
        Assert.Null(h.Host.CurrentRound);
        Assert.Equal(fourthStarts ? 0 : 3, h.Store.StartedOnUtcDay(h.Clock.UtcNow));

        if (outcome == "held") h.MoveMain();
        h.Host.ServiceTick(h.Kernel);
        if (fourthStarts)
        {
            await PanelTestHarness.Signal(h.Host.CurrentRound!, "fourth draft finished");
            Assert.Equal(4, h.Calls);
            Assert.Equal(4, h.Store.ReadAll().Count);
            Assert.Single(h.Store.ReadAll().Where(round => round.Outcome is null));
            Assert.Equal(1, h.Store.StartedOnUtcDay(h.Clock.UtcNow));
        }
        else
        {
            Assert.Null(h.Host.CurrentRound);
            Assert.Equal(3, h.Calls);
            Assert.Equal(3, h.Store.ReadAll().Count);
        }
    }
}
