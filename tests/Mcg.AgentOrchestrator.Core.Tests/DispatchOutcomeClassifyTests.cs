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

    private static TaskVerificationRecord Verification(
        int exitCode,
        string stdout,
        string stderr = "") =>
        new("cmd", "C:\\repo", exitCode, stdout, stderr, DateTimeOffset.UtcNow);

    [Xunit.Fact(DisplayName = "Classify returns VerifiedSuccess for exit zero success")]
    public void ClassifyVerifiedSuccess()
    {
        var outcome = DispatchFailureClassifier.Classify(SimpleTask(), Verification(0, "All done."));

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.Equal(RecoveryRecommendation.None, outcome.RecoveryRecommendation);
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
