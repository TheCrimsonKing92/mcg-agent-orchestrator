using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Xml.Linq;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed partial class ConductorDriverTestsLifecycleStates
{
    private static string CreateTempDirectory() => ConductorDriverTests.CreateTempDirectory();

    // ── Created state ─────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Created_creates_workspace_and_returns_Executed")]
    public void ConductorDriverCreatedCreatesWorkspaceAndReturnsExecuted()
    {
        var (_, goal) = SimpleGoal();
        var workspaceCreated = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            createWorkspace: _ => { workspaceCreated = true; return "/tmp/workspace"; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(workspaceCreated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.Created, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    // ── WorkspaceReady state ──────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_at_cap_returns_Held")]
    public void ConductorDriverWorkspaceReadyAtCapReturnsHeld()
    {
        var (_, goal) = SimpleGoal();
        var policy = ConductorAutonomyPolicy.Conservative; // MaxConcurrentPaidWorkers = 4
        var dispatchCalled = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => policy.MaxConcurrentPaidWorkers, // at cap
            dispatchAndStart: _ => { dispatchCalled = true; return DispatchStartOutcome.Started(); });

        var result = driver.AdvanceOnce(goal, policy);

        Assert.False(dispatchCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Held);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_under_cap_dispatches_and_returns_Executed")]
    public void ConductorDriverWorkspaceReadyUnderCapDispatchesAndReturnsExecuted()
    {
        var (_, goal) = SimpleGoal();
        var dispatchCalled = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ => { dispatchCalled = true; return DispatchStartOutcome.Started(); });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(dispatchCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    [Xunit.Theory(DisplayName = "ConductorDriver_worker_admission_snapshot_clamps_above_capacity")]
    [Xunit.InlineData(false, 0)]
    [Xunit.InlineData(true, 1)]
    public void ConductorDriverWorkerAdmissionSnapshotClampsAboveCapacity(bool gateReady, int expectedReservedSlots)
    {
        var policy = ConductorAutonomyPolicy.Conservative with
        {
            MaxConcurrentPaidWorkers = ConductorBatchLoop.WorkerAdmissionCapacity + 3
        };
        var driver = MakeDriver(hasGateReadyGoal: () => gateReady);

        var snapshot = driver.GetWorkerAdmissionSnapshot(policy);

        Assert.Equal(policy.MaxConcurrentPaidWorkers, snapshot.ConfiguredWorkerCap);
        Assert.Equal(ConductorBatchLoop.WorkerAdmissionCapacity, snapshot.AdmissionCapacity);
        Assert.Equal(expectedReservedSlots, snapshot.ReservedGateSlots);
        Assert.Equal(ConductorBatchLoop.WorkerAdmissionCapacity - expectedReservedSlots, snapshot.EffectiveWorkerCap);
        Assert.True(snapshot.EffectiveWorkerCap < snapshot.ConfiguredWorkerCap);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_capacity_clamp_reports_effective_cap_and_deferral")]
    public void ConductorDriverWorkspaceReadyCapacityClampReportsEffectiveCapAndDeferral()
    {
        var (_, goal) = SimpleGoal();
        var policy = ConductorAutonomyPolicy.Conservative with
        {
            MaxConcurrentPaidWorkers = ConductorBatchLoop.WorkerAdmissionCapacity + 3
        };
        var dispatchCalled = false;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorBatchLoop.WorkerAdmissionCapacity,
            dispatchAndStart: _ => { dispatchCalled = true; return DispatchStartOutcome.Started(); },
            hasGateReadyGoal: () => false);

        ConductorAdvanceResult? result = null;
        var output = AsyncLocalConsoleRouter.Capture(() => result = driver.AdvanceOnce(goal, policy));

        Assert.False(dispatchCalled);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result!.Outcome);
        Assert.Contains("worker admission capacity (9/9)", held.Reason, StringComparison.Ordinal);
        Assert.Contains("configured cap 12 is clamped", held.Reason, StringComparison.Ordinal);
        Assert.Contains("ADMISSION", output, StringComparison.Ordinal);
        Assert.Contains("reason=worker-admission-capacity", output, StringComparison.Ordinal);
        Assert.Contains("cap=9", output, StringComparison.Ordinal);
        Assert.Contains("configuredCap=12", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_reserves_acceptance_capacity_not_build_permit_count")]
    public void ConductorDriverWorkspaceReadyReservesAcceptanceCapacityNotBuildPermitCount()
    {
        var (_, goal) = SimpleGoal();
        var policy = ConductorAutonomyPolicy.Conservative with
        {
            MaxConcurrentPaidWorkers = ConductorBatchLoop.WorkerAdmissionCapacity
        };
        var dispatchCalled = false;

        // The gate reservation draws from the paid-worker ADMISSION pool (WorkerAdmissionCapacity),
        // NOT the parallel-acceptance width / build-permit count. At WorkerAdmissionCapacity - 1
        // running with a ready gate, the reserved slot holds the next dispatch.
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorBatchLoop.WorkerAdmissionCapacity - 1,
            dispatchAndStart: _ => { dispatchCalled = true; return DispatchStartOutcome.Started(); },
            hasGateReadyGoal: () => true);

        ConductorAdvanceResult? result = null;
        var output = AsyncLocalConsoleRouter.Capture(() => result = driver.AdvanceOnce(goal, policy));

        Assert.False(dispatchCalled);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result!.Outcome);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, held.State);
        Assert.Contains("gate-ready goal reserving a stable slot", held.Reason, StringComparison.Ordinal);
        Assert.Contains("ADMISSION", output, StringComparison.Ordinal);
        Assert.Contains("reason=reserved-gate-slot", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_gate_reservation_draws_from_worker_admission_not_acceptance_width")]
    public void ConductorDriverGateReservationDrawsFromWorkerAdmissionNotAcceptanceWidth()
    {
        // Parallel-acceptance width, build concurrency, and paid-worker admission are independently tuned.
        // The first two both default to 2 today, but neither derives from the other.
        Assert.Equal(2, ConductorBatchLoop.DefaultParallelAcceptanceCapacity);
        Assert.Equal(2, DotnetBuildEnvironmentManager.BuildConcurrencySlotCount);
        Assert.Equal(9, ConductorBatchLoop.WorkerAdmissionCapacity);
        Assert.NotEqual(
            ConductorBatchLoop.DefaultParallelAcceptanceCapacity,
            ConductorBatchLoop.WorkerAdmissionCapacity);

        var (_, goal) = SimpleGoal();
        var policy = ConductorAutonomyPolicy.Conservative with
        {
            MaxConcurrentPaidWorkers = ConductorBatchLoop.WorkerAdmissionCapacity
        };
        var dispatchCalled = false;

        // Paid workers already running == acceptance width (2) while a gate is ready. If the
        // reservation were (wrongly) drawn from acceptance width, the cap would collapse to
        // min(9, 2 - 1) = 1 and admission would be held. Decoupled from worker admission the
        // cap is min(9, 9 - 1) = 8, so the third worker is still admitted during the gate.
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorBatchLoop.DefaultParallelAcceptanceCapacity,
            dispatchAndStart: _ => { dispatchCalled = true; return DispatchStartOutcome.Started(); },
            hasGateReadyGoal: () => true);

        var result = driver.AdvanceOnce(goal, policy);

        Assert.True(dispatchCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(
            GoalLifecycleState.WorkspaceReady,
            ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_repairs_terminal_goal_with_assigned_task_before_acceptance")]
    public void ConductorDriverRepairsTerminalGoalWithAssignedTaskBeforeAcceptance()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Retry desync");
        var snapshot = kernel.ExportSnapshot();
        var badGoalSnapshot = snapshot.Goals.Single() with
        {
            Status = GoalStatus.Completed,
            Tasks = snapshot.Goals.Single().Tasks
                .Select(task => task with { Status = WorkTaskStatus.Assigned, LastVerification = null })
                .ToArray()
        };
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [badGoalSnapshot] });
        goal = kernel.Goals.Single();
        var dispatched = false;
        var acceptanceCalled = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ =>
            {
                dispatched = true;
                return DispatchStartOutcome.Started();
            },
            runAcceptance: _ =>
            {
                acceptanceCalled = true;
                return true;
            },
            normalizeLifecycleState: (g, reason) => kernel.NormalizeGoalLifecycleState(g.Id, reason));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(dispatched);
        Assert.False(acceptanceCalled);
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Conductor auto-repaired", StringComparison.Ordinal));
    }

    // ── Dispatched state ──────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Dispatched_starts_recorded_dispatch")]
    public void ConductorDriverDispatchedStartsRecordedDispatch()
    {
        var (kernel, goal) = SimpleGoal();
        DispatchTask(kernel, goal, goal.Tasks.Single());
        var startCalled = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            startRecordedDispatches: _ =>
            {
                startCalled = true;
                return DispatchStartOutcome.Started();
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(startCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.Dispatched, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_refinement_pending_holds_instead_of_escalating")]
    public void ConductorDriverWorkspaceReadyRefinementPendingHoldsInsteadOfEscalating()
    {
        var (_, goal) = SimpleGoal();
        var escalated = false;
        var pending =
            "SPEC_REFINEMENT_PENDING goal=deadbeef owner=durable-outbox executor_started=true detail=running";
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => DispatchStartOutcome.Deferred(pending),
            writeEscalationWithResult: (_, _, _) =>
            {
                escalated = true;
                return new LandingEscalationWriteResult(0, "ok", 0, "ok", 0, "ok");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(escalated);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, held.State);
        Assert.Equal(pending, held.Reason);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Dispatched_start_failure_escalates_with_reason")]
    public void ConductorDriverDispatchedStartFailureEscalatesWithReason()
    {
        var (kernel, goal) = SimpleGoal();
        DispatchTask(kernel, goal, goal.Tasks.Single());
        var callCount = 0;
        var shutdownCalled = false;
        string? escalationReason = null;
        var phaseTimings = new List<string>();
        const string startFailReason = "Recorded dispatch start failed: worker command refused to launch";

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            startRecordedDispatches: _ =>
            {
                callCount++;
                return DispatchStartOutcome.SpawnFailed(startFailReason);
            },
            buildServerShutdown: () => { shutdownCalled = true; },
            writeEscalationWithResult: (_, _, reason) =>
            {
                escalationReason = reason;
                return new LandingEscalationWriteResult(2, "ok", 25, "timeout", 3, "error");
            });
        driver.PhaseTimingSink = phaseTimings.Add;

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(2, callCount);
        Assert.True(shutdownCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(GoalLifecycleState.Dispatched, ((ConductorAdvanceOutcome.Escalated)result.Outcome).State);
        Assert.Equal(startFailReason, escalationReason);
        var remediationTiming = Assert.Single(
            phaseTimings,
            line => line.Contains("phase=dispatch-remediation", StringComparison.Ordinal));
        Assert.Contains("result=ran", remediationTiming, StringComparison.Ordinal);
        Assert.Matches(@"elapsed_ms=[1-9]\d*", remediationTiming);
        Assert.Contains(
            phaseTimings,
            line => line.Contains("phase=escalation-write", StringComparison.Ordinal) &&
                    line.Contains("json_ms=2 json=ok", StringComparison.Ordinal) &&
                    line.Contains("collab_ms=25 collab=timeout", StringComparison.Ordinal) &&
                    line.Contains("channel_ms=3 channel=error", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_dispatch_remediation_runs_once_per_tick_and_rearms")]
    public void ConductorDriverDispatchRemediationRunsOncePerTickAndRearms()
    {
        var (_, firstGoal) = SimpleGoal("First failing dispatch");
        var (_, secondGoal) = SimpleGoal("Second failing dispatch");
        var shutdownCalls = 0;
        var phaseTimings = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => DispatchStartOutcome.SpawnFailed("spawn failed"),
            buildServerShutdown: () => shutdownCalls++);
        driver.PhaseTimingSink = phaseTimings.Add;

        driver.BeginTick();
        driver.AdvanceOnce(firstGoal, ConductorAutonomyPolicy.Conservative);
        driver.AdvanceOnce(secondGoal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, shutdownCalls);
        var firstTickResults = phaseTimings
            .Where(line => line.Contains("phase=dispatch-remediation", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, firstTickResults.Length);
        Assert.Contains("result=ran", firstTickResults[0], StringComparison.Ordinal);
        Assert.Contains("result=skipped-tick-latch", firstTickResults[1], StringComparison.Ordinal);

        driver.BeginTick();
        driver.AdvanceOnce(firstGoal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(2, shutdownCalls);
        Assert.Contains(
            phaseTimings.Skip(firstTickResults.Length),
            line => line.Contains("phase=dispatch-remediation", StringComparison.Ordinal) &&
                    line.Contains("result=ran", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_dispatch_remediation_timeout_is_bounded_and_preserves_failure")]
    public void ConductorDriverDispatchRemediationTimeoutIsBoundedAndPreservesFailure()
    {
        var (_, goal) = SimpleGoal("Timed out remediation");
        using var shutdownEntered = new ManualResetEventSlim();
        using var releaseShutdown = new ManualResetEventSlim();
        var phaseTimings = new List<string>();
        var startCalls = 0;
        const string spawnFailReason = "worker spawn failed with access denied";
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => ++startCalls == 1
                ? DispatchStartOutcome.SpawnFailed(spawnFailReason)
                : DispatchStartOutcome.EmptyBatch("retry started no processes"),
            buildServerShutdown: () =>
            {
                shutdownEntered.Set();
                releaseShutdown.Wait();
            },
            buildServerShutdownTimeout: TimeSpan.FromMilliseconds(25));
        driver.PhaseTimingSink = phaseTimings.Add;

        try
        {
            var advance = Task.Run(() =>
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative));

            Assert.True(shutdownEntered.Wait(TimeSpan.FromSeconds(1)));
            Assert.True(advance.Wait(TimeSpan.FromSeconds(1)));
            var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(advance.Result.Outcome);

            Assert.Equal(spawnFailReason, escalated.Reason);
            Assert.Contains(
                phaseTimings,
                line => line.Contains("phase=dispatch-remediation", StringComparison.Ordinal) &&
                        line.Contains("result=timeout", StringComparison.Ordinal));
        }
        finally
        {
            releaseShutdown.Set();
        }
    }

    // ── Running state ─────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Running_returns_Held")]
    public void ConductorDriverRunningReturnsHeld()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        var process = new TaskProcessRecord(12345, "test.exe", "C:\\tmp",
            "C:\\tmp\\stdout", "C:\\tmp\\stderr", "C:\\tmp\\exit",
            StartedAt: DateTimeOffset.UtcNow, CompletedAt: null, ExitCode: null);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);

        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Held);
        Assert.Equal(GoalLifecycleState.Running, ((ConductorAdvanceOutcome.Held)result.Outcome).State);
    }

    // ── Verified state ────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_acceptance_fails_escalates")]
    public void ConductorDriverVerifiedAcceptanceFailsEscalates()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var escalated = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => false,
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(GoalLifecycleState.Verified, ((ConductorAdvanceOutcome.Escalated)result.Outcome).State);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_rebase_conflict_escalates_before_acceptance")]
    public void ConductorDriverVerifiedRebaseConflictEscalatesBeforeAcceptance()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var acceptanceCalled = false;
        string? escalationReason = null;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.Conflict, "goal/test", "conflict", ["src/Foo.cs"], "workspace rebase"),
            runAcceptance: _ => { acceptanceCalled = true; return true; },
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        // Rebase-before-acceptance: an un-integrable branch escalates without spending an acceptance run,
        // and acceptance never verifies the pre-integration branch.
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.False(acceptanceCalled);
        Assert.Contains("pre-landing rebase conflict", escalationReason!, StringComparison.Ordinal);
        Assert.Contains("src/Foo.cs", escalationReason!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_advisory_only_unmet_acceptance_criteria_land_with_task_note")]
    public void ConductorDriverVerifiedAdvisoryOnlyUnmetAcceptanceCriteriaLandWithTaskNote()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["stale required retry feedback"]);
        var landCalled = false;
        var retryCalled = false;
        var advisory = new AcceptanceCheckResult(
            "test tamper guard",
            false,
            0,
            "1 test degradation signal(s)",
            ResultSummary: "1 test degradation signal(s)",
            Advisory: true);

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [advisory]),
            retryTask: (_, _, _) =>
            {
                retryCalled = true;
                throw new InvalidOperationException("Advisory acceptance criteria must not retry tasks.");
            },
            recordTaskNote: (goalId, taskId, message) => kernel.RecordTaskNote(goalId, taskId, message),
            clearCriterionRetryFeedback: kernel.ClearCriterionRetryFeedback,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.False(retryCalled);
        Assert.True(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(1, task.CriterionRetryCount);
        Assert.Empty(task.CriterionRetryFeedback);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("Advisory acceptance criteria observed during landing (non-gating)", StringComparison.Ordinal) &&
            evt.Message.Contains("test tamper guard: 1 test degradation signal(s)", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_required_unmet_acceptance_criterion_retries_task_with_feedback")]
    public void ConductorDriverVerifiedRequiredUnmetAcceptanceCriterionRetriesTaskWithFeedback()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var landCalled = false;
        var retryCalled = false;
        string? retryMessage = null;
        var unmet = new AcceptanceCheckResult(
            "command-exit dotnet test --filter RetryEvidence",
            false,
            1,
            string.Join(Environment.NewLine,
            [
                "src/Foo.cs(12,34): error CS1002: ; expected",
                "[xUnit.net 00:00:01.23]     Mcg.AgentOrchestrator.Tests.RetryEvidenceTests.IncludesFailures [FAIL]",
            ]),
            ResultSummary: "focused conductor tests failed");

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (goalId, taskId, message) =>
            {
                retryCalled = true;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, RetryCause.Unknown);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
        var brief = kernel.BuildTaskBrief(goal.Id, task.Id);

        Assert.True(retryCalled);
        Assert.False(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(1, task.CriterionRetryCount);
        Assert.Contains("failed check: command-exit dotnet test --filter RetryEvidence (exit code 1)", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("src/Foo.cs(12,34): error CS1002: ; expected", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("Mcg.AgentOrchestrator.Tests.RetryEvidenceTests.IncludesFailures [FAIL]", retryMessage!, StringComparison.Ordinal);
        Assert.True(task.CriterionRetryFeedback.Any(item => item.Contains("src/Foo.cs(12,34): error CS1002: ; expected", StringComparison.Ordinal)));
        Assert.True(task.CriterionRetryFeedback.Any(item => item.Contains("Mcg.AgentOrchestrator.Tests.RetryEvidenceTests.IncludesFailures [FAIL]", StringComparison.Ordinal)));
        Assert.Contains("## Unmet acceptance criteria from the prior attempt - fix these:", brief.Content, StringComparison.Ordinal);
        Assert.Contains("src/Foo.cs(12,34): error CS1002: ; expected", brief.Content, StringComparison.Ordinal);
        Assert.Contains("Mcg.AgentOrchestrator.Tests.RetryEvidenceTests.IncludesFailures [FAIL]", brief.Content, StringComparison.Ordinal);
        Assert.Contains("focused conductor tests failed", brief.Content, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_retry_feedback_uses_complete_trx_receipt_without_changing_retry_state")]
    public void ConductorDriverRetryFeedbackUsesCompleteTrxReceiptWithoutChangingRetryState()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var trxPath = Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(),
            "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "TestData",
            "Fixtures",
            "mtp-xunit-v3-failures.trx.xml");
        string? retryMessage = null;
        var landCalled = false;
        var unmet = new AcceptanceCheckResult(
            "infrastructure tests: retry evidence",
            false,
            1,
            "[FAIL] GoalAcceptanceVerifier: Assert.Contains() Failure: Sub-string not found",
            ResultSummary: "infrastructure tests failed",
            TestResultPaths: [trxPath]);
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (goalId, taskId, message) =>
            {
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, RetryCause.Unknown);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

        Assert.False(landCalled);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(1, task.CriterionRetryCount);
        Assert.Contains(trxPath, retryMessage!, StringComparison.Ordinal);
        Assert.Contains(
            "[FAIL] Mcg.AgentOrchestrator.Infrastructure.Tests.RetryEvidenceTests.IncludesFailures (Failed)",
            retryMessage!,
            StringComparison.Ordinal);
        Assert.Contains("Expected: 2", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("Actual:   0", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("RetryEvidenceTests.cs:line 42", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("TimeoutTests.ReportsDuration (Timeout)", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("Expected: 2", brief, StringComparison.Ordinal);
        Assert.Contains("Actual:   0", brief, StringComparison.Ordinal);
        Assert.Contains("RetryEvidenceTests.cs:line 42", brief, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_retry_feedback_drops_whole_trx_entries_and_names_receipt")]
    public void ConductorDriverRetryFeedbackDropsWholeTrxEntriesAndNamesReceipt()
    {
        var tempDirectory = CreateTempDirectory();
        var trxPath = Path.Combine(tempDirectory, "many-failures.trx");
        try
        {
            new XDocument(
                new XElement(
                    "TestRun",
                    new XElement(
                        "Results",
                        Enumerable.Range(1, 32).Select(index =>
                            new XElement(
                                "UnitTestResult",
                                new XAttribute("testName", $"Example.Tests.CompleteTestName{index:D2}"),
                                new XAttribute("outcome", "Failed"),
                                new XElement(
                                    "Output",
                                    new XElement(
                                        "ErrorInfo",
                                        new XElement("Message", $"complete-message-{index:D2}-end"),
                                        new XElement("StackTrace", $"complete-stack-{index:D2}-end"))))))))
                .Save(trxPath);
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            string? retryMessage = null;
            var unmet = new AcceptanceCheckResult(
                "infrastructure tests: many failures",
                false,
                1,
                "[FAIL] Example.Tests.CompleteTestName",
                TestResultPaths: [trxPath]);
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
                retryTask: (goalId, taskId, message) =>
                {
                    retryMessage = message;
                    return kernel.RetryTask(goalId, taskId, message, RetryCause.Unknown);
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.Contains("Example.Tests.CompleteTestName30 (Failed)", retryMessage!, StringComparison.Ordinal);
            Assert.Contains("complete-message-30-end", retryMessage!, StringComparison.Ordinal);
            Assert.Contains("complete-stack-30-end", retryMessage!, StringComparison.Ordinal);
            Assert.DoesNotContain("Example.Tests.CompleteTestName31", retryMessage!, StringComparison.Ordinal);
            Assert.Contains($"2 more failures omitted — see {trxPath}.", retryMessage!, StringComparison.Ordinal);
            Assert.DoesNotContain("truncated", retryMessage!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_retry_feedback_names_missing_trx_and_keeps_summary")]
    public void ConductorDriverRetryFeedbackNamesMissingTrxAndKeepsSummary()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.trx");
        string? retryMessage = null;
        var unmet = new AcceptanceCheckResult(
            "infrastructure tests: missing receipt",
            false,
            1,
            "[FAIL] Summary.Name: Values differ",
            TestResultPaths: [missingPath]);
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (goalId, taskId, message) =>
            {
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, RetryCause.Unknown);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Contains("[FAIL] Summary.Name: Values differ", retryMessage!, StringComparison.Ordinal);
        Assert.Contains($"detail unavailable: no TRX exists for partition \"infrastructure tests: missing receipt\" at {missingPath}", retryMessage!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_gate_environment_interference_holds_without_developer_retry")]
    public void ConductorDriverGateEnvironmentInterferenceHoldsWithoutDeveloperRetry()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var retryCalled = false;
        var landCalled = false;
        var interference = new AcceptanceCheckResult(
            "structural test coverage: core tests",
            false,
            1,
            "classification: gate-environment-interference\nstructural coverage failed: discovered=615, executed=0, missing=615, emptyPartitions=1",
            ResultSummary: "structural coverage failed: discovered=615, executed=0, missing=615, emptyPartitions=1",
            FailureClassification: AcceptanceFailureClassifications.GateEnvironmentInterference);

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(false, [interference]),
            retryTask: (goalId, taskId, message) =>
            {
                retryCalled = true;
                return kernel.RetryTask(goalId, taskId, message, RetryCause.Unknown);
            },
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Xunit.Assert.Equal(GoalLifecycleState.Verified, held.State);
        Xunit.Assert.Contains("apparatus/environmental failure recorded", held.Reason, StringComparison.Ordinal);
        Xunit.Assert.Contains("will not re-run until", held.Reason, StringComparison.Ordinal);
        Xunit.Assert.Contains("no worker was reopened", held.Reason, StringComparison.Ordinal);
        Xunit.Assert.False(retryCalled);
        Xunit.Assert.False(landCalled);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Xunit.Assert.Equal(0, task.CriterionRetryCount);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_inherited_baseline_apparatus_regates_unchanged_candidate_without_retry")]
    public void ConductorDriverInheritedBaselineApparatusRegatesUnchangedCandidateWithoutRetry()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var retryCalled = false;
        var landCalled = false;
        var failure = new AcceptanceCheckResult(
            "infrastructure tests: Remainder",
            false,
            1,
            "typed inherited baseline apparatus receipt",
            FailureClassification: AcceptanceFailureClassifications.InheritedBaselineApparatus,
            ExecutedTestCount: 42);
        var acceptance = new AcceptanceVerificationSummary(
            false,
            [failure],
            FailedChecks: [failure.Name],
            BranchHeadSha: "candidate-a",
            MainHeadSha: "main-a",
            CheckAttributions:
            [
                new AcceptanceCheckAttribution(
                    failure.Name,
                    AcceptanceFailureOrigin.Inherited,
                    "same check failed across three goals at main-a",
                    AcceptanceFailureCause.EnvironmentalApparatus)
            ],
            BaselineAttestation: "attested-red");
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => acceptance,
            retryTask: (goalId, taskId, message) =>
            {
                retryCalled = true;
                return kernel.RetryTask(goalId, taskId, message, RetryCause.Unknown);
            },
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Xunit.Assert.Equal(GoalLifecycleState.Verified, held.State);
        Xunit.Assert.Contains("unchanged candidate", held.Reason, StringComparison.Ordinal);
        Xunit.Assert.False(retryCalled);
        Xunit.Assert.False(landCalled);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Xunit.Assert.Equal(0, task.CriterionRetryCount);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_introduced_environmental_apparatus_regates_without_retry")]
    public void ConductorDriverIntroducedEnvironmentalApparatusRegatesWithoutRetry()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var retryCalled = false;
        var acceptanceRuns = 0;
        var escalationWrites = 0;
        var acceptanceHeads = (BranchHeadSha: (string?)"candidate-a", MainHeadSha: (string?)"main-a");
        var failure = new AcceptanceCheckResult(
            "infrastructure tests: Remainder",
            false,
            1,
            "typed seeded repository process/output apparatus receipt",
            FailureClassification: AcceptanceFailureClassifications.SeededRepositoryProcessOutputApparatus,
            ExecutedTestCount: 42);
        var acceptance = new AcceptanceVerificationSummary(
            false,
            [failure],
            FailedChecks: [failure.Name],
            BranchHeadSha: "candidate-a",
            MainHeadSha: "main-a",
            CheckAttributions:
            [
                new AcceptanceCheckAttribution(
                    failure.Name,
                    AcceptanceFailureOrigin.Introduced,
                    "first observed typed apparatus receipt",
                    AcceptanceFailureCause.EnvironmentalApparatus)
            ]);
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ =>
            {
                acceptanceRuns++;
                return acceptance;
            },
            retryTask: (goalId, taskId, message) =>
            {
                retryCalled = true;
                return kernel.RetryTask(goalId, taskId, message, RetryCause.Unknown);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
            recordAcceptanceFailure: (heldGoal, checks, branch, main, attributions, attestation) =>
                kernel.RecordAcceptanceFailure(
                    heldGoal.Id,
                    checks,
                    branch,
                    main,
                    attributions,
                    attestation),
            resolveAcceptanceHeads: _ => acceptanceHeads,
            writeEscalation: (_, _, _) => escalationWrites++);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
        var unchangedPairResult = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(unchangedPairResult.Outcome);
        Xunit.Assert.Equal(GoalLifecycleState.Verified, held.State);
        Xunit.Assert.Equal(1, acceptanceRuns);
        Xunit.Assert.Equal(1, escalationWrites);
        Xunit.Assert.True(goal.LatestAcceptanceFailure?.IsEnvironmentalApparatus);
        Xunit.Assert.False(retryCalled);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Xunit.Assert.Equal(0, task.CriterionRetryCount);

        Xunit.Assert.Equal(1, kernel.RetryAcceptanceGate(goal.Id, "Operator confirmed the apparatus repair."));
        Xunit.Assert.Null(goal.LatestAcceptanceFailure);
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
        Xunit.Assert.Equal(2, acceptanceRuns);

        acceptanceHeads = ("candidate-a", "main-b");
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
        Xunit.Assert.Equal(3, acceptanceRuns);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_inherited_acceptance_failure_holds_without_retry")]
    public void InheritedFailureHoldsWithoutRetry()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var retryCalled = false;
        var rawAcceptance = new AcceptanceVerificationSummary(
            false,
            [new AcceptanceCheckResult("infrastructure tests: Remainder", false, 1, "inherited red")],
            FailedChecks: ["infrastructure tests: Remainder"],
            BranchHeadSha: "candidate-a",
            MainHeadSha: "main-a",
            CheckAttributions:
            [
                new AcceptanceCheckAttribution(
                    "infrastructure tests: Remainder",
                    AcceptanceFailureOrigin.Inherited,
                    "baseline red without an apparatus receipt")
            ],
            BaselineAttestation: "attested-red");
        var acceptance = ConductorDriver.ClassifyInheritedBaselineApparatus(rawAcceptance);
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => acceptance,
            retryTask: (goalId, taskId, message) =>
            {
                retryCalled = true;
                return kernel.RetryTask(goalId, taskId, message, RetryCause.Unknown);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Xunit.Assert.Contains("outside this goal's attributable scope", held.Reason, StringComparison.Ordinal);
        Xunit.Assert.False(retryCalled);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Xunit.Assert.Equal(0, task.CriterionRetryCount);
    }

    [Xunit.Fact]
    public void IntroducedFailureOutsideChangedScopeRetries()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var retryCalled = false;
        var identity = ExtractCanonicalTrxFailureIdentity(
            "Mcg.AgentOrchestrator.Infrastructure.Tests.DotnetBuildEnvironmentManagerTestsLockAttributionLandingFixtures",
            "DotnetBuildEnvironmentManagerNoHolderArtifactPrepLockRetriesAndAcquires",
            "DotnetBuildEnvironmentManager_no_holder_artifact_prep_lock_retries_and_acquires");
        var unmet = new AcceptanceCheckResult(
            "infrastructure tests: Remainder",
            false,
            1,
            "unrelated infrastructure failure",
            FailingTestIdentities: [identity],
            TestProjectPath: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            FailingTestAttributions:
            [
                new AcceptanceTestFailureAttribution(
                    identity,
                    AcceptanceTestFailureOrigin.Introduced,
                    "focused identity was green at merge-base main-a")
            ]);
        var acceptance = new AcceptanceVerificationSummary(
            false,
            [unmet],
            FailedChecks: [unmet.Name],
            BranchHeadSha: "candidate-a",
            MainHeadSha: "main-a",
            CheckAttributions:
            [
                new AcceptanceCheckAttribution(
                    unmet.Name,
                    AcceptanceFailureOrigin.Introduced,
                    "main is attested green")
            ],
            BaselineAttestation: "attested-green");
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => acceptance,
            retryTask: (goalId, taskId, message) =>
            {
                retryCalled = true;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
            getLandingFileScopes: _ =>
            [
                "src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Backlog.cs"
            ]);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.True(retryCalled);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(1, task.CriterionRetryCount);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_inherited_impacted_identity_holds_without_retry")]
    public void InheritedIdentityWithinImpactHoldsWithoutRetry()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var retryCalled = false;
        const string identity =
            "Mcg.AgentOrchestrator.Infrastructure.Tests.CliCommandTestsBacklogIntakeCommands.CliBacklogListSplitsLimitStatusAndTextFlags";
        var unmet = new AcceptanceCheckResult(
            "infrastructure tests: Remainder",
            false,
            1,
            "inherited CLI failure",
            FailingTestIdentities: [identity],
            TestProjectPath: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            FailingTestAttributions:
            [
                new AcceptanceTestFailureAttribution(
                    identity,
                    AcceptanceTestFailureOrigin.Inherited,
                    "same focused identity failed at merge-base main-a")
            ]);
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(
                false,
                [unmet],
                FailedChecks: [unmet.Name],
                BranchHeadSha: "candidate-a",
                MainHeadSha: "main-a"),
            retryTask: (goalId, taskId, message) =>
            {
                retryCalled = true;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
            getLandingFileScopes: _ =>
            [
                "src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Backlog.cs"
            ]);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Contains("pre-existing/main-red", held.Reason, StringComparison.Ordinal);
        Assert.False(retryCalled);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.CriterionRetryCount);
    }

    [Xunit.Fact]
    public void MixedFailuresRetryOnlyIntroducedIdentity()
    {
        var tempDirectory = CreateTempDirectory();
        var trxPath = Path.Combine(tempDirectory, "mixed-attribution.trx");
        try
        {
            const string impacted =
                "Mcg.AgentOrchestrator.Infrastructure.Tests.CliCommandTestsBacklogIntakeCommands.CliBacklogListSplitsLimitStatusAndTextFlags";
            const string unrelated =
                "Mcg.AgentOrchestrator.Infrastructure.Tests.DotnetBuildEnvironmentManagerTestsLockAttributionLandingFixtures.DotnetBuildEnvironmentManagerNoHolderArtifactPrepLockRetriesAndAcquires";
            AcceptanceFailureAttributionTestFixtures.WriteFailedTrx(
                trxPath,
                (impacted, "Cli_backlog_list_splits_limit_status_and_text_flags"),
                (unrelated, "DotnetBuildEnvironmentManager_no_holder_artifact_prep_lock_retries_and_acquires"));
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            string? retryMessage = null;
            var unmet = new AcceptanceCheckResult(
                "infrastructure tests: Remainder",
                false,
                1,
                "mixed infrastructure failures",
                TestResultPaths: [trxPath],
                FailingTestIdentities: [impacted, unrelated],
                TestProjectPath: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                FailingTestAttributions:
                [
                    new AcceptanceTestFailureAttribution(
                        impacted,
                        AcceptanceTestFailureOrigin.Introduced,
                        "focused identity was green at merge-base main-a"),
                    new AcceptanceTestFailureAttribution(
                        unrelated,
                        AcceptanceTestFailureOrigin.Inherited,
                        "same focused identity failed at merge-base main-a")
                ]);
            var acceptance = new AcceptanceVerificationSummary(
                false,
                [unmet],
                FailedChecks: [unmet.Name],
                BranchHeadSha: "candidate-a",
                MainHeadSha: "main-a",
                CheckAttributions:
                [
                    new AcceptanceCheckAttribution(
                        unmet.Name,
                        AcceptanceFailureOrigin.Introduced,
                        "main is attested green")
                ]);
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => acceptance,
                retryTask: (goalId, taskId, message) =>
                {
                    retryMessage = message;
                    return kernel.RetryTask(goalId, taskId, message);
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                getLandingFileScopes: _ =>
                [
                    "src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Backlog.cs"
                ]);

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            Assert.Contains(impacted, retryMessage!, StringComparison.Ordinal);
            Assert.DoesNotContain(unrelated, retryMessage!, StringComparison.Ordinal);
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Assert.Equal(1, task.CriterionRetryCount);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_introduced_test_failure_without_identity_retries")]
    public void IntroducedTestFailureWithoutIdentityRetries()
    {
        AssertIntroducedFailureRetries(
            identity: null,
            changedFile: "src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Backlog.cs");
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_substring_impact_selection_retries_introduced_failure")]
    public void SubstringImpactSelectionRetriesIntroducedFailure()
    {
        AssertIntroducedFailureRetries(
            "Mcg.AgentOrchestrator.Infrastructure.Tests.CliCommandTestsGenerated.BacklogListShowsOpenItems",
            "src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Backlog.cs");
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_unmapped_impact_project_retries_introduced_failure")]
    public void UnmappedImpactProjectRetriesIntroducedFailure()
    {
        AssertIntroducedFailureRetries(
            "Mcg.AgentOrchestrator.Infrastructure.Tests.UnmappedTests.CandidateRegression",
            "src/Mcg.AgentOrchestrator.Core/Domain/Goal.cs");
    }

    private static void AssertIntroducedFailureRetries(string? identity, string changedFile)
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var retryCalled = false;
        var identities = identity is null ? null : new[] { identity };
        var attributions = identity is null
            ? null
            : new[]
            {
                new AcceptanceTestFailureAttribution(
                    identity,
                    AcceptanceTestFailureOrigin.Introduced,
                    "focused identity was green at merge-base main-a")
            };
        var unmet = new AcceptanceCheckResult(
            "infrastructure tests: Remainder",
            false,
            1,
            "introduced regression",
            FailingTestIdentities: identities,
            TestProjectPath: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            FailingTestAttributions: attributions);
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(
                false,
                [unmet],
                FailedChecks: [unmet.Name],
                BranchHeadSha: "candidate-a",
                MainHeadSha: "main-a",
                CheckAttributions:
                [
                    new AcceptanceCheckAttribution(
                        unmet.Name,
                        AcceptanceFailureOrigin.Introduced,
                        "main is attested green")
                ]),
            retryTask: (goalId, taskId, message) =>
            {
                retryCalled = true;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
            getLandingFileScopes: _ => [changedFile]);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.True(retryCalled);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(1, task.CriterionRetryCount);
    }

    private static string ExtractCanonicalTrxFailureIdentity(
        string className,
        string methodName,
        string displayName)
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var trxPath = Path.Combine(tempDirectory, "canonical-identity.trx");
            new XDocument(
                new XElement(
                    "TestRun",
                    new XElement(
                        "TestDefinitions",
                        new XElement(
                            "UnitTest",
                            new XAttribute("id", "test-1"),
                            new XAttribute("name", displayName),
                            new XElement("DisplayName", displayName),
                            new XElement(
                                "TestMethod",
                                new XAttribute("className", className),
                                new XAttribute("name", methodName)))),
                    new XElement(
                        "Results",
                        new XElement(
                            "UnitTestResult",
                            new XAttribute("testId", "test-1"),
                            new XAttribute("testName", displayName),
                            new XAttribute("outcome", "Failed")))))
                .Save(trxPath);

            var identity = Assert.Single(GoalAcceptanceVerifier.ExtractTrxFailureIdentities(trxPath));
            Assert.Equal($"{className}.{methodName}", identity);
            return identity;
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_unmet_acceptance_retry_feedback_caps_concrete_evidence")]
    public void ConductorDriverVerifiedUnmetAcceptanceRetryFeedbackCapsConcreteEvidence()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        string? retryMessage = null;
        var output = string.Join(Environment.NewLine,
            Enumerable.Range(1, 35).Select(index => $"src/Foo{index}.cs({index},1): error CS1002: ; expected"));
        var unmet = new AcceptanceCheckResult(
            "command-exit dotnet test --filter ManyFailures",
            false,
            1,
            output,
            ResultSummary: "many compiler errors");

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (goalId, taskId, message) =>
            {
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, RetryCause.Unknown);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Contains("src/Foo30.cs(30,1): error CS1002: ; expected", retryMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("src/Foo31.cs(31,1): error CS1002: ; expected", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("5 more acceptance evidence entries omitted.", retryMessage!, StringComparison.Ordinal);
        Assert.True(task.CriterionRetryFeedback.Any(item => item.Contains("5 more acceptance evidence entries omitted.", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_unmet_acceptance_retry_feedback_preserves_no_evidence_fallback")]
    public void ConductorDriverVerifiedUnmetAcceptanceRetryFeedbackPreservesNoEvidenceFallback()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        string? retryMessage = null;
        var unmet = new AcceptanceCheckResult(
            "grep-present docs/usage.md contains Ready",
            false,
            1,
            "Pattern 'Ready' was not found.",
            ResultSummary: "docs/usage.md is missing Ready");

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (goalId, taskId, message) =>
            {
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, RetryCause.Unknown);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Contains("docs/usage.md is missing Ready", retryMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("Concrete acceptance failure evidence", retryMessage!, StringComparison.Ordinal);
        Assert.Equal(["grep-present docs/usage.md contains Ready: docs/usage.md is missing Ready"], task.CriterionRetryFeedback);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_unmet_acceptance_criterion_escalates_after_retry_budget")]
    public void ConductorDriverVerifiedUnmetAcceptanceCriterionEscalatesAfterRetryBudget()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["previous unmet criterion"]);
        var landCalled = false;
        string? escalationReason = null;
        var unmet = new AcceptanceCheckResult(
            "file-exists docs/usage.md",
            false,
            1,
            "file missing",
            ResultSummary: "docs/usage.md missing");

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (_, _, _) => throw new InvalidOperationException("Retry should not be called after budget is spent."),
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.False(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Contains("Acceptance criteria unmet after 1 retries", escalationReason!, StringComparison.Ordinal);
        Assert.Contains("docs/usage.md missing", escalationReason!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_acceptance_retry_with_cancelled_task_runs_acceptance")]
    public void ConductorDriverVerifiedAcceptanceRetryWithCancelledTaskRunsAcceptance()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "Run acceptance."));
        Assert.True(kernel.ReconcileGoalAcceptanceFailed(
            goal.Id,
            ["environment failure"],
            "Acceptance environment failed."));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Cancelled, "Operator deliberately descoped task.");
        kernel.RetryAcceptanceGate(goal.Id, "Environment repaired.");
        var acceptanceCalled = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ =>
            {
                acceptanceCalled = true;
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            land: g => new LandingResult(
                g.Id.Value,
                g.Id.Value[..8],
                new LandingDecision.Promote(),
                "integration",
                true,
                "Landed"));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(acceptanceCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(WorkTaskStatus.Cancelled, task.Status);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_all_acceptance_criteria_met_lands")]
    public void ConductorDriverVerifiedAllAcceptanceCriteriaMetLands()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["stale retry feedback"]);
        var landCalled = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            clearCriterionRetryFeedback: kernel.ClearCriterionRetryFeedback,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(0, task.CriterionRetryFeedback.Count);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_runs_semantic_acceptance_after_acceptance_and_landing")]
    public void ConductorDriverVerifiedRunsSemanticAcceptanceAfterAcceptanceAndLanding()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var order = new List<string>();

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ =>
            {
                order.Add("acceptance");
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            runAdvisorySemanticAcceptance: (_, _) => order.Add("semantic"),
            land: g =>
            {
                order.Add("land");
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Xunit.Assert.Equal(new[] { "acceptance", "land", "semantic" }, order);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_successful_landing_runs_semantic_then_post_landing_close_after_main_advances")]
    public void ConductorDriverVerifiedSuccessfulLandingRunsSemanticThenPostLandingCloseAfterMainAdvances()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var order = new List<string>();

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: (_, _) => order.Add("semantic"),
            land: g =>
            {
                order.Add("land");
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            afterSuccessfulLanding: (_, result) =>
            {
                Assert.True(result.MainAdvanced);
                order.Add("close");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Xunit.Assert.Equal(new[] { "land", "semantic", "close" }, order);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_main_advance_schedules_relaunch_before_fallible_post_landing_actions")]
    public void ConductorDriverMainAdvanceSchedulesRelaunchBeforeFalliblePostLandingActions()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        ConductorLandingReceipt? receipt = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: (_, _) =>
                throw new InvalidOperationException("post-merge advisory failed"),
            land: g =>
                new LandingResult(
                    g.Id.Value,
                    g.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "Landed"));
        driver.SuccessfulLandingSink = landed => receipt = landed;

        var error = Assert.Throws<InvalidOperationException>(
            () => driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative));

        Assert.Equal("post-merge advisory failed", error.Message);
        Assert.NotNull(receipt);
        Assert.Equal(goal.Id.Value, receipt!.GoalId);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_ownership_hold_escalates_and_skips_semantic_receipt")]
    public void ConductorDriverVerifiedOwnershipHoldEscalatesAndSkipsSemanticReceipt()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var semanticCalled = false;
        var landCalled = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: (_, _) => { semanticCalled = true; },
            land: g =>
            {
                landCalled = true;
                return new LandingResult(
                    g.Id.Value,
                    g.Id.Value[..8],
                    new LandingDecision.Escalate("ownership-denylist hold: task touched protected path"),
                    "integration",
                    false,
                    "Held");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Verified, escalated.State);
        Assert.True(LandingExecutor.IsOwnershipHoldEscalation(escalated.Reason));
        Assert.False(semanticCalled);
        Assert.True(landCalled);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_landing_writes_semantic_receipt_and_closes_backlog_item")]
    public async Task ConductorDriverVerifiedLandingWritesSemanticReceiptAndClosesBacklogItem()
    {
        var root = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        RunGit(root, "init");
        RunGit(root, "checkout", "-b", "main");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(root, "src", "A.cs"), "class A {}\n");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "initial");
        RunGit(root, "checkout", "-b", "goal/test");
        File.WriteAllText(Path.Combine(root, "src", "A.cs"), "class A { string Done() => \"done\"; }\n");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "goal change");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog(
        [
            new ModelFunctionBinding(ModelFunctionPurposes.AcceptanceJudge, ModelLane.CheapApi,
                new ModelProfile("Fake", "judge", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        var provider = new CountingModelProvider("Fake",
            """{"criteria_met": true, "confidence": "high", "reasons": ["implemented"], "unmet_criteria": []}""");
        var providers = new InMemoryModelProviderRegistry([provider]);
        var backlogStore = new BacklogStore(workspace.BacklogStorePath);
        var item = await backlogStore.AddAsync("Landing target");
        var (kernel, goal) = SimpleGoal("Implement required behavior");
        kernel.SetGoalSourceBacklogItemId(goal.Id, item.Id);
        PassVerification(kernel, goal, goal.Tasks.Single());

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: (g, summary) => GoalLandingPostActions.RunAdvisorySemanticAcceptance(
                g,
                workspace,
                providers,
                WorkerProfileCatalog.Default(),
                root,
                null),
            land: g => new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"),
            afterSuccessfulLanding: (g, result) =>
            {
                Assert.True(result.MainAdvanced);
                GoalLandingPostActions.AutoCloseSourceBacklogItem(g, workspace.BacklogStorePath);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.True(provider.Calls > 0);
        var receipt = File.ReadLines(workspace.SemanticAcceptanceLogPath).Single();
        Assert.True(receipt.Contains(goal.Id.Value, StringComparison.Ordinal));
        var fetched = await backlogStore.GetByExactIdAsync(item.Id);
        Assert.NotNull(fetched);
        Assert.Equal(BacklogItemStatus.Done, fetched!.Status);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_does_not_close_backlog_when_landing_does_not_advance_main")]
    public void ConductorDriverVerifiedDoesNotCloseBacklogWhenLandingDoesNotAdvanceMain()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var closeCalled = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            land: g => new LandingResult(g.Id.Value, g.Id.Value[..8],
                new LandingDecision.Promote(), "integration", false, "No main advance"),
            afterSuccessfulLanding: (_, _) => { closeCalled = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.False(closeCalled);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_zero_criterion_retry_budget_escalates_without_retry")]
    public void ConductorDriverVerifiedZeroCriterionRetryBudgetEscalatesWithoutRetry()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var retryCalled = false;
        string? escalationReason = null;
        var policy = ConductorAutonomyPolicy.Conservative with { MaxCriterionRetries = 0 };
        var unmet = new AcceptanceCheckResult(
            "command-exit dotnet test",
            false,
            1,
            "failed",
            ResultSummary: "focused command failed");

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (goalId, taskId, message) =>
            {
                retryCalled = true;
                return kernel.RetryTask(goalId, taskId, message, RetryCause.Unknown);
            },
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, policy);

        Assert.False(retryCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Contains("Acceptance criteria unmet after 0 retries", escalationReason!, StringComparison.Ordinal);
        Assert.Contains("focused command failed", escalationReason!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_security_risk_delegates_to_landing_engine")]
    public void ConductorDriverVerifiedSecurityRiskDelegatesToLandingEngine()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var escalated = false;
        var landCalled = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.Security,
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(landCalled);
        Assert.False(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_landing_engine_escalation_escalates")]
    public void ConductorDriverVerifiedLandingEngineEscalationEscalates()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var escalated = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            land: g => new LandingResult(g.Id.Value, g.Id.Value[..8],
                new LandingDecision.Escalate("Conflict detected"), "integration", false, "Conflict"),
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_clean_DocsOnly_risk_lands")]
    public void ConductorDriverVerifiedCleanDocsOnlyRiskLands()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var landCalled = false;

        // Conservative: threshold DocsOnly, DocsOnly risk → Auto
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            land: g => { landCalled = true; return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"); });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.Verified, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    [Xunit.Theory(DisplayName = "Post-landing canary sink failure stays non-blocking and cannot skip successful-landing callbacks")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void PostLandingCanaryFailureCannotSkipSuccessfulLandingCallbacks(bool breakSqliteStore)
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var root = CreateTempDirectory();
        var dbPath = Path.Combine(root, "run-events.db");
        if (breakSqliteStore)
        {
            Directory.CreateDirectory(dbPath);
        }

        var events = new PostLandingCanaryEventStore(
            new SqliteRunEventStore(dbPath, ensureSchema: !breakSqliteStore),
            dbPath);
        var circuit = new AcceptanceEngineCircuitBreaker(events);
        var coordinator = new PostLandingCanaryCoordinator(
            new PostLandingCanaryConfiguration(Enabled: true, TimeoutSeconds: 10, AdditionalEnginePathPrefixes: []),
            new PostLandingCanaryRunner(
                root,
                runOverride: (_, _) => Task.FromResult(PostLandingCanaryOutcome.Passed(1, "unused"))),
            events,
            circuit,
            progress: _ => { });
        var afterSuccessfulLandingCalled = false;
        var landingSha = breakSqliteStore ? "sha-broken-sqlite" : null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            land: g => new LandingResult(
                g.Id.Value,
                g.Id.Value[..8],
                new LandingDecision.Promote(),
                "integration",
                true,
                "Landed",
                landingSha,
                ["src/Mcg.AgentOrchestrator.App/Orchestration/PostLandingCanaryCoordinator.cs"]),
            afterSuccessfulLanding: (_, _) => afterSuccessfulLandingCalled = true);
        driver.SuccessfulLandingSink = receipt => coordinator.HandleLanding(receipt);

        try
        {
            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            Assert.True(afterSuccessfulLandingCalled);
            Assert.Equal(
                breakSqliteStore ? AcceptanceEngineHealth.Unavailable : AcceptanceEngineHealth.Healthy,
                circuit.Read().Health);
        }
        finally
        {
            PostLandingCanaryEmergencyCircuit.Clear(dbPath);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver rereads circuit before parallel completion can invoke land")]
    public void ConductorDriverHoldsAtLandingMutationBoundary()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var landCalled = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            land: g =>
            {
                landCalled = true;
                return new LandingResult(
                    g.Id.Value,
                    g.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "Landed");
            });
        driver.LandingMutationBlocker = () => "acceptance engine circuit is Pending";

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.False(landCalled);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Contains("mutation boundary", held.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver treats a circuit race inside landing as a retryable hold")]
    public void ConductorDriverHoldsWhenCircuitOpensInsideLanding()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var escalationWritten = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            land: g => new LandingResult(
                g.Id.Value,
                g.Id.Value[..8],
                new LandingDecision.Escalate(
                    "landing mutation blocked: acceptance engine circuit is Pending"),
                "integration",
                false,
                "Landing held before merge"),
            writeEscalation: (_, _, _) => escalationWritten = true);
        driver.LandingMutationBlocker = () => null;

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.False(escalationWritten);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Contains("circuit is Pending", held.Reason, StringComparison.Ordinal);
    }

    // ── Merged state ──────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Merged_records_and_returns_Executed")]
    public void ConductorDriverMergedRecordsAndReturnsExecuted()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var recorded = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(IsMerged: true),
            record: _ => { recorded = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(recorded);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.Merged, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    // ── Recorded state ────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Recorded_cleans_up_and_returns_Executed")]
    public void ConductorDriverRecordedCleansUpAndReturnsExecuted()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var cleanedUp = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(IsMerged: true, IsRecorded: true),
            cleanup: _ =>
            {
                cleanedUp = true;
                return new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(cleanedUp);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.Recorded, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Recorded_incomplete_cleanup_completes_with_deferred_diagnostics")]
    public void ConductorDriverRecordedIncompleteCleanupCompletesWithDeferredDiagnostics()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var cleanupCalls = 0;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(IsMerged: true, IsRecorded: true),
            cleanup: _ =>
            {
                cleanupCalls++;
                return new GoalWorktreeRemoveResult(
                    "Removed workspace, but leftover directory cleanup is incomplete.",
                    @"C:\repo\.orchestrator-worktrees\abc12345",
                    [new WorktreeLockHolder(1234, "dotnet", "dotnet test")],
                    "conduct abc12345 --loop",
                    CleanupBackoff: new GoalWorktreeCleanupBackoff(
                        "remove:cleanup-budget-exhausted",
                        DateTimeOffset.Parse("2026-07-02T05:01:00Z"),
                        TimeSpan.FromMinutes(1)));
            },
            completeGoal: g => kernel.CompleteGoal(g.Id, "test completed after deferred cleanup."));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Recorded, executed.FromState);
        Assert.Equal(1, cleanupCalls);
        Assert.True(executed.Description.Contains("leftover", StringComparison.Ordinal), executed.Description);
        Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Recorded_cleanup_failure_completes_without_throwing")]
    public void ConductorDriverRecordedCleanupFailureCompletesWithoutThrowing()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(IsMerged: true, IsRecorded: true),
            cleanup: _ => throw new InvalidOperationException("git worktree remove refused dirty workspace"),
            completeGoal: g => kernel.CompleteGoal(g.Id, "test completed after deferred cleanup."));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Recorded, executed.FromState);
        Assert.Contains("Workspace cleanup deferred after removal failure", executed.Description, StringComparison.Ordinal);
        Assert.Contains("git worktree remove refused dirty workspace", executed.Description, StringComparison.Ordinal);
        Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
    }

    // ── CleanedUp state ───────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_CleanedUp_returns_Done")]
    public void ConductorDriverCleanedUpReturnsDone()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(IsMerged: true, IsRecorded: true, IsCleanedUp: true));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Done);
        Assert.Equal(GoalLifecycleState.CleanedUp, ((ConductorAdvanceOutcome.Done)result.Outcome).State);
    }

    // ── Error states ──────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Failed_task_escalates_regardless_of_policy")]
    public void ConductorDriverFailedTaskEscalatesRegardlessOfPolicy()
    {
        var (kernel, goal) = SimpleGoal();
        FailVerification(kernel, goal, goal.Tasks.Single());

        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Failed_task_escalation_surfaces_latest_TaskFailed_reason")]
    public void ConductorDriverFailedTaskEscalationSurfacesLatestTaskFailedReason()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(
            goal.Id,
            task.Id,
            WorkTaskStatus.Failed,
            "Reviewer WORKER_RESULT criteria attestation invalid: missing criterion_index 0.");
        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Contains("TaskFailed: Reviewer WORKER_RESULT criteria attestation invalid", escalated.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_cancelled_goal_does_not_surface_task_failure_as_cancellation_reason")]
    public void ConductorDriverCancelledGoalDoesNotSurfaceTaskFailureAsCancellationReason()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "stale task failure");
        kernel.CancelGoal(goal.Id, "Operator cancelled the goal.");
        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.DoesNotContain("TaskFailed:", escalated.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_failed_goal_does_not_surface_failure_from_task_that_is_now_completed")]
    public void ConductorDriverFailedGoalDoesNotSurfaceFailureFromTaskThatIsNowCompleted()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        kernel.EscalateTaskFailure(goal.Id, task.Id, "stale task failure");
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "task recovered");
        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.DoesNotContain("TaskFailed:", escalated.Reason, StringComparison.Ordinal);
    }

}
