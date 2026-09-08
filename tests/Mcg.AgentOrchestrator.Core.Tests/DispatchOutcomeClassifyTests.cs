using Mcg.AgentOrchestrator.Core;

public sealed class DispatchOutcomeClassifyTests
{
    private static WorkerContextPackageReceipt EarlyConvergenceReceipt(string candidateSha)
    {
        var evidenceHash = new string('a', 64);
        return new WorkerContextPackageReceipt(
            "ctxpkg-test",
            [new WorkerContextSectionReceipt(
                $"goal/review-finding-receipts/{evidenceHash}.json",
                1,
                1,
                evidenceHash,
                ContextDeliveryMode.OnDemandFile,
                ContextContractVersion.V1.Value,
                [AgentRole.Developer])],
            ProviderUsageValue.Unknown("test"),
            ProviderUsageValue.Unknown("test"),
            ProviderUsageValue.Unknown("test"),
            EarlyConvergenceEligible: true,
            EarlyConvergenceCandidateSha: candidateSha,
            EarlyConvergenceReceiptHashes: [evidenceHash]);
    }

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

    private static TaskSpec SubscriptionTaskWithFailedVerification(
        AgentRole role,
        TaskVerificationRecord verification)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Classify subscription verification test goal");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(t => t.RequiredRole == role);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(
                "codex-cli",
                verification.Command,
                verification.WorkingDirectory,
                clock.UtcNow,
                WorkerProviderKind: ProviderKind.OpenAICodexCli));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);
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

    private static TaskSpec RetryTaskWithBaseCommit(
        string baseCommit,
        AgentRole role = AgentRole.Developer,
        bool includeContextReceipt = true)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Classify verify-only retry test goal");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(t => t.RequiredRole == role);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(
                "codex-cli",
                "codex exec prompt",
                "C:\\repo",
                clock.UtcNow,
                WorkerProviderKind: ProviderKind.OpenAICodexCli,
                ContextPackageReceipt: includeContextReceipt ? EarlyConvergenceReceipt(baseCommit) : null));
        kernel.RecordDispatchBaseCommit(goal.Id, task.Id, baseCommit);
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["gate-failure feedback: rerun receipts against current branch"]);
        return task;
    }

    private static TaskSpec RecoveredTaskWithBaseCommit(string baseCommit)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Classify operator recover test goal");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        kernel.RetryTask(goal.Id, task.Id, "operator recover: re-dispatch the developer round");
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(
                "codex-cli",
                "codex exec prompt",
                "C:\\repo",
                clock.UtcNow,
                WorkerProviderKind: ProviderKind.OpenAICodexCli,
                ContextPackageReceipt: EarlyConvergenceReceipt(baseCommit)));
        kernel.RecordDispatchBaseCommit(goal.Id, task.Id, baseCommit);
        return task;
    }

    private static TaskSpec FirstDispatchTaskWithBaseCommit(string baseCommit)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Classify first dispatch test goal");
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
                WorkerProviderKind: ProviderKind.OpenAICodexCli,
                ContextPackageReceipt: EarlyConvergenceReceipt(baseCommit)));
        kernel.RecordDispatchBaseCommit(goal.Id, task.Id, baseCommit);
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
        bool hasCommittedChanges = false,
        string standardError = "") =>
        new(
            "cmd",
            "C:\\repo",
            exitCode,
            stdout,
            standardError,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: hasCommittedChanges,
            HeartbeatStandardOutputBytes: stdout.Length);

    private static string WorkerResultStdout(
        string tests,
        string blockers = "none",
        string? deferrals = null,
        bool? assignedScopeComplete = null) =>
        $"WORKER_RESULT:{Environment.NewLine}" +
        $"files: none{Environment.NewLine}" +
        $"tests: {tests}{Environment.NewLine}" +
        (deferrals is null ? string.Empty : $"deferrals: {deferrals}{Environment.NewLine}") +
        (assignedScopeComplete is null ? string.Empty : $"assigned_scope_complete: {assignedScopeComplete.Value.ToString().ToLowerInvariant()}{Environment.NewLine}") +
        $"blockers: {blockers}{Environment.NewLine}" +
        "END_WORKER_RESULT";

    private static string WorkerResultStdoutWithCommit(string tests, string commit, string blockers = "none") =>
        $"WORKER_RESULT:{Environment.NewLine}" +
        $"files: none{Environment.NewLine}" +
        $"tests: {tests}{Environment.NewLine}" +
        $"commit: {commit}{Environment.NewLine}" +
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

    [Xunit.Fact]
    public void ClassifyIgnoresStderrDeferralWhenStdoutIsUnrecognized()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(AgentRole.Tester),
            WorkerResultVerification(
                0,
                WorkerResultStdout("unknown - no test host was available"),
                standardError: WorkerResultStdout("deferred - stale launcher prompt")));

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Contains("rule=unknown-failure", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Theory(DisplayName = "Classify Tester WORKER_RESULT blocker as real failure regardless of process exit")]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(1)]
    public void ClassifyTesterWorkerResultBlocker(int exitCode)
    {
        const string blocker = "worker-sweep-command-name-safety-list";
        var verification = WorkerResultVerification(
            exitCode,
            WorkerResultStdout("pass - focused verification completed", blocker));
        if (exitCode != 0)
        {
            verification = verification with { ProviderFailureKind = ProviderFailureKind.RateLimit };
        }

        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(AgentRole.Tester),
            verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Xunit.Assert.Equal(exitCode, outcome.ExitCode);
        Xunit.Assert.Equal(TaskOutcomeClass.RealFailure, outcome.OutcomeClass);
        Xunit.Assert.Contains("rule=tester-worker-result-blocker", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains($"blockers: {blocker}", outcome.EvidenceSummary, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("verdict=VerifiedSuccess", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("rule=subscription-limit", outcome.ClassifierReceipt, StringComparison.Ordinal);
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

    [Xunit.Fact(DisplayName = "Classify completes verify-only retry round with existing commit receipt")]
    public void ClassifyCompletesVerifyOnlyRetryRoundWithExistingCommitReceipt()
    {
        const string existingCommit = "48422231916172e8d172a0cc0428d13d222c071c";
        var outcome = DispatchFailureClassifier.Classify(
            RetryTaskWithBaseCommit(existingCommit),
            WorkerResultVerification(WorkerResultStdoutWithCommit(
                "pass - DispatchOutcomeClassifyTests 4/4 passed; build-check exit code 0",
                existingCommit)));

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.None, outcome.RecoveryRecommendation);
        Xunit.Assert.Contains("rule=verified-no-new-commit", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("verified-no-new-commit", outcome.EvidenceSummary, StringComparison.Ordinal);
        Xunit.Assert.Contains(existingCommit, outcome.EvidenceSummary, StringComparison.Ordinal);
        Xunit.Assert.Contains("evidence=verified-no-new-commit", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains(existingCommit, outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("rule=retry-round-produced-no-commit-and-no-deferral", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Classify_RetryDeveloperVerifiedNoChange_Completes()
    {
        const string baseCommit = "48422231916172e8d172a0cc0428d13d222c071c";
        var verification = WorkerResultVerification(
            1,
            WorkerResultStdout("pass - focused verification completed"),
            standardError: VerifiedNoChangeDiagnostics(verificationRecognized: true));

        var outcome = DispatchFailureClassifier.Classify(
            RetryTaskWithBaseCommit(baseCommit),
            verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.None, outcome.RecoveryRecommendation);
        Xunit.Assert.Contains("rule=verified-no-change-round", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("verdict=VerifiedSuccess", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Classify_RetryDeveloperVerifiedNoChangeWithoutConvergenceReceipt_Fails()
    {
        const string baseCommit = "48422231916172e8d172a0cc0428d13d222c071c";
        var verification = WorkerResultVerification(
            1,
            WorkerResultStdout("pass - focused verification completed"),
            standardError: VerifiedNoChangeDiagnostics(verificationRecognized: true));

        var outcome = DispatchFailureClassifier.Classify(
            RetryTaskWithBaseCommit(baseCommit, includeContextReceipt: false),
            verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.DoesNotContain("rule=verified-no-change-round", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Classify_RetryDeveloperDeferredNoChange_Fails()
    {
        const string baseCommit = "48422231916172e8d172a0cc0428d13d222c071c";
        var verification = WorkerResultVerification(
            1,
            WorkerResultStdout("deferred - acceptance gate owns the out-of-scope check"),
            standardError: VerifiedNoChangeDiagnostics(verificationRecognized: true));

        var outcome = DispatchFailureClassifier.Classify(RetryTaskWithBaseCommit(baseCommit), verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.DoesNotContain("rule=verified-no-change-round", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Classify_OperatorRecoverDeveloperVerifiedNoChange_Completes()
    {
        const string baseCommit = "48422231916172e8d172a0cc0428d13d222c071c";
        var task = RecoveredTaskWithBaseCommit(baseCommit);
        var verification = WorkerResultVerification(
            1,
            WorkerResultStdout("pass - focused verification completed"),
            standardError: VerifiedNoChangeDiagnostics(verificationRecognized: true));

        Xunit.Assert.Equal(0, task.CriterionRetryCount);
        Xunit.Assert.Empty(task.CriterionRetryFeedback);
        Xunit.Assert.NotNull(task.LatestRetryAt);
        Xunit.Assert.Equal(baseCommit, task.LastDispatch?.BaseCommit);

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.None, outcome.RecoveryRecommendation);
        Xunit.Assert.Contains("rule=verified-no-change-round", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("verdict=VerifiedSuccess", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Classify_OperatorRecoverWithoutRecognizedVerification_Fails()
    {
        var verification = WorkerResultVerification(
            1,
            WorkerResultStdout("pass - focused verification completed"),
            standardError: VerifiedNoChangeDiagnostics(verificationRecognized: false));

        var outcome = DispatchFailureClassifier.Classify(
            RecoveredTaskWithBaseCommit("48422231916172e8d172a0cc0428d13d222c071c"),
            verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Contains("rule=required-file-change-evidence-missing", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("rule=verified-no-change-round", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Classify_OperatorRecoverVerifiedNoChangeWithBlocker_Fails()
    {
        var verification = WorkerResultVerification(
            1,
            WorkerResultStdout("pass - focused verification completed", "exact-blocker - dependency unavailable"),
            standardError: VerifiedNoChangeDiagnostics(verificationRecognized: true));

        var outcome = DispatchFailureClassifier.Classify(
            RecoveredTaskWithBaseCommit("48422231916172e8d172a0cc0428d13d222c071c"),
            verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Contains("rule=required-file-change-evidence-missing", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("rule=verified-no-change-round", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Classify_FirstDispatchDeveloperVerifiedNoChange_Fails()
    {
        var verification = WorkerResultVerification(
            1,
            WorkerResultStdout("deferred - acceptance gate owns the out-of-scope check"),
            standardError: VerifiedNoChangeDiagnostics(verificationRecognized: true));

        var outcome = DispatchFailureClassifier.Classify(
            FirstDispatchTaskWithBaseCommit("48422231916172e8d172a0cc0428d13d222c071c"),
            verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.DoesNotContain("rule=verified-no-change-round", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Classify_NoChangeWithoutRecognizedVerification_Fails()
    {
        var verification = WorkerResultVerification(
            1,
            WorkerResultStdout("pass - focused verification completed"),
            standardError: VerifiedNoChangeDiagnostics(verificationRecognized: false));

        var outcome = DispatchFailureClassifier.Classify(
            RetryTaskWithBaseCommit("48422231916172e8d172a0cc0428d13d222c071c"),
            verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Contains("rule=required-file-change-evidence-missing", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("rule=verified-no-change-round", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Classify_NoChangeWithoutAuthoredMarker_Fails()
    {
        var verification = WorkerResultVerification(
            1,
            WorkerResultStdout("pass - focused verification completed"),
            standardError: DispatchRejectionDiagnosticMarker.Format(true, 0, "none"));

        var outcome = DispatchFailureClassifier.Classify(
            RetryTaskWithBaseCommit("48422231916172e8d172a0cc0428d13d222c071c"),
            verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Contains("rule=unknown-failure", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("rule=verified-no-change-round", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Classify_VerifiedNoChangeWithBlocker_Fails()
    {
        var verification = WorkerResultVerification(
            1,
            WorkerResultStdout("pass - focused verification completed", "exact-blocker - dependency unavailable"),
            standardError: VerifiedNoChangeDiagnostics(verificationRecognized: true));

        var outcome = DispatchFailureClassifier.Classify(
            RetryTaskWithBaseCommit("48422231916172e8d172a0cc0428d13d222c071c"),
            verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Contains("rule=required-file-change-evidence-missing", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("rule=verified-no-change-round", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Classify_VerifiedNoChangeWithFailingTests_Fails()
    {
        var verification = WorkerResultVerification(
            1,
            WorkerResultStdout("fail - focused verification failed"),
            standardError: VerifiedNoChangeDiagnostics(verificationRecognized: true));

        var outcome = DispatchFailureClassifier.Classify(
            RetryTaskWithBaseCommit("48422231916172e8d172a0cc0428d13d222c071c"),
            verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Contains("rule=required-file-change-evidence-missing", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("rule=verified-no-change-round", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    private static string VerifiedNoChangeDiagnostics(bool verificationRecognized) =>
        DispatchRejectionDiagnosticMarker.Format(verificationRecognized, 0, "none") + Environment.NewLine +
        DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.RequiredFileChangeEvidenceMissing);

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

    [Xunit.Fact(DisplayName = "Classify_completes_retry_developer_round_with_tests_deferred_token")]
    public void ClassifyCompletesRetryDeveloperRoundWithTestsDeferredToken()
    {
        var outcome = DispatchFailureClassifier.Classify(
            RetryTask(AgentRole.Developer),
            WorkerResultVerification(WorkerResultStdout("deferred - external verification requires operator follow-up")));

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

    [Xunit.Fact(DisplayName = "Classify_ignores_failure_words_after_structured_pass_tests_token")]
    public void ClassifyIgnoresFailureWordsAfterStructuredPassTestsToken()
    {
        var verification = WorkerResultVerification(
            WorkerResultStdout("pass - retry previously failed and timed out before this successful rerun"),
            hasCommittedChanges: false);

        var outcome = DispatchFailureClassifier.Classify(
            DispatchedTaskWithResultCommit("29edee5c", "ce5e35c1"),
            verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.Contains("rule=committed-worker-result-evidence", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("rule=succeeded-worker-result-failing-tests", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify_does_not_treat_blank_blockers_as_explicit_none")]
    public void ClassifyDoesNotTreatBlankBlockersAsExplicitNone()
    {
        var verification = WorkerResultVerification(
            WorkerResultStdout("pass - focused tests passed", blockers: string.Empty),
            hasCommittedChanges: false);

        var outcome = DispatchFailureClassifier.Classify(
            DispatchedTaskWithResultCommit("29edee5c", "ce5e35c1"),
            verification);

        Xunit.Assert.DoesNotContain("rule=committed-worker-result-evidence", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("worker_result=present(blockers=none)", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify_fails_exit_zero_worker_result_with_structured_fail_tests_token")]
    public void ClassifyFailsExitZeroWorkerResultWithStructuredFailTestsToken()
    {
        var outcome = DispatchFailureClassifier.Classify(
            DispatchedTaskWithResultCommit("29edee5c", "ce5e35c1"),
            WorkerResultVerification(WorkerResultStdout("fail - 1 test failed after commit")));

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Contains("rule=succeeded-worker-result-failing-tests", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("commit=orchestrator", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify routes incomplete Developer scope to bounded revision before every success branch")]
    public void ClassifyRoutesIncompleteDeveloperScopeToBoundedRevision()
    {
        var verification = WorkerResultVerification(
            WorkerResultStdout(
                "pass - focused verification completed",
                assignedScopeComplete: false),
            hasCommittedChanges: true);

        var outcome = DispatchFailureClassifier.Classify(
            DispatchedTaskWithResultCommit("29edee5c", "ce5e35c1"),
            verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Xunit.Assert.Contains("rule=incomplete-scope-declaration", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify gives failing tests precedence over incomplete Developer scope")]
    public void ClassifyGivesFailingTestsPrecedenceOverIncompleteDeveloperScope()
    {
        var outcome = DispatchFailureClassifier.Classify(
            DispatchedTaskWithResultCommit("29edee5c", "ce5e35c1"),
            WorkerResultVerification(WorkerResultStdout(
                "fail - 1 test failed",
                assignedScopeComplete: false)));

        Xunit.Assert.Contains("rule=succeeded-worker-result-failing-tests", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("rule=incomplete-scope-declaration", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify rejects malformed Developer scope declaration without treating it as incomplete")]
    public void ClassifyRejectsMalformedDeveloperScopeDeclaration()
    {
        var outcome = DispatchFailureClassifier.Classify(
            DispatchedTaskWithResultCommit("29edee5c", "ce5e35c1"),
            WorkerResultVerification(WorkerResultStdout("pass - focused verification completed")
                .Replace("blockers: none", "assigned_scope_complete: maybe")));

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Xunit.Assert.DoesNotContain("rule=incomplete-scope-declaration", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify_does_not_apply_failing_tests_rule_to_read_only_roles")]
    public void ClassifyDoesNotApplyFailingTestsRuleToReadOnlyRoles()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(AgentRole.Reviewer),
            WorkerResultVerification(WorkerResultStdout("fail - review found issues")));

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.DoesNotContain("rule=succeeded-worker-result-failing-tests", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify reads WORKER_RESULT blockers from stdout artifact")]
    public void ClassifyReadsWorkerResultBlockersFromStdoutArtifact()
    {
        var stdoutPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                stdoutPath,
                WorkerResultStdout("pass - dotnet test --filter DispatchOutcomeClassifyTests passed", "none"));
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

    [Xunit.Fact(DisplayName = "Classify reads WORKER_RESULT fields from middle of large stdout artifact")]
    public void ClassifyReadsWorkerResultFieldsFromMiddleOfLargeStdoutArtifact()
    {
        var stdoutPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                stdoutPath,
                new string('A', VerificationTextBounds.PreviewHeadChars) +
                Environment.NewLine +
                WorkerResultStdoutWithCommit("pass - focused checks passed", "ce5e35c1") +
                Environment.NewLine +
                new string('Z', VerificationTextBounds.PreviewTailChars));
            var retainedStdout = VerificationTextBounds.BoundText(File.ReadAllText(stdoutPath), stdoutPath);
            var verification = new TaskVerificationRecord(
                "cmd",
                "C:\\repo",
                0,
                retainedStdout,
                string.Empty,
                DateTimeOffset.UtcNow,
                StandardOutputPath: stdoutPath,
                WorkerResultPresent: true,
                HeartbeatStandardOutputBytes: 0);

            var outcome = DispatchFailureClassifier.Classify(
                RetryTaskWithBaseCommit("ce5e35c1"),
                verification);

            Xunit.Assert.DoesNotContain("WORKER_RESULT", verification.StandardOutput, StringComparison.Ordinal);
            Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
            Xunit.Assert.Contains("rule=verified-no-new-commit", outcome.ClassifierReceipt, StringComparison.Ordinal);
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

    [Xunit.Fact(DisplayName = "Classify committed worker evidence with orchestrator diagnostic as verified success")]
    public void ClassifyCommittedWorkerEvidenceWithOrchestratorDiagnosticAsVerifiedSuccess()
    {
        var verification = WorkerResultVerification(
            0,
            WorkerResultStdout("pass - committed candidate verified"),
            hasCommittedChanges: true) with
        {
            OrchestratorFailureReason = "hung-worker detector preserved for diagnosis"
        };

        var outcome = DispatchFailureClassifier.Classify(SimpleTask(AgentRole.Developer), verification);

        Xunit.Assert.False(verification.Succeeded);
        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.Contains("rule=committed-worker-result-evidence", outcome.ClassifierReceipt, StringComparison.Ordinal);
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
            "Rate limit reached for gpt-5.5. Please try again in 42s.", // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
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
            Verification(1, "", "ERROR: Rate limit reached for gpt-5.5. Please try again in 42s.")); // Deliberate fixture text pins historical/parser behavior independently of the live catalog.

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

    [Xunit.Fact(DisplayName = "Classify returns RecoverableSubscriptionLimit for provider HTTP 429 stdout")]
    public void ClassifyRecoverableSubscriptionLimitFromHttp429Stdout()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SubscriptionTask(),
            Verification(1, "ERROR: provider returned 429 Too Many Requests."));

        Xunit.Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Xunit.Assert.Null(outcome.RetryAfter);
        Xunit.Assert.Contains("429 Too Many Requests", outcome.EvidenceSummary);
        Xunit.Assert.Contains("evidence=ERROR: provider returned 429 Too Many Requests.", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify does not treat bare 429 duration as rate limit")]
    public void ClassifyDoesNotTreatBare429DurationAsRateLimit()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SubscriptionTask(),
            Verification(1, "", "Worker process exited after 429ms without provider HTTP status context."));

        Xunit.Assert.NotEqual(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.DoesNotContain("provider-rate-limit", outcome.ClassifierReceipt, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "Classify ignores typed rate limit without provider stream evidence")]
    public void ClassifyIgnoresTypedRateLimitWithoutProviderStreamEvidence()
    {
        const string stderr =
            "sandbox-preflight: low-integrity sandbox prepared\n" +
            "src/ProviderParser.cs:77: text.Contains(\"usage limit\", StringComparison.OrdinalIgnoreCase)\n" +
            "src/ProviderParser.cs:78: text.Contains(\"rate limit\", StringComparison.OrdinalIgnoreCase)\n" +
            "src/ProviderParser.cs:79: text.Contains(\"429\", StringComparison.OrdinalIgnoreCase)";
        var verification = Verification(1, "", stderr) with { ProviderFailureKind = ProviderFailureKind.RateLimit };

        var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), verification);

        Xunit.Assert.NotEqual(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.False(DispatchFailureClassifier.IsRecoverableSubscriptionLimitFailure(verification));
        Xunit.Assert.False(DispatchFailureClassifier.HasRecoverableSubscriptionLimitEvidence(verification));
        Xunit.Assert.DoesNotContain("rule=subscription-limit", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("rule=provider-rate-limit", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify preserves genuine codex usage limit provider footer")]
    public void ClassifyPreservesGenuineCodexUsageLimitProviderFooter()
    {
        const string stderr = "ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again later.";
        var verification = Verification(1, "", stderr) with { ProviderFailureKind = ProviderFailureKind.RateLimit };

        var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.True(DispatchFailureClassifier.IsRecoverableSubscriptionLimitFailure(verification));
        Xunit.Assert.Contains("rule=subscription-limit", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("You've hit your usage limit", outcome.EvidenceSummary, StringComparison.Ordinal);
        Xunit.Assert.Contains("evidence=ERROR: You've hit your usage limit", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Theory(DisplayName = "Classify ignores standalone worker-authored limit lookalikes")]
    [Xunit.InlineData("{\"event\":\"usage_limit_reached\",\"retry_after_seconds\":120}")]
    [Xunit.InlineData("rate limit reached while describing a fixture")]
    public void ClassifyIgnoresStandaloneWorkerAuthoredLimitLookalikes(string stderr)
    {
        var verification = Verification(1, "", stderr) with { ProviderFailureKind = ProviderFailureKind.RateLimit };

        var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), verification);

        Xunit.Assert.NotEqual(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.False(DispatchFailureClassifier.HasRecoverableSubscriptionLimitEvidence(verification));
        Xunit.Assert.DoesNotContain("rule=subscription-limit", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("rule=provider-rate-limit", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify preserves structured provider usage limit event")]
    public void ClassifyPreservesStructuredProviderUsageLimitEvent()
    {
        const string stderr = "ERROR: {\"event\":\"usage_limit_reached\",\"retry_after_seconds\":120}";
        var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), Verification(1, "", stderr));

        Xunit.Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.Contains("usage_limit_reached", outcome.EvidenceSummary, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify ignores sandbox preflight JSON with generic status 429")]
    public void ClassifyIgnoresSandboxPreflightJsonWithGenericStatus429()
    {
        const string stderr =
            "{\"event\":\"sandbox-prep\",\"phase\":\"launch-preflight\",\"status\":429}\n" +
            "powershell.exe: ParserError: Unexpected token '}' in expression.";
        var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), Verification(1, "", stderr));

        Xunit.Assert.NotEqual(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.Contains("rule=real-failure", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify treats scripting stderr after launch preflight as real failure")]
    public void ClassifyTreatsScriptingStderrAfterLaunchPreflightAsRealFailure()
    {
        const string stderr =
            "{\"event\":\"sandbox-prep\",\"phase\":\"launch-preflight\",\"elapsedMs\":200}\n" +
            "WORKER_RESULT assembly failed before final marker\n" +
            "Implemented classifier fixture setup and reviewed source snippets.\n" +
            "src/ProviderParser.cs:77: text.Contains(\"rate limit\", StringComparison.OrdinalIgnoreCase)\n" +
            "powershell.exe: ParserError: Missing closing quote in command argument.\n" +
            "At line:1 char:42";
        var verification = Verification(1, "", stderr) with { ProviderFailureKind = ProviderFailureKind.RateLimit };

        var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), verification);
        var classification = TaskOutcomeClassifier.Classify(WorkTaskStatus.Failed, TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Xunit.Assert.Equal(TaskOutcomeClass.RealFailure, classification.Class);
        Xunit.Assert.Contains("rule=real-failure", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("powershell.exe: ParserError", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("rule=subscription-limit", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("rule=preflight-failure", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("rule=empty-output-flake", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify preserves scripting stderr artifact tail in real failures")]
    public void ClassifyPreservesScriptingStderrArtifactTailInRealFailures()
    {
        var stderrPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                stderrPath,
                "Worker produced extensive retry evidence before process failure.\n" +
                "Reviewed fixture output and replayed focused classifier checks.\n" +
                "powershell.exe: ParserError: Missing closing quote in command argument.\n" +
                "At line:1 char:42");
            var verification = new TaskVerificationRecord(
                "cmd",
                "C:\\repo",
                1,
                string.Empty,
                "worker stderr was captured in the artifact path",
                DateTimeOffset.UtcNow,
                StandardErrorPath: stderrPath,
                ProviderFailureKind: ProviderFailureKind.RateLimit);

            var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), verification);

            Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
            Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
            Xunit.Assert.Contains("rule=real-failure", outcome.ClassifierReceipt, StringComparison.Ordinal);
            Xunit.Assert.Contains("outcome_class=real-failure", outcome.ClassifierReceipt, StringComparison.Ordinal);
            Xunit.Assert.Contains("powershell.exe: ParserError", outcome.ClassifierReceipt, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("rule=subscription-limit", outcome.ClassifierReceipt, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(stderrPath);
        }
    }

    [Xunit.Fact(DisplayName = "Classify auth evidence before connectivity or rate limit")]
    public void ClassifyAuthEvidenceBeforeConnectivityOrRateLimit()
    {
        const string stderr =
            "ERROR: Your access token could not be refreshed because your refresh token was already used.\n" +
            "ERROR: websocket closed with HTTP 401 while connecting to provider endpoint.\n" +
            "ERROR: status: 429";

        var first = DispatchFailureClassifier.Classify(SubscriptionTask(), Verification(1, "", stderr));
        var second = DispatchFailureClassifier.Classify(SubscriptionTask(), Verification(1, "", stderr));

        Xunit.Assert.Equal(DispatchOutcomeKind.ProviderAuthentication, first.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, first.RecoveryRecommendation);
        Xunit.Assert.NotEqual(RecoveryRecommendation.AutoRetry, first.RecoveryRecommendation);
        Xunit.Assert.NotEqual(DispatchOutcomeKind.RecoverableSubscriptionLimit, first.Kind);
        Xunit.Assert.NotEqual(DispatchOutcomeKind.ProviderConnectivity, first.Kind);
        Xunit.Assert.Equal(first.Kind, second.Kind);
        Xunit.Assert.Equal(first.RecoveryRecommendation, second.RecoveryRecommendation);
        Xunit.Assert.Contains("rule=provider-authentication", first.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("evidence=provider-authentication: ERROR: Your access token could not be refreshed", first.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("remediation=codex login / provider re-auth", first.EvidenceSummary, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify auth evidence before typed connectivity")]
    public void ClassifyAuthEvidenceBeforeTypedConnectivity()
    {
        const string stderr =
            "ERROR: Your access token could not be refreshed because your refresh token was already used.\n" +
            "ERROR: stream disconnected while connecting to provider endpoint.";
        var verification = Verification(1, "", stderr) with { ProviderFailureKind = ProviderFailureKind.Connectivity };

        var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.ProviderAuthentication, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Xunit.Assert.NotEqual(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Xunit.Assert.DoesNotContain("rule=provider-connectivity", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("rule=provider-authentication", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("refresh token was already used", outcome.EvidenceSummary, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify ignores typed connectivity without explicit connectivity evidence")]
    public void ClassifyIgnoresTypedConnectivityWithoutExplicitConnectivityEvidence()
    {
        const string stderr =
            "ERROR: Your access token could not be refreshed because your refresh token was already used.\n" +
            "ERROR: websocket closed with HTTP 401 while connecting to provider endpoint.";
        var verification = Verification(1, "", stderr) with { ProviderFailureKind = ProviderFailureKind.Connectivity };

        var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.ProviderAuthentication, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Xunit.Assert.DoesNotContain("rule=provider-connectivity", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("rule=provider-authentication", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("refresh token was already used", outcome.EvidenceSummary, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify useful worker evidence before typed rate limit")]
    public void ClassifyUsefulWorkerEvidenceBeforeTypedRateLimit()
    {
        const string stdout =
            "WORKER_RESULT:\n" +
            "files: src/Changed.cs\n" +
            "tests: pass - focused tests passed\n" +
            "blockers: none\n" +
            "END_WORKER_RESULT";
        const string stderr = "ERROR: provider returned HTTP 429 Too Many Requests.";
        var verification = new TaskVerificationRecord(
            "cmd",
            "C:\\repo",
            1,
            stdout,
            stderr,
            DateTimeOffset.UtcNow,
            ProviderFailureKind: ProviderFailureKind.RateLimit,
            WorkerResultPresent: true,
            HasCommittedChanges: true,
            HeartbeatStandardOutputBytes: stdout.Length);

        var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.None, outcome.RecoveryRecommendation);
        Xunit.Assert.DoesNotContain("rule=provider-rate-limit", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("rule=subscription-limit", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify defaults recoverable subscription limit retry when duration is absent")]
    public void ClassifyRecoverableSubscriptionLimitDefaultsWhenDurationAbsent()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SubscriptionTask(),
            Verification(1, "", "ERROR: Provider quota exceeded for this account."));

        Xunit.Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Xunit.Assert.Null(outcome.RetryAfter);
        Xunit.Assert.Contains("quota exceeded", outcome.EvidenceSummary);
    }

    [Xunit.Fact(DisplayName = "Classify ignores provider signatures inside worker displayed source lines")]
    public void ClassifyIgnoresProviderSignaturesInsideWorkerDisplayedSourceLines()
    {
        const string stderr =
            "183: catch (UnauthorizedAccessException ex)\n" +
            "184: var status = \"429 Too Many Requests\";\n" +
            "185 | logger.LogError(\"stream disconnected\");\n" +
            "    rate limit reached; retry after 42s\n" +
            "    quota exceeded while reading fixture text";

        var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), Verification(1, "", stderr));

        Xunit.Assert.NotEqual(DispatchOutcomeKind.ProviderAuthentication, outcome.Kind);
        Xunit.Assert.NotEqual(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.NotEqual(DispatchOutcomeKind.ProviderConnectivity, outcome.Kind);
        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Contains("rule=unknown-failure", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("outcome_class=unknown-era", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("rule=subscription-limit", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Theory(DisplayName = "Classify preserves genuine CLI provider diagnostics with evidence")]
    [Xunit.InlineData(
        "ERROR: 401 Unauthorized. Invalid API key.",
        DispatchOutcomeKind.ProviderAuthentication,
        "evidence=provider-authentication: ERROR: 401 Unauthorized. Invalid API key.")]
    [Xunit.InlineData(
        "ERROR: provider returned 429 Too Many Requests.",
        DispatchOutcomeKind.RecoverableSubscriptionLimit,
        "evidence=ERROR: provider returned 429 Too Many Requests.")]
    [Xunit.InlineData(
        "2026-07-14T13:15:00Z ERROR codex_core: stream disconnected while reading response.",
        DispatchOutcomeKind.ProviderConnectivity,
        "evidence=2026-07-14T13:15:00Z ERROR codex_core: stream disconnected")]
    public void ClassifyPreservesGenuineCliProviderDiagnosticsWithEvidence(
        string stderr,
        DispatchOutcomeKind expectedKind,
        string expectedEvidence)
    {
        var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), Verification(1, "", stderr));

        Xunit.Assert.Equal(expectedKind, outcome.Kind);
        Xunit.Assert.Contains(expectedEvidence, outcome.ClassifierReceipt, StringComparison.Ordinal);
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

    [Xunit.Fact(DisplayName = "Classify returns LaunchFailure for short nonzero exit with both streams empty")]
    public void ClassifySilentLaunchFailure()
    {
        var completedAt = DateTimeOffset.Parse("2026-08-03T03:22:50Z");
        var verification = new TaskVerificationRecord(
            "claude -p --model claude-opus-5 --permission-mode plan",
            "C:\\repo",
            1,
            string.Empty,
            string.Empty,
            completedAt,
            DispatchStartedAt: completedAt - TimeSpan.FromSeconds(51),
            ChildProcessId: 32164,
            ChildExitCode: 23);

        var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.LaunchFailure, outcome.Kind);
        Xunit.Assert.NotEqual(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Xunit.Assert.True(outcome.HasZeroByteOutput);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Xunit.Assert.Contains("rule=silent-launch-failure", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("duration_ms=51000", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("root_exit_code=1", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("child_exit_code=23", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify preserves worker failure when either redirected stream has output")]
    public void ClassifyWorkerFailureWithOutputDoesNotRetryAsLaunchFailure()
    {
        var completedAt = DateTimeOffset.Parse("2026-08-03T03:22:50Z");
        var verification = new TaskVerificationRecord(
            "claude -p",
            "C:\\repo",
            1,
            string.Empty,
            "worker reported a genuine failure",
            completedAt,
            DispatchStartedAt: completedAt - TimeSpan.FromSeconds(27));

        var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.NotEqual(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify preserves long-running nonzero empty-output retry")]
    public void ClassifyLongRunningEmptyFailurePreservesEmptyOutputRetry()
    {
        var completedAt = DateTimeOffset.Parse("2026-08-03T03:22:50Z");
        var verification = new TaskVerificationRecord(
            "claude -p",
            "C:\\repo",
            1,
            string.Empty,
            string.Empty,
            completedAt,
            DispatchStartedAt: completedAt - DispatchFailureClassifier.SilentLaunchFailureMaxDuration - TimeSpan.FromSeconds(1));

        var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.EmptyOutputFlake, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify ignores bookkeeping stderr when identifying a silent launch failure")]
    public void ClassifySilentLaunchFailureIgnoresBookkeepingStderr()
    {
        var completedAt = DateTimeOffset.Parse("2026-08-03T03:22:50Z");
        var verification = new TaskVerificationRecord(
            "claude -p",
            "C:\\repo",
            1,
            string.Empty,
            "RESOURCE phase=dispatch cpu_ms=3015 accounting_source=snapshot",
            completedAt,
            DispatchStartedAt: completedAt - TimeSpan.FromSeconds(27));

        var outcome = DispatchFailureClassifier.Classify(SubscriptionTask(), verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.LaunchFailure, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify preserves injected dirty-worktree diagnostics over empty stderr file")]
    public void ClassifyDirtyGuardDiagnosticDoesNotBecomeLaunchFailure()
    {
        var stderrPath = Path.GetTempFileName();
        try
        {
            var completedAt = DateTimeOffset.Parse("2026-08-03T03:22:50Z");
            var verification = new TaskVerificationRecord(
                "codex exec",
                "C:\\repo",
                1,
                string.Empty,
                "Developer/Tester dispatch exited 0 but left the worktree dirty; status_short=M src/file.cs.",
                completedAt,
                StandardErrorPath: stderrPath,
                DispatchStartedAt: completedAt - TimeSpan.FromSeconds(27));

            Xunit.Assert.False(DispatchFailureClassifier.IsSilentLaunchFailure(verification));
        }
        finally
        {
            File.Delete(stderrPath);
        }
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

    [Xunit.Fact(DisplayName = "Sandbox1312 producer paths emit launch-failure environmental classification")]
    public void Sandbox1312ProducerPathsEmitLaunchFailureEnvironmentalClassification()
    {
        var direct = DispatchFailureClassifier.ClassifyProviderFailure(
            ProviderFailureKind.Sandbox1312,
            1,
            hasZeroByteOutput: false,
            "sandbox denied the operation");
        var dispatch = DispatchFailureClassifier.Classify(
            SimpleTask(),
            Verification(1, "worker output", "provider failure") with
            {
                ProviderFailureKind = ProviderFailureKind.Sandbox1312
            });

        Xunit.Assert.Equal(DispatchOutcomeKind.LaunchFailure, direct.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, direct.RecoveryRecommendation);
        Xunit.Assert.Equal("provider-sandbox-launch-1312", TaskOutcomeClassifier.TryExtractRule(direct.ClassifierReceipt));
        Xunit.Assert.Equal(
            TaskOutcomeClassifier.TryExtractRule(direct.ClassifierReceipt),
            TaskOutcomeClassifier.TryExtractRule(dispatch.ClassifierReceipt));
        Xunit.Assert.Equal(TaskOutcomeClass.Environmental, direct.OutcomeClass);
        Xunit.Assert.Equal(direct.OutcomeClass, dispatch.OutcomeClass);
        Xunit.Assert.Equal(direct.OutcomeClass, TaskOutcomeClassifier.TryExtractClass(dispatch.ClassifierReceipt));
    }

    [Xunit.Fact(DisplayName = "Classify reads sandbox commit evidence from middle of large log artifacts")]
    public void ClassifyReadsSandboxCommitEvidenceFromMiddleOfLargeLogArtifacts()
    {
        var stdoutPath = Path.GetTempFileName();
        var stderrPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                stdoutPath,
                new string('A', VerificationTextBounds.PreviewHeadChars) +
                Environment.NewLine +
                "WORKER_RESULT:\nfiles: src/Foo.cs\ncommands: dotnet build\ntests: Passed: 3\nEND_WORKER_RESULT\n" +
                new string('Z', VerificationTextBounds.PreviewTailChars));
            File.WriteAllText(
                stderrPath,
                new string('E', VerificationTextBounds.PreviewHeadChars) +
                Environment.NewLine +
                "fatal: Unable to create '.git/index.lock': Permission denied\n" +
                new string('R', VerificationTextBounds.PreviewTailChars));
            var verification = new TaskVerificationRecord(
                "cmd",
                "C:\\repo",
                1,
                VerificationTextBounds.BoundText(File.ReadAllText(stdoutPath), stdoutPath),
                VerificationTextBounds.BoundText(File.ReadAllText(stderrPath), stderrPath),
                DateTimeOffset.UtcNow,
                StandardOutputPath: stdoutPath,
                StandardErrorPath: stderrPath);

            var outcome = DispatchFailureClassifier.Classify(SimpleTask(), verification);

            Xunit.Assert.DoesNotContain("WORKER_RESULT", verification.StandardOutput, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("index.lock", verification.StandardError, StringComparison.Ordinal);
            Xunit.Assert.Equal(DispatchOutcomeKind.SandboxCommitBlocked, outcome.Kind);
            Xunit.Assert.Equal(RecoveryRecommendation.CommitAndVerify, outcome.RecoveryRecommendation);
        }
        finally
        {
            File.Delete(stdoutPath);
            File.Delete(stderrPath);
        }
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
            Verification(1, "Connecting to API...", "ERROR: 401 Unauthorized. Invalid API key."));

        Xunit.Assert.Equal(DispatchOutcomeKind.ProviderAuthentication, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify returns ProviderConnectivity for connection refused output")]
    public void ClassifyProviderConnectivity()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(),
            Verification(1, "Connecting to API...", "ERROR: Unable to connect to API. ECONNREFUSED 127.0.0.1:443"));

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

    [Xunit.Fact(DisplayName = "Classify keeps deterministic Planner contract failure out of ProviderModelRejection")]
    public void ClassifyPlannerOutputContractFailure()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(AgentRole.Planner),
            Verification(
                1,
                "Connecting to API...",
                "Planner output contract failed: model-home target citation 'models/gpt-5.6-sol' does not exist and is not marked as a new file. Retry Planner for contract repair.")); // Deliberate fixture text pins historical/parser behavior independently of the live catalog.

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Contains("Planner output contract failed", outcome.EvidenceSummary, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify ignores quoted Planner contract prose when provider rejection is authoritative")]
    public void ClassifyProviderModelRejectionWithQuotedPlannerContractProse()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(AgentRole.Planner),
            Verification(
                1,
                "Connecting to API...",
                "Diagnostic text may quote 'Planner output contract failed: missing required evidence'.\n" +
                "Error: unknown model 'claude-xxx-4-99'. Model not supported."));

        Xunit.Assert.Equal(DispatchOutcomeKind.ProviderModelRejection, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify ignores non-authoritative Planner contract prose in stdout")]
    public void ClassifyProviderModelRejectionWithQuotedPlannerContractProseInStandardOutput()
    {
        var verification = Verification(
            1,
            "Diagnostic text may quote 'Planner output contract failed: missing required evidence'.",
            "Error: unknown model 'claude-xxx-4-99'. Model not supported.");

        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(AgentRole.Planner),
            verification);

        Xunit.Assert.True(DispatchFailureClassifier.IsRecoverableProviderModelRejectionFailure(verification));
        Xunit.Assert.Equal(DispatchOutcomeKind.ProviderModelRejection, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact]
    public void ProviderModelRejectionDetectionUsesIndependentProviderLine()
    {
        var verification = Verification(
            1,
            "Connecting to API...",
            "Diagnostic text may quote 'Planner output contract failed: missing required evidence'.\n" +
            "Error: unknown model 'claude-xxx-4-99'. Model not supported.");

        Xunit.Assert.True(DispatchFailureClassifier.IsRecoverableProviderModelRejectionFailure(verification));
    }

    [Xunit.Fact(DisplayName = "Classify honors independent provider rejection beside authoritative Planner contract diagnostic")]
    public void ClassifyProviderModelRejectionBesideAuthoritativePlannerContractFailure()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(AgentRole.Planner),
            Verification(
                1,
                "Connecting to API...",
                "Planner output contract failed: missing required evidence. Retry Planner for contract repair.\n" +
                "Error: unknown model 'claude-xxx-4-99'. Model not supported."));

        Xunit.Assert.Equal(DispatchOutcomeKind.ProviderModelRejection, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact(DisplayName = "Classify detects stdout provider rejection beside stderr Planner contract diagnostic")]
    public void ClassifyStdoutProviderModelRejectionBesideStderrPlannerContractFailure()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(AgentRole.Planner),
            Verification(
                1,
                "ERROR: invalid model 'gpt-5.3-codex' does not exist for this account.", // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
                "Planner output contract failed: missing required evidence. Retry Planner for contract repair."));

        Xunit.Assert.Equal(DispatchOutcomeKind.ProviderModelRejection, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    }

    [Xunit.Fact]
    public void ProviderModelRejectionHistoryUsesIndependentProviderLine()
    {
        var task = SubscriptionTaskWithFailedVerification(
            AgentRole.Planner,
            Verification(
                1,
                "ERROR: requested model gpt-5.6-sol is not supported.", // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
                "Planner output contract failed: missing required evidence. Retry Planner for contract repair."));

        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Xunit.Assert.NotNull(task.LastVerification);
        Xunit.Assert.True(DispatchFailureClassifier.IsRecoverableProviderModelRejectionFailure(task.LastVerification));
        Xunit.Assert.True(DispatchFailureClassifier.HasRecoverableProviderModelRejectionFailure(task));
    }

    [Xunit.Fact]
    public void ProviderModelRejectionCountUsesIndependentProviderLine()
    {
        var task = SubscriptionTaskWithFailedVerification(
            AgentRole.Planner,
            Verification(
                1,
                "ERROR: requested model gpt-5.6-sol is not supported.", // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
                "Planner output contract failed: missing required evidence. Retry Planner for contract repair."));

        Xunit.Assert.Single(task.VerificationHistory);
        Xunit.Assert.True(DispatchFailureClassifier.IsRecoverableProviderModelRejectionFailure(task.VerificationHistory[0]));
        Xunit.Assert.Equal(1, DispatchFailureClassifier.CountRecoverableProviderModelRejectionFailures(task));
    }

    [Xunit.Fact(DisplayName = "Classify reads log path for verification evidence outside retained excerpt")]
    public void ClassifyReadsLogPathForVerificationEvidenceOutsideRetainedExcerpt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-classify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var stdoutPath = Path.Combine(root, "out.log");
            var fullStdout =
                new string('A', VerificationTextBounds.PreviewHeadChars) +
                "\nTest run successful: 12 tests passed\n" +
                new string('Z', VerificationTextBounds.PreviewTailChars);
            File.WriteAllText(stdoutPath, fullStdout);
            var retainedStdout = VerificationTextBounds.BoundText(fullStdout, stdoutPath);
            var verification = new TaskVerificationRecord(
                "cmd",
                root,
                0,
                retainedStdout,
                string.Empty,
                DateTimeOffset.UtcNow,
                StandardOutputPath: stdoutPath);

            var outcome = DispatchFailureClassifier.Classify(SimpleTask(AgentRole.Reviewer), verification);

            Xunit.Assert.DoesNotContain("Test run successful", verification.StandardOutput, StringComparison.Ordinal);
            Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
            Xunit.Assert.Contains("Test run successful", outcome.EvidenceSummary, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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
            Verification(
                1,
                "Something unexpected happened.",
                "Internal error details.\n" +
                DispatchFailureDiagnosticMarker.Format("unregistered-extension-code")));

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Xunit.Assert.Equal(TaskOutcomeClass.UnknownEra, outcome.OutcomeClass);
        Xunit.Assert.Contains("rule=unknown-failure", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("Internal error details.", outcome.EvidenceSummary, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(DispatchFailureDiagnosticMarker.Prefix, outcome.EvidenceSummary, StringComparison.Ordinal);
    }

    // Completion sites carry markers. The dirty-worktree diagnostic is intentionally absent: every real dirty-worktree
    // diagnostic is claimed earlier by dirty-dispatch-recovery, which preserves its existing behavior.
    private static readonly (string Site, AgentRole Role, string Rule, string Reason)[] OrchestratorAuthoredFailures =
    [
        ("1187", AgentRole.Researcher, "researcher-output-contract-rejected", "Researcher output contract rejected the captured research."),
        ("1197", AgentRole.Researcher, "researcher-artifact-persistence-failed", "Researcher output contract could not persist the accepted research artifact."),
        ("1217", AgentRole.Planner, "planner-output-contract-rejected", "Planner output contract rejected the captured plan."),
        ("1231", AgentRole.Planner, "planner-plan-persistence-failed", "Planner output contract could not persist the accepted plan."),
        ("1280", AgentRole.Developer, "worker-build-check-failed", "Deterministic worker build check failed."),
        ("missing-build-evidence", AgentRole.Developer, "worker-build-evidence-missing", "Required worker build evidence was not reported."),
        ("wrapper-exit", AgentRole.Tester, "wrapper-process-exit-failure", "Wrapper process exited nonzero after the selected child succeeded, without usable completion evidence."),
        ("1376", AgentRole.Developer, "required-file-change-evidence-missing", "Developer/Tester dispatch did not produce required relevant file-change evidence."),
        ("1402", AgentRole.Developer, "worktree-inspection-failed", "Completed dispatch worktree inspection failed.")
    ];

    public static Xunit.TheoryData<string, AgentRole, string, string> OrchestratorAuthoredFailureCases
    {
        get
        {
            var cases = new Xunit.TheoryData<string, AgentRole, string, string>();
            foreach (var (site, role, rule, reason) in OrchestratorAuthoredFailures)
            {
                cases.Add(site, role, rule, reason);
            }

            return cases;
        }
    }

    [Xunit.Theory(DisplayName = "Classify names each orchestrator-authored completion failure without changing behavior")]
    [Xunit.MemberData(nameof(OrchestratorAuthoredFailureCases))]
    public void ClassifyNamesOrchestratorAuthoredCompletionFailure(
        string site,
        AgentRole role,
        string expectedRule,
        string expectedReason)
    {
        var verification = Verification(
            1,
            "Worker output",
            $"Existing human diagnostic for site {site}.\n{DispatchFailureDiagnosticMarker.Format(expectedRule)}");

        var outcome = DispatchFailureClassifier.Classify(SimpleTask(role), verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Equal(1, outcome.ExitCode);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Xunit.Assert.Equal(TaskOutcomeClass.UnknownEra, outcome.OutcomeClass);
        Xunit.Assert.Contains($"rule={expectedRule}", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.StartsWith(expectedReason, outcome.EvidenceSummary, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Existing human diagnostic for site {site}.", outcome.EvidenceSummary, StringComparison.Ordinal);
        Xunit.Assert.DoesNotMatch("(?i)exit code \\d+|failed with code \\d+", expectedReason);
    }

    [Xunit.Fact(DisplayName = "Classify uses the first orchestrator-authored marker when multiple markers coexist")]
    public void ClassifyUsesFirstOrchestratorAuthoredMarker()
    {
        var first = OrchestratorAuthoredFailures.Single(
            failure => failure.Rule == DispatchFailureDiagnosticMarker.WrapperProcessExitFailure);
        var second = OrchestratorAuthoredFailures.Single(
            failure => failure.Rule == DispatchFailureDiagnosticMarker.RequiredFileChangeEvidenceMissing);
        var verification = Verification(
            1,
            "Worker output",
            "The wrapper exited nonzero before file-change evidence was evaluated.\n" +
            DispatchFailureDiagnosticMarker.Format(first.Rule) + "\n" +
            DispatchFailureDiagnosticMarker.Format(second.Rule));

        var outcome = DispatchFailureClassifier.Classify(SimpleTask(first.Role), verification);

        Xunit.Assert.Contains($"rule={first.Rule}", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain($"rule={second.Rule}", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.StartsWith(first.Reason, outcome.EvidenceSummary, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Orchestrator-authored markers do not displace an already-named outcome")]
    public void OrchestratorAuthoredMarkersDoNotDisplaceAlreadyNamedOutcome()
    {
        Xunit.Assert.Equal(9, OrchestratorAuthoredFailures.Length);
        Xunit.Assert.Equal(
            OrchestratorAuthoredFailures.Length,
            OrchestratorAuthoredFailures.Select(failure => failure.Rule).Distinct(StringComparer.Ordinal).Count());

        foreach (var (_, role, expectedRule, _) in OrchestratorAuthoredFailures)
        {
            Xunit.Assert.Matches("^[a-z0-9]+(?:-[a-z0-9]+)*$", expectedRule);
            var verification = Verification(
                1,
                "Connecting to API...",
                "Error: unknown model 'claude-xxx-4-99'. Model not supported.\n" +
                DispatchFailureDiagnosticMarker.Format(expectedRule));

            var outcome = DispatchFailureClassifier.Classify(SimpleTask(role), verification);

            Xunit.Assert.Equal(DispatchOutcomeKind.ProviderModelRejection, outcome.Kind);
            Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
            Xunit.Assert.Equal(TaskOutcomeClass.Environmental, outcome.OutcomeClass);
            Xunit.Assert.Contains("rule=provider-model-rejection", outcome.ClassifierReceipt, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain($"rule={expectedRule}", outcome.ClassifierReceipt, StringComparison.Ordinal);
        }
    }

    [Xunit.Fact(DisplayName = "Classify treats structured Tester inconclusive as first class outcome despite stale blocker")]
    public void ClassifyTreatsStructuredTesterInconclusiveAsFirstClassOutcomeDespiteStaleBlocker()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(AgentRole.Tester),
            WorkerResultVerification(
                WorkerResultStdout(
                    "inconclusive - command timed out; no TRX",
                    "stale product blocker from a prior round")));

        Xunit.Assert.Equal(DispatchOutcomeKind.VerificationInconclusive, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Xunit.Assert.Contains("command timed out; no TRX", outcome.EvidenceSummary, StringComparison.Ordinal);
        Xunit.Assert.Contains("schema conflict", outcome.EvidenceSummary, StringComparison.Ordinal);
        Xunit.Assert.NotEqual(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
    }

    [Xunit.Fact(DisplayName = "Classify keeps Tester inconclusive precedence over conflicting blocker")]
    public void ClassifyKeepsTesterInconclusivePrecedenceOverConflictingBlocker()
    {
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(AgentRole.Tester),
            WorkerResultVerification(
                WorkerResultStdout(
                    "inconclusive - focused assertion did not finish",
                    "product behavior returns the wrong value")));

        Xunit.Assert.Equal(DispatchOutcomeKind.VerificationInconclusive, outcome.Kind);
        Xunit.Assert.Contains("schema conflict retained for audit", outcome.EvidenceSummary, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify binds structured Tester outcome to latest WORKER_RESULT")]
    public void ClassifyBindsStructuredTesterOutcomeToLatestWorkerResult()
    {
        var output =
            WorkerResultStdout("pass - stale first result", "none") +
            Environment.NewLine +
            WorkerResultStdout("inconclusive - latest command timed out; no TRX", "none");
        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(AgentRole.Tester),
            WorkerResultVerification(output));

        Xunit.Assert.Equal(DispatchOutcomeKind.VerificationInconclusive, outcome.Kind);
        Xunit.Assert.Contains("latest command timed out; no TRX", outcome.EvidenceSummary, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("stale first result", outcome.EvidenceSummary, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Classify field-scan Tester inconclusive without opener")]
    public void ClassifyFieldScanTesterInconclusiveWithoutOpener()
    {
        const string output = """
            files: none
            commands: dotnet test --no-build --filter Focused
            tests: inconclusive - command timed out; no TRX
            blockers: none
            model_fit: OpenAI/test - adequate - verification - sufficient
            skills: dotnet-windows-build-hygiene
            confidence: high
            """;

        var outcome = DispatchFailureClassifier.Classify(
            SimpleTask(AgentRole.Tester),
            WorkerResultVerification(output));

        Xunit.Assert.Equal(DispatchOutcomeKind.VerificationInconclusive, outcome.Kind);
    }

    [Xunit.Fact(DisplayName = "Classify dirty Tester inconclusive keeps dirty guard precedence")]
    public void ClassifyDirtyTesterInconclusiveKeepsDirtyGuardPrecedence()
    {
        const string dirtyGuardStderr =
            "Developer/Tester dispatch exited 0 but left the worktree dirty. " +
            "branch=goal/abc; head=def; worktree=dirty; commits_after_dispatch=0; " +
            "status_short=M src/Foo.cs.";
        var verification = WorkerResultVerification(
            WorkerResultStdout("inconclusive - command timed out; no TRX")) with
        {
            ExitCode = 1,
            StandardError = dirtyGuardStderr
        };
        var task = SimpleTask(AgentRole.Tester);
        task.RecordVerification(verification);

        var outcome = DispatchFailureClassifier.Classify(
            task,
            verification);

        Xunit.Assert.Equal(DispatchOutcomeKind.DirtyWorktreeRecoverable, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    }
}
