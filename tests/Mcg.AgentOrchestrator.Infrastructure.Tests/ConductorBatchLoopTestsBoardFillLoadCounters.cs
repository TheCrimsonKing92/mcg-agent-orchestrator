using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsBoardFillLoadCounters(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Xunit.Fact(DisplayName = "Shadow BoardFill counts all backlog rows and only post-filter readiness evaluations")]
    public void ShadowCountsRowsAndReadiness()
    {
        using var harness = new BoardFillTestHarness();
        harness.Held = true; // Signal-based release in fixture disposal, never pacing.
        harness.Policy = harness.Policy with
        {
            BoardFillMode = ConductorBoardFillMode.Shadow,
            BoardFillTargetActiveGoals = 10,
            BoardFillMaxDraftsPerDay = 5
        };
        var items = Enumerable.Range(0, 12).Select(index => BoardFillReadyItemSelectorTests.Item((char)('a' + index))).ToArray();
        items[0] = items[0] with { Status = BacklogItemStatus.Done };
        items[1] = items[1] with { Status = BacklogItemStatus.Superseded };
        items[2] = items[2] with { Tags = "owner-gated" };
        items[3] = items[3] with { Tags = "OWNER-GATED" };
        var previous = harness.Store.Begin(items[4], harness.Clock.UtcNow);
        harness.Store.Finish(previous, harness.Draft(), harness.Clock.UtcNow);
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(harness.Kernel, DefaultAgents(), "Claimed backlog goal");
        harness.Kernel.SetGoalSourceBacklogItemLink(goal.Id, items[5].Id, SourceBacklogCoverage.Full);
        harness.Items = items;
        var alreadyDrafted = harness.Store.AlreadyDrafted(items);
        var expected = items.Count(item => item.Status == BacklogItemStatus.Open &&
            !BoardFillReadyItemSelector.IsOwnerGated(item) && !alreadyDrafted.Contains(item.Id) &&
            !harness.Kernel.Goals.Any(candidate => !candidate.IsTerminal && candidate.SourceBacklogItemId == item.Id));
        Assert.Equal(6, expected);

        var loop = new ConductorBatchLoop(processCpuTime: () => TimeSpan.Zero).WithBoardFill(harness.Host);
        BatchTickSummary? captured = null;
        try
        {
            loop.Run(harness.Kernel, MakeDriver(), ConductorAutonomyPolicy.Conservative, NoStopPath(),
                maxIterations: 1, onTick: tick => { captured = tick; harness.Release(); });
        }
        finally { harness.Release(); }

        var sweep = ConductorBatchLoopTestsStepLedger.Phase(Assert.IsType<BatchTickSummary>(captured), "sweep");
        Assert.Contains("load_counters=goals_hydrated=0,metadata_rows=0,terminal_journal_stats=0,backlog_rows_read=12,readiness_evaluations=6", sweep);
        Assert.Contains($"readiness_evaluations={expected}", sweep);
        Assert.Equal(1, harness.Calls);
        Assert.Equal(ConductorBoardFillMode.Shadow, harness.Policy.BoardFillMode);
    }
}
