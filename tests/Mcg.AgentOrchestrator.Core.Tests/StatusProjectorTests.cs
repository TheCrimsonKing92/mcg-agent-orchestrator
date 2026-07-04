using Mcg.AgentOrchestrator.Core;

public sealed class StatusProjectorTests
{
    [Xunit.Fact(DisplayName = "StatusProjector_buckets_active_almost_done_landed_and_renders_escalation_count")]
    public void StatusProjectorBucketsActiveAlmostDoneLandedAndRendersEscalationCount()
    {
        var now = new DateTimeOffset(2026, 06, 18, 04, 30, 00, TimeSpan.Zero);
        var active = Goal("active-123456", "Build active work", GoalStatus.Active, now.AddMinutes(-4),
            Task(AgentRole.Planner, WorkTaskStatus.Completed),
            Task(AgentRole.Developer, WorkTaskStatus.Running),
            Task(AgentRole.Tester, WorkTaskStatus.Assigned));
        var almostDone = Goal("almost-123456", "Nearly ready", GoalStatus.Active, now.AddMinutes(-3),
            Task(AgentRole.Planner, WorkTaskStatus.Completed),
            Task(AgentRole.Researcher, WorkTaskStatus.Completed),
            Task(AgentRole.Developer, WorkTaskStatus.Completed),
            Task(AgentRole.Tester, WorkTaskStatus.Completed),
            Task(AgentRole.Reviewer, WorkTaskStatus.Assigned));
        var waiting = Goal("waiting-123456", "Needs operator answer", GoalStatus.WaitingForHuman, now.AddMinutes(-2),
            Task(AgentRole.Planner, WorkTaskStatus.Completed),
            Task(AgentRole.Developer, WorkTaskStatus.WaitingForHuman));
        var landed = GoalWithLifecycle("landed-123456", "Recently landed", GoalStatus.Completed, now.AddMinutes(-1), GoalLifecycleState.CleanedUp,
            Task(AgentRole.Developer, WorkTaskStatus.Completed));

        var projection = StatusProjector.Project(new StatusProjectionInput(
            [active, almostDone, waiting, landed],
            OpenEscalationCount: 2,
            EscalationThreadUrl: "https://discord.example/escalations",
            Now: now));

        Assert.Equal(1, projection.Buckets.Active.Count);
        Assert.Equal("active-123456", projection.Buckets.Active[0].Id);
        Assert.Equal(2, projection.Buckets.AlmostDone.Count);
        Assert.Contains(projection.Buckets.AlmostDone, item => item.Id == "almost-123456");
        Assert.Contains(projection.Buckets.AlmostDone, item => item.Id == "waiting-123456");
        Assert.Equal(1, projection.Buckets.Landed.Count);
        Assert.Equal("landed-123456", projection.Buckets.Landed[0].Id);
        Assert.Equal(2, projection.OpenEscalationCount);
        Assert.Contains(projection.RenderedContent, text => text.Contains("Open escalations: 2", StringComparison.Ordinal));
        Assert.Contains(projection.RenderedContent, text => text.Contains("https://discord.example/escalations", StringComparison.Ordinal));
        Assert.False(projection.Unchanged);
    }

    [Xunit.Fact(DisplayName = "StatusProjector_keeps_completed_without_cleanup_evidence_out_of_landed_bucket")]
    public void StatusProjectorKeepsCompletedWithoutCleanupEvidenceOutOfLandedBucket()
    {
        var now = new DateTimeOffset(2026, 06, 18, 04, 30, 00, TimeSpan.Zero);
        var unintegrated = Goal("raw-completed", "Completed before cleanup facts", GoalStatus.Completed, now.AddMinutes(-1),
            Task(AgentRole.Developer, WorkTaskStatus.Completed));

        var projection = StatusProjector.Project(new StatusProjectionInput([unintegrated], 0, null, now));

        Assert.Empty(projection.Buckets.Landed);
        var item = Assert.Single(projection.Buckets.AlmostDone);
        Assert.Equal("raw-completed", item.Id);
        Assert.Equal("accepting", item.Stage);
        Assert.Equal("integration or cleanup required", item.Health);
        Assert.Contains(projection.RenderedContent, text => text.Contains("integration or cleanup required", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "StatusProjector_reports_unchanged_for_byte_identical_render")]
    public void StatusProjectorReportsUnchangedForByteIdenticalRender()
    {
        var now = new DateTimeOffset(2026, 06, 18, 04, 30, 00, TimeSpan.Zero);
        var goal = Goal("active-abcdef", "Build active work", GoalStatus.Active, now,
            Task(AgentRole.Developer, WorkTaskStatus.Running));
        var input = new StatusProjectionInput([goal], 0, null, now);
        var first = StatusProjector.Project(input);

        var second = StatusProjector.Project(input with { PreviousRenderedContent = first.RenderedContent });

        Assert.True(second.Unchanged);
        Assert.Equal(first.RenderedContent, second.RenderedContent);
    }

    private static StatusProjectionGoal Goal(
        string id,
        string objective,
        GoalStatus status,
        DateTimeOffset lastEventAt,
        params StatusProjectionTask[] tasks) =>
        new(id, objective, status, tasks, lastEventAt);

    private static StatusProjectionGoal GoalWithLifecycle(
        string id,
        string objective,
        GoalStatus status,
        DateTimeOffset lastEventAt,
        GoalLifecycleState lifecycleState,
        params StatusProjectionTask[] tasks) =>
        new(id, objective, status, tasks, lastEventAt, lifecycleState);

    private static StatusProjectionTask Task(AgentRole role, WorkTaskStatus status) =>
        new(role, status);
}
