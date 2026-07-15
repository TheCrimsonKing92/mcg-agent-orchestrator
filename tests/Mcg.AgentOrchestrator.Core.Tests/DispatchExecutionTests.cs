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

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_records_classifier_receipt")]
    public void RecordDispatchExecutionResultRecordsClassifierReceipt()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Record classifier receipt");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var command = "codex exec prompt";
    var stdout = "WORKER_RESULT:\nfiles: src/Foo.cs\ntests: pass\nblockers: none\nEND_WORKER_RESULT";

    kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord("codex-cli", command, "C:\\repo", clock.UtcNow, WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchBaseCommit(goal.Id, task.Id, "29edee5c");
    kernel.RecordDispatchResultCommit(goal.Id, task.Id, "ce5e35c1");

    kernel.RecordDispatchExecutionResult(
        goal.Id,
        task.Id,
        new TaskVerificationRecord(
            command,
            "C:\\repo",
            0,
            stdout,
            string.Empty,
            clock.UtcNow,
            WorkerResultPresent: true,
            HeartbeatStandardOutputBytes: stdout.Length));

    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskNote &&
        evt.Message.StartsWith("CLASSIFIER ", StringComparison.Ordinal) &&
        evt.Message.Contains("worker_result=present(blockers=none)", StringComparison.Ordinal) &&
        evt.Message.Contains("commit=orchestrator", StringComparison.Ordinal) &&
        evt.Message.Contains("verdict=VerifiedSuccess", StringComparison.Ordinal));
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_ignores_dispatch_started_before_latest_retry")]
    public void RecordDispatchExecutionResultIgnoresDispatchStartedBeforeLatestRetry()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Ignore stale retry dispatch");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var command = "codex exec prompt";
    var staleDispatchAt = clock.UtcNow;
    var stdout = WorkerResultStdout("src/Foo.cs", "pass", "none");

    clock.Advance();
    kernel.RetryTask(goal.Id, task.Id, "Retry after acceptance failure.");
    var latestRetryAt = task.LatestRetryAt;

    kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord("codex-cli", command, "C:\\repo", staleDispatchAt, WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchBaseCommit(goal.Id, task.Id, "29edee5c");
    kernel.RecordDispatchResultCommit(goal.Id, task.Id, "ce5e35c1");

    kernel.RecordDispatchExecutionResult(
        goal.Id,
        task.Id,
        new TaskVerificationRecord(
            command,
            "C:\\repo",
            0,
            stdout,
            string.Empty,
            clock.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: true,
            HeartbeatStandardOutputBytes: stdout.Length));

    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(latestRetryAt, task.LatestRetryAt);
    Assert.Null(task.LastDispatch);
    Assert.Null(task.LastVerification);
    Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskNote &&
        evt.Message.Contains("Ignored stale dispatch execution evidence", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_completes_dispatch_started_after_latest_retry")]
    public void RecordDispatchExecutionResultCompletesDispatchStartedAfterLatestRetry()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Honor fresh retry dispatch");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var command = "codex exec prompt";
    var stdout = WorkerResultStdout("src/Foo.cs", "pass", "none");

    kernel.RetryTask(goal.Id, task.Id, "Retry with stronger instructions.");
    clock.Advance();
    kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord("codex-cli", command, "C:\\repo", clock.UtcNow, WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchBaseCommit(goal.Id, task.Id, "29edee5c");
    kernel.RecordDispatchResultCommit(goal.Id, task.Id, "ce5e35c1");

    kernel.RecordDispatchExecutionResult(
        goal.Id,
        task.Id,
        new TaskVerificationRecord(
            command,
            "C:\\repo",
            0,
            stdout,
            string.Empty,
            clock.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: true,
            HeartbeatStandardOutputBytes: stdout.Length));

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_completes_after_task_output_committed_worker_result")]
    public void RecordDispatchExecutionResultCompletesAfterTaskOutputCommittedWorkerResult()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Complete committed worker result");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var command = "codex exec prompt";
    var stdout = WorkerResultStdout("src/Foo.cs", "not-run - acceptance gate owns full verification", "none");

    kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord("codex-cli", command, "C:\\repo", clock.UtcNow, WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchBaseCommit(goal.Id, task.Id, "29edee5c");
    kernel.RecordDispatchResultCommit(goal.Id, task.Id, "ce5e35c1");
    kernel.RecordTaskNote(goal.Id, task.Id, "TaskOutputCommitted: sha=ce5e35c1; provenance=orchestrator.");

    kernel.RecordDispatchExecutionResult(
        goal.Id,
        task.Id,
        new TaskVerificationRecord(
            command,
            "C:\\repo",
            0,
            stdout,
            string.Empty,
            clock.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: true,
            HeartbeatStandardOutputBytes: stdout.Length));

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskNote &&
        evt.Message.StartsWith("CLASSIFIER ", StringComparison.Ordinal) &&
        evt.Message.Contains("rule=committed-worker-result-evidence", StringComparison.Ordinal) &&
        evt.Message.Contains("commit=orchestrator", StringComparison.Ordinal) &&
        evt.Message.Contains("verdict=VerifiedSuccess", StringComparison.Ordinal));
    var timeline = goal.Timeline.ToList();
    Assert.True(
        timeline.FindIndex(evt => evt.TaskId == task.Id && evt.Message.Contains("TaskOutputCommitted", StringComparison.Ordinal)) <
        timeline.FindIndex(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted));
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_self_heals_failed_dispatch_with_structured_green_worker_result")]
    public void RecordDispatchExecutionResultSelfHealsFailedDispatchWithStructuredGreenWorkerResult()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Self-heal failed dispatch");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var command = "codex exec prompt";
    var stdout = WorkerResultStdout(
        "src/Foo.cs",
        "pass - prior run failed and timed out, focused rerun passed",
        "none");

    kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord("codex-cli", command, "C:\\repo", clock.UtcNow, WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchBaseCommit(goal.Id, task.Id, "29edee5c");
    kernel.RecordDispatchResultCommit(goal.Id, task.Id, "ce5e35c1");

    kernel.RecordDispatchExecutionResult(
        goal.Id,
        task.Id,
        new TaskVerificationRecord(
            command,
            "C:\\repo",
            1,
            stdout,
            string.Empty,
            clock.UtcNow,
            WorkerResultPresent: true,
            HeartbeatStandardOutputBytes: stdout.Length));

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskNote &&
        evt.Message.Contains("Reconciled failed dispatch verification to Completed from structured WORKER_RESULT evidence", StringComparison.Ordinal));
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskCompleted &&
        evt.Message.Contains("Dispatch completed successfully", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_does_not_self_heal_blank_blockers_worker_result")]
    public void RecordDispatchExecutionResultDoesNotSelfHealBlankBlockersWorkerResult()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Do not self-heal blank blockers");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var command = "codex exec prompt";
    var stdout = WorkerResultStdout(
        "src/Foo.cs",
        "pass - focused tests passed",
        string.Empty);

    kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord("codex-cli", command, "C:\\repo", clock.UtcNow, WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchBaseCommit(goal.Id, task.Id, "29edee5c");
    kernel.RecordDispatchResultCommit(goal.Id, task.Id, "ce5e35c1");

    kernel.RecordDispatchExecutionResult(
        goal.Id,
        task.Id,
        new TaskVerificationRecord(
            command,
            "C:\\repo",
            1,
            stdout,
            string.Empty,
            clock.UtcNow,
            WorkerResultPresent: true,
            HeartbeatStandardOutputBytes: stdout.Length));

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.DoesNotContain(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskNote &&
        evt.Message.Contains("Reconciled failed dispatch verification to Completed from structured WORKER_RESULT evidence", StringComparison.Ordinal));
    Assert.DoesNotContain(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskCompleted);
}

    [Xunit.Fact(DisplayName = "RecordTaskDispatch_rejects_unassigned_task")]
    public void RecordTaskDispatchRejectsUnassignedTask()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Do not dispatch pending work directly", [new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord("codex", "implement feature", "C:\\repo", clock.UtcNow)));

    Assert.Contains("status is Pending", ex.Message, StringComparison.Ordinal);
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

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord("codex", "implement feature", "C:\\repo", clock.UtcNow)));

    Assert.Contains("already has passing verification", ex.Message, StringComparison.Ordinal);
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
        1234,
        UsesComplexModel: true,
        WorkerProviderKind: ProviderKind.OpenAICodexCli));

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
    Assert.True(restoredTask.LastDispatch.UsesComplexModel);
    Assert.Equal(ProviderKind.OpenAICodexCli, restoredTask.LastDispatch.WorkerProviderKind);
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

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_ignores_explicit_no_human_input_summary")]
    public void RecordDispatchExecutionResultIgnoresExplicitNoHumanInputSummary()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Complete dispatch without operator input");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "agent run", "C:\\repo", clock.UtcNow));
    var verification = new TaskVerificationRecord(
        "agent run",
        "C:\\repo",
        0,
        "Implemented setup.\nHuman input: not needed.",
        string.Empty,
        clock.UtcNow);

    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);

    Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(verification, task.LastVerification);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
    Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.HumanInputRequested));
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
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec",
        "C:\\repo",
        clock.UtcNow,
        WorkerProviderKind: ProviderKind.OpenAICodexCli));

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
        "Recoverable subscription usage limit",
        kernel.BuildGoalEvidenceSummary(goal.Id).Tasks.Single(item => item.TaskId == task.Id).Message,
        StringComparison.Ordinal);

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

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_reopens_task_on_typed_provider_rate_limit_without_output_text")]
    public void RecordDispatchExecutionResultReopensTaskOnTypedProviderRateLimitWithoutOutputText()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Retry dispatch after typed provider rate limit");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec",
        "C:\\repo",
        clock.UtcNow,
        WorkerProviderKind: ProviderKind.OpenAICodexCli));

    kernel.RecordDispatchExecutionResult(
        goal.Id,
        task.Id,
        new TaskVerificationRecord(
            "codex exec",
            "C:\\repo",
            1,
            string.Empty,
            "provider exited before writing a recognizable rate-limit line",
            clock.UtcNow),
        ProviderFailureKind.RateLimit);

    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.Null(task.LastVerification);
    Assert.Equal(ProviderFailureKind.RateLimit, task.VerificationHistory.Single().ProviderFailureKind);
    Assert.True(DispatchFailureClassifier.HasRecoverableSubscriptionLimitHistory(task));
    Assert.Equal(1, DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task));
    Assert.False(DispatchFailureClassifier.TryGetSubscriptionLimitRetryAfter(task, out _));
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskRetried &&
        evt.Message.Contains("recoverable subscription usage limit", StringComparison.Ordinal));

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredTask = restored.GetTask(goal.Id, task.Id);
    Assert.Equal(ProviderKind.OpenAICodexCli, restoredTask.LastDispatch!.WorkerProviderKind);
    Assert.Equal(ProviderFailureKind.RateLimit, restoredTask.VerificationHistory.Single().ProviderFailureKind);
    Assert.True(DispatchFailureClassifier.HasRecoverableSubscriptionLimitHistory(restoredTask));
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_reopens_task_on_provider_connectivity_failure")]
    public void RecordDispatchExecutionResultReopensTaskOnProviderConnectivityFailure()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Retry dispatch after provider connectivity");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec attempt 1",
        "C:\\repo",
        clock.UtcNow,
        WorkerProviderKind: ProviderKind.OpenAICodexCli));

    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, ProviderConnectivityVerification("codex exec attempt 1", clock.UtcNow));

    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.Null(task.LastVerification);
    Assert.Equal(1, DispatchFailureClassifier.CountRecoverableProviderConnectivityFailures(task));
    Assert.Equal(clock.UtcNow, task.LatestRetryAt);
    Assert.Equal(clock.UtcNow.AddMinutes(1), task.SubscriptionRetryAfter);
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskRetried &&
        evt.Message.Contains("provider connectivity failure", StringComparison.Ordinal) &&
        evt.Message.Contains("attempt 1/3", StringComparison.Ordinal));
    Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskFailed));

    var restoredBeforeRetry = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredBeforeRetryTask = restoredBeforeRetry.GetTask(goal.Id, task.Id);
    Assert.Equal(1, DispatchFailureClassifier.CountRecoverableProviderConnectivityFailures(restoredBeforeRetryTask));
    Assert.Equal(clock.UtcNow.AddMinutes(1), restoredBeforeRetryTask.SubscriptionRetryAfter);

    clock.Advance(TimeSpan.FromMinutes(1));
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec attempt 2",
        "C:\\repo",
        clock.UtcNow,
        WorkerProviderKind: ProviderKind.OpenAICodexCli));
    var stdout = WorkerResultStdout("src/Foo.cs", "pass - focused dispatch recovery policy tests", "none");
    kernel.RecordDispatchBaseCommit(goal.Id, task.Id, "29edee5c");
    kernel.RecordDispatchResultCommit(goal.Id, task.Id, "ce5e35c1");

    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "codex exec attempt 2",
        "C:\\repo",
        0,
        stdout,
        string.Empty,
        clock.UtcNow,
        WorkerResultPresent: true,
        HasCommittedChanges: true,
        HeartbeatStandardOutputBytes: stdout.Length));

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(2, task.VerificationHistory.Count);
    Assert.Null(task.SubscriptionRetryAfter);
    Assert.Equal(1, DispatchFailureClassifier.CountRecoverableProviderConnectivityFailures(task));
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_does_not_duplicate_provider_connectivity_retry_after_restart")]
    public void RecordDispatchExecutionResultDoesNotDuplicateProviderConnectivityRetryAfterRestart()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Keep provider connectivity retry idempotent");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var command = "codex exec attempt 1";
    var verification = ProviderConnectivityVerification(command, clock.UtcNow);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        command,
        "C:\\repo",
        clock.UtcNow,
        WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredGoal = restored.GetGoal(goal.Id);
    var restoredTask = restored.GetTask(goal.Id, task.Id);

    restored.RecordDispatchExecutionResult(goal.Id, task.Id, verification);

    Assert.Equal(WorkTaskStatus.Assigned, restoredTask.Status);
    Assert.Null(restoredTask.LastVerification);
    Assert.Null(restoredTask.LastDispatch);
    Assert.Equal(1, DispatchFailureClassifier.CountRecoverableProviderConnectivityFailures(restoredTask));
    Assert.Equal(clock.UtcNow.AddMinutes(1), restoredTask.SubscriptionRetryAfter);
    Assert.Single(restoredGoal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried);
    Assert.Contains(restoredGoal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskNote &&
        evt.Message.Contains("Ignored stale dispatch execution evidence", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_escalates_provider_connectivity_after_retry_cap")]
    public void RecordDispatchExecutionResultEscalatesProviderConnectivityAfterRetryCap()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Cap provider connectivity retries");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    TaskVerificationRecord? finalVerification = null;

    for (var attempt = 1; attempt <= 4; attempt++)
    {
        var command = $"codex exec attempt {attempt}";
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli",
            command,
            "C:\\repo",
            clock.UtcNow,
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
        finalVerification = ProviderConnectivityVerification(command, clock.UtcNow);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, finalVerification);
        clock.Advance(TimeSpan.FromMinutes(attempt));
    }

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.NotNull(task.LastVerification);
    Assert.Equal(4, DispatchFailureClassifier.CountRecoverableProviderConnectivityFailures(task));
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskRetried &&
        evt.Message.Contains("attempt 3/3", StringComparison.Ordinal));
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskFailed &&
        evt.Message.Contains("provider connectivity failed after 3 automatic retry attempt", StringComparison.Ordinal) &&
        evt.Message.Contains("stream disconnected", StringComparison.Ordinal));

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredGoal = restored.GetGoal(goal.Id);
    var restoredTask = restored.GetTask(goal.Id, task.Id);
    restored.RecordDispatchExecutionResult(goal.Id, task.Id, finalVerification!);

    Assert.Equal(WorkTaskStatus.Failed, restoredTask.Status);
    Assert.Equal(4, DispatchFailureClassifier.CountRecoverableProviderConnectivityFailures(restoredTask));
    Assert.Single(restoredGoal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskFailed);
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_does_not_reopen_task_on_provider_authentication_failure")]
    public void RecordDispatchExecutionResultDoesNotReopenTaskOnProviderAuthenticationFailure()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Escalate provider auth failure");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec",
        "C:\\repo",
        clock.UtcNow,
        WorkerProviderKind: ProviderKind.OpenAICodexCli));

    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "codex exec",
        "C:\\repo",
        1,
        string.Empty,
        "ERROR: Your access token could not be refreshed. Run codex login.",
        clock.UtcNow));

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.NotNull(task.LastVerification);
    Assert.True(DispatchFailureClassifier.HasRecoverableProviderAuthenticationFailure(task));
    Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried));
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskNote &&
        evt.Message.Contains("verdict=ProviderAuthentication", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_exit_zero_committed_work_records_WORKER_RESULT_blocker_as_advisory")]
    public void RecordDispatchExecutionResultExitZeroCommittedWorkRecordsWorkerResultBlockerAsAdvisory()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Complete dispatch with advisory blocker");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec",
        "C:\\repo",
        clock.UtcNow,
        WorkerProviderKind: ProviderKind.OpenAICodexCli));
    var stdout = string.Join(Environment.NewLine,
        "WORKER_RESULT:",
        "files: src/Foo.cs",
        "commands: dotnet test --filter WorkerDispatch",
        "tests: pass - focused dispatch-runner coverage",
        "commit: abc1234",
        "blockers: full suite deferred to orchestrator acceptance gate per current-task.md",
        "model_fit: OpenAI/gpt-5.5 - adequate - dispatch",
        "skills: dotnet-windows-build-hygiene",
        "confidence: high",
        "END_WORKER_RESULT");

    kernel.RecordDispatchExecutionResult(
        goal.Id,
        task.Id,
        new TaskVerificationRecord(
            "codex exec",
            "C:\\repo",
            0,
            stdout,
            string.Empty,
            clock.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: true));

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.NotNull(task.LastVerification);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskCompleted &&
        evt.Message.Contains("advisory WORKER_RESULT blocker", StringComparison.Ordinal) &&
        evt.Message.Contains("full suite deferred", StringComparison.Ordinal));
    Assert.False(goal.Timeline.Any(evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskFailed &&
        evt.Message.Contains("WORKER_RESULT reported blocker", StringComparison.Ordinal)));
}

    [Xunit.Theory(DisplayName = "RecordDispatchExecutionResult_completes_read_only_role_with_worker_result_output_and_no_changes")]
    [Xunit.InlineData(AgentRole.Planner)]
    [Xunit.InlineData(AgentRole.Researcher)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void RecordDispatchExecutionResultCompletesReadOnlyRoleWithWorkerResultOutputAndNoChanges(AgentRole role)
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Complete no-change read-only role", [new TaskSpec(TaskId.New(), "Inspect current state", role)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec",
        "C:\\repo",
        clock.UtcNow,
        WorkerProviderKind: ProviderKind.OpenAICodexCli));
    var stdout = WorkerResultStdout("none", "not-run - read-only analysis completed", "none");

    kernel.RecordDispatchExecutionResult(
        goal.Id,
        task.Id,
        new TaskVerificationRecord(
            "codex exec",
            "C:\\repo",
            0,
            stdout,
            string.Empty,
            clock.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: false,
            HeartbeatStandardOutputBytes: stdout.Length));

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.NotNull(task.LastVerification);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_completes_tester_with_worker_result_output_no_failing_tests_and_no_changes")]
    public void RecordDispatchExecutionResultCompletesTesterWithWorkerResultOutputNoFailingTestsAndNoChanges()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Complete tester verification", [new TaskSpec(TaskId.New(), "Verify behavior", AgentRole.Tester)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec",
        "C:\\repo",
        clock.UtcNow,
        WorkerProviderKind: ProviderKind.OpenAICodexCli));
    var stdout = WorkerResultStdout("none", "pass - verification-only review completed", "none");

    kernel.RecordDispatchExecutionResult(
        goal.Id,
        task.Id,
        new TaskVerificationRecord(
            "codex exec",
            "C:\\repo",
            0,
            stdout,
            string.Empty,
            clock.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: false,
            HeartbeatStandardOutputBytes: stdout.Length));

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.NotNull(task.LastVerification);
    Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskFailed);
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_does_not_complete_tester_with_failing_tests_and_no_changes")]
    public void RecordDispatchExecutionResultDoesNotCompleteTesterWithFailingTestsAndNoChanges()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Reject failed tester verification", [new TaskSpec(TaskId.New(), "Verify behavior", AgentRole.Tester)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec",
        "C:\\repo",
        clock.UtcNow,
        WorkerProviderKind: ProviderKind.OpenAICodexCli));
    var stdout = WorkerResultStdout("none", "fail - focused verification failed", "none");

    kernel.RecordDispatchExecutionResult(
        goal.Id,
        task.Id,
        new TaskVerificationRecord(
            "codex exec",
            "C:\\repo",
            0,
            stdout,
            string.Empty,
            clock.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: false,
            HeartbeatStandardOutputBytes: stdout.Length));

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(DispatchOutcomeKind.UnknownFailure, DispatchFailureClassifier.Classify(task, task.LastVerification!).Kind);
    Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_does_not_complete_developer_worker_result_without_changes")]
    public void RecordDispatchExecutionResultDoesNotCompleteDeveloperWorkerResultWithoutChanges()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Reject no-change implementation");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec",
        "C:\\repo",
        clock.UtcNow,
        WorkerProviderKind: ProviderKind.OpenAICodexCli));
    var stdout = WorkerResultStdout("none", "not-run - no source changes", "none");

    kernel.RecordDispatchExecutionResult(
        goal.Id,
        task.Id,
        new TaskVerificationRecord(
            "codex exec",
            "C:\\repo",
            0,
            stdout,
            string.Empty,
            clock.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: false,
            HeartbeatStandardOutputBytes: stdout.Length));

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(DispatchOutcomeKind.UnknownFailure, DispatchFailureClassifier.Classify(task, task.LastVerification!).Kind);
    Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_reviewer_worker_result_blocker_still_fails_gate")]
    public void RecordDispatchExecutionResultReviewerWorkerResultBlockerStillFailsGate()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Gate reviewer blocker", [new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec",
        "C:\\repo",
        clock.UtcNow,
        WorkerProviderKind: ProviderKind.OpenAICodexCli));
    var stdout = WorkerResultStdout("none", "pass - review completed", "unresolved correctness issue");

    kernel.RecordDispatchExecutionResult(
        goal.Id,
        task.Id,
        new TaskVerificationRecord(
            "codex exec",
            "C:\\repo",
            0,
            stdout,
            string.Empty,
            clock.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: false,
            HeartbeatStandardOutputBytes: stdout.Length));

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskFailed &&
        evt.Message.Contains("Reviewer WORKER_RESULT reported blocker", StringComparison.Ordinal));
    Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_fails_nonzero_worker_result_blocker_before_subscription_retry")]
    public void RecordDispatchExecutionResultFailsNonzeroWorkerResultBlockerBeforeSubscriptionRetry()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Fail dispatch on structured blocker");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec",
        "C:\\repo",
        clock.UtcNow,
        WorkerProviderKind: ProviderKind.OpenAICodexCli));
    var stdout = string.Join(Environment.NewLine,
        "WORKER_RESULT:",
        "files: none",
        "commands: dotnet test --no-build",
        "tests: fail - timed out",
        "commit: none",
        "blockers: full Infrastructure no-build timed out at 214s after local rate limit fixture",
        "model_fit: OpenAI/gpt-5.5 - adequate - dispatch",
        "skills: dotnet-windows-build-hygiene",
        "confidence: medium",
        "END_WORKER_RESULT");

    kernel.RecordDispatchExecutionResult(
        goal.Id,
        task.Id,
        new TaskVerificationRecord(
            "codex exec",
            "C:\\repo",
            1,
            stdout,
            string.Empty,
            clock.UtcNow),
        ProviderFailureKind.RateLimit);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.NotNull(task.LastVerification);
    Assert.Equal(ProviderFailureKind.RateLimit, task.LastVerification!.ProviderFailureKind);
    Assert.Equal<DateTimeOffset?>(null, task.SubscriptionRetryAfter);
    Assert.False(DispatchFailureClassifier.HasRecoverableSubscriptionLimitHistory(task));
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskFailed &&
        evt.Message.Contains("full Infrastructure no-build timed out at 214s after local rate limit fixture", StringComparison.Ordinal));
    Assert.False(goal.Timeline.Any(evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskRetried &&
        evt.Message.Contains("recoverable subscription usage limit", StringComparison.Ordinal)));
}
    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_reopens_task_on_powershell_wrapped_subscription_usage_limit")]
    public void RecordDispatchExecutionResultReopensTaskOnPowerShellWrappedSubscriptionUsageLimit()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Retry dispatch after PowerShell-wrapped subscription limit");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec",
        "C:\\repo",
        clock.UtcNow,
        WorkerProviderKind: ProviderKind.OpenAICodexCli));

    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "codex exec",
        "C:\\repo",
        1,
        string.Empty,
        "node.exe : ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 4:58 PM.",
        clock.UtcNow));

    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.True(task.LastVerification is null);
    Assert.True(DispatchFailureClassifier.TryGetSubscriptionLimitRetryAfter(task, out var retryAfter));
    Assert.Equal(new DateTimeOffset(2026, 06, 01, 16, 58, 00, TimeSpan.Zero), retryAfter);
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_does_not_reopen_task_on_quoted_usage_limit_fixture")]
    public void RecordDispatchExecutionResultDoesNotReopenTaskOnQuotedUsageLimitFixture()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Do not retry because transcript quoted a fixture");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", "C:\\repo", clock.UtcNow));

    var transcript = string.Join(
        Environment.NewLine,
        "tests/Mcg.AgentOrchestrator.Core.Tests/WorkerDispatchTests.cs:2509:        \"ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 4:58 PM.\",",
        "tests/Mcg.AgentOrchestrator.Core.Tests/WorkerDispatchTests.cs:2510:        Assert.Equal(expected, actual);");
    var verification = new TaskVerificationRecord(
        "codex exec",
        "C:\\repo",
        1,
        transcript,
        string.Empty,
        clock.UtcNow);

    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(verification, task.LastVerification);
    Assert.Equal<DateTimeOffset?>(null, task.SubscriptionRetryAfter);
    Assert.False(DispatchFailureClassifier.IsRecoverableSubscriptionLimitFailure(verification));
    Assert.False(DispatchFailureClassifier.TryGetSubscriptionLimitRetryAfter(verification, out _));
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskFailed);
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_keeps_repeated_subscription_usage_limits_retryable")]
    public void RecordDispatchExecutionResultKeepsRepeatedSubscriptionUsageLimitsRetryable()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Stop repeated subscription limit retries");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    for (var attempt = 1; attempt <= 3; attempt++)
    {
        var command = $"codex exec attempt {attempt}";
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli",
            command,
            "C:\\repo",
            clock.UtcNow,
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, SubscriptionLimitVerification(command, clock.UtcNow));

        if (attempt < 3)
        {
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Assert.True(task.LastVerification is null);
            Assert.True(task.SubscriptionRetryAfter is not null);
            kernel.RetryTask(goal.Id, task.Id, $"Manual retry after attempt {attempt}.");
            clock.Advance();
        }
    }

    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.True(task.LastVerification is null);
    Assert.Equal(3, DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task));
    Assert.True(task.SubscriptionRetryAfter is not null);
    Assert.True(DispatchFailureClassifier.RequiresSubscriptionLimitReview(task));
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskRetried &&
        evt.Message.Contains("recoverable subscription usage limit 3 time", StringComparison.Ordinal));
    Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskFailed));
}

    [Xunit.Fact(DisplayName = "DispatchFailureClassifier_requires_review_after_repeated_subscription_usage_limits")]
    public void DispatchFailureClassifierRequiresReviewAfterRepeatedSubscriptionUsageLimits()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Review repeated subscription limits");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec attempt 1",
        "C:\\repo",
        clock.UtcNow,
        WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, SubscriptionLimitVerification("codex exec attempt 1", clock.UtcNow));

    Assert.False(DispatchFailureClassifier.RequiresSubscriptionLimitReview(task));

    kernel.RetryTask(goal.Id, task.Id, "Manual retry after attempt 1.");
    clock.Advance();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec attempt 2",
        "C:\\repo",
        clock.UtcNow,
        WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, SubscriptionLimitVerification("codex exec attempt 2", clock.UtcNow));

    Assert.Equal(DispatchFailureClassifier.RecoverableSubscriptionLimitReviewThreshold, DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task));
    Assert.True(DispatchFailureClassifier.RequiresSubscriptionLimitReview(task));
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);

    kernel.AcknowledgeSubscriptionLimitReview(goal.Id, task.Id, "Reviewed profile and will retry after the provider window.");

    Assert.False(DispatchFailureClassifier.RequiresSubscriptionLimitReview(task));
    Assert.Equal(2, task.SubscriptionLimitReviewedFailureCount);
    Assert.Equal("Reviewed profile and will retry after the provider window.", task.SubscriptionLimitReviewNote);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskSubscriptionLimitReviewAcknowledged &&
        evt.Message.Contains("Reviewed profile", StringComparison.Ordinal));

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredTask = restored.GetTask(goal.Id, task.Id);
    Assert.Equal(2, restoredTask.SubscriptionLimitReviewedFailureCount);
    Assert.Equal("Reviewed profile and will retry after the provider window.", restoredTask.SubscriptionLimitReviewNote);
    Assert.False(DispatchFailureClassifier.RequiresSubscriptionLimitReview(restoredTask));
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

