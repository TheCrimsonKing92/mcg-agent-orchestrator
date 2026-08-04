using Mcg.AgentOrchestrator.App.Orchestration;
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

    [Xunit.Fact(DisplayName = "DispatchRecoveryPolicy_preserves_absent_process_with_synthetic_exit_artifact")]
    public void DispatchRecoveryPolicyPreservesAbsentProcessWithSyntheticExitArtifact()
    {
        var process = CreateProcess();
        DispatchExitArtifacts.Write(
            process.ExitCodePath,
            DispatchExitArtifacts.Synthetic(1, "startup sweep interrupted worker", Now));

        var decision = CreatePolicy().Evaluate(process, hasLiveProcess: false, hasDirtyWorktreeEvidence: true);

        Xunit.Assert.Equal(DispatchRecoveryAction.PreserveInterruptedWork, decision.Action);
        Xunit.Assert.Equal("preserve-interrupted-work", decision.ActionName);
        Xunit.Assert.Equal(process.ExitCodePath, decision.EvidencePath);
        Xunit.Assert.Contains("startup sweep interrupted worker", decision.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "DispatchExitArtifacts_round_trip_native_and_classify_legacy_integer")]
    public void DispatchExitArtifactsRoundTripNativeAndClassifyLegacyInteger()
    {
        var process = CreateProcess();
        DispatchExitArtifacts.Write(
            process.ExitCodePath,
            DispatchExitArtifacts.Native(17, "worker exited", Now));

        Xunit.Assert.True(DispatchExitArtifacts.TryRead(process.ExitCodePath, out var native));
        Xunit.Assert.Equal(17, native.ExitCode);
        Xunit.Assert.Equal(DispatchExitArtifactOrigin.Native, native.Origin);

        File.WriteAllText(process.ExitCodePath, "1");
        Xunit.Assert.True(DispatchExitArtifacts.TryRead(process.ExitCodePath, out var legacy));
        Xunit.Assert.Equal(DispatchExitArtifactOrigin.UnknownLegacy, legacy.Origin);
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
        WriteWorkerResultArtifact(root);
        var clock = new TestClock(Now);
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Mark stale no exit");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Researcher);
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
        WriteWorkerResultArtifact(root);
        var clock = new TestClock(Now);
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Mark stale no exit");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Researcher);
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
        clock.Advance();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, clock.UtcNow));
        var process = new TaskProcessRecord(999999, "codex exec prompt", root, stdout, stderr, exit, clock.UtcNow, null, null);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
        clock.Advance(TimeSpan.FromMinutes(5));

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
        File.WriteAllText(stdout, WorkerDispatchTestSupport.ResearcherContractFixture());
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        var clock = new TestClock(Now);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Reconcile stale heartbeat exit");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Researcher);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, Now.AddMinutes(-20)));
        var process = new TaskProcessRecord(999999, "codex exec prompt", root, stdout, stderr, exit, Now.AddMinutes(-20), null, null);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
        WriteHeartbeat(process, Now.AddMinutes(-10), Now.AddMinutes(-10), 0, 0, 0);

        var outcome = new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
            .ReconcileLatestProcess(kernel, goal.Id, task.Id);
        BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, task.Id, outcome);

        Xunit.Assert.Equal(DispatchRecoveryAction.ReconcileFromExit, outcome.RecoveryDecision!.Action);
        Xunit.Assert.Equal(0, outcome.ProcessRecord.ExitCode);
        Xunit.Assert.Equal(DispatchExitArtifactOrigin.UnknownLegacy, outcome.ProcessRecord.ExitArtifactOrigin);
        Xunit.Assert.NotNull(task.LastVerification);
        Xunit.Assert.Contains("action='reconcile-from-exit'", task.LastVerification!.StandardError, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_invalid_exit_artifact_holds_without_manufacturing_exit_one")]
    public void BackgroundDispatchRunnerInvalidExitArtifactHoldsWithoutManufacturingExitOne()
    {
        var root = CreateTempDirectory();
        var process = new TaskProcessRecord(
            999999,
            "codex exec prompt",
            root,
            Path.Combine(root, "out.log"),
            Path.Combine(root, "err.log"),
            Path.Combine(root, "worker.exit.txt"),
            Now.AddMinutes(-20),
            null,
            null);
        File.WriteAllText(process.StandardOutputPath, string.Empty);
        File.WriteAllText(process.StandardErrorPath, string.Empty);
        File.WriteAllText(process.ExitCodePath, "partial-write");
        var kernel = new AgentOrchestratorKernel(new TestClock(Now));
        var goal = kernel.CreateGoal("Hold invalid exit receipt", [new TaskSpec(TaskId.New(), "Inspect", AgentRole.Researcher)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", process.Command, root, process.StartedAt));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);

        var runner = new BackgroundDispatchRunner(new TestClock(Now), isStillRunning: _ => false);
        var state = new DispatchStateSurface(
                new TestClock(Now),
                isProcessAlive: _ => false,
                readCommandLines: _ => new Dictionary<int, string>(),
                inspectWorktree: false)
            .Evaluate(goal.Id, task);
        var outcome = runner.ReconcileLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(DispatchStateKind.WedgedProcess, state.Kind);
        Xunit.Assert.Equal("exit-artifact-invalid", state.RecoveryDecision.Blocker);
        Xunit.Assert.Equal(DispatchRecoveryAction.Hold, outcome.RecoveryDecision!.Action);
        Xunit.Assert.Contains("state=Invalid", outcome.RecoveryDecision.Reason, StringComparison.Ordinal);
        Xunit.Assert.Contains("content=partial-write", outcome.RecoveryDecision.Reason, StringComparison.Ordinal);
        Xunit.Assert.Null(outcome.ProcessRecord.ExitCode);
        Xunit.Assert.Null(task.LastVerification);

        runner.ApplyRefreshOutcomeAndWriteDiagnostics(kernel, goal.Id, task.Id, outcome);
        Xunit.Assert.Equal(WorkTaskStatus.Running, task.Status);
        Xunit.Assert.Empty(kernel.HumanInputRequests);
        Xunit.Assert.Contains(
            kernel.GetTimeline(goal.Id),
            evt => evt.TaskId == task.Id &&
                evt.Message.Contains("DispatchApparatusHoldObserved: observation=1/2", StringComparison.Ordinal) &&
                evt.Message.Contains("blocker='exit-artifact-invalid'", StringComparison.Ordinal));

        var repeatedOutcome = runner.ReconcileLatestProcess(kernel, goal.Id, task.Id);
        runner.ApplyRefreshOutcomeAndWriteDiagnostics(kernel, goal.Id, task.Id, repeatedOutcome);

        Xunit.Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
        Xunit.Assert.Null(task.LastVerification);
        Xunit.Assert.Null(task.LastProcess!.ExitCode);
        Xunit.Assert.Null(task.LatestRetryAt);
        var request = Xunit.Assert.Single(kernel.HumanInputRequests);
        Xunit.Assert.Equal(HumanWaitKind.RecoveryChoice, request.Kind);
        Xunit.Assert.Contains("exit-artifact-invalid", request.Question, StringComparison.Ordinal);
        Xunit.Assert.Contains(process.ExitCodePath, request.Question, StringComparison.Ordinal);
        Xunit.Assert.Equal(0, runner.SweepExitedProcesses(kernel, goal.Id));
        Xunit.Assert.Equal(
            2,
            kernel.GetTimeline(goal.Id).Count(evt =>
                evt.TaskId == task.Id &&
                evt.Message.StartsWith("DispatchApparatusHoldObserved:", StringComparison.Ordinal)));
        Xunit.Assert.Single(kernel.HumanInputRequests);
    }

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_unreadable_exit_artifact_holds_with_typed_evidence")]
    public void BackgroundDispatchRunnerUnreadableExitArtifactHoldsWithTypedEvidence()
    {
        var root = CreateTempDirectory();
        var process = new TaskProcessRecord(
            999999,
            "codex exec prompt",
            root,
            Path.Combine(root, "out.log"),
            Path.Combine(root, "err.log"),
            Path.Combine(root, "worker.exit.txt"),
            Now.AddMinutes(-20),
            null,
            null);
        File.WriteAllText(process.StandardOutputPath, string.Empty);
        File.WriteAllText(process.StandardErrorPath, string.Empty);
        File.WriteAllText(process.ExitCodePath, "0");
        using var exclusiveLock = new FileStream(process.ExitCodePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var kernel = new AgentOrchestratorKernel(new TestClock(Now));
        var goal = kernel.CreateGoal("Hold unreadable exit receipt", [new TaskSpec(TaskId.New(), "Inspect", AgentRole.Researcher)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", process.Command, root, process.StartedAt));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);

        var outcome = new BackgroundDispatchRunner(new TestClock(Now), isStillRunning: _ => false)
            .ReconcileLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(DispatchRecoveryAction.Hold, outcome.RecoveryDecision!.Action);
        Xunit.Assert.Contains("state=Unreadable", outcome.RecoveryDecision.Reason, StringComparison.Ordinal);
        Xunit.Assert.Contains("IOException", outcome.RecoveryDecision.Reason, StringComparison.Ordinal);
        Xunit.Assert.Null(outcome.ProcessRecord.ExitCode);
        Xunit.Assert.Null(task.LastVerification);
    }

    [Xunit.Fact(DisplayName = "DispatchRecoveryPolicy_invalid_heartbeat_is_apparatus_hold_not_stale_budget")]
    public void DispatchRecoveryPolicyInvalidHeartbeatIsApparatusHoldNotStaleBudget()
    {
        var process = CreateProcess();
        File.WriteAllText(BackgroundDispatchRunner.GetHeartbeatPath(process), "{not-json");

        var decision = CreatePolicy().Evaluate(process, hasLiveProcess: false, staleRetryBudgetRemaining: 1);

        Xunit.Assert.Equal(DispatchRecoveryAction.Hold, decision.Action);
        Xunit.Assert.Equal("heartbeat-invalid", decision.Blocker);
        Xunit.Assert.Contains("unavailable_reason=invalid", decision.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_unavailable_worktree_inspection_holds_unknown_state")]
    public void BackgroundDispatchRunnerUnavailableWorktreeInspectionHoldsUnknownState()
    {
        var root = CreateTempDirectory();
        var missingWorktree = Path.Combine(root, "missing-worktree");
        var process = new TaskProcessRecord(
            999999,
            "codex exec prompt",
            missingWorktree,
            Path.Combine(root, "out.log"),
            Path.Combine(root, "err.log"),
            Path.Combine(root, "worker.exit.txt"),
            Now.AddMinutes(-20),
            null,
            null);
        File.WriteAllText(process.StandardOutputPath, string.Empty);
        File.WriteAllText(process.StandardErrorPath, string.Empty);
        var kernel = new AgentOrchestratorKernel(new TestClock(Now));
        var goal = kernel.CreateGoal("Hold unknown worktree state", [new TaskSpec(TaskId.New(), "Implement change", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", process.Command, missingWorktree, process.StartedAt));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);

        var outcome = new BackgroundDispatchRunner(new TestClock(Now), isStillRunning: _ => false)
            .ReconcileLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(DispatchRecoveryAction.Hold, outcome.RecoveryDecision!.Action);
        Xunit.Assert.Equal("worktree-inspection-unavailable", outcome.RecoveryDecision.Blocker);
        Xunit.Assert.Contains("worktree inspection unavailable", outcome.RecoveryDecision.Reason, StringComparison.Ordinal);
        Xunit.Assert.Contains("unavailable_reason=directory-missing", outcome.RecoveryDecision.Reason, StringComparison.Ordinal);
        Xunit.Assert.Contains("git_receipt=git-not-run", outcome.RecoveryDecision.Reason, StringComparison.Ordinal);
        Xunit.Assert.Null(outcome.Verification);
    }

    [Xunit.Fact(DisplayName = "DispatchStateSurface_classifies_apparatus_hold_as_wedged_not_running")]
    public void DispatchStateSurfaceClassifiesApparatusHoldAsWedgedNotRunning()
    {
        var root = CreateTempDirectory();
        var process = new TaskProcessRecord(
            999999,
            "codex exec prompt",
            root,
            Path.Combine(root, "out.log"),
            Path.Combine(root, "err.log"),
            Path.Combine(root, "worker.exit.txt"),
            Now.AddMinutes(-20),
            null,
            null);
        File.WriteAllText(process.StandardOutputPath, string.Empty);
        File.WriteAllText(process.StandardErrorPath, string.Empty);
        File.WriteAllText(BackgroundDispatchRunner.GetHeartbeatPath(process), "{not-json");
        var kernel = new AgentOrchestratorKernel(new TestClock(Now));
        var goal = kernel.CreateGoal("Surface apparatus hold", [new TaskSpec(TaskId.New(), "Inspect", AgentRole.Researcher)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", process.Command, root, process.StartedAt));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);

        var state = new DispatchStateSurface(
                new TestClock(Now),
                isProcessAlive: _ => false,
                readCommandLines: _ => new Dictionary<int, string>(),
                inspectWorktree: false)
            .Evaluate(goal.Id, task);

        Xunit.Assert.Equal(DispatchStateKind.WedgedProcess, state.Kind);
        Xunit.Assert.Equal(DispatchRecoveryAction.Hold, state.RecoveryDecision.Action);
        Xunit.Assert.Equal("heartbeat-invalid", state.RecoveryDecision.Blocker);
    }

    [Xunit.Fact]
    public void DispatchStateSurfaceReadOnlyMissingWorktreeMatchesRefreshStaleDisposition()
    {
        var root = CreateTempDirectory();
        var missingWorktree = Path.Combine(root, "missing-worktree");
        var process = new TaskProcessRecord(
            999999,
            "codex exec prompt",
            missingWorktree,
            Path.Combine(root, "out.log"),
            Path.Combine(root, "err.log"),
            Path.Combine(root, "worker.exit.txt"),
            Now.AddMinutes(-20),
            null,
            null);
        File.WriteAllText(process.StandardOutputPath, string.Empty);
        File.WriteAllText(process.StandardErrorPath, string.Empty);
        var kernel = new AgentOrchestratorKernel(new TestClock(Now));
        var goal = kernel.CreateGoal("Read-only missing worktree", [new TaskSpec(TaskId.New(), "Inspect", AgentRole.Researcher)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", process.Command, missingWorktree, process.StartedAt));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);

        var state = new DispatchStateSurface(
                new TestClock(Now),
                isProcessAlive: _ => false,
                readCommandLines: _ => new Dictionary<int, string>())
            .Evaluate(goal.Id, task);

        Xunit.Assert.Equal(DispatchStateKind.StaleCleanup, state.Kind);
        Xunit.Assert.Equal(DispatchRecoveryAction.MarkStale, state.RecoveryDecision.Action);
        Xunit.Assert.DoesNotContain("worktree-inspection", state.RecoveryDecision.Blocker ?? string.Empty, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalRecoveryPlanner_surfaces_apparatus_hold_as_not_alive_with_typed_blocker")]
    public void GoalRecoveryPlannerSurfacesApparatusHoldAsNotAliveWithTypedBlocker()
    {
        var root = CreateTempDirectory();
        var process = new TaskProcessRecord(
            999999,
            "codex exec prompt",
            root,
            Path.Combine(root, "out.log"),
            Path.Combine(root, "err.log"),
            Path.Combine(root, "worker.exit.txt"),
            Now.AddMinutes(-20),
            null,
            null);
        File.WriteAllText(process.StandardOutputPath, string.Empty);
        File.WriteAllText(process.StandardErrorPath, string.Empty);
        File.WriteAllText(BackgroundDispatchRunner.GetHeartbeatPath(process), "{not-json");
        var kernel = new AgentOrchestratorKernel(new TestClock(Now));
        var goal = kernel.CreateGoal("Plan apparatus hold recovery", [new TaskSpec(TaskId.New(), "Inspect", AgentRole.Researcher)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", process.Command, root, process.StartedAt));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);

        var report = GoalRecoveryPlanner.Build(kernel, goal, root, includeCleanupBackoff: false);

        var finding = Xunit.Assert.Single(report.TaskFindings);
        Xunit.Assert.Contains("is not alive", finding.Finding, StringComparison.Ordinal);
        Xunit.Assert.Contains("apparatus blocker=heartbeat-invalid", finding.Finding, StringComparison.Ordinal);
        Xunit.Assert.Equal("refresh-dispatch 1", finding.SuggestedCommand);
        Xunit.Assert.Equal(DispatchRecoveryAction.Hold, finding.RecoveryDecision!.Action);
    }

    private static DispatchRecoveryPolicy CreatePolicy() => new(new TestClock(Now));

    private static void WriteWorkerResultArtifact(string root) =>
        File.WriteAllText(
            Path.Combine(root, "WORKER_RESULT.md"),
            "WORKER_RESULT:\nfiles: none\ncommands: none\ntests: deferred - fixture\nblockers: none\nmodel_fit: test fixture - adequate - recovery policy\nskills: none\nconfidence: high\nEND_WORKER_RESULT");

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
        public DateTimeOffset UtcNow { get; private set; } = utcNow;

        public void Advance(TimeSpan? by = null)
        {
            UtcNow += by ?? TimeSpan.FromTicks(1);
        }
    }
}
