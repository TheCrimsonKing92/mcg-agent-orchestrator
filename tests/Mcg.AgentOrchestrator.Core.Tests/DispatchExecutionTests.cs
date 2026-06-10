using Mcg.AgentOrchestrator.Core;

public sealed class DispatchExecutionTests
{
    [Xunit.Fact(DisplayName = "RecordTaskDispatch_records_instruction_and_timeline_event")]
    public void RecordTaskDispatchRecordsInstructionAndTimelineEvent()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Dispatch worker task");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var dispatch = new TaskDispatchRecord("codex", "implement feature", "C:\\repo", clock.UtcNow);

    kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);

    Assert.Equal(dispatch, task.LastDispatch);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskDispatchRecorded &&
        evt.Message.Contains("codex", StringComparison.Ordinal) &&
        evt.Message.Contains("implement feature", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "RecordTaskDispatch_rejects_unassigned_task")]
    public void RecordTaskDispatchRejectsUnassignedTask()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Do not dispatch pending work directly", [new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    var ex = Assert.Throws<InvalidOperationException>(() => kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord("codex", "implement feature", "C:\\repo", clock.UtcNow)));

    Assert.Contains(ex.Message, text => text.Contains("status is Pending", StringComparison.Ordinal));
    Assert.True(task.LastDispatch is null);
    Assert.Equal(WorkTaskStatus.Pending, task.Status);
}

    [Xunit.Fact(DisplayName = "RecordTaskDispatch_rejects_verified_task_without_retry")]
    public void RecordTaskDispatchRejectsVerifiedTaskWithoutRetry()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Do not repeat verified work");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Implemented.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "ok", string.Empty, clock.UtcNow));

    var ex = Assert.Throws<InvalidOperationException>(() => kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord("codex", "implement feature", "C:\\repo", clock.UtcNow)));

    Assert.Contains(ex.Message, text => text.Contains("already has passing verification", StringComparison.Ordinal));
    Assert.True(task.LastDispatch is null);
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
}
    [Xunit.Fact(DisplayName = "Snapshot_roundtrip_preserves_task_dispatch")]
    public void SnapshotRoundtripPreservesTaskDispatch()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Persist dispatch");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "worker-a",
        "do work",
        "C:\\repo",
        clock.UtcNow,
        "OpenAI",
        "gpt-5.3-codex",
        "medium",
        TaskComplexity.Simple,
        1234));

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredTask = restored.GetTask(goal.Id, task.Id);

    Assert.Equal("worker-a", restoredTask.LastDispatch!.WorkerName);
    Assert.Equal("do work", restoredTask.LastDispatch.Command);
    Assert.Equal("C:\\repo", restoredTask.LastDispatch.WorkingDirectory);
    Assert.Equal(clock.UtcNow, restoredTask.LastDispatch.DispatchedAt);
    Assert.Equal("OpenAI", restoredTask.LastDispatch.ProviderName);
    Assert.Equal("gpt-5.3-codex", restoredTask.LastDispatch.ModelName);
    Assert.Equal("medium", restoredTask.LastDispatch.ReasoningEffort);
    Assert.Equal(TaskComplexity.Simple, restoredTask.LastDispatch.TaskComplexity);
    Assert.Equal(1234, restoredTask.LastDispatch.PromptCharacterCount);
    Assert.Equal(WorkTaskStatus.Running, restoredTask.Status);
}
    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_completes_task_on_success")]
    public void RecordDispatchExecutionResultCompletesTaskOnSuccess()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Execute dispatch");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "echo ok", "C:\\repo", clock.UtcNow));

    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "echo ok",
        "C:\\repo",
        0,
        "ok",
        string.Empty,
        clock.UtcNow));

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal("ok", task.LastVerification!.StandardOutput);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskVerificationRecorded);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
}
    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_pauses_for_worker_requested_human_input")]
    public void RecordDispatchExecutionResultPausesForWorkerRequestedHumanInput()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Pause dispatch for operator input");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "agent run", "C:\\repo", clock.UtcNow));
    var verification = new TaskVerificationRecord(
        "agent run",
        "C:\\repo",
        0,
        "Implemented setup.\nHUMAN_INPUT: Which branch should I modify?",
        string.Empty,
        clock.UtcNow);

    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);

    var request = kernel.GetPendingHumanInput(goal.Id).Single();
    Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
    Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
    Assert.Equal(task.Id, request.TaskId);
    Assert.Equal("Which branch should I modify?", request.Question);
    Assert.Equal(verification, task.LastVerification);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskVerificationRecorded);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.HumanInputRequested);
    Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted));
}
    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_fails_task_on_nonzero_exit")]
    public void RecordDispatchExecutionResultFailsTaskOnNonzeroExit()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Fail dispatch");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "exit 42", "C:\\repo", clock.UtcNow));

    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "exit 42",
        "C:\\repo",
        42,
        string.Empty,
        "failed",
        clock.UtcNow));

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(42, task.LastVerification!.ExitCode);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskFailed);
}
    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_reopens_task_on_subscription_usage_limit")]
    public void RecordDispatchExecutionResultReopensTaskOnSubscriptionUsageLimit()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Retry dispatch after subscription limit");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", "C:\\repo", clock.UtcNow));

    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "codex exec",
        "C:\\repo",
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 4:58 PM.",
        clock.UtcNow));

    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.True(task.LastVerification is null);
    Assert.Equal(1, task.VerificationHistory.Count);
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskRetried &&
        evt.Message.Contains("recoverable subscription usage limit", StringComparison.Ordinal));
    Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskFailed));
    Assert.Contains(
        kernel.BuildGoalEvidenceSummary(goal.Id).Tasks.Single(item => item.TaskId == task.Id).Message,
        text => text.Contains("Recoverable subscription usage limit", StringComparison.Ordinal));

    Assert.True(DispatchFailureClassifier.TryGetSubscriptionLimitRetryAfter(task, out var retryAfter));
    Assert.Equal(new DateTimeOffset(2026, 06, 01, 16, 58, 00, TimeSpan.Zero), retryAfter);
    Assert.Equal(retryAfter, task.SubscriptionRetryAfter);
    Assert.True(DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, new DateTimeOffset(2026, 06, 01, 16, 57, 00, TimeSpan.Zero), out retryAfter));
    Assert.False(DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, new DateTimeOffset(2026, 06, 01, 16, 59, 00, TimeSpan.Zero), out _));

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredTask = restored.GetTask(goal.Id, task.Id);
    Assert.Equal(new DateTimeOffset(2026, 06, 01, 16, 58, 00, TimeSpan.Zero), restoredTask.SubscriptionRetryAfter);

    kernel.RetryTask(goal.Id, task.Id, "Retry after provider window.");
    Assert.Equal<DateTimeOffset?>(null, task.SubscriptionRetryAfter);
}
    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_fails_after_repeated_subscription_usage_limits")]
    public void RecordDispatchExecutionResultFailsAfterRepeatedSubscriptionUsageLimits()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Stop repeated subscription limit retries");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    for (var attempt = 1; attempt <= 3; attempt++)
    {
        var command = $"codex exec attempt {attempt}";
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", command, "C:\\repo", clock.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, SubscriptionLimitVerification(command, clock.UtcNow));

        if (attempt < 3)
        {
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Assert.True(task.LastVerification is null);
            Assert.True(task.SubscriptionRetryAfter is not null);
            kernel.RetryTask(goal.Id, task.Id, $"Manual retry after attempt {attempt}.");
        }
    }

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.True(task.LastVerification is not null);
    Assert.Equal(3, DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task));
    Assert.Equal<DateTimeOffset?>(null, task.SubscriptionRetryAfter);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskFailed &&
        evt.Message.Contains("recoverable subscription usage limit 3 time", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "Subscription_retry_after_snapshot_metadata_does_not_require_reparsing_provider_text")]
    public void SubscriptionRetryAfterSnapshotMetadataDoesNotRequireReparsingProviderText()
{
    var retryAfter = new DateTimeOffset(2026, 06, 01, 16, 58, 00, TimeSpan.Zero);
    var snapshot = new OrchestratorSnapshot(
        [
            new GoalSnapshot(
                "goal-a",
                "Persist retry metadata",
                GoalStatus.Active,
                [
                    new TaskSnapshot(
                        "task-a",
                        "Implement through subscription",
                        AgentRole.Developer,
                        WorkTaskStatus.Assigned,
                        "agent-a",
                        null,
                        null,
                        [
                            new TaskVerificationSnapshot(
                                "codex exec",
                                "C:\\repo",
                                1,
                                string.Empty,
                                "ERROR: You've hit your usage limit. Visit settings to purchase more credits.",
                                retryAfter.AddHours(-1))
                        ],
                        null,
                        null,
                        null,
                        retryAfter)
                ],
                [])
        ],
        []);

    var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot, new FakeClock());
    var task = kernel.GetTask(new GoalId("goal-a"), new TaskId("task-a"));

    Assert.True(DispatchFailureClassifier.TryGetSubscriptionLimitRetryAfter(task, out var storedRetryAfter));
    Assert.Equal(retryAfter, storedRetryAfter);
    Assert.True(DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, retryAfter.AddMinutes(-1), out storedRetryAfter));
    Assert.Equal(retryAfter, storedRetryAfter);

    kernel.RecordTaskDispatch(new GoalId("goal-a"), new TaskId("task-a"), new TaskDispatchRecord("codex-cli", "codex exec", "C:\\repo", retryAfter));
    Assert.Equal<DateTimeOffset?>(null, task.SubscriptionRetryAfter);
}