private static TaskVerificationRecord ProviderConnectivityVerification(string command, DateTimeOffset completedAt)
{
    return new TaskVerificationRecord(
        command,
        "C:\\repo",
        1,
        string.Empty,
        "ERROR: Falling back from WebSockets to HTTPS transport. stream disconnected",
        completedAt,
        ProviderFailureKind: ProviderFailureKind.Connectivity);
}

private static string WorkerResultStdout(string files, string tests, string blockers)
{
    return string.Join(Environment.NewLine,
        "WORKER_RESULT:",
        $"files: {files}",
        "commands: none",
        $"tests: {tests}",
        "commit: none",
        $"blockers: {blockers}",
        "model_fit: OpenAI/gpt-5.5 - adequate - dispatch",
        "skills: dotnet-windows-build-hygiene",
        "confidence: high",
        "END_WORKER_RESULT");
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_fails_task_when_exit_code_0_but_no_output")]
    public void RecordDispatchExecutionResultFailsTaskWhenExitCode0ButNoOutput()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Require output for dispatch success");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "silent-agent run", "C:\\repo", clock.UtcNow));

    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "silent-agent run",
        "C:\\repo",
        0,
        string.Empty,
        string.Empty,
        clock.UtcNow));

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskFailed &&
        evt.Message.Contains("no output", StringComparison.OrdinalIgnoreCase));
    Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted));
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_counts_nonzero_empty_stdout_as_empty_output_retry")]
    public void RecordDispatchExecutionResultCountsNonzeroEmptyStdoutAsEmptyOutputRetry()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Retry empty stdout dispatch");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "silent-agent run", "C:\\repo", clock.UtcNow));

    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "silent-agent run",
        "C:\\repo",
        1,
        string.Empty,
        string.Empty,
        clock.UtcNow));

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.EmptyOutputRetryCount);
    Assert.True(DispatchFailureClassifier.IsTransientEmptyOutputDispatchFlake(task.LastVerification!));
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_does_not_count_sandbox_preflight_failure_as_empty_output_retry")]
    public void RecordDispatchExecutionResultDoesNotCountSandboxPreflightFailureAsEmptyOutputRetry()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Sandbox preflight dispatch");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "silent-agent run", "C:\\repo", clock.UtcNow));

    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "silent-agent run",
        "C:\\repo",
        1,
        string.Empty,
        "Low Integrity sandbox setup failed while applying integrity label",
        clock.UtcNow));

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(0, task.EmptyOutputRetryCount);
    Assert.Equal(DispatchOutcomeKind.PreflightFailure, DispatchFailureClassifier.Classify(task, task.LastVerification!).Kind);
    Assert.False(DispatchFailureClassifier.IsTransientEmptyOutputDispatchFlake(task.LastVerification!));
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_resets_empty_output_retry_on_nonempty_stdout")]
    public void RecordDispatchExecutionResultResetsEmptyOutputRetryOnNonemptyStdout()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Reset empty stdout dispatch retry");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "silent-agent run", "C:\\repo", clock.UtcNow));
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "silent-agent run",
        "C:\\repo",
        1,
        string.Empty,
        string.Empty,
        clock.UtcNow));
    kernel.RetryTask(goal.Id, task.Id, "retry empty stdout");
    clock.Advance();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "agent run", "C:\\repo", clock.UtcNow));

    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "agent run",
        "C:\\repo",
        1,
        "x",
        string.Empty,
        clock.UtcNow));

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(0, task.EmptyOutputRetryCount);
    Assert.False(DispatchFailureClassifier.IsTransientEmptyOutputDispatchFlake(task.LastVerification!));
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_fails_task_when_exit_code_0_but_whitespace_only_output")]
    public void RecordDispatchExecutionResultFailsTaskWhenExitCode0ButWhitespaceOnlyOutput()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Require non-whitespace output for dispatch success");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "whitespace-agent run", "C:\\repo", clock.UtcNow));

    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "whitespace-agent run",
        "C:\\repo",
        0,
        "   \n  ",
        "\t",
        clock.UtcNow));

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskFailed &&
        evt.Message.Contains("no output", StringComparison.OrdinalIgnoreCase));
    Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted));
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_rejects_task_without_dispatch")]
    public void RecordDispatchExecutionResultRejectsTaskWithoutDispatch()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("No dispatch");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    Assert.ThrowsAny<InvalidOperationException>(() => kernel.RecordDispatchExecutionResult(
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
    Assert.Equal(GoalStatus.Verified, goal.Status);
}
}

