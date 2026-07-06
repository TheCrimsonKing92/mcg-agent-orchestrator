using Mcg.AgentOrchestrator.Core;

public sealed class TaskVerificationTests
{
    [Xunit.Fact(DisplayName = "RecordTaskVerification_persists_result_and_timeline_event")]
    public void RecordTaskVerificationPersistsResultAndTimelineEvent()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Verify task behavior");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
    var verification = new TaskVerificationRecord(
        "dotnet test",
        "C:\\repo",
        0,
        "Passed",
        string.Empty,
        clock.UtcNow);

    kernel.RecordTaskVerification(goal.Id, task.Id, verification);

    Assert.Equal(verification, task.LastVerification);
    Assert.Equal(1, task.VerificationHistory.Count);
    Assert.Equal(verification, task.VerificationHistory.Single());
    Assert.True(task.LastVerification!.Succeeded);
    AssertEx.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskVerificationRecorded &&
        evt.Message.Contains("passed", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "RecordTaskVerification_appends_history_and_updates_latest")]
    public void RecordTaskVerificationAppendsHistoryAndUpdatesLatest()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Retain verification history");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
    var first = new TaskVerificationRecord("dotnet build", "C:\\repo", 1, string.Empty, "failed", clock.UtcNow);
    var second = new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", string.Empty, clock.UtcNow);

    kernel.RecordTaskVerification(goal.Id, task.Id, first);
    kernel.RecordTaskVerification(goal.Id, task.Id, second);

    Assert.Equal(second, task.LastVerification);
    Assert.Equal(2, task.VerificationHistory.Count);
    Assert.Equal(first, task.VerificationHistory[0]);
    Assert.Equal(second, task.VerificationHistory[1]);
}
    [Xunit.Fact(DisplayName = "RecordTaskVerification_completes_goal_only_when_all_gates_pass")]
    public void RecordTaskVerificationCompletesGoalOnlyWhenAllGatesPass()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Require verification gate",
        [new TaskSpec(TaskId.New(), "Only task", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, [DefaultAgents().First(agent => agent.Role == AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Implementation done.");

    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(VerificationGateStatus.MissingVerification, kernel.BuildVerificationGate(goal.Id).Tasks.Single().GateStatus);

    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "dotnet test",
        "C:\\repo",
        0,
        "passed",
        string.Empty,
        clock.UtcNow));

    Assert.Equal(GoalStatus.Verified, goal.Status);
    Assert.True(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
}
    [Xunit.Fact(DisplayName = "RetryTask_reopens_task_and_preserves_verification_history")]
    public void RetryTaskReopensTaskAndPreservesVerificationHistory()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Retry failed verification");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Implementation done.");
    var failed = new TaskVerificationRecord("dotnet test", "C:\\repo", 1, "", "failed", clock.UtcNow);
    kernel.RecordTaskVerification(goal.Id, task.Id, failed);

    var retried = kernel.RetryTask(goal.Id, task.Id, "Fix and rerun implementation.");
    var gate = kernel.BuildVerificationGate(goal.Id).Tasks.Single(item => item.TaskId == task.Id);
    var stage = kernel.BuildStageReadinessReport(goal.Id).Stages.Single(item => item.TaskId == task.Id);

    Assert.Equal(task, retried);
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal<TaskVerificationRecord?>(null, task.LastVerification);
    Assert.Equal(1, task.VerificationHistory.Count);
    Assert.Equal(failed, task.VerificationHistory.Single());
    Assert.Equal(VerificationGateStatus.NotReady, gate.GateStatus);
    Assert.Equal(StageReadinessStatus.ReadyToRun, stage.StageStatus);
    AssertEx.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskRetried &&
        evt.Message.Contains("Fix and rerun", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "RetryTask_requires_message_before_clearing_evidence")]
    public void RetryTaskRequiresMessageBeforeClearingEvidence()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Retry needs reason");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var verification = new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", "", clock.UtcNow);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Implementation done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, verification);

    var ex = Assert.ThrowsAny<ArgumentException>(() => kernel.RetryTask(goal.Id, task.Id, " "));

    AssertEx.Contains(ex.Message, text => text.Contains("Retry message cannot be empty", StringComparison.Ordinal));
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(verification, task.LastVerification);
    Assert.True(!goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried));
}
    [Xunit.Fact(DisplayName = "RetryTask_rejects_running_or_waiting_tasks")]
    public void RetryTaskRejectsRunningOrWaitingTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Reject unsafe retry");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var running = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var waiting = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
    kernel.ReportTaskProgress(goal.Id, running.Id, WorkTaskStatus.Running, "Running.");
    kernel.RequestHumanInput(goal.Id, waiting.Id, "Which command?");

    Assert.ThrowsAny<InvalidOperationException>(() => kernel.RetryTask(goal.Id, running.Id, "Retry."));
    Assert.ThrowsAny<InvalidOperationException>(() => kernel.RetryTask(goal.Id, waiting.Id, "Retry."));
}
    [Xunit.Fact(DisplayName = "Snapshot_roundtrip_preserves_task_verification")]
    public void SnapshotRoundtripPreservesTaskVerification()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Persist verification");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);

    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "dotnet build",
        "C:\\repo",
        1,
        string.Empty,
        "Build failed",
        clock.UtcNow));
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "dotnet test",
        "C:\\repo",
        0,
        "Passed",
        string.Empty,
        clock.UtcNow));

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredTask = restored.GetTask(goal.Id, task.Id);

    Assert.Equal("dotnet test", restoredTask.LastVerification!.Command);
    Assert.Equal(0, restoredTask.LastVerification.ExitCode);
    Assert.True(restoredTask.LastVerification.Succeeded);
    Assert.Equal(2, restoredTask.VerificationHistory.Count);
    Assert.Equal("dotnet build", restoredTask.VerificationHistory[0].Command);
    Assert.Equal(1, restoredTask.VerificationHistory[0].ExitCode);
    Assert.Equal("Build failed", restoredTask.VerificationHistory[0].StandardError);
    Assert.False(restoredTask.VerificationHistory[0].Succeeded);
    Assert.Equal("dotnet test", restoredTask.VerificationHistory[1].Command);
}
    [Xunit.Fact(DisplayName = "Snapshot_roundtrip_preserves_ModelFitNote_when_set")]
    public void SnapshotRoundtripPreservesModelFitNoteWhenSet()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Persist ModelFitNote");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);

        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "dotnet test",
            "C:\\repo",
            0,
            "Passed",
            string.Empty,
            clock.UtcNow,
            ModelFitNote: "claude-sonnet-4-6 - adequate - straightforward test task"));

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
        var restoredTask = restored.GetTask(goal.Id, task.Id);

        Assert.Equal("claude-sonnet-4-6 - adequate - straightforward test task", restoredTask.LastVerification!.ModelFitNote);
        Assert.Equal("claude-sonnet-4-6 - adequate - straightforward test task", restoredTask.VerificationHistory.Single().ModelFitNote);
    }

    [Xunit.Fact(DisplayName = "Snapshot_roundtrip_loads_null_ModelFitNote_when_absent")]
    public void SnapshotRoundtripLoadsNullModelFitNoteWhenAbsent()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Persist without ModelFitNote");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);

        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "dotnet test",
            "C:\\repo",
            0,
            "Passed",
            string.Empty,
            clock.UtcNow));

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
        var restoredTask = restored.GetTask(goal.Id, task.Id);

        Assert.Equal<string?>(null, restoredTask.LastVerification!.ModelFitNote);
        Assert.Equal<string?>(null, restoredTask.VerificationHistory.Single().ModelFitNote);
    }

    [Xunit.Fact(DisplayName = "RecordVerification_populates_ModelFitNote_from_markdown_decorated_stdout_line")]
    public void RecordVerificationPopulatesModelFitNoteFromMarkdownDecoratedStdoutLine()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Populate ModelFitNote from stdout");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
        var stdout = "Tests passed.\n**Model fit:** Anthropic/claude-haiku-4-5 — adequate — file write — quick";

        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "dotnet test",
            "C:\\repo",
            0,
            stdout,
            string.Empty,
            clock.UtcNow));

        Assert.Equal(
            "Model fit: Anthropic/claude-haiku-4-5 - adequate - file write - quick",
            task.LastVerification!.ModelFitNote);
        Assert.Equal(
            "Model fit: Anthropic/claude-haiku-4-5 - adequate - file write - quick",
            task.VerificationHistory.Single().ModelFitNote);
    }

    [Xunit.Fact(DisplayName = "Snapshot_roundtrip_preserves_retried_task_without_latest_verification")]
    public void SnapshotRoundtripPreservesRetriedTaskWithoutLatestVerification()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Persist retried task");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "dotnet test",
        "C:\\repo",
        1,
        string.Empty,
        "failed",
        clock.UtcNow));
    kernel.RetryTask(goal.Id, task.Id, "Retry after failed test.");

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredTask = restored.GetTask(goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Assigned, restoredTask.Status);
    Assert.Equal<TaskVerificationRecord?>(null, restoredTask.LastVerification);
    Assert.Equal(1, restoredTask.VerificationHistory.Count);
    Assert.Equal("dotnet test", restoredTask.VerificationHistory.Single().Command);
    AssertEx.Contains(restored.GetGoal(goal.Id).Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried);
}

    [Xunit.Fact(DisplayName = "RetryTask_invalidates_downstream_verification_current_state_but_preserves_history")]
    public void RetryTaskInvalidatesDownstreamVerificationCurrentStateButPreservesHistory()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer);
    var tester = new TaskSpec(TaskId.New(), "Verify fix", AgentRole.Tester);
    var reviewer = new TaskSpec(TaskId.New(), "Review fix", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Retry cascades stale evidence", [developer, tester, reviewer]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());

    CompleteWithVerification(kernel, goal, developer, "developer ok", clock);
    CompleteWithVerification(kernel, goal, tester, "tester ok", clock);
    CompleteWithVerification(kernel, goal, reviewer, "reviewer ok", clock);
    Assert.Equal(GoalStatus.Verified, goal.Status);

    kernel.RetryTask(goal.Id, developer.Id, "Developer output changed; downstream evidence is stale.");

    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
    Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
    Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
    Assert.Null(developer.LastVerification);
    Assert.Null(tester.LastVerification);
    Assert.Null(reviewer.LastVerification);
    Assert.Single(tester.VerificationHistory);
    Assert.Single(reviewer.VerificationHistory);
    Assert.Equal(VerificationGateStatus.NotReady, kernel.BuildVerificationGate(goal.Id).Tasks.Single(item => item.TaskId == tester.Id).GateStatus);
    Assert.Equal(VerificationGateStatus.NotReady, kernel.BuildVerificationGate(goal.Id).Tasks.Single(item => item.TaskId == reviewer.Id).GateStatus);
}

private static void CompleteWithVerification(
    AgentOrchestratorKernel kernel,
    Goal goal,
    TaskSpec task,
    string stdout,
    FakeClock clock)
{
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, $"{task.RequiredRole} done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        $"{task.RequiredRole} verify",
        "C:\\repo",
        0,
        stdout,
        string.Empty,
        clock.UtcNow));
    clock.Advance();
}
}
