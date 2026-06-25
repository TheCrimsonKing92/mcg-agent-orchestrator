using Mcg.AgentOrchestrator.Core;

public sealed class DispatchOutcomeClassifyTests
{
    private static TaskSpec SimpleTask(AgentRole role = AgentRole.Developer)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Classify test goal");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        return goal.Tasks.First(t => t.RequiredRole == role);
    }

    private static TaskVerificationRecord Verification(
        int exitCode, string stdout, string stderr = "") =>
        new("cmd", "C:\\repo", exitCode, stdout, stderr, DateTimeOffset.UtcNow);

    [Xunit.Fact(DisplayName = "Classify_returns_VerifiedSuccess_for_exit_zero_success")]
    public void Classify_VerifiedSuccess()
    {
        var task = SimpleTask();
        var verification = Verification(0, "All done.", "");

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.None, outcome.RecoveryRecommendation);
        Xunit.Assert.Equal(0, outcome.ExitCode);
    }

    [Xunit.Fact(DisplayName = "Classify_returns_RecoverableSubscriptionLimit_for_usage_limit_output")]
    public void Classify_RecoverableSubscriptionLimit()
    {
        var task = SimpleTask();
        var verification = Verification(
            1,
            "Error: usage limit reached. Please try again at 4:30 PM or purchase more credits.",
            "");

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
    }

    [Xunit.Fact(DisplayName = "Classify_returns_RecoverableSubscriptionLimit_for_codex_rate_limit_stderr")]
    public void Classify_CodexRateLimitStderr()
    {
        var task = SimpleTask();
        var verification = Verification(
            1,
            "",
            "Rate limit reached for gpt-5.5. Please try again in 42s.");

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.NotEqual(DispatchOutcomeKind.EmptyOutputFlake, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.Deferred, outcome.RecoveryRecommendation);
        Xunit.Assert.True(outcome.RetryAfter is { TotalSeconds: > 0 });
        Xunit.Assert.Contains("Rate limit reached", outcome.EvidenceSummary);
    }

    [Xunit.Fact(DisplayName = "Classify_returns_RecoverableSubscriptionLimit_for_codex_retry_limit_429_stderr")]
    public void Classify_CodexRetryLimit429Stderr()
    {
        var task = SimpleTask();
        var verification = Verification(
            1,
            "",
            "exceeded retry limit, last status: 429 Too Many Requests, request id: req_123");

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.NotEqual(DispatchOutcomeKind.EmptyOutputFlake, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Xunit.Assert.Contains("429 Too Many Requests", outcome.EvidenceSummary);
    }

    [Xunit.Fact(DisplayName = "Classify_returns_RecoverableSubscriptionLimit_for_claude_usage_window_stderr")]
    public void Classify_ClaudeUsageWindowStderr()
    {
        var task = SimpleTask();
        var verification = Verification(
            1,
            "",
            "Error: reached your usage limit for this period. Resets in: 1h 15m");

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.Deferred, outcome.RecoveryRecommendation);
        Xunit.Assert.True(outcome.RetryAfter is { TotalMinutes: > 70 });
        Xunit.Assert.Contains("usage limit", outcome.EvidenceSummary);
    }

    [Xunit.Fact(DisplayName = "Classify_keeps_zero_byte_local_kill_as_EmptyOutputFlake_without_provider_reason")]
    public void Classify_LocalKillWithoutProviderReasonIsNotRateLimit()
    {
        var task = SimpleTask();
        var verification = Verification(
            1,
            "",
            "Background dispatch killed after startup hang; no provider output was captured.");

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.EmptyOutputFlake, outcome.Kind);
        Xunit.Assert.NotEqual(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
    }

    [Xunit.Fact(DisplayName = "Classify_returns_EmptyOutputFlake_for_zero_byte_stdout_with_nonzero_exit")]
    public void Classify_EmptyOutputFlake()
    {
        var task = SimpleTask();
        var verification = Verification(1, "", "");

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.EmptyOutputFlake, outcome.Kind);
        Xunit.Assert.True(outcome.HasZeroByteOutput);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify_returns_EmptyOutputFlake_for_exit_zero_with_no_output_or_artifacts")]
    public void Classify_ExitZeroEmptyOutputNoArtifactsIsFlake()
    {
        var task = SimpleTask();
        var verification = Verification(0, "", "");

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.EmptyOutputFlake, outcome.Kind);
        Xunit.Assert.True(outcome.HasZeroByteOutput);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify_returns_VerifiedSuccess_for_exit_zero_with_worker_result_artifact")]
    public void Classify_ExitZeroWorkerResultArtifactIsVerifiedSuccess()
    {
        var task = SimpleTask();
        var verification = Verification(0, "", "");

        var outcome = DispatchFailureClassifier.Classify(task, verification, workerResultPresent: true);

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.None, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify_returns_VerifiedSuccess_for_exit_zero_with_committed_changes")]
    public void Classify_ExitZeroCommittedChangesIsVerifiedSuccess()
    {
        var task = SimpleTask();
        var verification = Verification(0, "", "");

        var outcome = DispatchFailureClassifier.Classify(task, verification, hasCommittedChanges: true);

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.None, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify_does_not_return_VerifiedSuccess_for_nonzero_exit_with_artifacts")]
    public void Classify_NonZeroExitWithArtifactsIsNotVerifiedSuccess()
    {
        var task = SimpleTask();
        var verification = Verification(1, "", "");

        var outcome = DispatchFailureClassifier.Classify(
            task,
            verification,
            workerResultPresent: true,
            hasCommittedChanges: true);

        Xunit.Assert.NotEqual(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
    }

    [Xunit.Fact(DisplayName = "Classify_returns_SandboxCommitBlocked_for_index_lock_with_worker_result")]
    public void Classify_SandboxCommitBlocked()
    {
        var task = SimpleTask();
        var verification = Verification(
            1,
            "WORKER_RESULT:\nfiles: src/Foo.cs\ncommands: dotnet build\ntests: Passed: 3\nEND_WORKER_RESULT",
            "fatal: Unable to create '.git/index.lock': Permission denied");

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.SandboxCommitBlocked, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.CommitAndVerify, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify_returns_ProviderNeutralProgressStall_for_stall_timeout_output")]
    public void Classify_ProviderNeutralProgressStall()
    {
        var task = SimpleTask();
        // Non-empty stdout so EmptyOutputFlake does not fire first (stall text is in stderr).
        var verification = Verification(
            1,
            "Starting worker...",
            "Background dispatch made no observable progress before the stall timeout; wrapper heartbeat state=running, stall timeout exceeded; heartbeat age=21m.");

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.ProviderNeutralProgressStall, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify_returns_ProviderConnectivity_for_connection_refused_output")]
    public void Classify_ProviderConnectivity()
    {
        var task = SimpleTask();
        // Non-empty stdout so EmptyOutputFlake does not fire first.
        var verification = Verification(
            1,
            "Connecting to API...",
            "Error: Unable to connect to API. ECONNREFUSED 127.0.0.1:443");

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.ProviderConnectivity, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify_returns_ProviderModelRejection_for_unknown_model_output")]
    public void Classify_ProviderModelRejection()
    {
        var task = SimpleTask();
        // Non-empty stdout so EmptyOutputFlake does not fire first.
        var verification = Verification(
            1,
            "Connecting to API...",
            "Error: unknown model 'claude-xxx-4-99'. Model not supported.");

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.ProviderModelRejection, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify_returns_DirtyWorktreeRecoverable_for_dirty_guard_developer_task")]
    public void Classify_DirtyWorktreeRecoverable()
    {
        const string dirtyGuardStderr =
            "Developer/Tester dispatch exited 0 but left the worktree dirty. " +
            "branch=goal/abc; head=def; worktree=dirty; commits_after_dispatch=0; " +
            "status_short=M src/Foo.cs.";

        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Dirty worktree test goal");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", "C:\\repo", clock.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec", "C:\\repo", 1, "Files written.", dirtyGuardStderr, clock.UtcNow));

        var verification = Verification(1, "Files written.", dirtyGuardStderr);
        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.DirtyWorktreeRecoverable, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify_returns_UnknownFailure_for_unrecognized_nonzero_exit")]
    public void Classify_UnknownFailure()
    {
        var task = SimpleTask();
        var verification = Verification(1, "Something unexpected happened.", "Internal error details.");

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    }
}