private static TaskVerificationRecord SubscriptionLimitVerification(string command, DateTimeOffset completedAt)
{
    return new TaskVerificationRecord(
        command,
        "C:\\repo",
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 4:58 PM.",
        completedAt);
}
    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_rejects_task_without_dispatch")]
    public void RecordDispatchExecutionResultRejectsTaskWithoutDispatch()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("No dispatch");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    Assert.Throws<InvalidOperationException>(() => kernel.RecordDispatchExecutionResult(
        goal.Id,
        task.Id,
        new TaskVerificationRecord("echo ok", "C:\\repo", 0, "ok", string.Empty, DateTimeOffset.UtcNow)));

    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
}
    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_completes_goal_when_all_tasks_completed")]
    public void RecordDispatchExecutionResultCompletesGoalWhenAllTasksCompleted()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var tasks = new[]
    {
        new TaskSpec(TaskId.New(), "Only task", AgentRole.Developer)
    };
    var goal = kernel.CreateGoal("Complete entire goal", tasks);
    var agent = DefaultAgents().First(agent => agent.Role == AgentRole.Developer);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "echo done", "C:\\repo", clock.UtcNow));

    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "echo done",
        "C:\\repo",
        0,
        "done",
        string.Empty,
        clock.UtcNow));

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(GoalStatus.Completed, goal.Status);
}
}

