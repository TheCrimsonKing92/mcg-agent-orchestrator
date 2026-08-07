using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

public sealed class InterruptedDispatchRequeueTests
{
    [Xunit.Theory]
    [Xunit.InlineData(WorkTaskStatus.Cancelled)]
    [Xunit.InlineData(WorkTaskStatus.Completed)]
    [Xunit.InlineData(WorkTaskStatus.WaitingForHuman)]
    [Xunit.InlineData(WorkTaskStatus.Failed)]
    public void Recovery_NonDispatchableCurrentTask_SkipsOnce(WorkTaskStatus terminalStatus)
    {
        var (kernel, goal, task, runner) = InterruptedDispatch();
        InterruptedDispatchStateRead Read(GoalId _, TaskId __) => new(GoalStatus.Active, terminalStatus);

        Assert.Equal(0, runner.RequeueInterruptedDispatches(kernel, Read));
        Assert.Equal(0, runner.RequeueInterruptedDispatches(kernel, Read));

        var skipped = Assert.Single(goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRequeueSkipped));
        Assert.Equal(task.Id.Value, skipped.RequeueSkipped!.TaskId);
        Assert.Equal(goal.Id.Value, skipped.RequeueSkipped.GoalId);
        Assert.NotEmpty(skipped.RequeueSkipped.DispatchId);
        Assert.Equal("task", skipped.RequeueSkipped.BlockingEntity);
        Assert.Equal(terminalStatus.ToString(), skipped.RequeueSkipped.TerminalState);
        Assert.Equal("terminal-state", skipped.RequeueSkipped.Reason);
        Assert.Equal(terminalStatus, task.Status);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
    }

    [Xunit.Theory]
    [Xunit.InlineData(GoalStatus.Cancelled)]
    [Xunit.InlineData(GoalStatus.Superseded)]
    [Xunit.InlineData(GoalStatus.Completed)]
    public void Recovery_TerminalGoal_SkipsOnce(GoalStatus terminalStatus)
    {
        var (kernel, goal, _, runner) = InterruptedDispatch();
        kernel = WithGoalStatus(kernel, goal.Id, terminalStatus);
        goal = kernel.GetGoal(goal.Id);
        InterruptedDispatchStateRead Read(GoalId _, TaskId __) => new(terminalStatus, WorkTaskStatus.Running);

        Assert.Equal(0, runner.RequeueInterruptedDispatches(kernel, Read));
        Assert.Equal(0, runner.RequeueInterruptedDispatches(kernel, Read));

        var skipped = Assert.Single(goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRequeueSkipped));
        Assert.Equal("goal", skipped.RequeueSkipped!.BlockingEntity);
        Assert.Equal(terminalStatus.ToString(), skipped.RequeueSkipped.TerminalState);
        Assert.Equal(terminalStatus, goal.Status);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
    }

    [Xunit.Fact]
    public void Recovery_UnreadableState_FailsClosed()
    {
        var (kernel, goal, task, runner) = InterruptedDispatch();

        var recovered = runner.RequeueInterruptedDispatches(
            kernel,
            (_, _) => InterruptedDispatchStateRead.Unreadable("goal", "database unavailable"));

        Assert.Equal(0, recovered);
        var skipped = Assert.Single(goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRequeueSkipped));
        Assert.Equal("goal", skipped.RequeueSkipped!.BlockingEntity);
        Assert.Null(skipped.RequeueSkipped.TerminalState);
        Assert.Equal("state-unreadable", skipped.RequeueSkipped.Reason);
        Assert.Equal("database unavailable", skipped.RequeueSkipped.Detail);
        Assert.Equal(WorkTaskStatus.Cancelled, task.Status);
        Assert.True(task.WasCancelledByConductor);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);

        var recoveredAfterStoreReturns = runner.RequeueInterruptedDispatches(
            kernel,
            (_, _) => new InterruptedDispatchStateRead(
                GoalStatus.Active,
                WorkTaskStatus.Cancelled,
                WasTaskCancelledByConductor: true));

        Assert.Equal(1, recoveredAfterStoreReturns);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Single(goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRequeueSkipped));
        Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
    }

    [Xunit.Fact]
    public void Recovery_CurrentActiveState_RequeuesAsBefore()
    {
        var (kernel, goal, task, runner) = InterruptedDispatch();

        var recovered = runner.RequeueInterruptedDispatches(
            kernel,
            (_, _) => new InterruptedDispatchStateRead(GoalStatus.Active, WorkTaskStatus.Running));

        Assert.Equal(1, recovered);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.NotNull(task.InterruptedDispatchRecoveryId);
        Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRequeueSkipped);
    }

    [Xunit.Theory]
    [Xunit.InlineData(GoalStatus.Draft)]
    [Xunit.InlineData(GoalStatus.WaitingForHuman)]
    [Xunit.InlineData(GoalStatus.Parked)]
    [Xunit.InlineData(GoalStatus.Verifying)]
    [Xunit.InlineData(GoalStatus.Verified)]
    [Xunit.InlineData(GoalStatus.AcceptanceFailed)]
    [Xunit.InlineData(GoalStatus.Failed)]
    public void Recovery_NonActiveGoal_DoesNotInspectOrRequeue(GoalStatus status)
    {
        var (kernel, goal, _, runner) = InterruptedDispatch();
        kernel = WithGoalStatus(kernel, goal.Id, status);
        var reads = 0;

        var recovered = runner.RequeueInterruptedDispatches(
            kernel,
            (_, _) =>
            {
                reads++;
                return new InterruptedDispatchStateRead(GoalStatus.Active, WorkTaskStatus.Running);
            });

        Assert.Equal(0, recovered);
        Assert.Equal(0, reads);
        Assert.DoesNotContain(
            kernel.GetGoal(goal.Id).Timeline,
            evt => evt.Kind is ProgressKind.TaskRetried or ProgressKind.TaskRequeueSkipped);
    }

    [Xunit.Fact]
    public void Launch_TerminalStateAfterRequeue_DoesNotSpawn()
    {
        var (kernel, goal, task, recoveryRunner) = InterruptedDispatch();
        recoveryRunner.RequeueInterruptedDispatches(
            kernel,
            (_, _) => new InterruptedDispatchStateRead(GoalStatus.Active, WorkTaskStatus.Running));
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("paid-worker", "claude --print prompt", Path.GetTempPath(), DateTimeOffset.UtcNow));

        var spawnCount = 0;
        var checkpointCount = 0;
        var runner = new BackgroundDispatchRunner(
            disableProcessStart: false,
            startProcess: _ =>
            {
                spawnCount++;
                return null;
            });
        var logRoot = Path.Combine(Path.GetTempPath(), $"mcg-requeue-preflight-{Guid.NewGuid():N}");
        try
        {
            var result = runner.TryStartLatestDispatch(
                kernel,
                goal.Id,
                task.Id,
                logRoot,
                checkpointBeforeWorkerStart: (_, _, _, _) => checkpointCount++,
                readCurrentState: (_, _) =>
                    new InterruptedDispatchStateRead(GoalStatus.Active, WorkTaskStatus.Cancelled));

            Assert.True(result.RequeueSkipped);
            Assert.Equal(0, spawnCount);
            Assert.Equal(0, checkpointCount);
            Assert.Null(task.LastProcess);
            Assert.Equal(WorkTaskStatus.Cancelled, task.Status);
            Assert.Null(task.InterruptedDispatchRecoveryId);
            Assert.Contains(goal.Timeline, evt =>
                evt.Kind == ProgressKind.TaskRequeueSkipped &&
                evt.RequeueSkipped?.TerminalState == nameof(WorkTaskStatus.Cancelled));
            var retry = Assert.Throws<InvalidOperationException>(() =>
                runner.TryStartLatestDispatch(
                    kernel,
                    goal.Id,
                    task.Id,
                    logRoot,
                    checkpointBeforeWorkerStart: (_, _, _, _) => checkpointCount++,
                    readCurrentState: (_, _) =>
                        new InterruptedDispatchStateRead(GoalStatus.Active, WorkTaskStatus.Cancelled)));
            Assert.Contains("status is Cancelled", retry.Message, StringComparison.Ordinal);
            Assert.Single(goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRequeueSkipped));
            Assert.Equal(0, spawnCount);
            Assert.Equal(0, checkpointCount);
        }
        finally
        {
            if (Directory.Exists(logRoot)) Directory.Delete(logRoot, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Launch_UnreadableState_RetriesWithoutCancelling()
    {
        var (kernel, goal, task, recoveryRunner) = InterruptedDispatch();
        recoveryRunner.RequeueInterruptedDispatches(
            kernel,
            (_, _) => new InterruptedDispatchStateRead(GoalStatus.Active, WorkTaskStatus.Running));
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("paid-worker", "claude --print prompt", Path.GetTempPath(), DateTimeOffset.UtcNow));

        var spawnCount = 0;
        var runner = new BackgroundDispatchRunner(
            disableProcessStart: false,
            startProcess: _ =>
            {
                spawnCount++;
                return null;
            });
        var logRoot = Path.Combine(Path.GetTempPath(), $"mcg-unreadable-preflight-{Guid.NewGuid():N}");
        try
        {
            var first = runner.TryStartLatestDispatch(
                kernel,
                goal.Id,
                task.Id,
                logRoot,
                readCurrentState: (_, _) =>
                    InterruptedDispatchStateRead.Unreadable("task", "database unavailable"));

            Assert.True(first.RequeueSkipped);
            Assert.Equal(0, spawnCount);
            Assert.Equal(WorkTaskStatus.Running, task.Status);
            Assert.NotNull(task.InterruptedDispatchRecoveryId);

            var second = Assert.Throws<InvalidOperationException>(() =>
                runner.TryStartLatestDispatch(
                    kernel,
                    goal.Id,
                    task.Id,
                    logRoot,
                    readCurrentState: (_, _) =>
                        new InterruptedDispatchStateRead(GoalStatus.Active, WorkTaskStatus.Running)));

            Assert.Contains("Failed to start background dispatch process", second.Message, StringComparison.Ordinal);
            Assert.Equal(1, spawnCount);
            Assert.Equal(WorkTaskStatus.Running, task.Status);
            Assert.NotNull(task.InterruptedDispatchRecoveryId);
            Assert.Single(goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRequeueSkipped));
        }
        finally
        {
            if (Directory.Exists(logRoot)) Directory.Delete(logRoot, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ManualRetry_CancelledTask_ReachesProcessSpawn()
    {
        var (kernel, goal, task, runner) = InterruptedDispatch();
        runner.RequeueInterruptedDispatches(
            kernel,
            (_, _) => new InterruptedDispatchStateRead(GoalStatus.Active, WorkTaskStatus.Running));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Cancelled, "operator cancelled");

        kernel.RetryTask(goal.Id, task.Id, "operator explicitly retries");
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("paid-worker", "claude --print prompt", Path.GetTempPath(), DateTimeOffset.UtcNow));

        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.Null(task.InterruptedDispatchRecoveryId);

        var spawnCount = 0;
        var stateReadCount = 0;
        var manualRunner = new BackgroundDispatchRunner(
            disableProcessStart: false,
            startProcess: _ =>
            {
                spawnCount++;
                return null;
            });
        var logRoot = Path.Combine(Path.GetTempPath(), $"mcg-manual-retry-{Guid.NewGuid():N}");
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                manualRunner.TryStartLatestDispatch(
                    kernel,
                    goal.Id,
                    task.Id,
                    logRoot,
                    readCurrentState: (_, _) =>
                    {
                        stateReadCount++;
                        return new InterruptedDispatchStateRead(GoalStatus.Active, WorkTaskStatus.Cancelled);
                    }));

            Assert.Contains("Failed to start background dispatch process", error.Message, StringComparison.Ordinal);
            Assert.Equal(1, spawnCount);
            Assert.Equal(0, stateReadCount);
        }
        finally
        {
            if (Directory.Exists(logRoot)) Directory.Delete(logRoot, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Recovery_OperatorCancelAfterConductorReap_SkipsRequeue()
    {
        var (kernel, goal, task, runner) = InterruptedDispatch();
        Assert.True(task.WasCancelledByConductor);

        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Cancelled, "operator cancelled after restart");

        var recovered = runner.RequeueInterruptedDispatches(kernel);

        Assert.Equal(0, recovered);
        Assert.False(task.WasCancelledByConductor);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.TaskRequeueSkipped &&
            evt.RequeueSkipped?.TerminalState == nameof(WorkTaskStatus.Cancelled));
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
    }

    [Xunit.Fact]
    public void EventWriter_RequeueSkip_WritesTypedFields()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-requeue-event-{Guid.NewGuid():N}");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("event surface", [new TaskSpec(TaskId.New(), "task", AgentRole.Developer)]);
            var task = goal.Tasks.Single();
            var writer = new GoalLifecycleEventWriter(root);
            kernel.SetEventWriter(writer);

            Assert.True(kernel.RecordTaskRequeueSkipped(
                goal.Id,
                task.Id,
                "dispatch-123",
                "task",
                nameof(WorkTaskStatus.Cancelled),
                "terminal-state"));

            using var document = JsonDocument.Parse(File.ReadLines(writer.EventFilePath(goal.Id)).Single());
            var evt = document.RootElement;
            Assert.Equal("TaskRequeueSkipped", evt.GetProperty("eventType").GetString());
            Assert.Equal(task.Id.Value, evt.GetProperty("taskId").GetString());
            Assert.Equal("dispatch-123", evt.GetProperty("dispatchId").GetString());
            Assert.Equal("task", evt.GetProperty("blockingEntity").GetString());
            Assert.Equal("Cancelled", evt.GetProperty("terminalState").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, BackgroundDispatchRunner Runner)
        InterruptedDispatch()
    {
        var kernel = new AgentOrchestratorKernel();
        var interrupted = new TaskSpec(TaskId.New(), "interrupted developer", AgentRole.Developer);
        var pending = new TaskSpec(TaskId.New(), "pending tester", AgentRole.Tester);
        var goal = kernel.CreateGoal("interrupted dispatch", [interrupted, pending]);
        kernel.ActivateGoal(goal.Id, Agents());
        var task = goal.Tasks.Single(candidate => candidate.RequiredRole == AgentRole.Developer);
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("paid-worker", "claude --print prompt", "C:\\goal", now));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(
                4242,
                "claude --print prompt",
                "C:\\goal",
                "out.log",
                "err.log",
                "exit.log",
                now,
                null,
                null,
                WasCancelled: false,
                OwnedProcessIds: [4242]));
        kernel.RecordTaskProcessCancelled(
            goal.Id,
            task.Id,
            task.LastProcess with
            {
                CompletedAt = now,
                WasCancelled = true,
                WasCancelledByConductor = true
            });
        return (kernel, goal, task, new BackgroundDispatchRunner(isStillRunning: _ => false));
    }

    private static IReadOnlyList<AgentDefinition> Agents() =>
    [
        new(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("test", "test", ModelCapability.Text, SubscriptionMode.ApiKey)),
        new(
            new AgentId("tester"),
            "Tester",
            AgentRole.Tester,
            new ModelProfile("test", "test", ModelCapability.Text, SubscriptionMode.ApiKey))
    ];

    private static AgentOrchestratorKernel WithGoalStatus(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        GoalStatus status)
    {
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals
                .Select(goal => goal.Id == goalId.Value ? goal with { Status = status } : goal)
                .ToList()
        });
    }
}
