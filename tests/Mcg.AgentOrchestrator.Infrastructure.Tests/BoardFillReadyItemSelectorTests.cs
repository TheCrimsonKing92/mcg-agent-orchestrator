using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BoardFillReadyItemSelectorTests
{
    internal static readonly DateTimeOffset Now = new(2030, 1, 2, 0, 0, 0, TimeSpan.Zero);
    internal static BacklogItem Item(char id = 'a') => new(new string(id, 32), "Ready", "Build the receipt",
        BacklogItemStatus.Open, Now, Now, null);
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    [Theory]
    [InlineData(BacklogItemStatus.Open, true)]
    [InlineData(BacklogItemStatus.Closed, false)]
    [InlineData(BacklogItemStatus.Superseded, false)]
    public void Only_open_items_are_eligible(BacklogItemStatus status, bool eligible)
    {
        var item = Item() with { Status = status };
        Assert.Equal(eligible, Select([item]) is not null);
    }

    [Fact]
    public void Linked_nonterminal_goal_blocks_but_terminal_goal_does_not()
    {
        var item = Item();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Already linked");
        kernel.SetGoalSourceBacklogItemLink(goal.Id, item.Id, SourceBacklogCoverage.Full);
        Assert.Null(Select([item], kernel.Goals.ToArray()));
        kernel.CancelGoal(goal.Id, "Terminal fixture");
        Assert.Same(item, Select([item], kernel.Goals.ToArray()));
    }

    [Theory]
    [InlineData(BacklogDependencyTargetKind.Backlog)]
    [InlineData(BacklogDependencyTargetKind.Goal)]
    public void Unsatisfied_dependencies_of_both_kinds_block(BacklogDependencyTargetKind kind)
    {
        var prerequisite = Item('b');
        var goal = new AgentOrchestratorKernel().CreateGoal("Prerequisite");
        var item = Item() with { Dependencies = [new(Item().Id,
            kind == BacklogDependencyTargetKind.Backlog ? prerequisite.Id : goal.Id.Value, kind, Now)] };
        var readiness = BacklogDependencyReadiness.Evaluate(item, _ => goal, _ => prerequisite,
            _ => new(goal), _ => "Running");
        Assert.Null(BoardFillReadyItemSelector.Select([item], [goal], None, _ => readiness));
        Assert.Same(item, BoardFillReadyItemSelector.Select([item], [goal], None, _ =>
            BacklogDependencyReadiness.Evaluate(item, _ => goal, _ => prerequisite, _ => new(goal), _ => "Merged")));
    }

    [Fact]
    public void Owner_gated_is_a_case_insensitive_tag_token()
    {
        Assert.Null(Select([Item() with { Tags = "ready, OWNER-GATED\tother" }]));
        Assert.NotNull(Select([Item() with { Tags = "not-owner-gated" }]));
    }

    [Theory]
    [InlineData("OwNeR DeCiSiOn")]
    [InlineData("OwNeR ApPrOvAl")]
    [InlineData("OwNeR RuLiNg")]
    [InlineData("ApPrOvE-PoLiCy-ChAnGe")]
    [InlineData("DeCiSiOn FoR YoU")]
    public void Each_owner_marker_blocks_body_and_notes(string marker)
    {
        Assert.Null(Select([Item() with { Body = "Needs " + marker + " here" }]));
        Assert.Null(Select([Item() with { Notes = [new(Now, "Needs " + marker + " here")] }]));
        Assert.NotNull(Select([Item()]));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("stale")]
    public void Unchanged_verdict_suppresses_until_body_or_note_changes(string kind)
    {
        // Unique SQLite file, independent of every other test and the live stores.
        var root = Path.Combine(Path.GetTempPath(), "board-fill-selector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var item = Item() with { Notes = [new(Now.AddMinutes(1), "Original note")] };
            var store = new ConductorBoardFillDraftStore(Path.Combine(root, "drafts.db"));
            var round = store.Begin(item, Now);
            store.Finish(round, new(kind, kind == "stale" ? 2 : 0, new string('c', 40), null, null, []), Now.AddMinutes(2));
            Assert.Null(BoardFillReadyItemSelector.Select([item], [], store.AlreadyDrafted([item]), Ready));
            var edited = item with { UpdatedAt = Now.AddMinutes(3) };
            Assert.Same(edited, BoardFillReadyItemSelector.Select([edited], [], store.AlreadyDrafted([edited]), Ready));
            var noted = item with { Notes = [.. item.Notes, new(Now.AddMinutes(3), "New note")] };
            Assert.Same(noted, BoardFillReadyItemSelector.Select([noted], [], store.AlreadyDrafted([noted]), Ready));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Ordering_applies_priority_landed_dependency_recency_then_ordinal_id()
    {
        var goal = new AgentOrchestratorKernel().CreateGoal("Landed dependency");
        var landed = Item('c') with { Priority = "normal", Dependencies = [new(Item('c').Id, goal.Id.Value,
            BacklogDependencyTargetKind.Goal, Now)] };
        var tie = landed with { Id = new string('b', 32) };
        var older = landed with { Id = new string('a', 32), UpdatedAt = Now.AddDays(-1) };
        var noDependency = Item('d') with { Priority = "high", UpdatedAt = Now.AddDays(1) };
        var noPriority = landed with { Id = new string('e', 32), Priority = null, UpdatedAt = Now.AddDays(2) };
        var blankPriority = noPriority with { Id = new string('f', 32), Priority = " " };
        var remaining = new List<BacklogItem> { blankPriority, noPriority, noDependency, older, landed, tie };
        foreach (var expected in new[] { tie, landed, older, noDependency, noPriority, blankPriority })
        {
            var selected = BoardFillReadyItemSelector.Select(remaining, [goal], None,
                item => BacklogDependencyReadiness.Evaluate(item, _ => goal, _ => null, _ => new(null), _ => "Recorded"));
            Assert.Same(expected, selected);
            remaining.Remove(expected);
        }
        Assert.Null(Select([]));
    }

    private static BacklogReadiness Ready(BacklogItem item) =>
        BacklogDependencyReadiness.Evaluate(item, _ => null, _ => null, _ => new(null), _ => "Running");
    private static BacklogItem? Select(IReadOnlyList<BacklogItem> items, IReadOnlyList<Goal>? goals = null) =>
        BoardFillReadyItemSelector.Select(items, goals ?? [], None, Ready);
}
