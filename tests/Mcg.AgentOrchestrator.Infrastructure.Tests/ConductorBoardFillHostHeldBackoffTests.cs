using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: each harness owns its SQLite/event root, fixed clock and main-head seam.
public sealed class ConductorBoardFillHostHeldBackoffTests
{
    [Fact]
    public async Task Held_round_suppresses_ticks_until_main_moves_then_retries_the_same_item()
    {
        using var h = new BoardFillTestHarness();
        h.Reply = () => new("held", 1, null, null, null, [], "head-not-main");
        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "held draft finished");
        h.Host.ServiceTick(h.Kernel);
        var held = Assert.Single(h.Store.ReadAll());
        Assert.Equal("held", held.Outcome);

        for (var tick = 0; tick < 3; tick++)
        {
            h.Host.ServiceTick(h.Kernel);
            Assert.Null(h.Host.CurrentRound);
            Assert.Equal(1, h.Calls);
            Assert.Equal(held.Id, Assert.Single(h.Store.ReadAll()).Id);
            using var reported = BoardFillTestHarness.Event(Assert.Single(h.Events()));
            Assert.Equal("board-fill-draft", reported.RootElement.GetProperty("eventKind").GetString());
        }

        h.MoveMain();
        h.Host.ServiceTick(h.Kernel);
        Assert.NotNull(h.Host.CurrentRound);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "draft after main moved finished");
        var retry = Assert.Single(h.Store.ReadAll().Where(round => round.Outcome is null));
        Assert.Equal(held.BacklogItemId, retry.BacklogItemId);
        Assert.NotEqual(held.Id, retry.Id);
        Assert.Equal(2, h.Calls);
        Assert.Single(h.Events());
    }

    [Fact]
    public async Task Failed_round_with_unchanged_main_starts_the_next_round()
    {
        using var h = new BoardFillTestHarness();
        h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = 10 };
        h.Reply = () => new("failed", 1, null, null, null, [], "fixture");
        var main = h.MainHead;
        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "failed draft finished");
        h.Host.ServiceTick(h.Kernel);

        Assert.NotNull(h.Host.CurrentRound);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "next draft finished");
        var failed = Assert.Single(h.Store.ReadAll().Where(round => round.Outcome == "failed"));
        var retry = Assert.Single(h.Store.ReadAll().Where(round => round.Outcome is null));
        Assert.Equal(failed.BacklogItemId, retry.BacklogItemId);
        Assert.Equal(main, h.MainHead);
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task Hold_compares_the_rounds_observed_main_instead_of_the_harvest_tick()
    {
        using var h = new BoardFillTestHarness();
        var heldMain = h.MainHead;
        h.Reply = () => new("held", 1, null, null, null, [], "head-not-main", heldMain);
        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "held draft finished");
        h.MoveMain();
        h.Host.ServiceTick(h.Kernel);
        Assert.Null(h.Host.CurrentRound);

        h.Host.ServiceTick(h.Kernel);
        Assert.NotNull(h.Host.CurrentRound);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "draft after main moved finished");
        Assert.Equal(2, h.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("throws")]
    public async Task Unresolved_main_keeps_the_hold(string resolution)
    {
        using var h = new BoardFillTestHarness();
        h.Reply = () => new("held", 1, null, null, null, [], "head-not-main");
        var host = h.NewHost(() => resolution switch
        {
            "null" => null,
            "empty" => "",
            _ => throw new InvalidOperationException("main unavailable")
        });
        try
        {
            host.ServiceTick(h.Kernel);
            await PanelTestHarness.Signal(host.CurrentRound!, "held draft finished");
            host.ServiceTick(h.Kernel);
            h.MoveMain();
            host.ServiceTick(h.Kernel);

            Assert.Null(host.CurrentRound);
            Assert.Equal(1, h.Calls);
            Assert.Equal("held", Assert.Single(h.Store.ReadAll()).Outcome);
            Assert.Single(h.Events());
        }
        finally { host.Stop(); }
    }

    [Fact]
    public async Task Policy_and_backlog_changes_preserve_hold_but_a_new_host_retries()
    {
        using var h = new BoardFillTestHarness();
        h.Reply = () => new("held", 1, null, null, null, [], "toplevel-not-root");
        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "held draft finished");
        h.Host.ServiceTick(h.Kernel);
        h.Policy = h.Policy with
        {
            BoardFillMaxDraftsPerDay = h.Policy.BoardFillMaxDraftsPerDay + 1,
            BoardFillTargetActiveGoals = h.Policy.BoardFillTargetActiveGoals + 1,
            BoardFillMode = ConductorBoardFillMode.Off
        };
        h.Host.ServiceTick(h.Kernel);
        h.Policy = h.Policy with { BoardFillMode = ConductorBoardFillMode.Shadow };
        h.Items = [BoardFillReadyItemSelectorTests.Item('b')];
        h.Host.ServiceTick(h.Kernel);

        Assert.Null(h.Host.CurrentRound);
        Assert.Equal(1, h.Calls);
        Assert.Equal("held", Assert.Single(h.Store.ReadAll()).Outcome);
        Assert.Single(h.Events());
        h.Host.Stop();
        var restarted = h.NewHost();
        try
        {
            restarted.ServiceTick(h.Kernel);
            Assert.NotNull(restarted.CurrentRound);
            await PanelTestHarness.Signal(restarted.CurrentRound!, "draft after restart finished");
            Assert.Equal(2, h.Calls);
            Assert.Equal(Assert.Single(h.Items).Id,
                Assert.Single(h.Store.ReadAll().Where(round => round.Outcome is null)).BacklogItemId);
        }
        finally { restarted.Stop(); }
    }
}
