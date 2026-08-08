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

    Assert.Equal(
        dispatch with
        {
            BriefVersion = goal.AuthoritativeBrief.Version,
            BriefSnapshot = goal.Objective
        },
        task.LastDispatch);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskDispatchRecorded &&
        evt.Message.Contains("codex", StringComparison.Ordinal) &&
        evt.Message.Contains("implement feature", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "DispatchResumeAdmission_requires_session_id_and_matching_generation_tuple")]
    public void DispatchResumeAdmissionRequiresSessionIdAndMatchingGenerationTuple()
{
    var dispatch = new TaskDispatchRecord(
        "codex-cli",
        "codex exec",
        "C:\\repo",
        DateTimeOffset.Parse("2026-07-19T12:00:00Z"),
        ProviderSessionId: "codex-session-123",
        WorktreeHeadSha: "abc123",
        DirtyStateHash: "dirty-hash");

    var warm = DispatchResumeAdmission.Evaluate(dispatch, "abc123", "dirty-hash");
    var missingSession = DispatchResumeAdmission.Evaluate(dispatch with { ProviderSessionId = null }, "abc123", "dirty-hash");
    var mismatch = DispatchResumeAdmission.Evaluate(dispatch, "def456", "dirty-hash");
    var retired = DispatchResumeAdmission.Evaluate(
        dispatch with { ProviderSessionRetiredAt = DateTimeOffset.Parse("2026-07-20T12:00:00Z") },
        "abc123",
        "dirty-hash");
    var cleanedUp = DispatchResumeAdmission.Evaluate(dispatch, "abc123", "dirty-hash", goalCleanedUp: true);

    Assert.Equal(DispatchResumeAdmissionKind.WarmResume, warm.Kind);
    Assert.Equal(DispatchResumeAdmissionKind.FreshDispatchOnly, missingSession.Kind);
    Assert.Contains("missing provider session id", missingSession.Reason, StringComparison.Ordinal);
    Assert.Equal(DispatchResumeAdmissionKind.FreshDispatchOnly, mismatch.Kind);
    Assert.Equal(DispatchResumeAdmissionKind.Retired, retired.Kind);
    Assert.Equal(DispatchResumeAdmissionKind.Retired, cleanedUp.Kind);
}

    [Xunit.Fact(DisplayName = "RetireDispatchProviderSession_marks_session_reference_retired")]
    public void RetireDispatchProviderSessionMarksSessionReferenceRetired()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Retire dispatch session reference");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec",
        "C:\\repo",
        clock.UtcNow,
        ProviderSessionId: "codex-session-123",
        WorktreeHeadSha: "abc123",
        DirtyStateHash: "dirty-hash"));

    var retiredAt = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
    kernel.RetireDispatchProviderSession(goal.Id, task.Id, retiredAt);

    Assert.Equal(retiredAt, task.LastDispatch!.ProviderSessionRetiredAt);
    var admission = DispatchResumeAdmission.Evaluate(task.LastDispatch, "abc123", "dirty-hash");
    Assert.Equal(DispatchResumeAdmissionKind.Retired, admission.Kind);
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
    var output = new string('A', VerificationTextBounds.PreviewHeadChars) +
        "\n" +
        new string('M', 2_000) +
        "\nHUMAN_INPUT: Which branch should I modify?\n" +
        new string('N', 2_000) +
        "\n" +
        new string('Z', VerificationTextBounds.PreviewTailChars);
        var verification = new TaskVerificationRecord(
        "agent run",
        "C:\\repo",
        0,
        output,
        string.Empty,
        clock.UtcNow,
        WorkerResultPresent: false,
        HumanInputQuestion: AgentOutputDirectives.TryParseHumanInputRequest(output));

    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);

    var request = kernel.GetPendingHumanInput(goal.Id).Single();
    Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
    Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
    Assert.Equal(task.Id, request.TaskId);
    Assert.Equal("Which branch should I modify?", request.Question);
    Assert.Equal(verification, task.LastVerification);
    Assert.True(task.LastVerification!.StandardOutput.Length <= VerificationTextBounds.MaxRetainedChars);
    Assert.DoesNotContain("HUMAN_INPUT:", task.LastVerification.StandardOutput, StringComparison.Ordinal);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskVerificationRecorded);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.HumanInputRequested);
    Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted));
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_reuses_open_human_input_request_on_second_attempt")]
    public void RecordDispatchExecutionResultReusesOpenHumanInputRequestOnSecondAttempt()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Converge repeated dispatch blocker");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(candidate => candidate.RequiredRole == AgentRole.Developer);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("developer", "develop", "C:\\repo", clock.UtcNow));
        var stdout = WorkerResultStdout(
                "none",
                "not-run - blocked on operator decision",
                "exact-blocker - completion requires out-of-scope production hook plumbing") +
            Environment.NewLine +
            "HUMAN_INPUT: Authorize expanding scope to production hooks?";

        kernel.RecordDispatchExecutionResult(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                "develop",
                "C:\\repo",
                0,
                stdout,
                string.Empty,
                clock.UtcNow,
                WorkerResultPresent: true));
        var firstRequest = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        var firstRequestId = firstRequest.Id;
        var firstResumeCommand = firstRequest.ResumeCommand;

        clock.Advance();
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                "develop",
                "C:\\repo",
                0,
                stdout,
                string.Empty,
                clock.UtcNow,
                WorkerResultPresent: true));

        var reusedRequest = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.Equal(firstRequestId, reusedRequest.Id);
        Assert.Equal(firstResumeCommand, reusedRequest.ResumeCommand);
        Assert.Contains(firstRequestId.Value[..8], reusedRequest.ResumeCommand, StringComparison.Ordinal);
        Assert.Equal(new HumanInputRequestCounts(1, 1), kernel.GetHumanInputRequestCounts(goal.Id, task.Id));
        Assert.Equal(1, reusedRequest.SuppressionCount);
        Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.HumanInputRequested));
        Assert.Contains(goal.Timeline, item =>
            item.Kind == ProgressKind.DuplicateHumanInputSuppressed &&
            item.Message.Contains(firstRequestId.Value[..8], StringComparison.Ordinal));
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
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskFailed &&
        evt.Message.Contains("rule=unknown-failure", StringComparison.Ordinal) &&
        evt.Message.Contains("exit code 42", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_reports_classifier_rule_for_exit_zero_failing_tests")]
    public void RecordDispatchExecutionResultReportsClassifierRuleForExitZeroFailingTests()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Reconstruct archived Tester failure");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
        const string command = "codex exec tester";
        var stdout = string.Join(Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: dotnet test",
            "tests: fail - Core 67/67 and Infrastructure 196/196 passed; substantive finding blocks acceptance",
            "commit: none",
            "blockers: none",
            "model_fit: OpenAI/gpt-5.5 - adequate - verification",
            "skills: dotnet-windows-build-hygiene",
            "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("codex-cli", command, "C:\\repo", clock.UtcNow));

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
                HasCommittedChanges: false));

        var failure = Assert.Single(goal.Timeline.Where(evt =>
            evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskFailed));
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Contains("rule=succeeded-worker-result-failing-tests", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("exit code", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exit code 0", failure.Message, StringComparison.OrdinalIgnoreCase);
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

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_fails_task_on_typed_provider_rate_limit_without_output_text")]
    public void RecordDispatchExecutionResultFailsTaskOnTypedProviderRateLimitWithoutOutputText()
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

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.NotNull(task.LastVerification);
    Assert.Equal(ProviderFailureKind.RateLimit, task.VerificationHistory.Single().ProviderFailureKind);
    Assert.Equal(ProviderFailureKind.RateLimit, task.LastVerification!.ProviderFailureKind);
    Assert.False(DispatchFailureClassifier.HasRecoverableSubscriptionLimitHistory(task));
    Assert.Equal(0, DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task));
    Assert.False(DispatchFailureClassifier.TryGetSubscriptionLimitRetryAfter(task, out _));
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskFailed);
    Assert.False(goal.Timeline.Any(evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskRetried &&
        evt.Message.Contains("recoverable subscription usage limit", StringComparison.Ordinal)));

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredTask = restored.GetTask(goal.Id, task.Id);
    Assert.Equal(ProviderKind.OpenAICodexCli, restoredTask.LastDispatch!.WorkerProviderKind);
    Assert.Equal(ProviderFailureKind.RateLimit, restoredTask.LastVerification!.ProviderFailureKind);
    Assert.False(DispatchFailureClassifier.HasRecoverableSubscriptionLimitHistory(restoredTask));
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

    [Xunit.Theory(DisplayName = "RecordDispatchExecutionResult fails Tester WORKER_RESULT blocker regardless of process exit")]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(1)]
    public void RecordDispatchExecutionResultFailsTesterWorkerResultBlockerRegardlessOfExitCode(int exitCode)
    {
        const string blocker = "worker-sweep-command-name-safety-list";
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal(
            "Reject Tester blocker",
            [new TaskSpec(TaskId.New(), "Verify behavior", AgentRole.Tester)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli",
            "codex exec",
            "C:\\repo",
            clock.UtcNow,
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
        var stdout = WorkerResultStdout("none", "pass - verification completed", blocker);

        kernel.RecordDispatchExecutionResult(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                "codex exec",
                "C:\\repo",
                exitCode,
                stdout,
                string.Empty,
                clock.UtcNow,
                WorkerResultPresent: true,
                HasCommittedChanges: false,
                HeartbeatStandardOutputBytes: stdout.Length));

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(exitCode, task.LastVerification!.ExitCode);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("rule=tester-worker-result-blocker", StringComparison.Ordinal));
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains(blocker, StringComparison.Ordinal) &&
            evt.Message.Contains("same Tester retry or operator action required", StringComparison.Ordinal));
        Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
    }

    [Xunit.Fact(DisplayName = "RecordTaskVerification fails exit-zero Tester WORKER_RESULT blocker")]
    public void RecordTaskVerificationFailsExitZeroTesterWorkerResultBlocker()
    {
        const string blocker = "worker-sweep-command-name-safety-list";
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal(
            "Reject Tester blocker from direct verification",
            [new TaskSpec(TaskId.New(), "Verify behavior", AgentRole.Tester)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        var stdout = WorkerResultStdout("none", "pass - verification completed", blocker);

        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                "tester stdout",
                "C:\\repo",
                0,
                stdout,
                string.Empty,
                clock.UtcNow,
                WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("rule=tester-worker-result-blocker", StringComparison.Ordinal));
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains(blocker, StringComparison.Ordinal));
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
        evt.Message.Contains("Reviewer WORKER_RESULT verdict rejected", StringComparison.Ordinal) &&
        evt.Message.Contains("test-review-finding", StringComparison.Ordinal));
    Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_reviewer_pass_requires_zero_merged_open_findings")]
    public void RecordDispatchExecutionResultReviewerPassRequiresZeroMergedOpenFindings()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Gate reviewer pass on residual findings", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-1", "C:\\repo", clock.UtcNow));
        var round1 = StructuredReviewerResult(
            "needs-work",
            """[{"stable_id":"F-1","state":"open","location":{"file":"src/A.cs","region":"A.Run","hunk":"guard"},"description":"Missing guard."}]""",
            "Missing guard.");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-1", "C:\\repo", 1, round1, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        clock.Advance();
        kernel.RetryTask(goal.Id, reviewer.Id, "recheck");
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-2", "C:\\repo", clock.UtcNow));
        var round2 = StructuredReviewerResult("pass", "[]", "none");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-2", "C:\\repo", 0, round2, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains("verdict rejected", StringComparison.Ordinal) &&
            evt.Message.Contains("F-1", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_review_retry_cap_rejects_untouched_blocker_resolution")]
    public void RecordDispatchExecutionResultReviewRetryCapRejectsUntouchedBlockerResolution()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Keep an unchanged cap-round blocker open", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var location = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");

        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-1", "C:\\repo", clock.UtcNow, BaseCommit: "abc1234"));
        var open = StructuredReviewerResult(
            "needs-work",
            """[{"stable_id":"F-CAP","state":"open","location":{"file":"src/A.cs","region":"A.Run","hunk":"guard"},"description":"Missing guard.","severity":"blocking"}]""",
            "Missing guard.");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-1", "C:\\repo", 1, open, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        clock.Advance();
        kernel.RetryTask(goal.Id, reviewer.Id, "operator requested cap-round recheck");
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli",
            "review-2",
            "C:\\repo",
            clock.UtcNow,
            BaseCommit: "abc1234",
            ReviewFindingTouchedAnchors: [],
            ReviewFindingTouchProofDiagnostic: "Reviewed commits are identical; no touched anchors.",
            ReviewRetryCap: new ReviewRetryCapReceipt(7, 7)));
        var claimedResolved = StructuredReviewerResult(
            "pass",
            """[{"stable_id":"F-CAP","state":"resolved","location":{"file":"src/A.cs","region":"A.Run","hunk":"guard"},"description":"Missing guard.","severity":"blocking"}]""",
            "none");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-2", "C:\\repo", 0, claimedResolved, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        var violation = Assert.IsType<ReviewFindingContractViolation>(reviewer.LastVerification!.ReviewFindingContractViolation);
        Assert.Equal(ReviewFindingConvergence.UnprovenResolutionAtCapViolationCode, violation.Code);
        var retained = Assert.Single(reviewer.LastVerification.MergedReviewFindings!);
        Assert.Equal("F-CAP", retained.StableId);
        Assert.Equal(ReviewFindingState.Open, retained.State);
        Assert.Equal(location, retained.Location);
        Assert.NotEqual(GoalStatus.Verified, goal.Status);
    }

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_rejected_cap_round_does_not_clear_blocker_on_retry")]
    public void RecordDispatchExecutionResultRejectedCapRoundDoesNotClearBlockerOnRetry()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Keep a cap-rejected blocker across retries", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var location = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");

        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-1", "C:\\repo", clock.UtcNow, BaseCommit: "abc1234"));
        var open = StructuredReviewerResult(
            "needs-work",
            """[{"stable_id":"F-CAP","state":"open","location":{"file":"src/A.cs","region":"A.Run","hunk":"guard"},"description":"Missing guard.","severity":"blocking"}]""",
            "Missing guard.");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-1", "C:\\repo", 1, open, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        clock.Advance();
        kernel.RetryTask(goal.Id, reviewer.Id, "cap-round recheck");
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli",
            "review-2",
            "C:\\repo",
            clock.UtcNow,
            BaseCommit: "abc1234",
            ReviewFindingTouchedAnchors: [],
            ReviewFindingTouchProofDiagnostic: "Reviewed commits are identical; no touched anchors.",
            ReviewRetryCap: new ReviewRetryCapReceipt(7, 7)));
        var claimedResolved = StructuredReviewerResult(
            "pass",
            """[{"stable_id":"F-CAP","state":"resolved","location":{"file":"src/A.cs","region":"A.Run","hunk":"guard"},"description":"Missing guard.","severity":"blocking"}]""",
            "none");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-2", "C:\\repo", 0, claimedResolved, string.Empty, clock.UtcNow, WorkerResultPresent: true));
        Assert.Equal(
            ReviewFindingConvergence.UnprovenResolutionAtCapViolationCode,
            reviewer.LastVerification!.ReviewFindingContractViolation?.Code);

        clock.Advance();
        kernel.RetryTask(goal.Id, reviewer.Id, "operator-directed retry");
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli",
            "review-3",
            "C:\\repo",
            clock.UtcNow,
            BaseCommit: "abc1234",
            ReviewFindingTouchedAnchors: [],
            ReviewFindingTouchProofDiagnostic: "Reviewed commits are identical; no touched anchors.",
            ReviewRetryCap: new ReviewRetryCapReceipt(7, 7)));
        var emptyPass = StructuredReviewerResult("pass", "[]", "none");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-3", "C:\\repo", 0, emptyPass, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        var retained = Assert.Single(reviewer.LastVerification!.MergedReviewFindings!);
        Assert.Equal("F-CAP", retained.StableId);
        Assert.Equal(ReviewFindingState.Open, retained.State);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains("F-CAP", StringComparison.Ordinal));
        Assert.NotEqual(GoalStatus.Verified, goal.Status);
    }

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_missing_review_retry_cap_receipt_fails_closed_on_unproven_resolution")]
    public void RecordDispatchExecutionResultMissingReviewRetryCapReceiptFailsClosedOnUnprovenResolution()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Fail closed when legacy cap context is unavailable", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-1", "C:\\repo", clock.UtcNow, BaseCommit: "abc1234"));
        var open = StructuredReviewerResult(
            "needs-work",
            """[{"stable_id":"F-LEGACY-CAP","state":"open","location":{"file":"src/A.cs","region":"A.Run","hunk":"guard"},"description":"Missing guard.","severity":"blocking"}]""",
            "Missing guard.");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-1", "C:\\repo", 1, open, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        clock.Advance();
        kernel.RetryTask(goal.Id, reviewer.Id, "legacy snapshot recheck");
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli",
            "review-2",
            "C:\\repo",
            clock.UtcNow,
            BaseCommit: "abc1234",
            ReviewFindingTouchedAnchors: [],
            ReviewFindingTouchProofDiagnostic: "Reviewed commits are identical; no touched anchors."));
        var claimedResolved = StructuredReviewerResult(
            "pass",
            """[{"stable_id":"F-LEGACY-CAP","state":"resolved","location":{"file":"src/A.cs","region":"A.Run","hunk":"guard"},"description":"Missing guard.","severity":"blocking"}]""",
            "none");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-2", "C:\\repo", 0, claimedResolved, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        var violation = Assert.IsType<ReviewFindingContractViolation>(reviewer.LastVerification!.ReviewFindingContractViolation);
        Assert.Equal(ReviewFindingConvergence.MissingReviewRetryCapReceiptViolationCode, violation.Code);
        var retained = Assert.Single(reviewer.LastVerification.MergedReviewFindings!);
        Assert.Equal("F-LEGACY-CAP", retained.StableId);
        Assert.Equal(ReviewFindingState.Open, retained.State);
    }

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_blocked_at_cap_is_rejected_before_recorded_cap")]
    public void RecordDispatchExecutionResultBlockedAtCapIsRejectedBeforeRecordedCap()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Reject early blocked-at-cap", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli",
            "review",
            "C:\\repo",
            clock.UtcNow,
            ReviewRetryCap: new ReviewRetryCapReceipt(6, 7)));
        var blocked = StructuredReviewerResult(
            "blocked-at-cap",
            """[{"stable_id":"F-EARLY-CAP","state":"open","location":{"file":"src/A.cs","region":"A.Run"},"description":"Missing guard.","severity":"blocking"}]""",
            "F-EARLY-CAP - Missing guard.");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review", "C:\\repo", 0, blocked, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains("blocked-at-cap is only valid", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_review_retry_cap_accepts_touched_blocker_resolution")]
    public void RecordDispatchExecutionResultReviewRetryCapAcceptsTouchedBlockerResolution()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Accept a proven cap-round blocker resolution", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var location = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");

        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-1", "C:\\repo", clock.UtcNow, BaseCommit: "abc1234"));
        var open = StructuredReviewerResult(
            "needs-work",
            """[{"stable_id":"F-CAP","state":"open","location":{"file":"src/A.cs","region":"A.Run","hunk":"guard"},"description":"Missing guard.","severity":"blocking"}]""",
            "Missing guard.");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-1", "C:\\repo", 1, open, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        clock.Advance();
        kernel.RetryTask(goal.Id, reviewer.Id, "recheck changed anchor");
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli",
            "review-2",
            "C:\\repo",
            clock.UtcNow,
            BaseCommit: "def5678",
            ReviewFindingTouchedAnchors: [location],
            ReviewRetryCap: new ReviewRetryCapReceipt(7, 7)));
        var resolved = StructuredReviewerResult(
            "pass",
            """[{"stable_id":"F-CAP","state":"resolved","location":{"file":"src/A.cs","region":"A.Run","hunk":"guard"},"description":"Missing guard.","severity":"blocking"}]""",
            "none");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-2", "C:\\repo", 0, resolved, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
        Assert.Null(reviewer.LastVerification!.ReviewFindingContractViolation);
        Assert.Equal(ReviewFindingState.Resolved, Assert.Single(reviewer.LastVerification.MergedReviewFindings!).State);
    }

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_review_retry_cap_allows_explicit_operator_waiver")]
    public void RecordDispatchExecutionResultReviewRetryCapAllowsExplicitOperatorWaiver()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Audit an explicit incomplete-work override", [reviewer]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Review the blocking criterion",
            ["Missing guard."],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-1", "C:\\repo", clock.UtcNow, BaseCommit: "abc1234"));
        var open = StructuredReviewerResult(
            "needs-work",
            """[{"stable_id":"F-WAIVE","state":"open","location":{"file":"src/A.cs","region":"A.Run"},"description":"Missing guard.","severity":"blocking","category":"spec-compliance"}]""",
            "Missing guard.",
            """[{"criterion_index":0,"verdict":"not-met","evidence":"src/A.cs:10"}]""");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-1", "C:\\repo", 1, open, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        kernel.WaiveAcceptanceCriterion(goal.Id, "Missing guard.", "Operator accepts the known gap for this candidate.", "operator@example");
        clock.Advance();
        kernel.RetryTask(goal.Id, reviewer.Id, "review explicit waiver");
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli",
            "review-2",
            "C:\\repo",
            clock.UtcNow,
            BaseCommit: "abc1234",
            ReviewRetryCap: new ReviewRetryCapReceipt(7, 7)));
        var waivedPass = StructuredReviewerResult(
            "pass",
            """[{"stable_id":"F-WAIVE","state":"open","location":{"file":"src/A.cs","region":"A.Run"},"description":"Missing guard.","severity":"blocking","category":"spec-compliance"}]""",
            "none",
            """[{"criterion_index":0,"verdict":"not-met","evidence":"Explicit operator waiver recorded."}]""");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-2", "C:\\repo", 0, waivedPass, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("stable_id=F-WAIVE", StringComparison.Ordinal) &&
            evt.Message.Contains("review_cap=7/7", StringComparison.Ordinal) &&
            evt.Message.Contains("candidate_sha=abc1234", StringComparison.Ordinal) &&
            evt.Message.Contains("operator@example", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_reviewer_pass_requires_complete_criteria_attestation")]
    public void RecordDispatchExecutionResultReviewerPassRequiresCompleteCriteriaAttestation()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Require criteria attestation", [reviewer]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Review every acceptance criterion",
            ["first criterion", "second criterion"],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-1", "C:\\repo", clock.UtcNow));
        var missing = StructuredReviewerResult("pass", "[]", "none", criteriaVerdictsJson: null);

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-1", "C:\\repo", 0, missing, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains("criteria attestation invalid", StringComparison.Ordinal));

        clock.Advance();
        kernel.RetryTask(goal.Id, reviewer.Id, "provide criteria attestation");
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-2", "C:\\repo", clock.UtcNow));
        var complete = StructuredReviewerResult(
            "pass",
            "[]",
            "none",
            """[{"criterion_index":0,"verdict":"met","evidence":"src/A.cs:10"},{"criterion_index":1,"verdict":"met","evidence":"tests/A.cs:20"}]""");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-2", "C:\\repo", 0, complete, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
    }

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_non_passing_criteria_rejects_pass_but_preserves_needs_work_blockers")]
    public void RecordDispatchExecutionResultNonPassingCriteriaRejectsPassButPreservesNeedsWorkBlockers()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Enforce criteria only as a pass gate", [reviewer]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Review the guard",
            ["Guard is implemented."],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-pass", "C:\\repo", clock.UtcNow));
        var invalidPass = StructuredReviewerResult(
            "pass",
            "[]",
            "none",
            """[{"criterion_index":0,"verdict":"not-verifiable","evidence":"Required receipt is unavailable."}]""");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-pass", "C:\\repo", 0, invalidPass, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains("criteria attestation rejected", StringComparison.Ordinal));

        clock.Advance();
        kernel.RetryTask(goal.Id, reviewer.Id, "report the open blocker");
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-needs-work", "C:\\repo", clock.UtcNow));
        var needsWork = StructuredReviewerResult(
            "needs-work",
            """[{"stable_id":"F-CRITERION","state":"open","location":{"file":"src/A.cs","region":"A.Run"},"description":"Guard is missing.","severity":"blocking"}]""",
            "F-CRITERION - Guard is missing.",
            """[{"criterion_index":0,"verdict":"not-met","evidence":"src/A.cs has no guard."}]""");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-needs-work", "C:\\repo", 1, needsWork, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        var failure = goal.Timeline.Last(evt => evt.TaskId == reviewer.Id && evt.Kind == ProgressKind.TaskFailed);
        Assert.Contains("open blocking stable_id(s): F-CRITERION", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("criteria attestation rejected", failure.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_reviewer_pass_accepts_and_records_extra_criteria_attestations")]
    public void RecordDispatchExecutionResultReviewerPassAcceptsAndRecordsExtraCriteriaAttestations()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Accept diligent reviewer attestations", [reviewer]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Review the registered acceptance criterion",
            ["registered criterion"],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review", "C:\\repo", clock.UtcNow));
        var passingSuperset = StructuredReviewerResult(
            "pass",
            "[]",
            "none",
            """[{"criterion_index":0,"verdict":"met","evidence":"tests/Registered.trx"},{"criterion_index":1,"verdict":"met","evidence":"tests/ExtraOne.trx"},{"criterion_index":2,"verdict":"met","evidence":"tests/ExtraTwo.trx"}]""");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review", "C:\\repo", 0, passingSuperset, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.DoesNotContain(goal.Timeline, item =>
            item.TaskId == reviewer.Id && item.Kind == ProgressKind.TaskFailed);
        Assert.Contains(goal.Timeline, item =>
            item.TaskId == reviewer.Id &&
            item.Kind == ProgressKind.TaskNote &&
            item.Message.Contains("criterion_index=1", StringComparison.Ordinal) &&
            item.Message.Contains("tests/ExtraOne.trx", StringComparison.Ordinal));
        Assert.Contains(goal.Timeline, item =>
            item.TaskId == reviewer.Id &&
            item.Kind == ProgressKind.TaskNote &&
            item.Message.Contains("criterion_index=2", StringComparison.Ordinal) &&
            item.Message.Contains("tests/ExtraTwo.trx", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_reviewer_merge_preserves_rounds_with_equal_timestamps")]
    public void RecordDispatchExecutionResultReviewerMergePreservesRoundsWithEqualTimestamps()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Preserve equal-timestamp review rounds", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-1", "C:\\repo", clock.UtcNow));
        var round1 = StructuredReviewerResult(
            "needs-work",
            """[{"stable_id":"F-1","state":"open","location":{"file":"src/A.cs","region":"A.Run"},"description":"Missing guard."}]""",
            "Missing guard.");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-1", "C:\\repo", 1, round1, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        kernel.RetryTask(goal.Id, reviewer.Id, "recheck");
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-2", "C:\\repo", clock.UtcNow));
        var round2 = StructuredReviewerResult("pass", "[]", "none");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-2", "C:\\repo", 0, round2, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Equal("F-1", Assert.Single(reviewer.LastVerification!.MergedReviewFindings!).StableId);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains("open blocking stable_id(s): F-1", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_reviewer_pass_accepts_and_records_open_advisory_findings")]
    public void RecordDispatchExecutionResultReviewerPassAcceptsAndRecordsOpenAdvisoryFindings()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Retain advisory reviewer findings", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskNote(
            goal.Id,
            reviewer.Id,
            "CRITERIA CORRECTION: supersedes=\"obsolete readability suggestion\"; correction=\"current style is accepted\"");
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review", "C:\\repo", clock.UtcNow));
        var result = StructuredReviewerResult(
            "pass",
            """[{"stable_id":"A-1","state":"open","location":{"file":"src/A.cs","region":"A.Run"},"description":"Consider simplifying this branch.","severity":"advisory"},{"stable_id":"A-2","state":"resolved","location":{"file":"src/B.cs","region":"B.Run"},"description":"Resolved advisory.","severity":"advisory"},{"stable_id":"A-3","state":"open","location":{"file":"src/C.cs","region":"C.Run"},"description":"Obsolete readability suggestion.","severity":"advisory"}]""",
            "none");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review", "C:\\repo", 0, result, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
        Assert.Equal(3, reviewer.LastVerification!.MergedReviewFindings!.Count);
        var advisory = Assert.Single(kernel.GetOpenAdvisoryReviewFindings(goal.Id));
        Assert.Equal("A-1", advisory.StableId);
        Assert.Equal(FindingSeverity.Advisory, advisory.Severity);

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
        Assert.Equal("A-1", Assert.Single(restored.GetOpenAdvisoryReviewFindings(goal.Id)).StableId);
    }

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_reviewer_pass_rejected_by_identity_contract_stores_typed_violation")]
    public void RecordDispatchExecutionResultReviewerPassRejectedByIdentityContractStoresTypedViolation()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Repair moved reviewer identity", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-1", "C:\\repo", clock.UtcNow));
        var accepted = StructuredReviewerResult(
            "pass",
            """[{"stable_id":"A-1","state":"open","location":{"file":"src/A.cs","region":"A.Run","hunk":"guard"},"description":"Readability suggestion.","severity":"advisory"}]""",
            "none");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-1", "C:\\repo", 0, accepted, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        clock.Advance();
        kernel.RetryTask(goal.Id, reviewer.Id, "recheck");
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-2", "C:\\repo", clock.UtcNow));
        var moved = StructuredReviewerResult(
            "pass",
            """[{"stable_id":"A-1","state":"open","location":{"file":"src/B.cs","region":"B.Run","hunk":"guard"},"description":"Readability suggestion.","severity":"advisory"}]""",
            "none");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-2", "C:\\repo", 0, moved, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        var violation = Assert.IsType<ReviewFindingContractViolation>(
            reviewer.LastVerification!.ReviewFindingContractViolation);
        Assert.Equal(ReviewFindingConvergence.IdentityMovedViolationCode, violation.Code);
        Assert.Equal("A-1", violation.PriorStableId);
        Assert.Equal("A-1", violation.SubmittedStableId);
        Assert.Null(reviewer.LastVerification.MergedReviewFindings);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.ReviewFindingContractViolationRecorded &&
            evt.Message.Contains("code=ERR_REVIEW_FINDING_IDENTITY_MOVED", StringComparison.Ordinal) &&
            evt.Message.Contains("reviewer_wall_ms=", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_canonicalized_identity_is_recorded_as_a_note")]
    public void RecordDispatchExecutionResultCanonicalizedIdentityIsRecordedAsNote()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Canonicalize reviewer identity", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-1", "C:\\repo", clock.UtcNow));
        var accepted = StructuredReviewerResult(
            "pass",
            """[{"stable_id":"A-1","state":"open","location":{"file":"src/A.cs","region":"A.Run","hunk":"guard"},"description":"Readability suggestion.","severity":"advisory"}]""",
            "none");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-1", "C:\\repo", 0, accepted, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        clock.Advance();
        kernel.RetryTask(goal.Id, reviewer.Id, "recheck");
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-2", "C:\\repo", clock.UtcNow));
        var resolved = StructuredReviewerResult(
            "pass",
            """[{"stable_id":"A-NEW","state":"resolved","location":{"file":"src/A.cs","region":"A.Run","hunk":"guard"},"description":"Readability suggestion resolved.","severity":"advisory"}]""",
            "none");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-2", "C:\\repo", 0, resolved, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
        Assert.Equal("A-1", Assert.Single(reviewer.LastVerification!.MergedReviewFindings!).StableId);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("submitted_stable_id=A-NEW", StringComparison.Ordinal) &&
            evt.Message.Contains("canonical_stable_id=A-1", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "GetOpenAdvisoryReviewFindings_does_not_fall_back_after_latest_review_fails")]
    public void GetOpenAdvisoryReviewFindingsDoesNotFallBackAfterLatestReviewFails()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Do not export stale advisory findings", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-1", "C:\\repo", clock.UtcNow));
        var accepted = StructuredReviewerResult(
            "pass",
            """[{"stable_id":"A-1","state":"open","location":{"file":"src/A.cs","region":"A.Run"},"description":"Readability suggestion.","severity":"advisory"}]""",
            "none");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-1", "C:\\repo", 0, accepted, string.Empty, clock.UtcNow, WorkerResultPresent: true));
        Assert.Equal("A-1", Assert.Single(kernel.GetOpenAdvisoryReviewFindings(goal.Id)).StableId);

        clock.Advance();
        kernel.RetryTask(goal.Id, reviewer.Id, "recheck");
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-2", "C:\\repo", clock.UtcNow));
        var invalid = StructuredReviewerResult(
            "pass",
            """[{"stable_id":"A-1","state":"open","location":{"file":"src/A.cs","region":"A.Run"},"description":"Readability suggestion.","severity":""}]""",
            "none");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-2", "C:\\repo", 0, invalid, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Empty(kernel.GetOpenAdvisoryReviewFindings(goal.Id));
    }

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_reviewer_pass_still_rejects_open_blocking_among_advisories")]
    public void RecordDispatchExecutionResultReviewerPassStillRejectsOpenBlockingAmongAdvisories()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Reject blocking reviewer finding", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review", "C:\\repo", clock.UtcNow));
        var result = StructuredReviewerResult(
            "pass",
            """[{"stable_id":"B-1","state":"open","location":{"file":"src/A.cs","region":"A.Run"},"description":"Correctness defect.","severity":"blocking"},{"stable_id":"A-1","state":"open","location":{"file":"src/B.cs","region":"B.Run"},"description":"Readability suggestion.","severity":"advisory"}]""",
            "none");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review", "C:\\repo", 0, result, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains("open blocking stable_id(s): B-1", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_waived_finding_is_suppressed_at_recording")]
    public void RecordDispatchExecutionResultWaivedFindingIsSuppressedAtRecording()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Suppress waived structured finding", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskNote(
            goal.Id,
            reviewer.Id,
            "CRITERIA CORRECTION: supersedes=\"full Infrastructure suite before review\"; correction=\"focused build-check evidence is sufficient\"");
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review", "C:\\repo", clock.UtcNow));
        var result = StructuredReviewerResult(
            "pass",
            """[{"stable_id":"W-1","state":"open","location":{"file":"src/A.cs","region":"A.Run"},"description":"Missing full Infrastructure suite before review.","severity":"blocking"}]""",
            "none");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review", "C:\\repo", 0, result, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
        Assert.Equal("W-1", Assert.Single(reviewer.LastVerification!.MergedReviewFindings!).StableId);
        Assert.Empty(kernel.GetOpenAdvisoryReviewFindings(goal.Id));
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains(
                "Suppressed Reviewer structured finding matching operator criteria correction: stable_id=W-1",
                StringComparison.Ordinal) &&
            evt.Message.Contains(
                "finding: Missing full Infrastructure suite before review.",
                StringComparison.Ordinal) &&
            evt.Message.Contains(
                "superseded criterion: full Infrastructure suite before review",
                StringComparison.Ordinal) &&
            evt.Message.Contains(
                "correction recorded",
                StringComparison.Ordinal) &&
            evt.Message.Contains(
                "by operator",
                StringComparison.Ordinal));
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains("verdict rejected", StringComparison.Ordinal));
    }

    [Xunit.Theory(DisplayName = "RecordDispatchExecutionResult_reviewer_nonpass_open_findings_cannot_complete_without_text_blocker")]
    [Xunit.InlineData("needs-work")]
    [Xunit.InlineData("fail")]
    public void RecordDispatchExecutionResultReviewerNonPassOpenFindingsCannotCompleteWithoutTextBlocker(
        string verdict)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Keep structured residual findings authoritative", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review", "C:\\repo", clock.UtcNow));
        var result = StructuredReviewerResult(
            verdict,
            """[{"stable_id":"F-open","state":"open","location":{"file":"src/A.cs","region":"A.Run","hunk":"guard"},"description":"Missing guard."}]""",
            "none");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review", "C:\\repo", 0, result, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains("verdict rejected", StringComparison.Ordinal) &&
            evt.Message.Contains("F-open", StringComparison.Ordinal));
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id && evt.Kind == ProgressKind.TaskCompleted);
    }

    [Xunit.Theory(DisplayName = "RecordDispatchExecutionResult_reviewer_zero_open_findings_requires_pass_verdict")]
    [Xunit.InlineData("needs-work")]
    [Xunit.InlineData("fail")]
    [Xunit.InlineData("reviewed")]
    public void RecordDispatchExecutionResultReviewerZeroOpenFindingsRequiresPassVerdict(string verdict)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Require an explicit passing reviewer verdict", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review", "C:\\repo", clock.UtcNow));
        var result = StructuredReviewerResult(verdict, "[]", "none");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review", "C:\\repo", 0, result, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains("zero open blocking structured findings requires verdict: pass", StringComparison.Ordinal));
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id && evt.Kind == ProgressKind.TaskCompleted);
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

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_records_structured_Tester_inconclusive_without_completion")]
    public void RecordDispatchExecutionResultRecordsStructuredTesterInconclusiveWithoutCompletion()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Record inconclusive Tester receipt");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var tester = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
        kernel.RecordTaskDispatch(
            goal.Id,
            tester.Id,
            new TaskDispatchRecord("tester", "test", "C:\\repo", clock.UtcNow));
        var stdout = WorkerResultStdout(
            "none",
            "inconclusive - command timed out; no TRX",
            "stale blocker from a prior round");

        kernel.RecordDispatchExecutionResult(
            goal.Id,
            tester.Id,
            new TaskVerificationRecord(
                "test",
                "C:\\repo",
                0,
                stdout,
                "",
                clock.UtcNow,
                WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, tester.Status);
        Assert.Equal(1, tester.EmptyOutputRetryCount);
        Assert.Single(tester.VerificationHistory);
        Assert.Equal(
            DispatchOutcomeKind.VerificationInconclusive,
            DispatchFailureClassifier.Classify(tester, tester.LastVerification!).Kind);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId == tester.Id && evt.Kind == ProgressKind.TaskCompleted);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == tester.Id &&
            evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains("schema conflict retained for audit", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_prioritizes_HUMAN_INPUT_over_worker_result_blocker")]
    public void RecordDispatchExecutionResultPrioritizesHumanInputOverWorkerResultBlocker()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Prioritize explicit human input");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var developer = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        kernel.RecordTaskDispatch(
            goal.Id,
            developer.Id,
            new TaskDispatchRecord("developer", "develop", "C:\\repo", clock.UtcNow));
        var stdout = WorkerResultStdout(
            "none",
            "fail - cannot choose safely",
            "additional product blocker") +
            Environment.NewLine +
            "HUMAN_INPUT: Which supported behavior should be authoritative?";

        kernel.RecordDispatchExecutionResult(
            goal.Id,
            developer.Id,
            new TaskVerificationRecord(
                "develop",
                "C:\\repo",
                0,
                stdout,
                "",
                clock.UtcNow,
                WorkerResultPresent: true));

        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.Equal(WorkTaskStatus.WaitingForHuman, developer.Status);
        Assert.Contains("Which supported behavior", request.Question, StringComparison.Ordinal);
        Assert.Contains("additional product blocker", request.Question, StringComparison.Ordinal);
        Assert.Equal(
            HumanInputRequest.BuildQuestionFingerprint("Which supported behavior should be authoritative?"),
            request.QuestionFingerprint);
        Assert.NotNull(request.BlockerFingerprint);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId == developer.Id && evt.Kind == ProgressKind.TaskFailed);
    }

    [Xunit.Theory(DisplayName = "RecordDispatchExecutionResult_routes_Planner_or_Researcher_premise_invalid_to_clarification")]
    [Xunit.InlineData(AgentRole.Planner)]
    [Xunit.InlineData(AgentRole.Researcher)]
    public void RecordDispatchExecutionResultRoutesPremiseInvalidToClarification(AgentRole role)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Clarify invalid premise");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(candidate => candidate.RequiredRole == role);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(role.ToString(), "inspect", "C:\\repo", clock.UtcNow));
        var stdout = WorkerResultStdout(
            "none",
            "not-run - repository inspection only",
            "premise-invalid - required API was removed; src/Api.cs proves replacement semantics");

        kernel.RecordDispatchExecutionResult(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                "inspect",
                "C:\\repo",
                0,
                stdout,
                "",
                clock.UtcNow,
                WorkerResultPresent: true));

        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
        Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
        Assert.Contains("required API was removed", request.Question, StringComparison.Ordinal);
        Assert.Single(task.VerificationHistory);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId == task.Id && (evt.Kind is ProgressKind.TaskFailed or ProgressKind.TaskCompleted));

        kernel.SubmitHumanInput(request.Id, "The removed API is authoritative.");
        for (var round = 2; round <= 4; round++)
        {
            clock.Advance();
            var command = $"inspect-{round}";
            kernel.RetryTask(goal.Id, task.Id, $"Replay premise-invalid round {round}.");
            kernel.RecordTaskDispatch(
                goal.Id,
                task.Id,
                new TaskDispatchRecord(role.ToString(), command, "C:\\repo", clock.UtcNow));
            kernel.RecordDispatchExecutionResult(
                goal.Id,
                task.Id,
                new TaskVerificationRecord(
                    command,
                    "C:\\repo",
                    0,
                    stdout,
                    "",
                    clock.UtcNow,
                    StandardOutputPath: $"C:\\logs\\premise-{round}.out.log",
                    WorkerResultPresent: true));
        }

        Assert.Equal(3, request.SuppressionCount);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        var thresholdFailure = Assert.Single(goal.Timeline.Where(evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains(request.Id.Value[..8], StringComparison.Ordinal)));
        Assert.Contains("round=4", thresholdFailure.Message, StringComparison.Ordinal);
        Assert.Contains("premise-4.out.log", thresholdFailure.Message, StringComparison.Ordinal);
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
    var hasBlocker = !blockers.Equals("none", StringComparison.OrdinalIgnoreCase);
    return string.Join(Environment.NewLine,
        "WORKER_RESULT:",
        $"files: {files}",
        "commands: none",
        $"tests: {tests}",
        "commit: none",
        $"blockers: {blockers}",
        hasBlocker
            ? """findings: [{"stable_id":"test-review-finding","state":"open","location":{"file":"src/Test.cs","region":"Test.Run"},"description":"Reviewer blocker."}]"""
            : "findings: []",
        "touched_anchors: []",
        $"verdict: {(hasBlocker ? "needs-work" : "pass")}",
        "model_fit: OpenAI/gpt-5.5 - adequate - dispatch",
        "skills: dotnet-windows-build-hygiene",
        "confidence: high",
        "END_WORKER_RESULT");
}

