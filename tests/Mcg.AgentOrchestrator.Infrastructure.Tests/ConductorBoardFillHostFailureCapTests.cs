using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each harness owns its SQLite/event files; all rounds use its fixed clock.
public sealed class ConductorBoardFillHostFailureCapTests
{
    [Fact]
    public async Task Two_failures_select_the_next_item_and_a_new_note_reenables_the_first()
    {
        using var h = new BoardFillTestHarness();
        h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = 10 };
        var first = BoardFillReadyItemSelectorTests.Item('a');
        var second = BoardFillReadyItemSelectorTests.Item('b');
        h.Items = [first, second];
        h.Reply = () => new("failed", 1, null, null, null, [], "fixture");

        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "first failure finished");
        Assert.Equal(first.Id, RunningRound(h).BacklogItemId);
        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "second failure finished");
        Assert.Equal(first.Id, RunningRound(h).BacklogItemId);

        h.Reply = h.Draft;
        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "second item draft finished");
        Assert.Equal(second.Id, RunningRound(h).BacklogItemId);
        var failures = h.Store.ReadAll().Where(round => round.Outcome == "failed").ToArray();
        Assert.Equal(2, failures.Length);
        Assert.All(failures, round => Assert.Equal(first.Id, round.BacklogItemId));

        var annotated = first with { Notes = [new(first.UpdatedAt.AddMinutes(1), "Additional evidence")] };
        Assert.Equal(first.UpdatedAt, annotated.UpdatedAt);
        h.Items = [annotated, second];
        h.Reply = () => new("failed", 1, null, null, null, [], "fixture");
        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "annotated item draft finished");
        var retry = RunningRound(h);
        Assert.Equal(first.Id, retry.BacklogItemId);
        Assert.Equal(BoardFillReadyItemSelector.ChangeStamp(annotated), retry.ChangeStamp);
        Assert.Equal(4, h.Calls);
        Assert.Equal("draft", Assert.Single(h.Store.ReadAll().Where(round => round.BacklogItemId == second.Id)).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_cap_compares_the_latest_body_or_note_stamp(bool noteChange)
    {
        using var h = new BoardFillTestHarness();
        var old = BoardFillReadyItemSelectorTests.Item();
        var stamp = old.UpdatedAt.AddMinutes(1);
        var current = noteChange ? old with { Notes = [new(stamp, "New evidence")] } : old with { UpdatedAt = stamp };
        h.Items = [current];
        for (var i = 0; i < 2; i++)
            h.Store.Finish(h.Store.Begin(old, h.Clock.UtcNow), new("failed", 1, null, null, null, []), h.Clock.UtcNow);

        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "edited item draft finished");
        Assert.Equal(current.Id, RunningRound(h).BacklogItemId);
        Assert.Equal(stamp, RunningRound(h).ChangeStamp);
        Assert.Equal(1, h.Calls);
    }

    private static BoardFillDraftRound RunningRound(BoardFillTestHarness h) =>
        Assert.Single(h.Store.ReadAll().Where(round => round.Outcome is null));
}
