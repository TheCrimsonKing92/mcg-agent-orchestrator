using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorBoardFillHostTests
{
    [Fact]
    public void Off_is_a_noop_without_store_snapshot_or_model_access()
    {
        using var h = new BoardFillTestHarness();
        var host = new ConductorBoardFillHost(h.Store, (_, _) => throw new Exception("draft must not run"),
            () => throw new Exception("snapshot must not run"), (_, _) => throw new Exception("readiness must not run"),
            () => h.Policy with { BoardFillMode = ConductorBoardFillMode.Off }, new(h.EventsPath), () => h.Clock.UtcNow);
        host.ServiceTick(h.Kernel);
        Assert.Null(host.CurrentRound);
        Assert.False(File.Exists(h.StorePath));
        Assert.Empty(h.Events());
        host.Stop();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void At_or_above_target_starts_no_round(int active)
    {
        using var h = new BoardFillTestHarness();
        h.Policy = h.Policy with { BoardFillTargetActiveGoals = active == 0 ? 0 : 1 };
        for (var i = 0; i < active; i++) h.Kernel.CreateGoal("Active " + i);
        h.Host.ServiceTick(h.Kernel);
        Assert.Null(h.Host.CurrentRound);
        Assert.Equal(0, h.Calls);
        Assert.Empty(h.Store.ReadAll());
    }

    [Fact]
    public async Task Terminal_goals_do_not_count_toward_target()
    {
        using var h = new BoardFillTestHarness();
        h.Policy = h.Policy with { BoardFillTargetActiveGoals = 1 };
        var terminal = h.Kernel.CreateGoal("Terminal");
        h.Kernel.CancelGoal(terminal.Id, "Fixture");
        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "draft finished");
        Assert.Equal(1, h.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Daily_cap_counts_starts_including_failed_rounds(int cap)
    {
        using var h = new BoardFillTestHarness();
        h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = cap };
        if (cap == 1)
        {
            var round = h.Store.Begin(h.Items[0], h.Clock.UtcNow);
            h.Store.Finish(round, new("failed", 1, null, null, null, [], "fixture"), h.Clock.UtcNow);
        }
        h.Host.ServiceTick(h.Kernel);
        Assert.Null(h.Host.CurrentRound);
        Assert.Equal(0, h.Calls);
        Assert.Equal(cap, h.Store.ReadAll().Count);
    }

    [Theory]
    [InlineData(ConductorBoardFillMode.Shadow)]
    [InlineData(ConductorBoardFillMode.File)]
    public async Task Single_off_tick_round_is_harvested_and_reported_once(ConductorBoardFillMode mode)
    {
        using var h = new BoardFillTestHarness();
        h.Held = true;
        h.Policy = h.Policy with { BoardFillMode = mode };
        var start = h.Clock.UtcNow;
        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Started.Task, "draft started");
        var task = h.Host.CurrentRound;
        Assert.NotNull(task);
        Assert.False(task.IsCompleted);
        Assert.Null(Assert.Single(h.Store.ReadAll()).Outcome);
        Assert.Empty(h.Events());
        h.Host.ServiceTick(h.Kernel);
        Assert.Same(task, h.Host.CurrentRound);
        Assert.Equal(1, h.Calls);
        h.Release();
        await PanelTestHarness.Signal(task, "draft finished");
        // Completion alone changes neither the durable row nor event log.
        Assert.Null(Assert.Single(h.Store.ReadAll()).Outcome);
        Assert.Empty(h.Events());
        h.Clock.UtcNow = start.AddMinutes(1);
        h.Host.ServiceTick(h.Kernel);
        var round = Assert.Single(h.Store.ReadAll());
        Assert.Equal("draft", round.Outcome);
        Assert.Equal(h.Items[0].Id, round.BacklogItemId);
        Assert.Equal(h.Items[0].UpdatedAt, round.ItemUpdatedAt);
        Assert.Equal(new string('c', 40), round.MainHead);
        Assert.Equal(h.Draft().DraftPath, round.DraftPath);
        Assert.Equal(h.Draft().ReceiptPath, round.ReceiptPath);
        Assert.Equal(h.Draft().Checks.ToArray(), round.Checks.ToArray());
        Assert.Equal(start, round.StartedAt);
        Assert.Equal(h.Clock.UtcNow, round.FinishedAt);
        Assert.True(round.Reported);
        Assert.Null(h.Host.CurrentRound);
        Assert.Equal(1, h.Calls);
        using var entry = BoardFillTestHarness.Event(Assert.Single(h.Events()));
        Assert.Equal("board-fill-draft", entry.RootElement.GetProperty("eventKind").GetString());
        Assert.Equal("decision", entry.RootElement.GetProperty("operator").GetString());
        Assert.StartsWith($"BOARD_FILL_DRAFT backlog={h.Items[0].Id[..8]} outcome=draft checks=2/2 draft={h.Draft().DraftPath}",
            entry.RootElement.GetProperty("detail").GetString());
        h.Host.ServiceTick(h.Kernel);
        Assert.Single(h.Events());
        Assert.Single(new ConductorBoardFillDraftStore(h.StorePath).ReadAll());
    }

    [Fact]
    public async Task Seam_exception_is_failed_and_reported_once()
    {
        using var h = new BoardFillTestHarness();
        h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = 1 };
        h.Reply = () => throw new InvalidOperationException("injected seam failure");
        h.Host.ServiceTick(h.Kernel);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PanelTestHarness.Signal(h.Host.CurrentRound!, "failed round finished"));
        h.Host.ServiceTick(h.Kernel);
        var round = Assert.Single(h.Store.ReadAll());
        Assert.Equal("failed", round.Outcome);
        Assert.Contains("injected seam failure", round.Failure);
        using var entry = BoardFillTestHarness.Event(Assert.Single(h.Events()));
        Assert.StartsWith($"BOARD_FILL_DRAFT backlog={h.Items[0].Id[..8]} outcome=failed checks=0/0 draft=none",
            entry.RootElement.GetProperty("detail").GetString());
        h.Host.ServiceTick(h.Kernel);
        Assert.Single(h.Events());
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Previous_utc_day_does_not_consume_today_and_goal_scope_is_idle()
    {
        using var h = new BoardFillTestHarness();
        h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = 1 };
        var round = h.Store.Begin(h.Items[0], h.Clock.UtcNow.AddDays(-1));
        h.Store.Finish(round, new("failed", 1, null, null, null, [], "fixture"), h.Clock.UtcNow.AddDays(-1));
        h.Host.ServiceTick(h.Kernel, "scoped-goal");
        Assert.Null(h.Host.CurrentRound);
        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "today's draft finished");
        Assert.Equal(1, h.Calls);
        Assert.Equal(1, h.Store.StartedOnUtcDay(h.Clock.UtcNow));
    }

    [Fact]
    public void Restart_recovers_unfinished_round_and_deduplicates_its_event()
    {
        using var h = new BoardFillTestHarness();
        h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = 1 };
        h.Store.Begin(h.Items[0], h.Clock.UtcNow);
        h.Host.ServiceTick(h.Kernel);
        var round = Assert.Single(h.Store.ReadAll());
        Assert.Equal("failed", round.Outcome);
        Assert.Equal("interrupted", round.Failure);
        Assert.Single(h.Events());
        // Replay the durable notification after an event-write/mark-reported crash boundary.
        var writer = new ConductEventLogWriter(h.EventsPath);
        Assert.True(writer.AppendRequired("board-fill-draft", null, "duplicate", h.Clock.UtcNow, "board-fill-" + round.Id));
        var restarted = h.NewHost();
        restarted.ServiceTick(h.Kernel);
        Assert.Single(h.Events());
        Assert.Equal(0, h.Calls);
        restarted.Stop();
    }
}
