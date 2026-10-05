using Mcg.AgentOrchestrator.Core;

public sealed class FailureClusterRootEventDedupeTests
{
    [Fact]
    public void RealRequeueTicksCountOnceAndRealProcessStartResetsTheNoteRoot()
    {
        var rows = FailureClusterFixtureData.Read().Build(FailureClusterFixtureData.Since, FailureClusterFixtureData.Until);
        var requeue = Assert.Single(rows, r => r.MessageFamily.StartsWith("Auto-requeued interrupted dispatch"));
        Assert.Equal(1, requeue.RootEvents);
        Assert.Equal(1, requeue.PaidRounds);

        // The operator-authorized recurrence fixture is a successful pre-check note, so count roots
        // through the report's dedupe seam without turning successful notes into failure clusters.
        var events = FailureClusterFixtureData.ReadGoals("goal-events-dedupe-precheck.jsonl");
        Assert.Single(FailureClusterReport.RootEvents(events.Take(5)));
        var roots = FailureClusterReport.RootEvents(events).ToArray();
        Assert.Equal(2, roots.Length);
        Assert.Equal(events[0], roots[0]);
        Assert.Equal(events[^1], roots[1]);
        Assert.Empty(FailureClusterReport.Build(events, [], [], FailureClusterFixtureData.Since, FailureClusterFixtureData.Until));
    }

    [Fact]
    public void FiveTicksCountOnceAndRedispatchStartsAnotherRoot()
    {
        var at = FailureClusterTestData.Since;
        var events = new List<FailureClusterGoalEvent> { new("TaskDispatched", "start", "goal", "task", at) };
        events.AddRange(Enumerable.Range(1, 5).Select(i => new FailureClusterGoalEvent("TaskRetried",
            "Auto-requeued interrupted dispatch after conductor loop stop", "goal", "task", at.AddMinutes(i))));
        var first = Assert.Single(FailureClusterReport.Build(events, [], [], at, at.AddDays(1)));
        Assert.Equal(1, first.PaidRounds);
        Assert.Equal(1, first.RootEvents);
        events.Add(new("TaskDispatched", "second start", "goal", "task", at.AddMinutes(6)));
        events.Add(events[1] with { Timestamp = at.AddMinutes(7) });
        var second = Assert.Single(FailureClusterReport.Build(events, [], [], at, at.AddDays(1)));
        Assert.Equal(2, second.PaidRounds);
        Assert.Equal(2, second.RootEvents);
        Assert.Equal(at.AddMinutes(1), second.FirstSeen);
        Assert.Equal(at.AddMinutes(7), second.LastSeen);
    }

    [Fact]
    public void OtherTaskDispatchDoesNotResetTaskRootButResetsGoalLevelHold()
    {
        var at = FailureClusterTestData.Since;
        var rows = FailureClusterReport.Build([
            new("TaskRetried", "same failure", "goal", "task", at),
            new("GoalEscalated", "same hold", "goal", null, at),
            new("TaskDispatched", "other start", "goal", "other", at.AddMinutes(1)),
            new("TaskRetried", "same failure", "goal", "task", at.AddMinutes(2)),
            new("GoalEscalated", "same hold", "goal", null, at.AddMinutes(2))
        ], [], [], at, at.AddDays(1));
        Assert.Equal(1, Assert.Single(rows, r => r.EventKind == "TaskRetried").RootEvents);
        Assert.Equal(2, Assert.Single(rows, r => r.EventKind == "GoalEscalated").RootEvents);
    }

    [Fact]
    public void OlderRootIsNotRecountedWhenRepeatFallsInsideWindow()
    {
        var at = FailureClusterTestData.Since;
        Assert.Empty(FailureClusterReport.Build([
            new("TaskRetried", "same failure", "goal", "task", at.AddMinutes(-1)),
            new("TaskRetried", "same failure", "goal", "task", at.AddMinutes(1))
        ], [], [], at, at.AddDays(1)));
    }
}
