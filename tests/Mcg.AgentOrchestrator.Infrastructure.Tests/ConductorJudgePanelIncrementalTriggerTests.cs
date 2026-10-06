using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class ConductorJudgePanelIncrementalTriggerTests
{
    [Fact]
    public async Task IdleTickReadsNoLinesOrGoals()
    {
        using var h = new PanelIncrementalTestFixture();
        await h.Fixture.SaveCriteria();
        h.Escalate(1);
        h.Tick();
        var first = Assert.Single(h.Fixture.Panel.Store.Cases());
        Assert.Equal(h.FullReadKey(first.Key.TriggerId), first.Key);
        Assert.Equal(1, h.Reads.EventLines);
        Assert.Equal(1, h.Reads.ParsedLines);
        Assert.Equal(1, h.Reads.GoalReads);
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(0, h.Reads.EventLines);
        Assert.Equal(0, h.Reads.ParsedLines);
        Assert.Equal(0, h.Reads.GoalReads);
        Assert.Equal(0, h.Reads.StoreScans);
        Assert.Equal(first, Assert.Single(h.Fixture.Panel.Store.Cases()));
    }

    [Fact]
    public async Task AppendsMatchFullReadAndTornTailIsRetried()
    {
        using var h = new PanelIncrementalTestFixture();
        await h.Fixture.SaveCriteria();
        h.Escalate(1);
        h.Tick();
        h.Reads.Reset();
        h.Escalate(2, "PRE_TESTER_RED_LOOP");
        h.Tick();
        var cases = h.Fixture.Panel.Store.Cases();
        Assert.Equal(2, cases.Count);
        var added = Assert.Single(cases, item => item.Key.TriggerId.EndsWith(":2", StringComparison.Ordinal));
        Assert.Equal(h.FullReadKey(added.Key.TriggerId), added.Key);
        Assert.Equal(ConductorJudgePanelCaseStore.CaseId(h.FullReadKey(added.Key.TriggerId)), added.Id);
        Assert.Equal(1, h.Reads.EventLines);
        Assert.Equal(1, h.Reads.ParsedLines);
        Assert.Equal(1, h.Reads.GoalReads);

        h.Reads.Reset();
        h.Fixture.Timeline(3, "ordinary progress", eventType: "GoalCreated");
        File.AppendAllText(h.TimelinePath, h.Line(4, "verification-inconclusive-unchanged-inputs"));
        h.Tick();
        Assert.Equal(2, h.Fixture.Panel.Store.Cases().Count);
        Assert.Equal(2, h.Reads.EventLines); // ordinary line plus the unconsumed torn record
        Assert.Equal(1, h.Reads.ParsedLines);
        Assert.Equal(0, h.Reads.GoalReads);
        h.Reads.Reset();
        File.AppendAllText(h.TimelinePath, "\n");
        h.Tick();
        Assert.Equal(1, h.Reads.EventLines);
        Assert.Equal(1, h.Reads.ParsedLines);
        Assert.Equal(3, h.Fixture.Panel.Store.Cases().Count);
        var completed = Assert.Single(h.Fixture.Panel.Store.Cases(), item => item.Key.TriggerId.EndsWith(":4", StringComparison.Ordinal));
        Assert.Equal(h.FullReadKey(completed.Key.TriggerId), completed.Key);
    }

    [Fact]
    public async Task FilteredTriggersSurviveCursorAdvanceAndRelaunch()
    {
        using var h = new PanelIncrementalTestFixture();
        await h.Fixture.SaveCriteria();
        h.Escalate(1);
        h.Tick("another-goal");
        Assert.Empty(h.Fixture.Panel.Store.Cases());
        h.Relaunch();
        h.Reads.Reset();
        h.Tick(h.Fixture.GoalId);
        Assert.Single(h.Fixture.Panel.Store.Cases());
        Assert.Equal(0, h.Reads.EventLines);
        Assert.Equal(1, h.Reads.GoalReads);
        Assert.Equal(h.FullReadKey($"goal-event:{h.Fixture.GoalId}:1"), Assert.Single(h.Fixture.Panel.Store.Cases()).Key);
    }

    [Fact]
    public async Task OwnerDigestReadStillReturnsEveryTrigger()
    {
        using var h = new PanelIncrementalTestFixture();
        await h.Fixture.SaveCriteria();
        h.Escalate(1);
        h.Tick();
        Assert.Equal(h.Fixture.Sources.Read().Select(item => item.TriggerId), h.Fixture.Sources.Read().Select(item => item.TriggerId));
        Assert.Single(h.Fixture.Sources.Read());
    }
}
