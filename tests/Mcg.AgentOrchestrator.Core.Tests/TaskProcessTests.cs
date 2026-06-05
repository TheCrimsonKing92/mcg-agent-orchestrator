using Mcg.AgentOrchestrator.Core;

public sealed class TaskProcessTests
{
    [Xunit.Fact(DisplayName = "BuildProcessBatchPlan_explains_start_dispatch_readiness")]
    public void BuildProcessBatchPlanExplainsStartDispatchReadiness()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Start local workers",
        [
            new TaskSpec(TaskId.New(), "Ready", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "No dispatch", AgentRole.Tester),
            new TaskSpec(TaskId.New(), "Already running", AgentRole.Reviewer)
        ]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var ready = goal.Tasks[0];
    var noDispatch = goal.Tasks[1];
    var alreadyRunning = goal.Tasks[2];
    kernel.RecordTaskDispatch(goal.Id, ready.Id, new TaskDispatchRecord("local", "echo ready", "C:\\repo", clock.UtcNow));
    kernel.ReportTaskProgress(goal.Id, noDispatch.Id, WorkTaskStatus.Running, "Started without dispatch.");
    kernel.RecordTaskDispatch(goal.Id, alreadyRunning.Id, new TaskDispatchRecord("local", "echo running", "C:\\repo", clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, alreadyRunning.Id, new TaskProcessRecord(1234, "echo running", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null));

    var plan = kernel.BuildProcessBatchPlan(goal.Id, ProcessBatchActionKind.StartDispatches);

    Assert.Equal(ProcessBatchActionKind.StartDispatches, plan.Action);
    Assert.Equal(1, plan.ReadyCount);
    Assert.Equal(2, plan.SkippedCount);
    Assert.Equal(ProcessBatchItemStatus.Ready, plan.Items.Single(item => item.TaskId == ready.Id).Status);
    Assert.Contains(plan.Items.Single(item => item.TaskId == noDispatch.Id).Reason, text => text.Contains("no recorded dispatch", StringComparison.Ordinal));
    Assert.Contains(plan.Items.Single(item => item.TaskId == alreadyRunning.Id).Reason, text => text.Contains("already has a running process", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildProcessBatchPlan_explains_refresh_dispatch_readiness")]
    public void BuildProcessBatchPlanExplainsRefreshDispatchReadiness()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Refresh local workers",
        [
            new TaskSpec(TaskId.New(), "Running process", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "No process", AgentRole.Tester),
            new TaskSpec(TaskId.New(), "Completed process", AgentRole.Reviewer)
        ]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var running = goal.Tasks[0];
    var noProcess = goal.Tasks[1];
    var completed = goal.Tasks[2];
    kernel.RecordTaskDispatch(goal.Id, running.Id, new TaskDispatchRecord("local", "echo running", "C:\\repo", clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, running.Id, new TaskProcessRecord(1234, "echo running", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null));
    kernel.ReportTaskProgress(goal.Id, noProcess.Id, WorkTaskStatus.Running, "No process yet.");
    kernel.RecordTaskDispatch(goal.Id, completed.Id, new TaskDispatchRecord("local", "echo done", "C:\\repo", clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, completed.Id, new TaskProcessRecord(5678, "echo done", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null));
    kernel.RecordTaskProcessRefreshed(goal.Id, completed.Id, new TaskProcessRecord(5678, "echo done", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, clock.UtcNow, 0), null);

    var plan = kernel.BuildProcessBatchPlan(goal.Id, ProcessBatchActionKind.RefreshDispatches);

    Assert.Equal(ProcessBatchActionKind.RefreshDispatches, plan.Action);
    Assert.Equal(1, plan.ReadyCount);
    Assert.Equal(2, plan.SkippedCount);
    Assert.Equal(ProcessBatchItemStatus.Ready, plan.Items.Single(item => item.TaskId == running.Id).Status);
    Assert.Contains(plan.Items.Single(item => item.TaskId == noProcess.Id).Reason, text => text.Contains("no background process", StringComparison.Ordinal));
    Assert.Contains(plan.Items.Single(item => item.TaskId == completed.Id).Reason, text => text.Contains("already completed", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildProcessBatchPlan_explains_cancel_dispatch_readiness")]
    public void BuildProcessBatchPlanExplainsCancelDispatchReadiness()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Cancel local workers",
        [
            new TaskSpec(TaskId.New(), "Running process", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "No process", AgentRole.Tester),
            new TaskSpec(TaskId.New(), "Completed process", AgentRole.Reviewer)
        ]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var running = goal.Tasks[0];
    var noProcess = goal.Tasks[1];
    var completed = goal.Tasks[2];
    kernel.RecordTaskDispatch(goal.Id, running.Id, new TaskDispatchRecord("local", "echo running", "C:\\repo", clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, running.Id, new TaskProcessRecord(1234, "echo running", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null));
    kernel.ReportTaskProgress(goal.Id, noProcess.Id, WorkTaskStatus.Running, "No process yet.");
    kernel.RecordTaskDispatch(goal.Id, completed.Id, new TaskDispatchRecord("local", "echo done", "C:\\repo", clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, completed.Id, new TaskProcessRecord(5678, "echo done", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null));
    kernel.RecordTaskProcessRefreshed(goal.Id, completed.Id, new TaskProcessRecord(5678, "echo done", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, clock.UtcNow, 0), null);

    var plan = kernel.BuildProcessBatchPlan(goal.Id, ProcessBatchActionKind.CancelDispatches);

    Assert.Equal(ProcessBatchActionKind.CancelDispatches, plan.Action);
    Assert.Equal(1, plan.ReadyCount);
    Assert.Equal(2, plan.SkippedCount);
    Assert.Equal(ProcessBatchItemStatus.Ready, plan.Items.Single(item => item.TaskId == running.Id).Status);
    Assert.Contains(plan.Items.Single(item => item.TaskId == running.Id).Reason, text => text.Contains("cancel process", StringComparison.Ordinal));
    Assert.Contains(plan.Items.Single(item => item.TaskId == noProcess.Id).Reason, text => text.Contains("no background process", StringComparison.Ordinal));
    Assert.Contains(plan.Items.Single(item => item.TaskId == completed.Id).Reason, text => text.Contains("already completed", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "RecordTaskProcessStarted_requires_dispatch_and_records_running_process")]
    public void RecordTaskProcessStartedRequiresDispatchAndRecordsRunningProcess()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Start background process");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var process = new TaskProcessRecord(1234, "dotnet test", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null);

    Assert.Throws<InvalidOperationException>(() => kernel.RecordTaskProcessStarted(goal.Id, task.Id, process));

    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "dotnet test", "C:\\repo", clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);

    Assert.Equal(process, task.LastProcess);
    Assert.True(task.LastProcess!.IsRunning);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskProcessStarted);
}
    [Xunit.Fact(DisplayName = "RecordTaskProcessRefreshed_records_completion_evidence")]
    public void RecordTaskProcessRefreshedRecordsCompletionEvidence()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Refresh background process");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "dotnet --version", "C:\\repo", clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(1234, "dotnet --version", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null));
    var completed = new TaskProcessRecord(1234, "dotnet --version", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, clock.UtcNow, 0);
    var verification = new TaskVerificationRecord("dotnet --version", "C:\\repo", 0, "10.0.103", string.Empty, clock.UtcNow);

    kernel.RecordTaskProcessRefreshed(goal.Id, task.Id, completed, verification);

    Assert.Equal(completed, task.LastProcess);
    Assert.Equal(verification, task.LastVerification);
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
}
    [Xunit.Fact(DisplayName = "RecordTaskProcessRefreshed_pauses_for_worker_requested_human_input")]
    public void RecordTaskProcessRefreshedPausesForWorkerRequestedHumanInput()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Refresh background process needing input");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "agent run", "C:\\repo", clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(1234, "agent run", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null));
    var completed = new TaskProcessRecord(1234, "agent run", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, clock.UtcNow, 0);
    var verification = new TaskVerificationRecord(
        "agent run",
        "C:\\repo",
        0,
        "HUMAN INPUT: Which test command should I run?",
        string.Empty,
        clock.UtcNow);

    kernel.RecordTaskProcessRefreshed(goal.Id, task.Id, completed, verification);

    var request = kernel.GetPendingHumanInput(goal.Id).Single();
    Assert.Equal(completed, task.LastProcess);
    Assert.Equal(verification, task.LastVerification);
    Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
    Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
    Assert.Equal("Which test command should I run?", request.Question);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.HumanInputRequested);
    Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted));
}
    [Xunit.Fact(DisplayName = "Snapshot_roundtrip_preserves_task_process")]
    public void SnapshotRoundtripPreservesTaskProcess()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Persist process");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "dotnet test", "C:\\repo", clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(5678, "dotnet test", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null));

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredTask = restored.GetTask(goal.Id, task.Id);

    Assert.Equal(5678, restoredTask.LastProcess!.ProcessId);
    Assert.Equal("dotnet test", restoredTask.LastProcess.Command);
    Assert.True(restoredTask.LastProcess.IsRunning);
}
    [Xunit.Fact(DisplayName = "RecordTaskProcessCancelled_marks_task_cancelled")]
    public void RecordTaskProcessCancelledMarksTaskCancelled()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Cancel process");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "Start-Sleep -Seconds 30", "C:\\repo", clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(1234, "Start-Sleep -Seconds 30", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null));
    var cancelled = new TaskProcessRecord(1234, "Start-Sleep -Seconds 30", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, clock.UtcNow, null, WasCancelled: true);

    kernel.RecordTaskProcessCancelled(goal.Id, task.Id, cancelled);

    Assert.Equal(WorkTaskStatus.Cancelled, task.Status);
    Assert.Equal(cancelled, task.LastProcess);
    Assert.True(task.LastProcess!.WasCancelled);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCancelled);
}
}

