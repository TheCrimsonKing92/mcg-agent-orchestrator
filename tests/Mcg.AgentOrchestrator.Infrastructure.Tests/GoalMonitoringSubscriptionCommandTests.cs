using System.Text;
using System.Text.Json;
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
            "--once",
            "--format",
            "ndjson",
            "--goal-prefix",
            "abc",
            "--task",
            "task1",
            "--event-kind",
            "TaskStarted,TaskCompleted",
            "--from-cursor",
            "4",
            "--wait-terminal",
            "--timeout",
            "5s"
        ]);

        Assert.True(options.IsLocal);
        Assert.Null(options.DashboardUri);
        Assert.Equal("abc123", options.GoalId);
        Assert.True(options.Once);
        Assert.True(options.WaitTerminal);
        Assert.Equal(GoalMonitoringOutputFormat.Ndjson, options.Format);
        Assert.Equal("abc", options.GoalPrefix);
        Assert.Equal("task1", options.TaskId);
        Assert.Equal(4, options.SinceEventId);
        Assert.Equal(4, options.ResumeCursor.TimelineCursor);
        Assert.Equal(0, options.ResumeCursor.RunEventCursor);
        Assert.Equal(["TaskStarted", "TaskCompleted"], options.EventKinds);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Timeout);
    }

    [Xunit.Fact(DisplayName = "Goals_subscribe_parses_goal_prefix_as_headless_target")]
    public void GoalsSubscribeParsesGoalPrefixAsHeadlessTarget()
    {
        var options = GoalMonitoringSubscriptionCommand.Parse([
            "goals",
            "subscribe",
            "--goal-prefix",
            "abc123",
            "--format",
            "human",
            "--wait-terminal"
        ]);

        Assert.True(options.IsLocal);
        Assert.Equal("abc123", options.GoalId);
        Assert.Equal("abc123", options.GoalPrefix);
        Assert.Equal(GoalMonitoringOutputFormat.Human, options.Format);
        Assert.True(options.WaitTerminal);
    }

    [Xunit.Fact(DisplayName = "Goals_subscribe_defaults_to_ndjson_for_headless_consumers")]
    public void GoalsSubscribeDefaultsToNdjsonForHeadlessConsumers()
    {
        var subscribe = GoalMonitoringSubscriptionCommand.Parse([
            "goals",
            "subscribe",
            "--goal-prefix",
            "abc123"
        ]);
        var legacy = GoalMonitoringSubscriptionCommand.Parse([
            "monitor-goal",
            "abc123"
        ]);

        Assert.Equal(GoalMonitoringOutputFormat.Ndjson, subscribe.Format);
        Assert.Equal(GoalMonitoringOutputFormat.Sse, legacy.Format);
    }

    [Xunit.Fact(DisplayName = "Goals_subscribe_runs_outside_state_transaction")]
    public void GoalsSubscribeRunsOutsideStateTransaction()
    {
        Assert.True(CliPersistentStateRunner.IsGoalSubscriptionCommand(["goals", "subscribe", "--goal-prefix", "abc123"]));
        Assert.False(CliPersistentStateRunner.IsMetadataOnlyListing(["goals", "subscribe", "--goal-prefix", "abc123"]));
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_filter_predicate_applies_AND_semantics")]
    public void MonitorGoalFilterPredicateAppliesAndSemantics()
    {
        var evt = new GoalStateSubscriptionEvent(
            1,
            12,
            DateTimeOffset.UnixEpoch,
            "TaskCompleted",
            "abc12345",
            "task1",
            "Completed",
            "run-events.db",
            42,
            "done");

        Assert.True(GoalMonitoringSubscriptionCommand.Matches(
            evt,
            new GoalMonitoringSubscriptionOptions(null, "abc12345", 0, false, GoalPrefix: "abc", TaskId: "task1", EventKinds: ["TaskCompleted"])));
        Assert.False(GoalMonitoringSubscriptionCommand.Matches(
            evt,
            new GoalMonitoringSubscriptionOptions(null, "abc12345", 0, false, GoalPrefix: "abc", TaskId: "other", EventKinds: ["TaskCompleted"])));
        Assert.False(GoalMonitoringSubscriptionCommand.Matches(
            evt,
            new GoalMonitoringSubscriptionOptions(null, "abc12345", 0, false, GoalPrefix: "abc", TaskId: "task1", EventKinds: ["TaskStarted"])));
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_event_envelope_schema_is_versioned")]
    public void MonitorGoalEventEnvelopeSchemaIsVersioned()
    {
        Assert.Equal(
            [
                "schemaVersion",
                "cursor",
                "timestamp",
                "eventKind",
                "goalId",
                "taskId",
                "currentState",
                "artifactPath",
                "processId",
                "message"
            ],
            GoalMonitoringSubscriptionCommand.GoalStateEventSchemaFields());
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_event_envelope_serializes_versioned_ndjson_contract")]
    public void MonitorGoalEventEnvelopeSerializesVersionedNdjsonContract()
    {
        var evt = new GoalStateSubscriptionEvent(
            1,
            12,
            DateTimeOffset.UnixEpoch,
            "TaskCompleted",
            "abc12345",
            "task1",
            "Completed",
            "run-events.db",
            42,
            "done")
        {
            CursorDomain = GoalStateCursorDomain.RunEvent,
            CursorToken = "run-event:12"
        };

        var json = JsonSerializer.Serialize(evt);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("schemaVersion", out _));
        Assert.True(root.TryGetProperty("cursor", out _));
        Assert.True(root.TryGetProperty("eventKind", out _));
        Assert.True(root.TryGetProperty("goalId", out _));
        Assert.True(root.TryGetProperty("taskId", out _));
        Assert.True(root.TryGetProperty("currentState", out _));
        Assert.True(root.TryGetProperty("artifactPath", out _));
        Assert.True(root.TryGetProperty("processId", out _));
        Assert.True(root.TryGetProperty("message", out _));
        Assert.False(root.TryGetProperty("CursorDomain", out _));
        Assert.False(root.TryGetProperty("CursorSequence", out _));
        Assert.False(root.TryGetProperty("CursorToken", out _));
    }

    [Xunit.Theory(DisplayName = "Monitor_goal_wait_terminal_treats_operator_action_states_as_terminal")]
    [Xunit.InlineData(GoalLifecycleState.Verified, true)]
    [Xunit.InlineData(GoalLifecycleState.Merged, true)]
    [Xunit.InlineData(GoalLifecycleState.Recorded, true)]
    [Xunit.InlineData(GoalLifecycleState.CleanedUp, true)]
    [Xunit.InlineData(GoalLifecycleState.Failed, true)]
    [Xunit.InlineData(GoalLifecycleState.Blocked, true)]
    [Xunit.InlineData(GoalLifecycleState.AwaitingClarification, true)]
    [Xunit.InlineData(GoalLifecycleState.AwaitingHumanInput, true)]
    [Xunit.InlineData(GoalLifecycleState.Running, false)]
    [Xunit.InlineData(GoalLifecycleState.Dispatched, false)]
    public void MonitorGoalWaitTerminalTreatsOperatorActionStatesAsTerminal(
        GoalLifecycleState state,
        bool expected)
    {
        Assert.Equal(expected, GoalMonitoringSubscriptionCommand.IsTerminalForWait(state));
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

    [Xunit.Fact(DisplayName = "Monitor_goal_prints_compact_snapshot_goal_label_when_present")]
    public void MonitorGoalPrintsCompactSnapshotGoalLabelWhenPresent()
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
                    1,
                    [],
                    0,
                    [],
                    observedAt),
                [
                    new TaskMonitoringSnapshotDto(1, "task1", AgentRole.Developer, WorkTaskStatus.Completed, null, null)
                ]),
            [],
            "/api/goals/abc12345/events/stream");
        using var output = new StringWriter();

        GoalMonitoringSubscriptionCommand.PrintBatch(batch, output, "Friendly backlog title");

        var text = output.ToString();
        Xunit.Assert.Contains("snapshot goal=abc12345 (Friendly backlog title) status=Active tasks=1 completed=1 running=0 failed=0", text);
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_production_snapshot_prints_source_backlog_label")]
    public async Task MonitorGoalProductionSnapshotPrintsSourceBacklogLabel()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement labeled snapshot", AgentRole.Developer);
        var goal = kernel.CreateGoal("Monitor labeled goal", [task]);
        var backlogItem = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Production backlog title");
        kernel.SetGoalSourceBacklogItemId(goal.Id, backlogItem.Id);
        kernel.ActivateGoal(goal.Id, []);
        var batch = GoalMonitoringStream.BuildBatch(
            kernel,
            kernel.GetGoal(goal.Id),
            0,
            [],
            WorkerProfileCatalog.Default(),
            workspace);
        using var output = new StringWriter();

        GoalMonitoringSubscriptionCommand.PrintBatch(batch, output);

        var text = output.ToString();
        Xunit.Assert.Contains($"snapshot goal={goal.Id.Value[..8]} (Production backlog title)", text);
        Xunit.Assert.Contains("tasks=1 completed=0 running=0 failed=0", text);
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_prints_compact_snapshot_without_goal_label_when_absent")]
    public void MonitorGoalPrintsCompactSnapshotWithoutGoalLabelWhenAbsent()
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
                    1,
                    [],
                    0,
                    [],
                    observedAt),
                [
                    new TaskMonitoringSnapshotDto(1, "task1", AgentRole.Developer, WorkTaskStatus.Completed, null, null)
                ]),
            [],
            "/api/goals/abc12345/events/stream");
        using var output = new StringWriter();

        GoalMonitoringSubscriptionCommand.PrintBatch(batch, output);

        var text = output.ToString();
        Xunit.Assert.Contains("snapshot goal=abc12345 status=Active tasks=1 completed=1 running=0 failed=0", text);
        Xunit.Assert.DoesNotContain("snapshot goal=abc12345 (", text);
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

    [Xunit.Fact(DisplayName = "Monitor_goal_local_ndjson_resumes_from_cursor")]
    public async Task MonitorGoalLocalNdjsonResumesFromCursor()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement cursor resume", AgentRole.Developer);
        var goal = kernel.CreateGoal("Monitor cursor", [task]);
        var agent = new AgentDefinition(
            AgentId.New(),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Started local work.");
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Finished local work.");
        using var output = new StringWriter();

        await GoalMonitoringSubscriptionCommand.RunAsync(
            ["monitor-goal", goal.Id.Value[..8], "--once", "--format", "ndjson", "--from-cursor", "1"],
            output,
            kernel,
            workspace,
            [agent],
            WorkerProfileCatalog.Default());

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        Xunit.Assert.All(lines, line =>
        {
            using var doc = JsonDocument.Parse(line);
            Assert.StartsWith("timeline:", doc.RootElement.GetProperty("cursor").GetString());
            Assert.True(doc.RootElement.TryGetProperty("timestamp", out _));
            Assert.True(doc.RootElement.TryGetProperty("eventKind", out _));
            Assert.Equal(goal.Id.Value, doc.RootElement.GetProperty("goalId").GetString());
            Assert.True(doc.RootElement.TryGetProperty("currentState", out _));
            Assert.True(doc.RootElement.TryGetProperty("artifactPath", out _) || doc.RootElement.TryGetProperty("processId", out _));
            Assert.False(doc.RootElement.TryGetProperty("CursorDomain", out _));
        });
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_local_ndjson_resumes_persisted_run_events_from_cursor")]
    public async Task MonitorGoalLocalNdjsonResumesPersistedRunEventsFromCursor()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var store = new SqliteRunEventStore(workspace.RunEventStorePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Persist event", AgentRole.Developer);
        var goal = kernel.CreateGoal("Monitor durable cursor", [task]);
        kernel.ActivateGoal(goal.Id, []);
        var first = await store.AppendAsync(new RunEventAppend(
            RunEventTypes.GoalOperation,
            goal.Id.Value,
            "conductor:dispatch",
            "Begin",
            "ignored",
            null));
        await store.AppendAsync(new RunEventAppend(
            RunEventTypes.GoalOperation,
            goal.Id.Value,
            "conductor:dispatch",
            "Completed",
            "process=123",
            null));
        using var output = new StringWriter();

        await GoalMonitoringSubscriptionCommand.RunAsync(
            ["goals", "subscribe", "--goal-prefix", goal.Id.Value[..8], "--once", "--from-cursor", $"run-event:{first.Sequence}", "--event-kind", "conductor:dispatch"],
            output,
            kernel,
            workspace,
            [],
            WorkerProfileCatalog.Default());

        var line = Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal($"timeline:1;run-event:{first.Sequence + 1}", doc.RootElement.GetProperty("cursor").GetString());
        Assert.Equal("conductor:dispatch", doc.RootElement.GetProperty("eventKind").GetString());
        Assert.Equal("Created", doc.RootElement.GetProperty("currentState").GetString());
        Assert.Equal(workspace.RunEventStorePath, doc.RootElement.GetProperty("artifactPath").GetString());
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_local_ndjson_timeline_cursor_does_not_skip_lower_run_events")]
    public async Task MonitorGoalLocalNdjsonTimelineCursorDoesNotSkipLowerRunEvents()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var store = new SqliteRunEventStore(workspace.RunEventStorePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Persist event", AgentRole.Developer);
        var goal = kernel.CreateGoal("Monitor cross-domain cursor", [task]);
        kernel.ActivateGoal(goal.Id, []);
        await store.AppendAsync(new RunEventAppend(
            RunEventTypes.GoalOperation,
            goal.Id.Value,
            "conductor:dispatch",
            "Completed",
            "process=123",
            null));
        using var output = new StringWriter();

        await GoalMonitoringSubscriptionCommand.RunAsync(
            ["goals", "subscribe", "--goal-prefix", goal.Id.Value[..8], "--once", "--from-cursor", "timeline:50", "--event-kind", "conductor:dispatch"],
            output,
            kernel,
            workspace,
            [],
            WorkerProfileCatalog.Default());

        var line = Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("timeline:50;run-event:1", doc.RootElement.GetProperty("cursor").GetString());
        Assert.Equal("conductor:dispatch", doc.RootElement.GetProperty("eventKind").GetString());
        Assert.Equal("Created", doc.RootElement.GetProperty("currentState").GetString());
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_local_once_with_stale_composite_cursor_emits_current_snapshot")]
    public async Task MonitorGoalLocalOnceWithStaleCompositeCursorEmitsCurrentSnapshot()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Emit current state", AgentRole.Developer);
        var goal = kernel.CreateGoal("Monitor current state fallback", [task]);
        kernel.ActivateGoal(goal.Id, []);
        using var output = new StringWriter();

        await GoalMonitoringSubscriptionCommand.RunAsync(
            [
                "goals",
                "subscribe",
                "--goal-prefix",
                goal.Id.Value[..8],
                "--once",
                "--from-cursor",
                "timeline:999;run-event:999",
                "--event-kind",
                "goal.snapshot"
            ],
            output,
            kernel,
            workspace,
            [],
            WorkerProfileCatalog.Default());

        var line = Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("timeline:999;run-event:999", doc.RootElement.GetProperty("cursor").GetString());
        Assert.Equal("goal.snapshot", doc.RootElement.GetProperty("eventKind").GetString());
        Assert.Equal(goal.Id.Value, doc.RootElement.GetProperty("goalId").GetString());
        Assert.Equal("Created", doc.RootElement.GetProperty("currentState").GetString());
    }

    [Xunit.Theory(DisplayName = "Monitor_goal_local_current_state_uses_lifecycle_facts_for_completed_goal")]
    [Xunit.InlineData("conductor:land", GoalLifecycleState.Merged)]
    [Xunit.InlineData("conductor:record", GoalLifecycleState.Recorded)]
    [Xunit.InlineData("conductor:cleanup", GoalLifecycleState.CleanedUp)]
    public async Task MonitorGoalLocalCurrentStateUsesLifecycleFactsForCompletedGoal(
        string completedOperation,
        GoalLifecycleState expectedState)
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Complete lifecycle", AgentRole.Developer);
        var goal = kernel.CreateGoal("Monitor lifecycle facts", [task]);
        kernel.ActivateGoal(goal.Id, []);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "manual",
            root,
            0,
            "ok",
            string.Empty,
            DateTimeOffset.UtcNow));
        if (expectedState != GoalLifecycleState.CleanedUp)
        {
            CreateLinkedWorktreeMarker(root, goal.Id);
        }

        GoalOperationJournal.Completed(root, goal, "conductor:land", "landed");
        if (completedOperation is "conductor:record" or "conductor:cleanup")
        {
            GoalOperationJournal.Completed(root, goal, "conductor:record", "recorded");
        }

        if (completedOperation == "conductor:cleanup")
        {
            GoalOperationJournal.Completed(root, goal, "conductor:cleanup", "cleaned");
        }

        using var output = new StringWriter();

        await GoalMonitoringSubscriptionCommand.RunAsync(
            ["monitor-goal", goal.Id.Value[..8], "--once", "--format", "ndjson", "--event-kind", "goal.snapshot"],
            output,
            kernel,
            workspace,
            [],
            WorkerProfileCatalog.Default());

        var line = Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal(expectedState.ToString(), doc.RootElement.GetProperty("currentState").GetString());
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_lifecycle_facts_treat_manual_acceptance_and_workspace_remove_as_cleaned")]
    public async Task MonitorGoalLifecycleFactsTreatManualAcceptanceAndWorkspaceRemoveAsCleaned()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Complete manually", AgentRole.Developer);
        var goal = kernel.CreateGoal("Monitor manual acceptance lifecycle", [task]);
        kernel.ActivateGoal(goal.Id, []);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "manual",
            root,
            0,
            "ok",
            string.Empty,
            DateTimeOffset.UtcNow));
        GoalOperationJournal.Completed(root, goal, "acceptance", "Acceptance passed and merge completed.");
        GoalOperationJournal.Completed(root, goal, "workspace:remove", "Workspace removed.");
        kernel.CompleteGoal(goal.Id, "Manual acceptance completed after cleanup evidence.");
        using var output = new StringWriter();

        await GoalMonitoringSubscriptionCommand.RunAsync(
            ["monitor-goal", goal.Id.Value[..8], "--once", "--format", "ndjson", "--event-kind", "goal.snapshot"],
            output,
            kernel,
            workspace,
            [],
            WorkerProfileCatalog.Default());

        var line = Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal(GoalLifecycleState.CleanedUp.ToString(), doc.RootElement.GetProperty("currentState").GetString());
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_local_current_state_reports_open_clarification")]
    public async Task MonitorGoalLocalCurrentStateReportsOpenClarification()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Clarify", AgentRole.Planner);
        var goal = kernel.CreateGoal("Monitor clarification", [task]);
        var agent = new AgentDefinition(
            AgentId.New(),
            "Planner",
            AgentRole.Planner,
            new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory).RaiseAsync(
            CollaborationItemType.Clarification,
            goal.Id.Value,
            "Need answer",
            "Question?",
            correlationKey: $"spec-clarification:{goal.Id.Value}:observable-behavior:test");
        var facts = GoalMonitoringSubscriptionCommand.ReadLifecycleFacts(workspace, kernel.GetGoal(goal.Id));
        Assert.Equal(GoalLifecycleState.AwaitingClarification, GoalLifecycle.ResolveState(kernel.GetGoal(goal.Id), facts));
        using var output = new StringWriter();

        await GoalMonitoringSubscriptionCommand.RunAsync(
            ["monitor-goal", goal.Id.Value[..8], "--once", "--format", "ndjson", "--event-kind", "goal.snapshot"],
            output,
            kernel,
            workspace,
            [agent],
            WorkerProfileCatalog.Default());

        var line = Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("AwaitingClarification", doc.RootElement.GetProperty("currentState").GetString());
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_run_event_envelopes_use_lifecycle_state_not_run_event_status")]
    public void MonitorGoalRunEventEnvelopesUseLifecycleStateNotRunEventStatus()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Blocked", AgentRole.Developer);
        var goal = kernel.CreateGoal("Monitor blocked", [task]);
        kernel.ActivateGoal(goal.Id, []);
        var batch = BuildEmptyMonitoringBatch(goal);
        var records = new[]
        {
            new RunEventRecord(
                3,
                "evt-3",
                DateTimeOffset.UnixEpoch,
                RunEventTypes.GoalOperation,
                goal.Id.Value,
                "conductor:cleanup",
                "Completed",
                "cleanup finished",
                null)
        };

        var events = GoalMonitoringSubscriptionCommand.BuildSubscriptionEvents(
            batch,
            goal,
            GoalLifecycleState.Blocked,
            workspace,
            records);

        var runEvent = Assert.Single(events.Where(evt => evt.CursorDomain == GoalStateCursorDomain.RunEvent));
        Assert.Equal("conductor:cleanup", runEvent.EventKind);
        Assert.Equal("Blocked", runEvent.CurrentState);
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_local_process_envelopes_include_dispatch_heartbeat_and_exit")]
    public void MonitorGoalLocalProcessEnvelopesIncludeDispatchHeartbeatAndExit()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Run with process", AgentRole.Developer);
        var goal = kernel.CreateGoal("Monitor process facts", [task]);
        var agent = new AgentDefinition(
            AgentId.New(),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        var startedAt = DateTimeOffset.Parse("2026-06-12T20:00:00Z");
        var completedAt = startedAt.AddMinutes(1);
        var stdout = Path.Combine(root, "worker.out.log");
        var stderr = Path.Combine(root, "worker.err.log");
        var exit = Path.Combine(root, "worker.exit.txt");
        File.WriteAllText(stdout, "ok");
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "echo done", root, startedAt));
        var process = new TaskProcessRecord(123, "echo done", root, stdout, stderr, exit, startedAt, completedAt, 0);
        File.WriteAllText(BackgroundDispatchRunner.GetHeartbeatPath(process), """
        {"pid":123,"childPid":456,"ownedPids":[123,456],"state":"running","lastObservedAt":"2026-06-12T20:00:30Z","lastProgressAt":"2026-06-12T20:00:20Z","stdoutBytes":2,"stderrBytes":0,"ownedCpuMs":10}
        """);
        kernel.RecordTaskProcessRefreshed(goal.Id, task.Id, process, verification: null);
        var batch = GoalMonitoringStream.BuildBatch(
            kernel,
            kernel.GetGoal(goal.Id),
            sinceEventId: 0,
            agents: [],
            workerProfiles: WorkerProfileCatalog.Default(),
            workspace);

        var events = GoalMonitoringSubscriptionCommand.BuildSubscriptionEvents(
            batch,
            kernel.GetGoal(goal.Id),
            GoalLifecycleState.Running,
            workspace,
            []);

        Assert.Contains(events, evt =>
            evt.EventKind == "dispatch.heartbeat" &&
            evt.TaskId == task.Id.Value &&
            evt.ProcessId == 123 &&
            evt.Message?.Contains("stdout=2", StringComparison.Ordinal) == true);
        Assert.Contains(events, evt =>
            evt.EventKind == "dispatch.exit" &&
            evt.TaskId == task.Id.Value &&
            evt.ProcessId == 123 &&
            evt.Message?.Contains("exit=0", StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_local_resumed_stream_includes_process_heartbeat_and_exit")]
    public async Task MonitorGoalLocalResumedStreamIncludesProcessHeartbeatAndExit()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Resume process stream", AgentRole.Developer);
        var goal = kernel.CreateGoal("Monitor resumed process facts", [task]);
        var agent = new AgentDefinition(
            AgentId.New(),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        var startedAt = DateTimeOffset.Parse("2026-06-12T20:00:00Z");
        var completedAt = startedAt.AddMinutes(1);
        var stdout = Path.Combine(root, "worker.out.log");
        var stderr = Path.Combine(root, "worker.err.log");
        var exit = Path.Combine(root, "worker.exit.txt");
        File.WriteAllText(stdout, "ok");
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "echo done", root, startedAt));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Timeline event already consumed.");
        var process = new TaskProcessRecord(123, "echo done", root, stdout, stderr, exit, startedAt, completedAt, 0);
        File.WriteAllText(BackgroundDispatchRunner.GetHeartbeatPath(process), """
        {"pid":123,"childPid":456,"ownedPids":[123,456],"state":"running","lastObservedAt":"2026-06-12T20:00:30Z","lastProgressAt":"2026-06-12T20:00:20Z","stdoutBytes":2,"stderrBytes":0,"ownedCpuMs":10}
        """);
        kernel.RecordTaskProcessRefreshed(goal.Id, task.Id, process, verification: null);
        using var output = new StringWriter();

        await GoalMonitoringSubscriptionCommand.RunAsync(
            [
                "goals",
                "subscribe",
                "--goal-prefix",
                goal.Id.Value[..8],
                "--once",
                "--format",
                "ndjson",
                "--from-cursor",
                "timeline:999",
                "--event-kind",
                "dispatch.heartbeat,dispatch.exit"
            ],
            output,
            kernel,
            workspace,
            [agent],
            WorkerProfileCatalog.Default());

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        var events = lines.Select(line =>
        {
            using var doc = JsonDocument.Parse(line);
            return (
                Kind: doc.RootElement.GetProperty("eventKind").GetString(),
                Cursor: doc.RootElement.GetProperty("cursor").GetString());
        }).ToArray();
        Assert.Contains(events, evt =>
            evt.Kind == "dispatch.heartbeat" &&
            evt.Cursor?.Contains("timeline:999", StringComparison.Ordinal) == true &&
            evt.Cursor.Contains("process:", StringComparison.Ordinal));
        Assert.Contains(events, evt =>
            evt.Kind == "dispatch.exit" &&
            evt.Cursor?.Contains("timeline:999", StringComparison.Ordinal) == true &&
            evt.Cursor.Contains("process:", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_prefix_subscription_excludes_global_conductor_ticks")]
    public void MonitorGoalPrefixSubscriptionExcludesGlobalConductorTicks()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Filtered", AgentRole.Developer);
        var goal = kernel.CreateGoal("Monitor filtered ticks", [task]);
        kernel.ActivateGoal(goal.Id, []);
        var batch = BuildEmptyMonitoringBatch(goal);
        var records = new[]
        {
            new RunEventRecord(
                3,
                "evt-3",
                DateTimeOffset.UnixEpoch,
                RunEventTypes.GoalOperation,
                null,
                "conduct:tick",
                "Completed",
                "tick summary",
                null),
            new RunEventRecord(
                4,
                "evt-4",
                DateTimeOffset.UnixEpoch.AddSeconds(1),
                RunEventTypes.GoalOperation,
                goal.Id.Value,
                "conductor:dispatch",
                "Completed",
                "goal dispatch",
                null)
        };

        var events = GoalMonitoringSubscriptionCommand.BuildSubscriptionEvents(
            batch,
            goal,
            GoalLifecycleState.Running,
            workspace,
            records);
        var options = new GoalMonitoringSubscriptionOptions(
            null,
            goal.Id.Value,
            0,
            false,
            GoalPrefix: goal.Id.Value[..8]);

        var globalTick = Assert.Single(events.Where(evt => evt.EventKind == "conduct:tick"));
        Assert.Null(globalTick.GoalId);
        Assert.False(GoalMonitoringSubscriptionCommand.Matches(globalTick, options));
        Assert.Contains(events, evt =>
            evt.EventKind == "conductor:dispatch" &&
            GoalMonitoringSubscriptionCommand.Matches(evt, options));
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_local_ndjson_tracks_timeline_and_run_event_cursors_independently")]
    public async Task MonitorGoalLocalNdjsonTracksTimelineAndRunEventCursorsIndependently()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var store = new SqliteRunEventStore(workspace.RunEventStorePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Catch late run event", AgentRole.Developer);
        var goal = kernel.CreateGoal("Monitor independent cursors", [task]);
        var agent = new AgentDefinition(
            AgentId.New(),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Timeline cursor moves first.");
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Timeline cursor moves again.");
        var reloadCount = 0;
        using var output = new StringWriter();

        await GoalMonitoringSubscriptionCommand.RunAsync(
            ["monitor-goal", goal.Id.Value[..8], "--wait-terminal", "--format", "ndjson"],
            output,
            kernel,
            workspace,
            [agent],
            WorkerProfileCatalog.Default(),
            reloadKernel: () =>
            {
                reloadCount++;
                if (reloadCount == 2)
                {
                    store.AppendAsync(new RunEventAppend(
                        RunEventTypes.GoalOperation,
                        goal.Id.Value,
                        "conductor:dispatch",
                        "Completed",
                        "process=123",
                        null)).GetAwaiter().GetResult();
                    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Finished.");
                    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                        "manual",
                        root,
                        0,
                        "ok",
                        string.Empty,
                        DateTimeOffset.UtcNow));
                }

                return kernel;
            });

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line =>
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.GetProperty("eventKind").GetString() == "conductor:dispatch" &&
                doc.RootElement.GetProperty("cursor").GetString()?.EndsWith("run-event:1", StringComparison.Ordinal) == true;
        });
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_local_ndjson_without_once_stays_attached_until_cancelled")]
    public async Task MonitorGoalLocalNdjsonWithoutOnceStaysAttachedUntilCancelled()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Keep streaming", AgentRole.Developer);
        var goal = kernel.CreateGoal("Monitor continuous", [task]);
        kernel.ActivateGoal(goal.Id, []);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Started local work.");
        using var output = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Xunit.Assert.ThrowsAnyAsync<OperationCanceledException>(() => GoalMonitoringSubscriptionCommand.RunAsync(
            ["monitor-goal", goal.Id.Value[..8], "--format", "ndjson"],
            output,
            kernel,
            workspace,
            [],
            WorkerProfileCatalog.Default(),
            cancellationToken: cts.Token));

        Assert.Contains("\"eventKind\":\"goal.snapshot\"", output.ToString());
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_wait_terminal_exits_zero_for_completed_and_nonzero_for_failed")]
    public async Task MonitorGoalWaitTerminalExitBehavior()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var completedKernel = new AgentOrchestratorKernel();
        var completedTask = new TaskSpec(TaskId.New(), "Complete", AgentRole.Developer);
        var completedGoal = completedKernel.CreateGoal("Wait completed", [completedTask]);
        completedKernel.ActivateGoal(completedGoal.Id, []);
        completedKernel.ReportTaskProgress(completedGoal.Id, completedTask.Id, WorkTaskStatus.Completed, "Done.");
        completedKernel.RecordTaskVerification(completedGoal.Id, completedTask.Id, new TaskVerificationRecord(
            "manual",
            root,
            0,
            "ok",
            string.Empty,
            DateTimeOffset.UtcNow));
        CreateLinkedWorktreeMarker(root, completedGoal.Id);
        using var completedOutput = new StringWriter();

        await GoalMonitoringSubscriptionCommand.RunAsync(
            ["monitor-goal", completedGoal.Id.Value[..8], "--wait-terminal", "--format", "human"],
            completedOutput,
            completedKernel,
            workspace,
            [],
            WorkerProfileCatalog.Default());

        Assert.Contains("-> Verified", completedOutput.ToString());

        var failedKernel = new AgentOrchestratorKernel();
        var failedTask = new TaskSpec(TaskId.New(), "Fail", AgentRole.Developer);
        var failedGoal = failedKernel.CreateGoal("Wait failed", [failedTask]);
        failedKernel.ActivateGoal(failedGoal.Id, []);
        failedKernel.ReportTaskProgress(failedGoal.Id, failedTask.Id, WorkTaskStatus.Failed, "Failed.");
        using var failedOutput = new StringWriter();

        var ex = await Xunit.Assert.ThrowsAsync<CliExitException>(() => GoalMonitoringSubscriptionCommand.RunAsync(
            ["monitor-goal", failedGoal.Id.Value[..8], "--wait-terminal", "--format", "human"],
            failedOutput,
            failedKernel,
            workspace,
            [],
            WorkerProfileCatalog.Default()));
        Assert.Equal(1, ex.ExitCode);
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_wait_terminal_exits_nonzero_for_abandoned_goal")]
    public async Task MonitorGoalWaitTerminalExitsNonzeroForAbandonedGoal()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Stop", AgentRole.Developer);
        var goal = kernel.CreateGoal("Wait abandoned", [task]);
        kernel.ActivateGoal(goal.Id, []);
        kernel.CancelGoal(goal.Id, "Abandoned by operator.");
        using var output = new StringWriter();

        var ex = await Xunit.Assert.ThrowsAsync<CliExitException>(() => GoalMonitoringSubscriptionCommand.RunAsync(
            ["monitor-goal", goal.Id.Value[..8], "--wait-terminal", "--format", "human"],
            output,
            kernel,
            workspace,
            [],
            WorkerProfileCatalog.Default()));

        Assert.Equal(1, ex.ExitCode);
        Assert.Contains("-> Failed", output.ToString());
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_wait_terminal_exits_nonzero_for_awaiting_human_input")]
    public async Task MonitorGoalWaitTerminalExitsNonzeroForAwaitingHumanInput()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Ask", AgentRole.Developer);
        var goal = kernel.CreateGoal("Wait input", [task]);
        kernel.ActivateGoal(goal.Id, []);
        kernel.RequestHumanInput(goal.Id, task.Id, "Which command should run?");
        using var output = new StringWriter();

        var ex = await Xunit.Assert.ThrowsAsync<CliExitException>(() => GoalMonitoringSubscriptionCommand.RunAsync(
            ["monitor-goal", goal.Id.Value[..8], "--wait-terminal", "--format", "human"],
            output,
            kernel,
            workspace,
            [],
            WorkerProfileCatalog.Default()));

        Assert.Equal(1, ex.ExitCode);
        Assert.Contains("-> AwaitingHumanInput", output.ToString());
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_wait_terminal_times_out_with_compact_event_and_nonzero_exit")]
    public async Task MonitorGoalWaitTerminalTimesOutWithCompactEventAndNonzeroExit()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Keep running", AgentRole.Developer);
        var goal = kernel.CreateGoal("Wait timeout", [task]);
        kernel.ActivateGoal(goal.Id, []);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Still active.");
        using var output = new StringWriter();

        var ex = await Xunit.Assert.ThrowsAsync<CliExitException>(() => GoalMonitoringSubscriptionCommand.RunAsync(
            ["goals", "subscribe", "--goal-prefix", goal.Id.Value[..8], "--wait-terminal", "--format", "ndjson", "--timeout", "10ms"],
            output,
            kernel,
            workspace,
            [],
            WorkerProfileCatalog.Default()));

        Assert.Equal(124, ex.ExitCode);
        Assert.Contains("\"eventKind\":\"monitor.timeout\"", output.ToString());
        Assert.Contains("\"currentState\":\"Dispatched\"", output.ToString());
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_lifecycle_facts_surface_acceptance_failure_as_blocked")]
    public void MonitorGoalLifecycleFactsSurfaceAcceptanceFailureAsBlocked()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Done", AgentRole.Developer);
        var goal = kernel.CreateGoal("Blocked acceptance", [task]);
        kernel.ActivateGoal(goal.Id, []);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "manual",
            root,
            0,
            "ok",
            string.Empty,
            DateTimeOffset.UtcNow));
        kernel.RecordAcceptanceFailure(goal.Id, ["acceptance"]);

        var facts = GoalMonitoringSubscriptionCommand.ReadLifecycleFacts(workspace, kernel.GetGoal(goal.Id));

        Assert.True(facts.IsBlocked);
        Assert.Equal(GoalLifecycleState.Blocked, GoalLifecycle.ResolveState(kernel.GetGoal(goal.Id), facts));
    }

    [Xunit.Fact(DisplayName = "Monitor_goal_wait_terminal_does_not_emit_snapshot_that_violates_filters")]
    public async Task MonitorGoalWaitTerminalDoesNotEmitSnapshotThatViolatesFilters()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Complete quietly", AgentRole.Developer);
        var goal = kernel.CreateGoal("Wait filtered terminal", [task]);
        kernel.ActivateGoal(goal.Id, []);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "manual",
            root,
            0,
            "ok",
            string.Empty,
            DateTimeOffset.UtcNow));
        using var output = new StringWriter();

        await GoalMonitoringSubscriptionCommand.RunAsync(
            ["monitor-goal", goal.Id.Value[..8], "--wait-terminal", "--format", "ndjson", "--event-kind", "conductor:dispatch"],
            output,
            kernel,
            workspace,
            [],
            WorkerProfileCatalog.Default());

        Assert.Equal(string.Empty, output.ToString());
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

    private static void CreateLinkedWorktreeMarker(string root, GoalId goalId)
    {
        var path = GoalWorktrees.WorktreePath(root, goalId);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, ".git"), "gitdir: test");
    }

    private static GoalMonitoringBatchDto BuildEmptyMonitoringBatch(Goal goal)
    {
        var observedAt = DateTimeOffset.UnixEpoch;
        return new GoalMonitoringBatchDto(
            goal.Id.Value,
            0,
            1,
            new GoalMonitoringSnapshotDto(
                goal.Id.Value,
                observedAt,
                1,
                new MonitorDto(
                    goal.Id.Value,
                    goal.Objective,
                    goal.Status,
                    goal.Tasks.Count,
                    [],
                    0,
                    [],
                    observedAt),
                []),
            [],
            "/events/stream");
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
