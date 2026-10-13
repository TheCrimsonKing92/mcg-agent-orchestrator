using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Uses completed log/exit fixtures and the real recording path. No paid worker is started.
public sealed class SubscriptionVerificationCompletionTests : WorkerDispatchTestSupport
{
    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void ExitZeroIntroductionDoesNotCompleteOrAdmitAcceptance(AgentRole role)
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-12T22:16:55Z"));
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(root, role,
            "I will inspect the candidate and report the evidence.", "", clock,
            workerName: "grok-cli", workerProviderKind: ProviderKind.XaiGrokCli);
        var runner = new BackgroundDispatchRunner(clock, isStillRunning: _ => false);
        var outcome = runner.ReconcileLatestProcess(kernel, goal.Id, task.Id);
        Assert.NotNull(outcome.Verification); // Prove the completed subscription path was reached.
        Assert.Equal(0, outcome.Verification!.ExitCode);
        Assert.Equal(ProviderKind.XaiGrokCli, task.LastDispatch!.WorkerProviderKind);
        Assert.Equal(process.StartedAt, outcome.Verification.DispatchStartedAt);
        BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, task.Id, outcome);
        AssertIncomplete(kernel, goal, task);
        Assert.Equal(VerificationTerminalContractStatus.Missing,
            WorkerResultBlockers.AssessSubscriptionVerification(task, Assert.Single(task.VerificationHistory)).Status);
        Assert.DoesNotContain(task.VerificationHistory, record =>
            record.StandardError.Contains("required relevant file-change evidence"));
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void StderrAndLooseContractsCannotRepairCurrentStdout(AgentRole role)
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-12T22:16:55Z"));
        var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(root, role,
            "Partial current introduction.", Contract(role), clock,
            workerName: "grok-cli", workerProviderKind: ProviderKind.XaiGrokCli);
        File.WriteAllText(Path.Combine(task.LastDispatch!.WorkingDirectory, "WORKER_RESULT.md"), Contract(role));
        new BackgroundDispatchRunner(clock, isStillRunning: _ => false).RefreshLatestProcess(kernel, goal.Id, task.Id);
        AssertIncomplete(kernel, goal, task);
        Assert.False(Assert.Single(task.VerificationHistory).WorkerResultPresent);
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void WrapperFailureCannotReconcileFromRetainedContract(AgentRole role)
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-12T22:16:55Z"));
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(root, role,
            "Partial current introduction.", Contract(role), clock,
            workerName: "grok-cli", workerProviderKind: ProviderKind.XaiGrokCli, childExitCode: 0);
        File.WriteAllText(process.ExitCodePath, "1");
        var runner = new BackgroundDispatchRunner(clock, isStillRunning: _ => false);
        var outcome = runner.ReconcileLatestProcess(kernel, goal.Id, task.Id);
        Assert.NotNull(outcome.Verification);
        Assert.False(outcome.Verification!.ReconciledToSuccess);
        BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, task.Id, outcome);
        AssertIncomplete(kernel, goal, task);
    }

    [Fact]
    public void FailedProviderTurnCannotBeReconciledFromChildZeroAndValidContract()
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-12T22:16:55Z"));
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(root, AgentRole.Reviewer,
            Contract(AgentRole.Reviewer), "{\"type\":\"turn.failed\",\"error\":{\"message\":\"permission cancelled\"}}", clock,
            workerName: "codex-cli", workerProviderKind: ProviderKind.OpenAICodexCli, childExitCode: 0);
        File.WriteAllText(process.ExitCodePath, "1");
        var outcome = new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
            .ReconcileLatestProcess(kernel, goal.Id, task.Id);
        Assert.NotNull(outcome.Verification);
        Assert.False(outcome.Verification!.ReconciledToSuccess);
        BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, task.Id, outcome);
        AssertIncomplete(kernel, goal, task);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Contains("turn.failed", Assert.Single(task.VerificationHistory).AuthoritativeStandardError!);
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void ValidNoChangeContractCompletesWithReceiptReference(AgentRole role)
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-12T22:16:55Z"));
        var gateReceipt = Path.Combine(root, "fixture-gate.trx");
        // Fixture-owned deterministic gate evidence; the worker refers to it rather than claiming a launch.
        File.WriteAllText(gateReceipt, "<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" passed=\"1\" failed=\"0\" /></ResultSummary></TestRun>");
        var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(root, role,
            Contract(role, role == AgentRole.Tester ? $"pass - {gateReceipt}; Passed: 1" : null), "", clock,
            workerName: "grok-cli", workerProviderKind: ProviderKind.XaiGrokCli);
        new BackgroundDispatchRunner(clock, isStillRunning: _ => false).RefreshLatestProcess(kernel, goal.Id, task.Id);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.True(task.LastVerification!.CompletionVerdictVerifiedSuccess);
        Assert.True(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
        Assert.True(AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal));
        Assert.Contains(role == AgentRole.Tester ? gateReceipt : "files: none", task.LastVerification.AuthoritativeStandardOutput!);
        // Re-reading a reused log must not replace the captured report during replay.
        File.WriteAllText(task.LastProcess!.StandardOutputPath, "WORKER_RESULT:\nblockers: obsolete neighboring attempt");
        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        Assert.True(restored.BuildVerificationGate(goal.Id).IsSatisfied);
        Assert.True(AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(restored.GetGoal(goal.Id)));
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void FalsePersistedVerdictCannotPassRestoredGatesOrDuplicateReplay(AgentRole role)
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-12T22:16:55Z"));
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(root, role,
            "Partial introduction.", "", clock, workerName: "grok-cli", workerProviderKind: ProviderKind.XaiGrokCli);
        var snapshot = kernel.ExportSnapshot();
        var stored = new TaskVerificationSnapshot(process.Command, process.WorkingDirectory, 0,
            "Partial introduction.", "", clock.UtcNow.AddSeconds(1),
            StandardOutputPath: process.StandardOutputPath, DispatchStartedAt: process.StartedAt,
            AuthoritativeStandardOutput: "Partial introduction.", CompletionVerdictVerifiedSuccess: true,
            CompletionVerdictRule: "succeeded-dispatch-completion-evidence");
        var goalSnapshot = snapshot.Goals.Single();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goalSnapshot with { Tasks = [goalSnapshot.Tasks.Single() with
                { Status = WorkTaskStatus.Completed, LastVerification = stored, VerificationHistory = [stored] }] }]
        });
        goal = kernel.GetGoal(goal.Id);
        task = goal.Tasks.Single();
        Assert.False(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
        Assert.False(AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, task.LastVerification!);
        Assert.False(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
        Assert.False(AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal));
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void DelayedOutcomeCannotReplaceNewProcessWithoutContextTelemetry(AgentRole role)
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-12T22:16:55Z"));
        var (kernel, goal, task, oldProcess) = CreateCompletedGoalWorktreeDispatch(root, role,
            Contract(role), "", clock, workerName: "grok-cli", workerProviderKind: ProviderKind.XaiGrokCli);
        var oldOutcome = new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
            .ReconcileLatestProcess(kernel, goal.Id, task.Id);
        Assert.NotNull(oldOutcome.Verification);
        Assert.Equal(VerificationTerminalContractStatus.Valid,
            WorkerResultBlockers.AssessSubscriptionVerification(task, oldOutcome.Verification!).Status);
        Assert.Null(oldOutcome.DispatchAttemptAt); // No context-package telemetry guard in this fixture.
        var oldDispatch = task.LastDispatch!;
        kernel.RecordTaskProcessCancelled(goal.Id, task.Id,
            oldProcess with { WasCancelled = true, CompletedAt = clock.UtcNow, ExitCode = 1 });
        kernel.RetryTask(goal.Id, task.Id, "New attempt.");
        var next = task.LatestRetryAt!.Value.AddMinutes(1);
        kernel.RecordTaskDispatch(goal.Id, task.Id, oldDispatch with { DispatchedAt = next });
        var newProcess = oldProcess with { StartedAt = next, StandardOutputPath = oldProcess.StandardOutputPath + ".new" };
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, newProcess);
        BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, task.Id, oldOutcome);
        Assert.Equal(newProcess, task.LastProcess);
        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.Null(task.LastVerification);
        Assert.Empty(task.VerificationHistory);
        Assert.Null(task.LastDispatch!.ResultCommit);
    }

    private static void AssertIncomplete(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        Assert.NotEqual(WorkTaskStatus.Completed, task.Status);
        Assert.DoesNotContain(task.VerificationHistory, record => record.CompletionVerdictVerifiedSuccess);
        Assert.False(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
        Assert.False(AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal));
        Assert.NotEqual(GoalStatus.Verified, goal.Status);
        Assert.Equal(0, kernel.BuildGoalAcceptanceSummary(goal.Id).PassedTasks);
    }

    private static string Contract(AgentRole role, string? tests = null) =>
        "WORKER_RESULT:\nfiles: none\ncommands: inspected receipt\ntests: " +
        (tests ?? (role == AgentRole.Tester ? "pass - fixture-gate.trx; Passed: 1" : "deferred - AcceptanceTests")) +
        "\ncommit: none\nblockers: none\nmodel_fit: fixture\nskills: none\nconfidence: high\n" +
        (role == AgentRole.Reviewer ? "findings: []\ntouched_anchors: []\ncriteria_verdicts: []\nverdict: pass\n" : "") +
        "END_WORKER_RESULT";
}
