using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each harness owns its store and injected clock.
public sealed class BoardFillFailureKindSelectionTests
{
    [Theory]
    [InlineData("pre-model", true)]
    [InlineData("model-round", false)]
    [InlineData(null, false)]
    public void Two_failures_exclude_only_model_rounds_until_item_changes(string? kind, bool selected)
    {
        using var h = new BoardFillTestHarness();
        var item = Assert.Single(h.Items);
        for (var index = 0; index < 2; index++)
        {
            var round = h.Store.Begin(item, h.Clock.UtcNow.AddMinutes(index));
            h.Store.Finish(round, new("failed", 1, null, null, null, [], "fault", FailureKind: kind), h.Clock.UtcNow);
        }
        Assert.Equal(2, h.Store.StartedOnUtcDay(h.Clock.UtcNow));
        Assert.Equal(selected ? 0 : 2, h.Store.ReadAll().Count(BoardFillFailureKind.CountsTowardItem));
        Assert.Equal(selected, Select(item, h.Store) is not null);
        var edited = item with { UpdatedAt = h.Clock.UtcNow.AddMinutes(3) };
        Assert.Same(edited, Select(edited, h.Store));
        var noted = item with { Notes = [new(h.Clock.UtcNow.AddMinutes(3), "item changed")] };
        Assert.Same(noted, Select(noted, h.Store));
        Assert.All(h.Store.ReadAll(), round => Assert.Equal(kind, round.FailureKind));
    }

    private static BacklogItem? Select(BacklogItem item, ConductorBoardFillDraftStore store) =>
        BoardFillReadyItemSelector.Select([item], [], store.AlreadyDrafted([item]), Ready, store.ReadAll());

    internal static BacklogReadiness Ready(BacklogItem item) =>
        BacklogDependencyReadiness.Evaluate(item, _ => null, _ => null, _ => new(null), _ => "Running");
}
