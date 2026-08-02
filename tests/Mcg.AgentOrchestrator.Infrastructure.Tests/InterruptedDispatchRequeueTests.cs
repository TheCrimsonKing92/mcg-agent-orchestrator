using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

public sealed class InterruptedDispatchRequeueTests
{
    [Xunit.Theory]
    [Xunit.InlineData(WorkTaskStatus.Cancelled)]
    [Xunit.InlineData(WorkTaskStatus.Completed)]
    public void Recovery_TerminalTask_SkipsOnce(WorkTaskStatus terminalStatus)
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
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
    }

    [Xunit.Theory]
    [Xunit.InlineData(GoalStatus.Cancelled)]
    [Xunit.InlineData(GoalStatus.Superseded)]
    [Xunit.InlineData(GoalStatus.Completed)]
    public void Recovery_TerminalGoal_SkipsOnce(GoalStatus terminalStatus)
    {
        var (kernel, goal, _, runner) = InterruptedDispatch();
        InterruptedDispatchStateRead Read(GoalId _, TaskId __) => new(terminalStatus, WorkTaskStatus.Running);

        Assert.Equal(0, runner.RequeueInterruptedDispatches(kernel, Read));
        Assert.Equal(0, runner.RequeueInterruptedDispatches(kernel, Read));

        var skipped = Assert.Single(goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRequeueSkipped));
        Assert.Equal("goal", skipped.RequeueSkipped!.BlockingEntity);
        Assert.Equal(terminalStatus.ToString(), skipped.RequeueSkipped.TerminalState);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
    }

    [Xunit.Fact]
    public void Recovery_UnreadableState_FailsClosed()
    {
        var (kernel, goal, _, runner) = InterruptedDispatch();

        var recovered = runner.RequeueInterruptedDispatches(
            kernel,
            (_, _) => InterruptedDispatchStateRead.Unreadable("goal", "database unavailable"));

        Assert.Equal(0, recovered);
        var skipped = Assert.Single(goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRequeueSkipped));
        Assert.Equal("goal", skipped.RequeueSkipped!.BlockingEntity);
        Assert.Null(skipped.RequeueSkipped.TerminalState);
        Assert.Equal("state-unreadable", skipped.RequeueSkipped.Reason);
        Assert.Equal("database unavailable", skipped.RequeueSkipped.Detail);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
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
                readCurrentState: (_, _) =>
                    new InterruptedDispatchStateRead(GoalStatus.Active, WorkTaskStatus.Cancelled));

            Assert.True(result.RequeueSkipped);
            Assert.Equal(0, spawnCount);
            Assert.Null(task.LastProcess);
            Assert.Contains(goal.Timeline, evt =>
                evt.Kind == ProgressKind.TaskRequeueSkipped &&
                evt.RequeueSkipped?.TerminalState == nameof(WorkTaskStatus.Cancelled));
        }
        finally
        {
            if (Directory.Exists(logRoot)) Directory.Delete(logRoot, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ManualRetry_CancelledTask_ClearsAutomaticMarker()
    {
        var (kernel, goal, task, runner) = InterruptedDispatch();
        runner.RequeueInterruptedDispatches(
            kernel,
            (_, _) => new InterruptedDispatchStateRead(GoalStatus.Active, WorkTaskStatus.Running));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Cancelled, "operator cancelled");

        kernel.RetryTask(goal.Id, task.Id, "operator explicitly retries");

        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Null(task.InterruptedDispatchRecoveryId);
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
                now,
                null,
                WasCancelled: true,
                OwnedProcessIds: [4242]));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Cancelled, "loop stopped");
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
}
