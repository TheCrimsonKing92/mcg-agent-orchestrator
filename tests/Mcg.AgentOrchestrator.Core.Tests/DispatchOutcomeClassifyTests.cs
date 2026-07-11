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

    private static TaskSpec SubscriptionTask(AgentRole role = AgentRole.Developer)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Classify subscription test goal");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(t => t.RequiredRole == role);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(
                "codex-cli",
                "opaque command",
                "C:\\repo",
                clock.UtcNow,
                WorkerProviderKind: ProviderKind.OpenAICodexCli));
        return task;
    }

    private static TaskSpec RetryTask(AgentRole role = AgentRole.Developer)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Classify retry test goal");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(t => t.RequiredRole == role);
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["change-demanding retry feedback"]);
        return task;
    }

    private static TaskSpec DispatchedTaskWithResultCommit(string baseCommit, string resultCommit)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Classify committed dispatch test goal");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(
                "codex-cli",
                "codex exec prompt",
                "C:\\repo",
                clock.UtcNow,
                WorkerProviderKind: ProviderKind.OpenAICodexCli));
        kernel.RecordDispatchBaseCommit(goal.Id, task.Id, baseCommit);
        kernel.RecordDispatchResultCommit(goal.Id, task.Id, resultCommit);
        return task;
    }

    private static TaskVerificationRecord Verification(
        int exitCode,
        string stdout,
        string stderr = "") =>
        new("cmd", "C:\\repo", exitCode, stdout, stderr, DateTimeOffset.UtcNow);

    private static TaskVerificationRecord WorkerResultVerification(string stdout, bool hasCommittedChanges = false) =>
        new(
            "cmd",
            "C:\\repo",
            0,
            stdout,
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: hasCommittedChanges,
            HeartbeatStandardOutputBytes: stdout.Length);

    private static TaskVerificationRecord WorkerResultVerification(
        int exitCode,
        string stdout,
        bool hasCommittedChanges = false) =>
        new(
            "cmd",
            "C:\\repo",
            exitCode,
            stdout,
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: hasCommittedChanges,
            HeartbeatStandardOutputBytes: stdout.Length);

    private static string WorkerResultStdout(string tests, string blockers = "none", string? deferrals = null) =>
        $"WORKER_RESULT:{Environment.NewLine}" +
        $"files: none{Environment.NewLine}" +
        $"tests: {tests}{Environment.NewLine}" +
        (deferrals is null ? string.Empty : $"deferrals: {deferrals}{Environment.NewLine}") +
        $"blockers: {blockers}{Environment.NewLine}" +
        "END_WORKER_RESULT";

    [Xunit.Fact(DisplayName = "Classify returns VerifiedSuccess for exit zero success")]
    public void ClassifyVerifiedSuccess()
    {
        var outcome = DispatchFailureClassifier.Classify(SimpleTask(), Verification(0, "All done."));

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.None, outcome.RecoveryRecommendation);
        Xunit.Assert.Equal(0, outcome.ExitCode);
        Xunit.Assert.StartsWith("CLASSIFIER ", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("verdict=VerifiedSuccess", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Theory(DisplayName = "Classify completes read-only role with worker result output and no changes")]
    [Xunit.InlineData(AgentRole.Planner)]
    [Xunit.InlineData(AgentRole.Researcher)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void ClassifyCompletesReadOnlyRoleWithWorkerResultOutputAndNoChanges(AgentRole role)
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(role),
            WorkerResultVerification(WorkerResultStdout("not-run - read-only output recorded")));

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
    }

    [Xunit.Fact(DisplayName = "Classify completes tester with worker result output and no failing tests")]
    public void ClassifyCompletesTesterWithWorkerResultOutputAndNoFailingTests()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(AgentRole.Tester),
            WorkerResultVerification(WorkerResultStdout("pass - verification completed")));

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
    }

    [Xunit.Fact(DisplayName = "Classify fails retry developer round with no commit and no deferral")]
    public void ClassifyFailsRetryDeveloperRoundWithNoCommitAndNoDeferral()
    {
        var outcome = DispatchFailureClassifier.Classify(
            RetryTask(AgentRole.Developer),
            WorkerResultVerification(WorkerResultStdout("pass - no code changes needed", deferrals: "none")));

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Xunit.Assert.Contains("rule=retry-round-produced-no-commit-and-no-deferral", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("retry round produced no commit and no deferral", outcome.EvidenceSummary, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("verdict=VerifiedSuccess", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify completes retry developer round with explicit deferral and no commit")]
    public void ClassifyCompletesRetryDeveloperRoundWithExplicitDeferralAndNoCommit()
    {
        var outcome = DispatchFailureClassifier.Classify(
            RetryTask(AgentRole.Developer),
            WorkerResultVerification(WorkerResultStdout(
                "pass - dependency not available",
                deferrals: "external dependency requires operator follow-up")));

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.Contains("rule=succeeded-dispatch-completion-evidence", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify still completes reviewer with no commit")]
    public void ClassifyStillCompletesReviewerWithNoCommit()
    {
        var outcome = DispatchFailureClassifier.Classify(
            RetryTask(AgentRole.Reviewer),
            WorkerResultVerification(WorkerResultStdout("pass - review completed", deferrals: "none")));

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
    }

    [Xunit.Fact(DisplayName = "Classify preserves first-round developer no-commit behavior")]
    public void ClassifyPreservesFirstRoundDeveloperNoCommitBehavior()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(AgentRole.Developer),
            WorkerResultVerification(WorkerResultStdout("pass - first round no code changes needed", deferrals: "none")));

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.Contains("rule=succeeded-dispatch-completion-evidence", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify keeps developer worker result without changes incomplete")]
    public void ClassifyKeepsDeveloperWorkerResultWithoutChangesIncomplete()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(AgentRole.Developer),
            WorkerResultVerification(WorkerResultStdout("not-run - no source changes")));

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
    }

    [Xunit.Fact(DisplayName = "Classify completes any role with exit zero and green test evidence")]
    public void ClassifyCompletesAnyRoleWithExitZeroAndGreenTestEvidence()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(AgentRole.Developer),
            WorkerResultVerification(WorkerResultStdout("TRX 5/5 passed")));

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
    }

    [Xunit.Theory(DisplayName = "Classify treats post-dispatch result commit as Developer change evidence")]
    [Xunit.InlineData("1d2223c2/2c233f17", "ce5e35c1")]
    [Xunit.InlineData("57cb9f11/cd72a0d3", "bae19444")]
    public void ClassifyTreatsPostDispatchResultCommitAsDeveloperChangeEvidence(string preservedInstance, string resultCommit)
    {
        var verification = WorkerResultVerification(
            WorkerResultStdout($"dotnet test --filter DispatchOutcomeClassifyTests passed; preserved instance {preservedInstance}"),
            hasCommittedChanges: false);

        var outcome = DispatchFailureClassifier.Classify(
            DispatchedTaskWithResultCommit("29edee5c", resultCommit),
            verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.Equal(0, outcome.ExitCode);
        Xunit.Assert.Contains("commit=orchestrator", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("worker_result=present(blockers=none)", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify reads WORKER_RESULT blockers from stdout artifact")]
    public void ClassifyReadsWorkerResultBlockersFromStdoutArtifact()
    {
        var stdoutPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                stdoutPath,
                WorkerResultStdout("dotnet test --filter DispatchOutcomeClassifyTests passed", "none"));
            var verification = new TaskVerificationRecord(
                "cmd",
                "C:\\repo",
                0,
                string.Empty,
                string.Empty,
                DateTimeOffset.UtcNow,
                StandardOutputPath: stdoutPath,
                WorkerResultPresent: true,
                HasCommittedChanges: false,
                HeartbeatStandardOutputBytes: 0);

            var outcome = DispatchFailureClassifier.Classify(
                DispatchedTaskWithResultCommit("29edee5c", "ce5e35c1"),
                verification);

            Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
            Xunit.Assert.Contains("rule=committed-worker-result-evidence", outcome.ClassifierReceipt, StringComparison.Ordinal);
            Xunit.Assert.Contains("worker_result=present(blockers=none)", outcome.ClassifierReceipt, StringComparison.Ordinal);
            Xunit.Assert.Contains("commit=orchestrator", outcome.ClassifierReceipt, StringComparison.Ordinal);
            Xunit.Assert.Contains("verdict=VerifiedSuccess", outcome.ClassifierReceipt, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(stdoutPath);
        }
    }

    [Xunit.Theory(DisplayName = "Classify completes historical committed worker result variants")]
    [Xunit.InlineData(-1, AgentRole.Developer, true, false, "manufactured-exit-minus-one")]
    [Xunit.InlineData(0, AgentRole.Tester, true, false, "tester-with-commit")]
    [Xunit.InlineData(0, AgentRole.Developer, false, true, "developer-with-orchestrator-commit")]
    [Xunit.InlineData(0, AgentRole.Developer, true, false, "exit-zero-green")]
    public void ClassifyCompletesHistoricalCommittedWorkerResultVariants(
        int exitCode,
        AgentRole role,
        bool verificationHasCommittedChanges,
        bool orchestratorResultCommit,
        string variant)
    {
        var stdout = WorkerResultStdout($"pass - historical variant {variant}");
        var verification = WorkerResultVerification(exitCode, stdout, verificationHasCommittedChanges);
        var task = orchestratorResultCommit
            ? DispatchedTaskWithResultCommit("29edee5c", "ce5e35c1")
            : SimpleTask(role);

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.None, outcome.RecoveryRecommendation);
        Xunit.Assert.Contains("rule=committed-worker-result-evidence", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("worker_result=present(blockers=none)", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("verdict=VerifiedSuccess", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify fails exit zero worker result with failing tests")]
    public void ClassifyFailsExitZeroWorkerResultWithFailingTests()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(AgentRole.Tester),
            WorkerResultVerification(WorkerResultStdout("Failed: 1, Passed: 4")));

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
    }

    [Xunit.Fact(DisplayName = "Classify keeps exit zero worker evidence as completion despite usage limit text")]
    public void ClassifyExitZeroWorkerEvidenceOverridesUsageLimitText()
    {
        var verification = new TaskVerificationRecord(
            "cmd",
            "C:\\repo",
            0,
            "WORKER_RESULT:\nfiles: src/Foo.cs\ntests: pass\nblockers: none\nEND_WORKER_RESULT",
            "Rate limit reached for gpt-5.5. Please try again in 42s.",
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: true);

        var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.NotEqual(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.Equal(0, outcome.ExitCode);
    }

    [Xunit.Fact(DisplayName = "Classify returns RecoverableSubscriptionLimit for provider rate limit stderr")]
    public void ClassifyRecoverableSubscriptionLimitFromStderr()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SubscriptionTask(),
            Verification(1, "", "Rate limit reached for gpt-5.5. Please try again in 42s."));

        Xunit.Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.Deferred, outcome.RecoveryRecommendation);
        Xunit.Assert.True(outcome.RetryAfter is { TotalSeconds: > 0 });
        Xunit.Assert.Contains("Rate limit reached", outcome.EvidenceSummary);
    }

    [Xunit.Fact(DisplayName = "ClassifyProviderFailure emits complete classifier receipt signal shape")]
    public void ClassifyProviderFailureEmitsCompleteClassifierReceiptSignalShape()
    {
        var outcome = DispatchFailureClassifier.ClassifyProviderFailure(
            ProviderFailureKind.Connectivity,
            1,
            hasZeroByteOutput: true,
            "Unable to connect to API.");

        Xunit.Assert.Equal(DispatchOutcomeKind.ProviderConnectivity, outcome.Kind);
        Xunit.Assert.StartsWith("CLASSIFIER ", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("rule=provider-connectivity", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("exit_code=1", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("exit_artifact=direct-provider-failure", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("stdout_bytes=unknown", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("stderr_bytes=unknown", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("heartbeat_stdout_bytes=unknown", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("worker_result=absent", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("commit=none", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("verdict=ProviderConnectivity", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify returns RecoverableSubscriptionLimit for provider rate limit stdout")]
    public void ClassifyRecoverableSubscriptionLimitFromStdout()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SubscriptionTask(),
            Verification(1, "Error: provider returned 429."));

        Xunit.Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Xunit.Assert.Null(outcome.RetryAfter);
        Xunit.Assert.Contains("provider returned 429", outcome.EvidenceSummary);
    }

    [Xunit.Fact(DisplayName = "Classify defaults recoverable subscription limit retry when duration is absent")]
    public void ClassifyRecoverableSubscriptionLimitDefaultsWhenDurationAbsent()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SubscriptionTask(),
            Verification(1, "", "Provider quota exceeded for this account."));

        Xunit.Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Xunit.Assert.Null(outcome.RetryAfter);
        Xunit.Assert.Contains("quota exceeded", outcome.EvidenceSummary);
    }

    [Xunit.Fact(DisplayName = "Classify returns PreflightFailure for sandbox preflight evidence")]
    public void ClassifyPreflightFailure()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(),
            Verification(1, "", "Low Integrity sandbox setup failed while applying label"));

        Xunit.Assert.Equal(DispatchOutcomeKind.PreflightFailure, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Xunit.Assert.Contains("sandbox-preflight-failure", outcome.EvidenceSummary);
    }

    [Xunit.Fact(DisplayName = "Classify returns EmptyOutputFlake for empty subscription output with nonzero exit")]
    public void ClassifyEmptyOutputFlake()
    {
        var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), Verification(1, ""));

        Xunit.Assert.Equal(DispatchOutcomeKind.EmptyOutputFlake, outcome.Kind);
        Xunit.Assert.NotEqual(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.True(outcome.HasZeroByteOutput);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify preserves exit zero empty output failover when heartbeat has no bytes")]
    public void ClassifyPreservesExitZeroEmptyOutputFailoverWhenHeartbeatHasNoBytes()
    {
        var verification = new TaskVerificationRecord(
            "cmd",
            "C:\\repo",
            0,
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow,
            HeartbeatStandardOutputBytes: 0);

        var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.EmptyOutputFlake, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Xunit.Assert.Contains("rule=empty-output-flake", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("heartbeat_stdout_bytes=0", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify returns SandboxCommitBlocked for index lock with worker result")]
    public void ClassifySandboxCommitBlocked()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(),
            Verification(
                1,
                "WORKER_RESULT:\nfiles: src/Foo.cs\ncommands: dotnet build\ntests: Passed: 3\nEND_WORKER_RESULT",
                "fatal: Unable to create '.git/index.lock': Permission denied"));

        Xunit.Assert.Equal(DispatchOutcomeKind.SandboxCommitBlocked, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.CommitAndVerify, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify returns ProviderNeutralProgressStall for stall timeout output")]
    public void ClassifyProviderNeutralProgressStall()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(),
            Verification(
                1,
                "Starting worker...",
                "Background dispatch made no observable progress before the stall timeout; wrapper heartbeat state=running, stall timeout exceeded; heartbeat age=21m."));

        Xunit.Assert.Equal(DispatchOutcomeKind.ProviderNeutralProgressStall, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify returns ProviderAuthentication for provider auth output")]
    public void ClassifyProviderAuthentication()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(),
            Verification(1, "Connecting to API...", "Error: 401 Unauthorized. Invalid API key."));

        Xunit.Assert.Equal(DispatchOutcomeKind.ProviderAuthentication, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify returns ProviderConnectivity for connection refused output")]
    public void ClassifyProviderConnectivity()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(),
            Verification(1, "Connecting to API...", "Error: Unable to connect to API. ECONNREFUSED 127.0.0.1:443"));

        Xunit.Assert.Equal(DispatchOutcomeKind.ProviderConnectivity, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify returns ProviderModelRejection for unknown model output")]
    public void ClassifyProviderModelRejection()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(),
            Verification(1, "Connecting to API...", "Error: unknown model 'claude-xxx-4-99'. Model not supported."));

        Xunit.Assert.Equal(DispatchOutcomeKind.ProviderModelRejection, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify returns DirtyWorktreeRecoverable for dirty guard developer task")]
    public void ClassifyDirtyWorktreeRecoverable()
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
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(
                "codex-cli",
                "codex exec",
                "C:\\repo",
                clock.UtcNow,
                WorkerProviderKind: ProviderKind.OpenAICodexCli));
        var verification = new TaskVerificationRecord(
            "codex exec",
            "C:\\repo",
            1,
            "Files written.",
            dirtyGuardStderr,
            clock.UtcNow);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.DirtyWorktreeRecoverable, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify dirty worker output before usage limit text")]
    public void ClassifyDirtyWorkerOutputBeforeUsageLimitText()
    {
        const string dirtyGuardStderr =
            "Developer/Tester dispatch exited 0 but left the worktree dirty. " +
            "branch=goal/abc; head=def; worktree=dirty; commits_after_dispatch=0; " +
            "status_short=M src/Foo.cs. Rate limit reached; please try again in 42s.";

        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Dirty usage text test goal");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(
                "codex-cli",
                "codex exec",
                "C:\\repo",
                clock.UtcNow,
                WorkerProviderKind: ProviderKind.OpenAICodexCli));
        var verification = new TaskVerificationRecord(
            "codex exec",
            "C:\\repo",
            1,
            "WORKER_RESULT:\nfiles: src/Foo.cs\ntests: pass\nblockers: none\nEND_WORKER_RESULT",
            dirtyGuardStderr,
            clock.UtcNow);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.DirtyWorktreeRecoverable, outcome.Kind);
        Xunit.Assert.NotEqual(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.False(DispatchFailureClassifier.HasRecoverableSubscriptionLimitHistory(task));
    }

    [Xunit.Fact(DisplayName = "Classify returns UnknownFailure for unrecognized nonzero exit")]
    public void ClassifyUnknownFailure()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(),
            Verification(1, "Something unexpected happened.", "Internal error details."));

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    }
}
