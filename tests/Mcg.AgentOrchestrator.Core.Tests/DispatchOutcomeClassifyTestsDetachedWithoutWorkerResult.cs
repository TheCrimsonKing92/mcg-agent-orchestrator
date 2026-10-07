using System.Text;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: process records are in-memory data; no real process or clock is used.
public sealed class DispatchOutcomeClassifyTestsDetachedWithoutWorkerResult
{
    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Developer)]
    public void Classify_DetachedProseRound_ReturnsProviderInterruption(AgentRole role)
    {
        var (task, verification) = CompletedRound(role, detached: true);
        Assert.Equal(1248, Encoding.UTF8.GetByteCount(verification.StandardOutput));
        Assert.DoesNotContain("WORKER_RESULT", verification.StandardOutput, StringComparison.Ordinal);
        Assert.False(verification.WorkerResultPresent);

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Assert.Equal(DispatchOutcomeKind.ProviderInterruption, outcome.Kind);
        Assert.Equal("detached-without-worker-result",
            TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Assert.StartsWith(
            $"detached-without-worker-result: process {task.LastProcess!.ProcessId} was gracefully detached and exited 0 without a WORKER_RESULT block",
            outcome.EvidenceSummary, StringComparison.Ordinal);
        Assert.Equal(TaskOutcomeClass.Environmental,
            TaskOutcomeRules.Known["detached-without-worker-result"].Class);
    }

    [Theory]
    [InlineData(AgentRole.Reviewer, false)]
    [InlineData(AgentRole.Developer, false)]
    [InlineData(AgentRole.Reviewer, true)]
    [InlineData(AgentRole.Developer, true)]
    public void Classify_DetachedWorkerResult_PreservesSuccess(AgentRole role, bool flagArgument)
    {
        var (task, verification) = CompletedRound(role, detached: true);
        verification = new TaskVerificationRecord(
            verification.Command, verification.WorkingDirectory, 0,
            "WORKER_RESULT:\nfiles: none\ntests: pass - round completed\n" +
                "deferrals: none\nblockers: none\nEND_WORKER_RESULT",
            string.Empty, verification.CompletedAt, WorkerResultPresent: !flagArgument);

        var outcome = DispatchFailureClassifier.Classify(task, verification,
            workerResultPresent: flagArgument);

        Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Assert.Equal("succeeded-dispatch-completion-evidence",
            TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Developer)]
    public void Classify_NonDetachedProseRound_PreservesSuccess(AgentRole role)
    {
        var (task, verification) = CompletedRound(role, detached: false);

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Assert.Equal("succeeded-dispatch-completion-evidence",
            TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
    }

    [Fact]
    public void Classify_DetachedRunningProcess_PreservesSuccess()
    {
        var (task, verification) = CompletedRound(AgentRole.Reviewer, detached: true);
        task.RecordProcess(task.LastProcess! with { CompletedAt = null, ExitCode = null });

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Assert.Equal("succeeded-dispatch-completion-evidence",
            TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
    }

    [Fact]
    public void Classify_DetachedFailedVerification_PreservesFailureClassification()
    {
        var (task, verification) = CompletedRound(AgentRole.Reviewer, detached: true);
        verification = verification with { ExitCode = 1 };
        task.RecordProcess(task.LastProcess! with { ExitCode = 1 });
        var detachedOutcome = DispatchFailureClassifier.Classify(task, verification);
        task.RecordProcess(task.LastProcess! with { WasGracefullyDetachedByConductor = false });

        var ordinaryOutcome = DispatchFailureClassifier.Classify(task, verification);

        Assert.NotEqual("detached-without-worker-result",
            TaskOutcomeClassifier.TryExtractRule(detachedOutcome.ClassifierReceipt));
        Assert.Equal(ordinaryOutcome, detachedOutcome);
    }

    private static (TaskSpec Task, TaskVerificationRecord Verification) CompletedRound(
        AgentRole role, bool detached)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Classify detached round",
            [new TaskSpec(TaskId.New(), "Inspect round output", role)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = Assert.Single(goal.Tasks);
        const string command = "codex exec round";
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("codex-cli", command, "C:\\repo", clock.UtcNow,
                WorkerProviderKind: ProviderKind.OpenAICodexCli));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(4242, command, "C:\\repo", "out.log", "err.log", "exit.txt",
                clock.UtcNow, null, null));
        if (detached)
        {
            kernel.RecordTaskProcessGracefullyDetached(goal.Id, task.Id,
                task.LastProcess! with { WasGracefullyDetachedByConductor = true });
        }

        clock.Advance();
        kernel.RecordTaskProcessRefreshed(goal.Id, task.Id,
            task.LastProcess! with { CompletedAt = clock.UtcNow, ExitCode = 0 }, null);
        var prose = string.Concat(Enumerable.Repeat("The current source has been inspected. ", 32))
            .PadRight(1248, ' ');
        return (task, new TaskVerificationRecord(command, "C:\\repo", 0, prose,
            string.Empty, clock.UtcNow));
    }
}
