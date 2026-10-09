using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BoardFillPlanOrderSelectionTests
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    [Fact]
    public void Plan_order_beats_priority_and_preserves_slice_positions()
    {
        var outside = BoardFillReadyItemSelectorTests.Item('a') with
        { Priority = "high", UpdatedAt = BoardFillReadyItemSelectorTests.Now.AddDays(-1) };
        var first = BoardFillReadyItemSelectorTests.Item('c');
        var second = BoardFillReadyItemSelectorTests.Item('b') with { Priority = "high" };
        Assert.Same(outside, BoardFillReadyItemSelector.Select([outside, first], [], None, Ready));
        Assert.Same(first, BoardFillReadyItemSelector.Select([outside, second, first], [], None, Ready,
            preferredOrder: [first.Id, second.Id]));
        Assert.Same(second, BoardFillReadyItemSelector.Select([outside, second], [], None, Ready,
            preferredOrder: [first.Id, second.Id]));
    }

    [Theory]
    [InlineData("owner-gated")]
    [InlineData("drafted")]
    [InlineData("failure-cap")]
    [InlineData("live-goal")]
    [InlineData("not-open")]
    [InlineData("not-ready")]
    public void Ineligible_head_is_skipped_and_no_qualifying_slice_uses_fallback(string filter)
    {
        var first = BoardFillReadyItemSelectorTests.Item('a');
        var second = BoardFillReadyItemSelectorTests.Item('b');
        var outside = BoardFillReadyItemSelectorTests.Item('c') with { Priority = "high" };
        var drafted = new HashSet<string>();
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<BoardFillDraftRound> rounds = [];
        switch (filter)
        {
            case "owner-gated": first = first with { Tags = "owner-gated" }; break;
            case "drafted": drafted.Add(first.Id); break;
            case "failure-cap":
                rounds = Enumerable.Range(0, BoardFillReadyItemSelector.MaxFailedRoundsPerChange)
                    .Select(index => new BoardFillDraftRound(index.ToString(), first.Id, first.UpdatedAt,
                        null, null, "failed", null, null, [], first.UpdatedAt, first.UpdatedAt)).ToArray();
                break;
            case "live-goal":
                var goal = kernel.CreateGoal("Live slice");
                kernel.SetGoalSourceBacklogItemLink(goal.Id, first.Id, SourceBacklogCoverage.Full);
                break;
            case "not-open": first = first with { Status = BacklogItemStatus.Done }; break;
            case "not-ready":
                first = first with { Dependencies = [new(first.Id, "missing", BacklogDependencyTargetKind.Backlog, first.UpdatedAt)] };
                break;
        }
        var goals = kernel.Goals.ToArray();
        var preferred = new[] { first.Id, second.Id };
        Assert.Same(second, BoardFillReadyItemSelector.Select([outside, first, second], goals, drafted, Ready, rounds, preferred));
        var expected = BoardFillReadyItemSelector.Select([outside, first], goals, drafted, Ready, rounds);
        Assert.Same(outside, expected);
        Assert.Same(expected, BoardFillReadyItemSelector.Select([outside, first], goals, drafted, Ready, rounds, preferred));
    }

    [Fact]
    public void Null_empty_and_unmatched_preferences_preserve_existing_order()
    {
        var a = BoardFillReadyItemSelectorTests.Item('a');
        var b = BoardFillReadyItemSelectorTests.Item('b') with { Priority = "high" };
        var c = BoardFillReadyItemSelectorTests.Item('c') with { UpdatedAt = a.UpdatedAt.AddDays(1) };
        var baseline = new[] { b, c, a };
        foreach (var preference in new IReadOnlyList<string>?[] { null, [], ["absent"] })
        {
            var remaining = new List<BacklogItem> { a, b, c };
            foreach (var expected in baseline)
            {
                Assert.Same(expected, BoardFillReadyItemSelector.Select(remaining, [], None, Ready, preferredOrder: preference));
                remaining.Remove(expected);
            }
        }
    }

    [Fact]
    public void Duplicate_preference_keeps_the_first_occurrence()
    {
        var a = BoardFillReadyItemSelectorTests.Item('a');
        var b = BoardFillReadyItemSelectorTests.Item('b');
        Assert.Same(b, BoardFillReadyItemSelector.Select([a, b], [], None, Ready, preferredOrder: [b.Id, a.Id, b.Id]));
    }

    private static BacklogReadiness Ready(BacklogItem item) =>
        BacklogDependencyReadiness.Evaluate(item, _ => null, _ => null, _ => new(null), _ => "Running");
}
