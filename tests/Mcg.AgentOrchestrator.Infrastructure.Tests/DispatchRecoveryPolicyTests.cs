using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DispatchRecoveryPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-28T12:00:00Z");

    [Xunit.Fact(DisplayName = "DispatchRecoveryPolicy_reconciles_absent_process_from_exit_artifact")]
    public void DispatchRecoveryPolicyReconcilesAbsentProcessFromExitArtifact()
    {
        var process = CreateProcess();
        File.WriteAllText(process.ExitCodePath, "0");

        var decision = CreatePolicy().Evaluate(process, hasLiveProcess: false);

        Xunit.Assert.Equal(DispatchRecoveryAction.ReconcileFromExit, decision.Action);
        Xunit.Assert.Equal("reconcile-from-exit", decision.ActionName);
        Xunit.Assert.Equal(process.ExitCodePath, decision.EvidencePath);
    }

    [Xunit.Fact(DisplayName = "DispatchRecoveryPolicy_retries_stale_no_artifact_when_budget_remains")]
    public void DispatchRecoveryPolicyRetriesStaleNoArtifactWhenBudgetRemains()
    {
        var process = CreateProcess();
        WriteHeartbeat(process, Now.AddMinutes(-10), Now.AddMinutes(-10), 0, 0, 0);

        var decision = CreatePolicy().Evaluate(process, hasLiveProcess: false, staleRetryBudgetRemaining: 1);

        Xunit.Assert.Equal(DispatchRecoveryAction.MarkStale, decision.Action);
        Xunit.Assert.Equal("mark-stale", decision.ActionName);
        Xunit.Assert.Equal(BackgroundDispatchRunner.GetHeartbeatPath(process), decision.EvidencePath);
    }

    [Xunit.Fact(DisplayName = "DispatchRecoveryPolicy_surfaces_budget_blocker_for_stale_no_artifact_when_budget_exhausted")]
    public void DispatchRecoveryPolicySurfacesBudgetBlockerForStaleNoArtifactWhenBudgetExhausted()
    {
        var process = CreateProcess();

        var decision = CreatePolicy().Evaluate(process, hasLiveProcess: false, staleRetryBudgetRemaining: 0);

        Xunit.Assert.Equal(DispatchRecoveryAction.BudgetExhausted, decision.Action);
        Xunit.Assert.Equal("budget-exhausted", decision.ActionName);
        Xunit.Assert.Equal("heartbeat-absent", decision.EvidencePath);
        Xunit.Assert.Equal("stale-dispatch retry budget exhausted", decision.Blocker);
    }

    [Xunit.Fact(DisplayName = "DispatchRecoveryPolicy_holds_live_recently_active_process")]
    public void DispatchRecoveryPolicyHoldsLiveRecentlyActiveProcess()
    {
        var process = CreateProcess();
        WriteHeartbeat(process, Now.AddMinutes(-1), Now.AddMinutes(-1), 7, 0, 12);

        var decision = CreatePolicy().Evaluate(process, hasLiveProcess: true);

        Xunit.Assert.Equal(DispatchRecoveryAction.Hold, decision.Action);
        Xunit.Assert.Equal("hold", decision.ActionName);
        Xunit.Assert.Equal(BackgroundDispatchRunner.GetHeartbeatPath(process), decision.EvidencePath);
    }

    [Xunit.Fact(DisplayName = "DispatchRecoveryPolicy_holds_live_process_with_recent_cpu_or_output_progress")]
    public void DispatchRecoveryPolicyHoldsLiveProcessWithRecentCpuOrOutputProgress()
    {
        var process = CreateProcess();
        WriteHeartbeat(process, Now.AddMinutes(-10), Now.AddMinutes(-1), 0, 0, 25_000);

        var decision = CreatePolicy().Evaluate(process, hasLiveProcess: true);

        Xunit.Assert.Equal(DispatchRecoveryAction.Hold, decision.Action);
        Xunit.Assert.Equal("hold", decision.ActionName);
        Xunit.Assert.Contains("CPU activity or output progress", decision.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "DispatchRecoveryPolicy_classifies_live_idle_past_policy")]
    public void DispatchRecoveryPolicyClassifiesLiveIdlePastPolicy()
    {
        var process = CreateProcess();
        WriteHeartbeat(process, Now.AddMinutes(-40), Now.AddMinutes(-40), 0, 0, 0);

        var decision = CreatePolicy().Evaluate(process, hasLiveProcess: true);

        Xunit.Assert.Equal(DispatchRecoveryAction.ClassifyBlocker, decision.Action);
        Xunit.Assert.Equal("classify-blocker", decision.ActionName);
        Xunit.Assert.Equal("live-idle-no-progress", decision.Blocker);
    }

    [Xunit.Fact(DisplayName = "DispatchRecoveryPolicy_does_not_hold_live_process_on_stale_cumulative_cpu")]
    public void DispatchRecoveryPolicyDoesNotHoldLiveProcessOnStaleCumulativeCpu()
    {
        var process = CreateProcess();
        WriteHeartbeat(process, Now.AddMinutes(-40), Now.AddMinutes(-40), 0, 0, 25_000);

        var decision = CreatePolicy().Evaluate(process, hasLiveProcess: true);

        Xunit.Assert.Equal(DispatchRecoveryAction.ClassifyBlocker, decision.Action);
        Xunit.Assert.Equal("classify-blocker", decision.ActionName);
        Xunit.Assert.Equal("live-idle-no-progress", decision.Blocker);
    }

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_stale_no_exit_does_not_consume_empty_output_flake_budget")]
    public void BackgroundDispatchRunnerStaleNoExitDoesNotConsumeEmptyOutputFlakeBudget()
    {
        var root = CreateTempDirectory();
        var stdout = Path.Combine(root, "out.log");
        var stderr = Path.Combine(root, "err.log");
        var exit = Path.Combine(root, "worker.exit.txt");
        File.WriteAllText(stdout, string.Empty);
        File.WriteAllText(stderr, string.Empty);
        var clock = new TestClock(Now);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Mark stale no exit");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, Now.AddMinutes(-20)));
        var process = new TaskProcessRecord(999999, "codex exec prompt", root, stdout, stderr, exit, Now.AddMinutes(-20), null, null);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);

        var outcome = new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
            .ReconcileLatestProcess(kernel, goal.Id, task.Id);
        BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, task.Id, outcome);

        Xunit.Assert.Equal(DispatchRecoveryAction.MarkStale, outcome.RecoveryDecision!.Action);
        Xunit.Assert.Equal(0, task.EmptyOutputRetryCount);
        Xunit.Assert.Contains("action='mark-stale'", task.LastVerification!.StandardError, StringComparison.Ordinal);
        Xunit.Assert.Contains("stale retry budget remaining=1", task.LastVerification.StandardError, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_stale_no_exit_surfaces_budget_exhaustion_after_stale_retry_spent")]
    public void BackgroundDispatchRunnerStaleNoExitSurfacesBudgetExhaustionAfterStaleRetrySpent()
    {
        var root = CreateTempDirectory();
        var stdout = Path.Combine(root, "out.log");
        var stderr = Path.Combine(root, "err.log");
        var exit = Path.Combine(root, "worker.exit.txt");
        File.WriteAllText(stdout, string.Empty);
        File.WriteAllText(stderr, string.Empty);
        var clock = new TestClock(Now);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Mark stale no exit");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, Now.AddMinutes(-20)));
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                "codex exec prompt",
                root,
                1,
                "",
                "Dispatch recovery policy action='mark-stale' evidence='heartbeat-absent' reason='previous stale attempt stale retry budget remaining=1'.",
                Now.AddMinutes(-10)));
        kernel.RetryTask(goal.Id, task.Id, "retry previous stale dispatch");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, Now.AddMinutes(-5)));
        var process = new TaskProcessRecord(999999, "codex exec prompt", root, stdout, stderr, exit, Now.AddMinutes(-5), null, null);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);

        var outcome = new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
            .ReconcileLatestProcess(kernel, goal.Id, task.Id);
        BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, task.Id, outcome);

        Xunit.Assert.Equal(DispatchRecoveryAction.BudgetExhausted, outcome.RecoveryDecision!.Action);
        Xunit.Assert.Equal(0, task.EmptyOutputRetryCount);
        Xunit.Assert.Contains("action='budget-exhausted'", task.LastVerification!.StandardError, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_reconciles_exit_artifact_despite_stale_child_pid_heartbeat")]
    public void BackgroundDispatchRunnerReconcilesExitArtifactDespiteStaleChildPidHeartbeat()
    {
        var root = CreateTempDirectory();
        var stdout = Path.Combine(root, "out.log");
        var stderr = Path.Combine(root, "err.log");
        var exit = Path.Combine(root, "worker.exit.txt");
        File.WriteAllText(stdout, "WORKER_RESULT:\ntests: pass\nEND_WORKER_RESULT\n");
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        var clock = new TestClock(Now);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Reconcile stale heartbeat exit");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Planner);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, Now.AddMinutes(-20)));
        var process = new TaskProcessRecord(999999, "codex exec prompt", root, stdout, stderr, exit, Now.AddMinutes(-20), null, null);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
        WriteHeartbeat(process, Now.AddMinutes(-10), Now.AddMinutes(-10), 0, 0, 0);

        var outcome = new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
            .ReconcileLatestProcess(kernel, goal.Id, task.Id);
        BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, task.Id, outcome);

        Xunit.Assert.Equal(DispatchRecoveryAction.ReconcileFromExit, outcome.RecoveryDecision!.Action);
        Xunit.Assert.Equal(0, outcome.ProcessRecord.ExitCode);
        Xunit.Assert.NotNull(task.LastVerification);
        Xunit.Assert.Contains("action='reconcile-from-exit'", task.LastVerification!.StandardError, StringComparison.Ordinal);
    }

    private static DispatchRecoveryPolicy CreatePolicy() => new(new TestClock(Now));

    private static TaskProcessRecord CreateProcess()
    {
        var root = CreateTempDirectory();
        return new TaskProcessRecord(
            999999,
            "codex exec prompt",
            root,
            Path.Combine(root, "out.log"),
            Path.Combine(root, "err.log"),
            Path.Combine(root, "worker.exit.txt"),
            Now.AddMinutes(-45),
            null,
            null);
    }

    private static void WriteHeartbeat(
        TaskProcessRecord process,
        DateTimeOffset lastObservedAt,
        DateTimeOffset lastProgressAt,
        long stdoutBytes,
        long stderrBytes,
        long ownedCpuMs)
    {
        File.WriteAllText(
            BackgroundDispatchRunner.GetHeartbeatPath(process),
            "{" +
            "\"pid\":999999," +
            "\"childPid\":888888," +
            "\"ownedPids\":[999999]," +
            $"\"startedAt\":\"{process.StartedAt:O}\"," +
            $"\"lastObservedAt\":\"{lastObservedAt:O}\"," +
            $"\"lastProgressAt\":\"{lastProgressAt:O}\"," +
            "\"state\":\"running\"," +
            $"\"stdoutBytes\":{stdoutBytes}," +
            $"\"stderrBytes\":{stderrBytes}," +
            "\"exitFileExists\":false," +
            $"\"ownedCpuMs\":{ownedCpuMs}" +
            "}");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-dispatch-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
