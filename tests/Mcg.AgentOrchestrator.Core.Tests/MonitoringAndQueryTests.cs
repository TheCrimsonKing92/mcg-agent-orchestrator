using Mcg.AgentOrchestrator.Core;

public sealed class MonitoringAndQueryTests
{
    [Xunit.Fact(DisplayName = "BuildMonitor_counts_task_statuses_and_pending_human_input")]
    public void BuildMonitorCountsTaskStatusesAndPendingHumanInput()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Monitor status");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var plannerTask = goal.Tasks.First(task => task.RequiredRole == AgentRole.Planner);
    var developerTask = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.ReportTaskProgress(goal.Id, developerTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RequestHumanInput(goal.Id, plannerTask.Id, "Which branch?");

    var monitor = kernel.BuildMonitor(goal.Id);

    Assert.Equal(goal.Id, monitor.GoalId);
    Assert.Equal(goal.Tasks.Count, monitor.TotalTasks);
    Assert.Equal(1, monitor.PendingHumanInputCount);
    Assert.Equal(1, monitor.TaskStatusCounts.Single(count => count.Status == WorkTaskStatus.Completed).Count);
    Assert.Equal(1, monitor.TaskStatusCounts.Single(count => count.Status == WorkTaskStatus.WaitingForHuman).Count);
    Assert.Contains(monitor.AttentionItems, item => item.Kind == TaskAttentionKind.PendingHumanInput && item.TaskId == plannerTask.Id);
}
    [Xunit.Fact(DisplayName = "BuildMonitor_flags_failed_running_dispatch_and_missing_verification")]
    public void BuildMonitorFlagsFailedRunningDispatchAndMissingVerification()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Monitor attention");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var developerTask = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var testerTask = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
    var reviewerTask = goal.Tasks.First(task => task.RequiredRole == AgentRole.Reviewer);

    kernel.RecordTaskDispatch(goal.Id, developerTask.Id, new TaskDispatchRecord("local", "dotnet test", "C:\\repo", clock.UtcNow));
    kernel.ReportTaskProgress(goal.Id, testerTask.Id, WorkTaskStatus.Failed, "Tests failed.");
    kernel.ReportTaskProgress(goal.Id, reviewerTask.Id, WorkTaskStatus.Completed, "Reviewed.");

    var monitor = kernel.BuildMonitor(goal.Id);

