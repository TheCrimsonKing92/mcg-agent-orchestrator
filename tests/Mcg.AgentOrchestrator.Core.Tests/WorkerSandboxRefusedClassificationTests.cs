using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each case owns its in-memory kernel; no process or filesystem I/O.
public sealed class WorkerSandboxRefusedClassificationTests
{
    private const string Refusal = @"WORKER_SANDBOX_REFUSED reason=shared-git-metadata-low-writable path=C:\repo-shared\objects";

    [Fact]
    public void BareStderrRefusalRequiresOperatorWithReasonAndPath()
    {
        var outcome = DispatchFailureClassifier.Classify(CliTask(), Verification(stderr: Refusal));

        AssertRefusal(outcome);
        Assert.True(outcome.HasZeroByteOutput);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal(@"worker sandbox refused: reason=shared-git-metadata-low-writable path=C:\repo-shared\objects",
            outcome.EvidenceSummary);
        Assert.Contains("shared-git-metadata-low-writable", outcome.EvidenceSummary, StringComparison.Ordinal);
        Assert.Contains(@"C:\repo-shared\objects", outcome.EvidenceSummary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("writable-sandbox-outside-linked-worktree")]
    [InlineData("shared-git-metadata-low-writable")]
    [InlineData("shared-git-metadata-unverifiable")]
    public void EachGuardReasonUsesEnvironmentalOperatorRule(string reason)
    {
        var outcome = DispatchFailureClassifier.Classify(CliTask(),
            Verification(stderr: $@"WORKER_SANDBOX_REFUSED reason={reason} path=C:\repo-shared\worktree"));

        AssertRefusal(outcome);
        Assert.Equal($@"worker sandbox refused: reason={reason} path=C:\repo-shared\worktree", outcome.EvidenceSummary);
    }

    [Fact]
    public void HostFramedInnerUnauthorizedExceptionStaysSandboxRefusal()
    {
        const string stderr = "[dispatch-host] worker launch/run failed: " +
            "Mcg.AgentOrchestrator.Infrastructure.WorkerSandboxRefusedException: " +
            @"WORKER_SANDBOX_REFUSED reason=shared-git-metadata-unverifiable path=C:\repo shared\.git" +
            " ---> System.UnauthorizedAccessException: Access denied\n   at Guard.Check()";

        var outcome = DispatchFailureClassifier.Classify(CliTask(), Verification(stderr: stderr));

        AssertRefusal(outcome);
        Assert.Equal(@"worker sandbox refused: reason=shared-git-metadata-unverifiable path=C:\repo shared\.git",
            outcome.EvidenceSummary);
        Assert.DoesNotContain("codex login", outcome.EvidenceSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstStderrRefusalWinsOverLaterLinesAndStdout()
    {
        var outcome = DispatchFailureClassifier.Classify(CliTask(), Verification(
            stdout: "WORKER_SANDBOX_REFUSED reason=stdout path=other",
            stderr: "unrelated log\r\n\t" + Refusal + "\r\nWORKER_SANDBOX_REFUSED reason=later path=other"));

        AssertRefusal(outcome);
        Assert.Equal(@"worker sandbox refused: reason=shared-git-metadata-low-writable path=C:\repo-shared\objects",
            outcome.EvidenceSummary);
    }

    [Fact]
    public void StdoutOnlyRefusalUsesTheSameRule()
    {
        var outcome = DispatchFailureClassifier.Classify(CliTask(), Verification(stdout: Refusal));

        AssertRefusal(outcome);
        Assert.Contains(@"C:\repo-shared\objects", outcome.EvidenceSummary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WORKER_SANDBOX_REFUSED reason=future-code path=C:\\new path", "worker sandbox refused: reason=future-code path=C:\\new path")]
    [InlineData("WORKER_SANDBOX_REFUSED reason=future-code", "worker sandbox refused: reason=future-code; refusal=WORKER_SANDBOX_REFUSED reason=future-code")]
    public void FutureReasonOrMissingPathStillRequiresOperator(string line, string expected)
    {
        var outcome = DispatchFailureClassifier.Classify(CliTask(), Verification(stderr: line));

        AssertRefusal(outcome);
        Assert.Equal(expected, outcome.EvidenceSummary);
    }

    [Theory]
    [InlineData("log: saw WORKER_SANDBOX_REFUSED reason=x path=y")]
    [InlineData("WORKER_SANDBOX_REFUSED_OTHER reason=x path=y")]
    [InlineData("worker_sandbox_refused reason=x path=y")]
    [InlineData("WORKER_SANDBOX_REFUSED path=y")]
    [InlineData("[dispatch-host] worker launch/run failed: OtherException: WORKER_SANDBOX_REFUSED reason=x path=y")]
    public void ProseAndOtherPrefixesKeepUnknownFailure(string line)
    {
        var outcome = DispatchFailureClassifier.Classify(CliTask(), Verification(stderr: line));

        Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Assert.Equal("unknown-failure", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(TaskOutcomeClass.UnknownEra, outcome.OutcomeClass);
    }

    [Fact]
    public void SuccessfulExitDoesNotAdmitRefusal()
    {
        var outcome = DispatchFailureClassifier.Classify(CliTask(), Verification(stderr: Refusal, exitCode: 0));

        Assert.NotEqual("worker-sandbox-refused", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
    }

    [Fact]
    public void WorkerResultPreventsQuotedRefusalFromTakingOver()
    {
        var verification = Verification(stderr: Refusal) with { WorkerResultPresent = true };

        var outcome = DispatchFailureClassifier.Classify(CliTask(), verification);

        Assert.NotEqual("worker-sandbox-refused", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
    }

    [Theory]
    [InlineData("", DispatchOutcomeKind.LaunchFailure, RecoveryRecommendation.AutoRetry, "silent-launch-failure")]
    [InlineData("Unrelated failure", DispatchOutcomeKind.UnknownFailure, RecoveryRecommendation.OperatorNeeded, "unknown-failure")]
    [InlineData("ERROR: Your access token could not be refreshed. Run codex login.", DispatchOutcomeKind.ProviderAuthentication, RecoveryRecommendation.OperatorNeeded, "provider-authentication")]
    public void NoPrefixKeepsExistingRule(string stderr, DispatchOutcomeKind kind,
        RecoveryRecommendation recovery, string rule)
    {
        var outcome = DispatchFailureClassifier.Classify(CliTask(), Verification(stderr: stderr));

        Assert.Equal(kind, outcome.Kind);
        Assert.Equal(recovery, outcome.RecoveryRecommendation);
        Assert.Equal(rule, TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
    }

    private static void AssertRefusal(DispatchOutcome outcome)
    {
        Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Assert.Equal(TaskOutcomeClass.Environmental, outcome.OutcomeClass);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Assert.NotEqual(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Assert.Equal("worker-sandbox-refused", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Contains("rule=worker-sandbox-refused", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    private static TaskSpec CliTask()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Classify sandbox refusal",
            [new TaskSpec(TaskId.New(), "Implement scoped behavior", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = Assert.Single(goal.Tasks);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", @"C:\repo", clock.UtcNow, WorkerProviderKind: ProviderKind.OpenAICodexCli));
        return task;
    }

    private static TaskVerificationRecord Verification(string stdout = "", string stderr = "", int exitCode = 1) =>
        new("codex exec", @"C:\repo", exitCode, stdout, stderr,
            new DateTimeOffset(2026, 10, 5, 10, 12, 56, TimeSpan.Zero),
            DispatchStartedAt: new DateTimeOffset(2026, 10, 5, 10, 12, 55, TimeSpan.Zero));
}
