using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

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

    [Xunit.Fact(DisplayName = "Monitor_goal_parses_local_goal_subscription_without_dashboard_url")]
    public void MonitorGoalParsesLocalGoalSubscriptionWithoutDashboardUrl()
    {
        var options = GoalMonitoringSubscriptionCommand.Parse([
            "monitor-goal",
            "abc123",
            "--once"
        ]);

        Assert.True(options.IsLocal);
        Assert.Null(options.DashboardUri);
        Assert.Equal("abc123", options.GoalId);
        Assert.True(options.Once);
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
                ],
                ProviderCapacity: new ProviderCapacityScheduleDto(
                    ProviderCapacityDisposition.Deferred,
                    "Wait for retry-after or route to another provider.",
                    0,
                    1,
                    observedAt.AddMinutes(30),
                    false,
                    [
                        new ProviderCapacityActionDto(
                            2,
                            "task2",
                            "OpenAI",
                            ProviderCapacityDisposition.Deferred,
                            observedAt.AddMinutes(30),
                            "Retry later.",
                            ["Route to a different provider profile."])
                    ])),
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
        Xunit.Assert.Contains("snapshot goal=abc12345 status=Active tasks=2 completed=1 running=1 failed=0 lastEvent=7 attention=0 inbox=0 capacity=Deferred ready=0 deferred=1", text);
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

    [Xunit.Fact(DisplayName = "Monitor_goal_local_once_emits_snapshot_before_incremental_events")]
    public async Task MonitorGoalLocalOnceEmitsSnapshotBeforeIncrementalEvents()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement local monitor", AgentRole.Developer);
        var goal = kernel.CreateGoal("Monitor locally", [task]);
        var agent = new AgentDefinition(
            AgentId.New(),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Started local work.");
        using var output = new StringWriter();

        await GoalMonitoringSubscriptionCommand.RunAsync(
            ["monitor-goal", goal.Id.Value[..8], "--once"],
            output,
            kernel,
            workspace,
            [agent],
            WorkerProfileCatalog.Default());

        var text = output.ToString();
        var snapshotIndex = text.IndexOf("event: goal.snapshot", StringComparison.Ordinal);
        var timelineIndex = text.IndexOf("event: timeline", StringComparison.Ordinal);
        Assert.True(snapshotIndex >= 0);
        Assert.True(timelineIndex > snapshotIndex);
        Assert.Contains("event: task.status", text);
        Assert.Contains("Started local work.", text);
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_local_unknown_goal_emits_structured_error_event")]
    public async Task MonitorGoalLocalUnknownGoalEmitsStructuredErrorEvent()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        using var output = new StringWriter();

        await GoalMonitoringSubscriptionCommand.RunAsync(
            ["monitor-goal", "missing", "--once"],
            output,
            new AgentOrchestratorKernel(),
            workspace,
            [],
            WorkerProfileCatalog.Default());

        var text = output.ToString();
        Assert.Contains("event: monitor.error", text);
        Assert.Contains("\"GoalId\": \"missing\"", text);
        Assert.Contains("\"Code\": \"goal_not_found\"", text);
    }

    [Xunit.Fact(DisplayName = "Goal_monitoring_stream_continuous_emits_initial_snapshot_before_poll_interval")]
    public async Task GoalMonitoringStreamContinuousEmitsInitialSnapshotBeforePollInterval()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Monitor stream promptly", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        await using var stream = new RecordingStream();
        using var cts = new CancellationTokenSource();
        var pollInterval = TimeSpan.FromSeconds(30);

        var streamTask = GoalMonitoringStream.StreamAsync(
            stream,
            goal.Id.Value[..8],
            _ => Task.FromResult(kernel),
            (current, resolvedGoal, since) => DashboardMonitoringEvents.BuildBatch(current, resolvedGoal, since),
            EmptyRunEventStore.Instance,
            sinceEventId: 0,
            once: false,
            pollInterval,
            cts.Token);

        var completed = await Task.WhenAny(stream.FirstWrite, Task.Delay(TimeSpan.FromSeconds(1)));

        Xunit.Assert.Same(stream.FirstWrite, completed);
        Assert.Contains("event: goal.snapshot", stream.Text);
        cts.Cancel();
        await Xunit.Assert.ThrowsAnyAsync<OperationCanceledException>(() => streamTask);
    }

    [Xunit.Fact(DisplayName = "Goal_monitoring_stream_emits_monitor_error_when_goal_disappears_during_repoll")]
    public async Task GoalMonitoringStreamEmitsMonitorErrorWhenGoalDisappearsDuringRepoll()
    {
        var initialKernel = new AgentOrchestratorKernel();
        var goal = initialKernel.CreateGoal("Monitor disappearing goal", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var missingKernel = new AgentOrchestratorKernel();
        var loadCount = 0;
        await using var stream = new RecordingStream();

        await GoalMonitoringStream.StreamAsync(
            stream,
            goal.Id.Value[..8],
            _ =>
            {
                loadCount++;
                return Task.FromResult(loadCount == 1 ? initialKernel : missingKernel);
            },
            (current, resolvedGoal, since) => DashboardMonitoringEvents.BuildBatch(current, resolvedGoal, since),
            EmptyRunEventStore.Instance,
            sinceEventId: 0,
            once: false,
            pollInterval: TimeSpan.Zero,
            CancellationToken.None);

        var text = stream.Text;
        Assert.Contains("event: goal.snapshot", text);
        Assert.Contains("event: monitor.error", text);
        Assert.Contains("\"GoalId\": \"" + goal.Id.Value[..8] + "\"", text);
        Assert.Contains("\"Code\": \"goal_not_found\"", text);
        Assert.Equal(1, CountOccurrences(text, "event: monitor.error"));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-monitor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private sealed class RecordingStream : MemoryStream
    {
        private readonly TaskCompletionSource _firstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstWrite => _firstWrite.Task;

        public string Text => Encoding.UTF8.GetString(ToArray());

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            _firstWrite.TrySetResult();
        }
    }

    private sealed class EmptyRunEventStore : IRunEventStore
    {
        public static readonly EmptyRunEventStore Instance = new();

        public Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(
            long afterSequence = 0,
            string? goalId = null,
            int maxCount = 500,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
        }
    }
}