    Assert.Contains(monitor.AttentionItems, item => item.Kind == TaskAttentionKind.RunningDispatch && item.TaskId == developerTask.Id);
    Assert.Contains(monitor.AttentionItems, item => item.Kind == TaskAttentionKind.FailedTask && item.TaskId == testerTask.Id);
    Assert.Contains(monitor.AttentionItems, item => item.Kind == TaskAttentionKind.MissingVerification && item.TaskId == reviewerTask.Id);
}
    [Xunit.Fact(DisplayName = "BuildMonitor_flags_failed_verification")]
    public void BuildMonitorFlagsFailedVerification()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Monitor failed verification");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var testerTask = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);

    kernel.RecordTaskVerification(goal.Id, testerTask.Id, new TaskVerificationRecord(
        "dotnet test",
        "C:\\repo",
        1,
        string.Empty,
        "failed",
        clock.UtcNow));

    var monitor = kernel.BuildMonitor(goal.Id);

    Assert.Contains(monitor.AttentionItems, item =>
        item.Kind == TaskAttentionKind.FailedVerification &&
        item.TaskId == testerTask.Id &&
        item.Message.Contains("exit=1", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildMonitor_roundtrip_from_snapshot_preserves_attention")]
    public void BuildMonitorRoundtripFromSnapshotPreservesAttention()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Monitor restored state");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var developerTask = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, developerTask.Id, new TaskDispatchRecord("local", "dotnet build", "C:\\repo", clock.UtcNow));

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var monitor = restored.BuildMonitor(goal.Id);

    Assert.Equal(goal.Id, monitor.GoalId);
    Assert.Contains(monitor.AttentionItems, item => item.Kind == TaskAttentionKind.RunningDispatch && item.TaskId == developerTask.Id);
}
    [Xunit.Fact(DisplayName = "QueryTasks_filters_by_status_role_and_id_prefix")]
    public void QueryTasksFiltersByStatusRoleAndIdPrefix()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Query tasks",
        [
            new TaskSpec(new TaskId("planner-alpha"), "Plan", AgentRole.Planner),
            new TaskSpec(new TaskId("developer-beta"), "Build", AgentRole.Developer),
            new TaskSpec(new TaskId("developer-gamma"), "Fix", AgentRole.Developer)
        ]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    kernel.ReportTaskProgress(goal.Id, goal.Tasks[1].Id, WorkTaskStatus.Running, "Building.");

    var running = kernel.QueryTasks(goal.Id, new TaskQuery(Status: WorkTaskStatus.Running));
    var developers = kernel.QueryTasks(goal.Id, new TaskQuery(Role: AgentRole.Developer));
    var exactDeveloper = kernel.QueryTasks(goal.Id, new TaskQuery(Role: AgentRole.Developer, IdPrefix: "developer-b"));

    Assert.Equal(goal.Id, running.GoalId);
    Assert.Equal(goal.Tasks[1].Id, running.Tasks.Single().Id);
    Assert.Equal(2, developers.Tasks.Count);
    Assert.Equal(goal.Tasks[1].Id, exactDeveloper.Tasks.Single().Id);
}
    [Xunit.Fact(DisplayName = "QueryTasks_filters_by_evidence_and_timeline_event")]
    public void QueryTasksFiltersByEvidenceAndTimelineEvent()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Query evidence",
        [
            new TaskSpec(new TaskId("developer-build"), "Build", AgentRole.Developer),
            new TaskSpec(new TaskId("tester-check"), "Check", AgentRole.Tester),
            new TaskSpec(new TaskId("reviewer-clean"), "Review", AgentRole.Reviewer)
        ]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var developerTask = goal.Tasks[0];
    var testerTask = goal.Tasks[1];
    var reviewerTask = goal.Tasks[2];
    kernel.RecordTaskDispatch(goal.Id, developerTask.Id, new TaskDispatchRecord("local", "dotnet build", "C:\\repo", clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, developerTask.Id, new TaskProcessRecord(1234, "dotnet build", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null));
    kernel.RecordTaskVerification(goal.Id, testerTask.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, "", "failed", clock.UtcNow));

    var runningProcesses = kernel.QueryTasks(goal.Id, new TaskQuery(Evidence: TaskEvidenceKind.RunningProcess));
    var failedVerification = kernel.QueryTasks(goal.Id, new TaskQuery(Evidence: TaskEvidenceKind.FailedVerification));
    var dispatchEvents = kernel.QueryTasks(goal.Id, new TaskQuery(EventKind: ProgressKind.TaskDispatchRecorded));
    var noEvidence = kernel.QueryTasks(goal.Id, new TaskQuery(Evidence: TaskEvidenceKind.None));

    Assert.Equal(developerTask.Id, runningProcesses.Tasks.Single().Id);
    Assert.Equal(testerTask.Id, failedVerification.Tasks.Single().Id);
    Assert.Equal(developerTask.Id, dispatchEvents.Tasks.Single().Id);
    Assert.Equal(reviewerTask.Id, noEvidence.Tasks.Single().Id);
}
    [Xunit.Fact(DisplayName = "BuildGoalEvidenceSummary_rolls_up_task_evidence")]
    public async Task BuildGoalEvidenceSummaryRollsUpTaskEvidence()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Roll up evidence",
        [
            new TaskSpec(TaskId.New(), "Needs input", AgentRole.Planner),
            new TaskSpec(TaskId.New(), "Model output", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Running process", AgentRole.Tester),
            new TaskSpec(TaskId.New(), "Failed verification", AgentRole.Reviewer)
        ]);
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var inputTask = goal.Tasks[0];
    var executionTask = goal.Tasks[1];
    var processTask = goal.Tasks[2];
    var verificationTask = goal.Tasks[3];
    kernel.RequestHumanInput(goal.Id, inputTask.Id, "Which branch?");
    await new AgentTaskRunner(
        kernel,
        agents,
        new InMemoryModelProviderRegistry([new FakeModelProvider("OpenAI", "Implemented change.")]),
        clock)
        .RunAsync(goal.Id, executionTask.Id);
    kernel.RecordTaskDispatch(goal.Id, processTask.Id, new TaskDispatchRecord("local", "dotnet test", "C:\\repo", clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, processTask.Id, new TaskProcessRecord(1234, "dotnet test", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null));
    kernel.ReportTaskProgress(goal.Id, verificationTask.Id, WorkTaskStatus.Completed, "Review done.");
    kernel.RecordTaskVerification(goal.Id, verificationTask.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, "", "failed", clock.UtcNow));

    var summary = kernel.BuildGoalEvidenceSummary(goal.Id);

    Assert.Equal(goal.Id, summary.GoalId);
    Assert.Equal(4, summary.TotalTasks);
    Assert.Equal(1, summary.TasksWithExecution);
    Assert.Equal(1, summary.TasksWithDispatch);
    Assert.Equal(1, summary.TasksWithProcess);
    Assert.Equal(1, summary.RunningProcesses);
    Assert.Equal(1, summary.TasksWithVerification);
    Assert.Equal(0, summary.PassedVerifications);
    Assert.Equal(1, summary.FailedVerifications);
    Assert.Equal(1, summary.PendingHumanInputCount);
    Assert.Equal(100, summary.InputTokens);
    Assert.Equal(25, summary.OutputTokens);
    Assert.Equal(100, summary.PotentiallyPaidInputTokens);
    Assert.Equal(25, summary.PotentiallyPaidOutputTokens);
    var modelUsage = summary.ModelUsage.Single();
    Assert.Equal("OpenAI", modelUsage.ProviderName);
    Assert.Equal("gpt-5.5", modelUsage.ModelName);
    Assert.Equal(1, modelUsage.ExecutionCount);
    Assert.Equal(100, modelUsage.InputTokens);
    Assert.Equal(25, modelUsage.OutputTokens);
    Assert.Equal(0, modelUsage.OutputTokenLimitHitCount);
    Assert.Equal(1024, modelUsage.MaxOutputTokens);
    Assert.Equal(TaskComplexity.Simple, modelUsage.TaskComplexity);
    Assert.True(modelUsage.IsPotentiallyPaidProvider);
    Assert.Equal(TaskEvidenceKind.None, summary.Tasks.Single(item => item.TaskId == inputTask.Id).LatestEvidence);
    Assert.Equal(1, summary.Tasks.Single(item => item.TaskId == inputTask.Id).PendingHumanInputCount);
    Assert.Equal(TaskEvidenceKind.Execution, summary.Tasks.Single(item => item.TaskId == executionTask.Id).LatestEvidence);
    Assert.Equal(TaskEvidenceKind.RunningProcess, summary.Tasks.Single(item => item.TaskId == processTask.Id).LatestEvidence);
    Assert.Equal(TaskEvidenceKind.FailedVerification, summary.Tasks.Single(item => item.TaskId == verificationTask.Id).LatestEvidence);
    Assert.Contains(summary.Tasks.Single(item => item.TaskId == verificationTask.Id).Message, text => text.Contains("failed", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildStageReadinessReport_maps_sdlc_stage_statuses")]
    public void BuildStageReadinessReportMapsSdlcStageStatuses()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Readiness by SDLC stage",
        [
            new TaskSpec(TaskId.New(), "Needs delegation", AgentRole.Planner),
            new TaskSpec(TaskId.New(), "Ready to run", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Running process", AgentRole.Tester),
            new TaskSpec(TaskId.New(), "Needs human input", AgentRole.Researcher),
            new TaskSpec(TaskId.New(), "Needs verification", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Failed verification", AgentRole.Tester),
            new TaskSpec(TaskId.New(), "Verified review", AgentRole.Reviewer)
        ]);
    var agents = DefaultAgents()
        .Where(agent => agent.Role != AgentRole.Planner)
        .ToList();
    kernel.ActivateGoal(goal.Id, agents);
    var needsDelegation = goal.Tasks[0];
    var ready = goal.Tasks[1];
    var running = goal.Tasks[2];
    var input = goal.Tasks[3];
    var missingVerification = goal.Tasks[4];
    var failedVerification = goal.Tasks[5];
    var verified = goal.Tasks[6];

    kernel.RecordTaskDispatch(goal.Id, running.Id, new TaskDispatchRecord("local", "dotnet test", "C:\\repo", clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, running.Id, new TaskProcessRecord(1234, "dotnet test", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null));
    kernel.RequestHumanInput(goal.Id, input.Id, "Which API should I inspect?");
    kernel.ReportTaskProgress(goal.Id, missingVerification.Id, WorkTaskStatus.Completed, "Done.");
    kernel.ReportTaskProgress(goal.Id, failedVerification.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, failedVerification.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, "", "failed", clock.UtcNow));
    kernel.ReportTaskProgress(goal.Id, verified.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, verified.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "ok", "", clock.UtcNow));

    var report = kernel.BuildStageReadinessReport(goal.Id);

    Assert.Equal(goal.Id, report.GoalId);
    Assert.False(report.IsReadyForAcceptance);
    Assert.Equal(7, report.TotalStages);
    Assert.Equal(1, report.VerifiedStages);
    Assert.Equal(6, report.OpenStages);
    Assert.Equal(2, report.BlockedStages);
    Assert.Equal(StageReadinessStatus.NeedsDelegation, report.Stages.Single(stage => stage.TaskId == needsDelegation.Id).StageStatus);
    Assert.Equal(StageReadinessStatus.ReadyToRun, report.Stages.Single(stage => stage.TaskId == ready.Id).StageStatus);
    Assert.Equal(StageReadinessStatus.InProgress, report.Stages.Single(stage => stage.TaskId == running.Id).StageStatus);
    Assert.Equal(StageReadinessStatus.WaitingForHuman, report.Stages.Single(stage => stage.TaskId == input.Id).StageStatus);
    Assert.Equal(StageReadinessStatus.NeedsVerification, report.Stages.Single(stage => stage.TaskId == missingVerification.Id).StageStatus);
    Assert.Equal(StageReadinessStatus.VerificationFailed, report.Stages.Single(stage => stage.TaskId == failedVerification.Id).StageStatus);
    Assert.Equal(StageReadinessStatus.Verified, report.Stages.Single(stage => stage.TaskId == verified.Id).StageStatus);
    Assert.Contains(report.Stages.Single(stage => stage.TaskId == ready.Id).SuggestedAction, text => text.Contains("Run", StringComparison.Ordinal));
    Assert.Equal(TaskEvidenceKind.RunningProcess, report.Stages.Single(stage => stage.TaskId == running.Id).LatestEvidence);
}
}

