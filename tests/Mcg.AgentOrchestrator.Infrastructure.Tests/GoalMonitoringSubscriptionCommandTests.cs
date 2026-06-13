using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Core;

public sealed class GoalMonitoringSubscriptionCommandTests
{
    [Xunit.Fact(DisplayName = "Monitor_goal_parses_arguments_and_builds_subscription_urls")]
    public void MonitorGoalParsesArgumentsAndBuildsSubscriptionUrls()
    {
        var options = GoalMonitoringSubscriptionCommand.Parse([
            "monitor-goal",
            "http://localhost:5087",
            "abc123",
            "--since",
            "42",
            "--once"
        ]);

        Assert.True(options.Once);
        Assert.Equal(42, options.SinceEventId);
        Assert.Equal("http://localhost:5087/api/goals/abc123/events?since=42", GoalMonitoringSubscriptionCommand.BuildSnapshotUri(options).ToString());
        Assert.Equal("http://localhost:5087/api/goals/abc123/events/stream?since=42", GoalMonitoringSubscriptionCommand.BuildStreamUri(options).ToString());
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_prints_compact_snapshot_and_timeline_lines")]
    public void MonitorGoalPrintsCompactSnapshotAndTimelineLines()
    {
        var observedAt = new DateTimeOffset(2026, 6, 13, 1, 0, 0, TimeSpan.Zero);
        var batch = new GoalMonitoringBatchDto(
            "abc12345",
            0,
            7,
            new GoalMonitoringSnapshotDto(
                "abc12345",
                observedAt,
                7,
                new MonitorDto(
                    "abc12345",
                    "Add monitoring",
                    GoalStatus.Active,
                    2,
                    [],
                    0,
                    [],
                    observedAt),
                [
                    new TaskMonitoringSnapshotDto(1, "task1", AgentRole.Developer, WorkTaskStatus.Completed, null, null),
                    new TaskMonitoringSnapshotDto(2, "task2", AgentRole.Tester, WorkTaskStatus.Running, null, null)
                ]),
            [
                new GoalMonitoringEventDto(
                    7,
                    "timeline",
                    "abc12345",
                    "task1",
                    1,
                    AgentRole.Developer,
                    WorkTaskStatus.Completed,
                    ProgressKind.TaskCompleted,
                    "Done",
                    false,
                    4,
                    observedAt)
            ],
            "/api/goals/abc12345/events/stream");
        using var output = new StringWriter();

        GoalMonitoringSubscriptionCommand.PrintBatch(batch, output);

        var text = output.ToString();
        Xunit.Assert.Contains("snapshot goal=abc12345 status=Active tasks=2 completed=1 running=1 failed=0 lastEvent=7 attention=0", text);
        Xunit.Assert.Contains("event 7 2026-06-13 01:00:00Z TaskCompleted task 1: Done", text);
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_reads_server_sent_events")]
    public async Task MonitorGoalReadsServerSentEvents()
    {
        var payload = new GoalMonitoringEventDto(
            3,
            "timeline",
            "abc12345",
            null,
            null,
            null,
            null,
            ProgressKind.GoalCreated,
            "Created",
            false,
            7,
            DateTimeOffset.UnixEpoch);
        await using var stream = new MemoryStream();
        await DashboardMonitoringEvents.WriteServerSentEventAsync(stream, "timeline", payload, "3", CancellationToken.None);
        stream.Position = 0;
        var events = new List<ServerSentEvent>();

        await foreach (var serverEvent in GoalMonitoringSubscriptionCommand.ReadServerSentEventsAsync(stream))
        {
            events.Add(serverEvent);
        }

        var evt = Xunit.Assert.Single(events);
        Assert.Equal("3", evt.Id);
        Assert.Equal("timeline", evt.Event);
        Xunit.Assert.Contains("Created", evt.Data);
    }
}