private static string StructuredReviewerResult(
    string verdict,
    string findingsJson,
    string blockers,
    string? criteriaVerdictsJson = "[]")
{
    return string.Join(Environment.NewLine,
        "WORKER_RESULT:",
        "files: none",
        "commands: review",
        "tests: pass - deterministic fixture",
        "commit: none",
        $"blockers: {blockers}",
        $"findings: {findingsJson}",
        "touched_anchors: []",
        criteriaVerdictsJson is null ? string.Empty : $"criteria_verdicts: {criteriaVerdictsJson}",
        $"verdict: {verdict}",
        "model_fit: fixture/model - adequate - deterministic review",
        "skills: none",
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
        clock.UtcNow,
        DispatchStartedAt: clock.UtcNow - TimeSpan.FromSeconds(30)));

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.EmptyOutputRetryCount);
    Assert.True(DispatchFailureClassifier.IsTransientEmptyOutputDispatchFlake(task.LastVerification!));
}

    [Xunit.Fact(DisplayName = "RecordDispatchExecutionResult_counts_sandbox_preflight_failure_on_shared_empty_output_retry_budget")]
    public void RecordDispatchExecutionResultCountsSandboxPreflightFailureOnSharedEmptyOutputRetryBudget()
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
    Assert.Equal(1, task.EmptyOutputRetryCount);
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

