using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its store and uses an injected clock.
public sealed class BoardFillStaleItemRuleTests
{
    [Fact]
    public void Latest_stale_waits_for_item_or_main_change()
    {
        using var h = new BoardFillTestHarness();
        var item = Assert.Single(h.Items);
        var head = new string('c', 40);
        var round = h.Store.Begin(item, h.Clock.UtcNow);
        h.Store.Finish(round, new("stale", 2, head, null, null, []), h.Clock.UtcNow);
        Assert.Null(Select(item, h.Store, head));
        Assert.Null(Select(item, h.Store, null));
        Assert.Same(item, Select(item, h.Store, new string('d', 40)));
        var edited = item with { UpdatedAt = h.Clock.UtcNow.AddMinutes(1) };
        Assert.Same(edited, Select(edited, h.Store, head));
        var noted = item with { Notes = [new(h.Clock.UtcNow.AddMinutes(1), "item changed")] };
        Assert.Same(noted, Select(noted, h.Store, head));
        Assert.Equal(1, h.Store.StartedOnUtcDay(h.Clock.UtcNow));
    }

    [Fact]
    public void Legacy_stale_without_a_main_baseline_does_not_park_the_item()
    {
        using var h = new BoardFillTestHarness();
        var item = Assert.Single(h.Items);
        var round = h.Store.Begin(item, h.Clock.UtcNow);
        h.Store.Finish(round, new("stale", 2, null, null, null, []), h.Clock.UtcNow);
        Assert.Same(item, Select(item, h.Store, new string('c', 40)));
    }

    [Fact]
    public void Older_stale_does_not_override_a_later_round()
    {
        using var h = new BoardFillTestHarness();
        var item = Assert.Single(h.Items);
        var head = new string('c', 40);
        var stale = h.Store.Begin(item, h.Clock.UtcNow);
        h.Store.Finish(stale, new("stale", 2, head, null, null, []), h.Clock.UtcNow);
        var failed = h.Store.Begin(item, h.Clock.UtcNow);
        h.Store.Finish(failed, new("failed", 1, head, null, null, [], FailureKind: "pre-model"), h.Clock.UtcNow);
        Assert.Same(item, Select(item, h.Store, head));
    }

    [Fact]
    public void Existing_draft_remains_excluded_when_main_changes()
    {
        using var h = new BoardFillTestHarness();
        var item = Assert.Single(h.Items);
        var round = h.Store.Begin(item, h.Clock.UtcNow);
        h.Store.Finish(round, h.Draft(), h.Clock.UtcNow);
        Assert.Null(Select(item, h.Store, new string('d', 40)));
    }

    private static BacklogItem? Select(BacklogItem item, ConductorBoardFillDraftStore store, string? head) =>
        BoardFillReadyItemSelector.Select([item], [], store.AlreadyDrafted([item], head),
            BoardFillFailureKindSelectionTests.Ready, store.ReadAll());
}
