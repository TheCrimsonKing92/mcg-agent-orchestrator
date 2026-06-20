using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorDriverTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<AgentDefinition> DefaultAgents() => AgentCatalog.Default().Agents;

    private static (AgentOrchestratorKernel Kernel, Goal Goal) SimpleGoal(string objective = "Test goal")
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), objective);
        return (kernel, goal);
    }

    private static void DispatchTask(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        var dispatch = new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
    }

    private static void PassVerification(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        DispatchTask(kernel, goal, task);
        var verification = new TaskVerificationRecord("test.exe", "C:\\tmp", 0, "ok", "", DateTimeOffset.UtcNow);
        kernel.RecordTaskVerification(goal.Id, task.Id, verification);
    }

    private static void FailVerification(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        DispatchTask(kernel, goal, task);
        // RecordDispatchExecutionResult sets WorkTaskStatus.Failed; RecordTaskVerification does not
        var verification = new TaskVerificationRecord("test.exe", "C:\\tmp", 1, "fail", "error", DateTimeOffset.UtcNow);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);
    }

    private static GoalWorktreeRebaseResult DefaultRebaseSuccess() =>
        new(GoalWorktreeRebaseStatus.AlreadyFastForwardable, "goal/test", "Already fast-forwardable", [], null);

    private static ConductorDriver MakeDriver(
        Func<Goal, GoalLifecycleFacts>? getFacts = null,
        Func<int>? getRunningCount = null,
        Func<Goal, string>? createWorkspace = null,
        Func<Goal, DispatchStartOutcome>? dispatchAndStart = null,
        Func<Goal, DispatchStartOutcome>? startRecordedDispatches = null,
        Action? buildServerShutdown = null,
        Func<Goal, bool>? runAcceptance = null,
        Func<Goal, AcceptanceVerificationSummary>? runAcceptanceSummary = null,
        Func<GoalId, TaskId, string, TaskSpec>? retryTask = null,
        Func<GoalId, TaskId, IReadOnlyList<string>, int>? recordCriterionRetryFeedback = null,
        Action<GoalId, TaskId>? clearCriterionRetryFeedback = null,
        Func<Goal, GoalWorktreeRebaseResult>? rebaseOntoMain = null,
        Func<Goal, LandingResult>? land = null,
        Action<Goal>? record = null,
        Action<Goal>? cleanup = null,
        Action<Goal, GoalLifecycleState, string>? writeEscalation = null,
        Func<Goal, ChangeRiskTier?>? classifyRisk = null)
    {
        return new ConductorDriver(
            getFacts ?? (_ => GoalLifecycleFacts.None),
            getRunningCount ?? (() => 0),
            createWorkspace ?? (_ => "/tmp/workspace"),
            dispatchAndStart ?? (_ => DispatchStartOutcome.Started()),
            startRecordedDispatches,
            buildServerShutdown,
            runAcceptanceSummary ?? (goal => (runAcceptance ?? (_ => true))(goal)
                ? AcceptanceVerificationSummary.PassedWithNoUnmetCriteria
                : AcceptanceVerificationSummary.Failed),
            retryTask,
            recordCriterionRetryFeedback,
            clearCriterionRetryFeedback,
            rebaseOntoMain ?? (_ => DefaultRebaseSuccess()),
            land is null
                ? ((g, _) => new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"))
                : ((g, _) => land(g)),
            record ?? (_ => { }),
            cleanup ?? (_ => { }),
            writeEscalation ?? ((_, _, _) => { }),
            classifyRisk ?? (_ => null));
    }

    // ── Empty-batch escalation diagnostics ───────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_batch_surfaces_operator_approval_reasons")]
    public void ConductorDriverEmptyBatchSurfacesOperatorApprovalReasons()
    {
        var plan = new ParallelExecutionPlan(
            [],
            [
                new ParallelExecutionDecision(
                    "task-1",
                    ParallelExecutionDisposition.RequiresOperatorApproval,
                    null,
                    ["high-risk ownership area requires operator approval: Script scripts/Invoke-TestSummary.ps1"])
            ]);

        var reason = ConductorDriver.DescribeEmptyBatch(plan);

        Assert.True(reason.Contains("require operator approval", StringComparison.OrdinalIgnoreCase));
        Assert.True(reason.Contains("scripts/Invoke-TestSummary.ps1", StringComparison.Ordinal));
        Assert.False(reason.Contains("no assigned or ready tasks", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_batch_without_approval_blocks_uses_generic_reason")]
    public void ConductorDriverEmptyBatchWithoutApprovalBlocksUsesGenericReason()
    {
        var plan = new ParallelExecutionPlan([], []);

        var reason = ConductorDriver.DescribeEmptyBatch(plan);

        Assert.True(reason.Contains("no assigned or ready tasks", StringComparison.Ordinal));
    }

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

    [Xunit.Fact(DisplayName = "ConductorDriver_Dispatched_start_failure_escalates_with_reason")]
    public void ConductorDriverDispatchedStartFailureEscalatesWithReason()
    {
        var (kernel, goal) = SimpleGoal();
        DispatchTask(kernel, goal, goal.Tasks.Single());
        var callCount = 0;
        var shutdownCalled = false;
        string? escalationReason = null;
        const string startFailReason = "Recorded dispatch start failed: worker command refused to launch";

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            startRecordedDispatches: _ =>
            {
                callCount++;
                return DispatchStartOutcome.SpawnFailed(startFailReason);
            },
            buildServerShutdown: () => { shutdownCalled = true; },
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(2, callCount);
        Assert.True(shutdownCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(GoalLifecycleState.Dispatched, ((ConductorAdvanceOutcome.Escalated)result.Outcome).State);
        Assert.Equal(startFailReason, escalationReason);
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

    // ── AwaitingVerification state ────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_AwaitingVerification_returns_Held")]
    public void ConductorDriverAwaitingVerificationReturnsHeld()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        // Mark task Completed without verification → MissingVerification gate keeps goal Active,
        // all tasks Completed → GoalLifecycleState.AwaitingVerification.
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "done");

        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Held);
        Assert.Equal(GoalLifecycleState.AwaitingVerification, ((ConductorAdvanceOutcome.Held)result.Outcome).State);
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
        Assert.Contains(escalationReason!, text => text.Contains("pre-landing rebase conflict", StringComparison.Ordinal));
        Assert.Contains(escalationReason!, text => text.Contains("src/Foo.cs", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_unmet_acceptance_criterion_retries_task_with_feedback")]
    public void ConductorDriverVerifiedUnmetAcceptanceCriterionRetriesTaskWithFeedback()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var landCalled = false;
        var retryCalled = false;
        string? retryMessage = null;
        var unmet = new AcceptanceCheckResult(
            "grep-present docs/usage.md contains Ready",
            false,
            1,
            "Pattern 'Ready' was not found.",
            ResultSummary: "docs/usage.md is missing Ready",
            Advisory: true);

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (goalId, taskId, message) =>
            {
                retryCalled = true;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message);
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
        Assert.Contains(retryMessage!, text => text.Contains("docs/usage.md is missing Ready", StringComparison.Ordinal));
        Assert.True(task.CriterionRetryFeedback.Any(item => item.Contains("docs/usage.md is missing Ready", StringComparison.Ordinal)));
        Assert.Contains(brief.Content, text => text.Contains("## Unmet acceptance criteria from the prior attempt - fix these:", StringComparison.Ordinal));
        Assert.Contains(brief.Content, text => text.Contains("docs/usage.md is missing Ready", StringComparison.Ordinal));
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
            ResultSummary: "docs/usage.md missing",
            Advisory: true);

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
        Assert.Contains(escalationReason!, text => text.Contains("Acceptance criteria unmet after 1 retries", StringComparison.Ordinal));
        Assert.Contains(escalationReason!, text => text.Contains("docs/usage.md missing", StringComparison.Ordinal));
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
            ResultSummary: "focused command failed",
            Advisory: true);

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (goalId, taskId, message) =>
            {
                retryCalled = true;
                return kernel.RetryTask(goalId, taskId, message);
            },
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, policy);

        Assert.False(retryCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Contains(escalationReason!, text => text.Contains("Acceptance criteria unmet after 0 retries", StringComparison.Ordinal));
        Assert.Contains(escalationReason!, text => text.Contains("focused command failed", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_policy_risk_gate_escalates_Security_risk")]
    public void ConductorDriverVerifiedPolicyRiskGateEscalatesSecurityRisk()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var escalated = false;

        // Conservative: AutoPromoteRiskThreshold = DocsOnly → Security risk → Escalate
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.Security,
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
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
            cleanup: _ => { cleanedUp = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(cleanedUp);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.Recorded, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
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

    [Xunit.Fact(DisplayName = "ConductorDriver_Failed_empty_output_flake_auto_retries_instead_of_escalating")]
    public void ConductorDriverFailedEmptyOutputFlakeAutoRetriesInsteadOfEscalating()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        // Worker exited 0 but produced no output: the kernel marks the task Failed ("verification
        // cannot be confirmed"), putting the goal in the Failed lifecycle state. This is a transient
        // headless-CLI flake, so the conductor should re-dispatch the task rather than escalate.
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", "C:\\tmp", 0, "", "", DateTimeOffset.UtcNow));
        Assert.Equal(WorkTaskStatus.Failed, task.Status);

        var retried = false;
        var escalated = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(retried);
        Assert.False(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Blocked_facts_escalates")]
    public void ConductorDriverBlockedFactsEscalates()
    {
        var (_, goal) = SimpleGoal();

        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(IsBlocked: true));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(GoalLifecycleState.Blocked, ((ConductorAdvanceOutcome.Escalated)result.Outcome).State);
    }

    // ── Manual policy ─────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Manual_policy_escalates_at_Created")]
    public void ConductorDriverManualPolicyEscalatesAtCreated()
    {
        var (_, goal) = SimpleGoal();
        var escalated = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Manual);

        Assert.True(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    // ── Policy-over-engine promotion gate ────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Conservative_policy_blocks_Behavior_risk_before_calling_land")]
    public void ConductorDriverConservativePolicyBlocksBehaviorRiskBeforeCallingLand()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var escalated = false;
        var landCalled = false;

        // Conservative: threshold DocsOnly → Behavior risk → Escalate (even if engine would Promote)
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.Behavior,
            land: g => { landCalled = true; return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"); },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(escalated);
        Assert.False(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    // ── Dispatch-start retry on transient spawn failure ───────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_spawn_fail_then_recorded_start_success_advances_without_escalation")]
    public void ConductorDriverWorkspaceReadySpawnFailThenRecordedStartSuccessAdvancesWithoutEscalation()
    {
        var (_, goal) = SimpleGoal();
        var dispatchStartCalls = 0;
        var recordedStartCalls = 0;
        var shutdownCalled = false;
        var escalated = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ =>
            {
                dispatchStartCalls++;
                return DispatchStartOutcome.SpawnFailed("Dispatched 1 task(s) but no processes started (spawn failed)");
            },
            startRecordedDispatches: _ =>
            {
                recordedStartCalls++;
                return DispatchStartOutcome.Started();
            },
            buildServerShutdown: () => { shutdownCalled = true; },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, dispatchStartCalls);
        Assert.Equal(1, recordedStartCalls);
        Assert.True(shutdownCalled);
        Assert.False(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_spawn_fail_twice_escalates_after_exactly_one_retry")]
    public void ConductorDriverWorkspaceReadySpawnFailTwiceEscalatesAfterExactlyOneRetry()
    {
        var (_, goal) = SimpleGoal();
        var callCount = 0;
        var shutdownCalled = false;
        string? escalationReason = null;
        const string spawnFailReason = "Dispatched 1 task(s) but no processes started (spawn failed)";

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ =>
            {
                callCount++;
                return DispatchStartOutcome.SpawnFailed(spawnFailReason);
            },
            buildServerShutdown: () => { shutdownCalled = true; },
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(2, callCount);
        Assert.True(shutdownCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(spawnFailReason, escalationReason);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_empty_batch_escalates_immediately_without_retry")]
    public void ConductorDriverWorkspaceReadyEmptyBatchEscalatesImmediatelyWithoutRetry()
    {
        var (_, goal) = SimpleGoal();
        var callCount = 0;
        var shutdownCalled = false;
        const string emptyBatchReason = "No tasks in ready batch; goal may have no assigned or ready tasks";

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ =>
            {
                callCount++;
                return DispatchStartOutcome.EmptyBatch(emptyBatchReason);
            },
            buildServerShutdown: () => { shutdownCalled = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, callCount);
        Assert.False(shutdownCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_started_first_try_advances_without_retry")]
    public void ConductorDriverWorkspaceReadyStartedFirstTryAdvancesWithoutRetry()
    {
        var (_, goal) = SimpleGoal();
        var callCount = 0;
        var shutdownCalled = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ =>
            {
                callCount++;
                return DispatchStartOutcome.Started();
            },
            buildServerShutdown: () => { shutdownCalled = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, callCount);
        Assert.False(shutdownCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Permissive_policy_allows_Broad_risk_land")]
    public void ConductorDriverPermissivePolicyAllowsBroadRiskLand()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var landCalled = false;

        // Permissive: threshold Broad → all risk tiers → Auto
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.Broad,
            land: g => { landCalled = true; return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"); });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    // ── Result metadata ───────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_result_carries_goalId_goalPrefix_and_policyName")]
    public void ConductorDriverResultCarriesGoalIdGoalPrefixAndPolicyName()
    {
        var (_, goal) = SimpleGoal();
        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(goal.Id.Value, result.GoalId);
        Assert.Equal(goal.Id.Value[..8], result.GoalPrefix);
        Assert.Equal("Conservative", result.PolicyName);
    }

    // ── Pre-landing rebase (Defect 1 fix) ────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_disjoint_advance_rebase_lands_without_escalation")]
    public void ConductorDriverVerifiedDisjointAdvanceRebaseLandsWithoutEscalation()
    {
        // Goal branch behind an advanced-but-disjoint main: rebase succeeds, landing proceeds.
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var landCalled = false;
        var escalated = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.Broad,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.Rebased, "goal/test", "Rebased onto main", [], null),
            land: g => { landCalled = true; return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"); },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(landCalled);
        Assert.False(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_rebase_conflict_escalates_with_conflict_reason")]
    public void ConductorDriverVerifiedRebaseConflictEscalatesWithConflictReason()
    {
        // Genuine overlapping-hunk conflict: rebase returns Conflict → escalate, land is never called.
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var landCalled = false;
        string? escalationReason = null;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.Broad,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.Conflict, "goal/test",
                "Rebase found conflicts", ["src/Foo.cs"], null),
            land: g => { landCalled = true; return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"); },
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(GoalLifecycleState.Verified, ((ConductorAdvanceOutcome.Escalated)result.Outcome).State);
        Assert.True(escalationReason is not null);
        Assert.Contains(escalationReason!, v => v.Contains("conflict", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(escalationReason!, v => v.Contains("src/Foo.cs", StringComparison.OrdinalIgnoreCase));
    }

    // ── Dispatch-start reason clarity (Defect 2 fix) ─────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_empty_batch_escalation_names_batch_reason")]
    public void ConductorDriverWorkspaceReadyEmptyBatchEscalationNamesBatchReason()
    {
        var (_, goal) = SimpleGoal();
        string? escalationReason = null;
        const string emptyBatchReason = "No tasks in ready batch; goal may have no assigned or ready tasks";

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ => DispatchStartOutcome.EmptyBatch(emptyBatchReason),
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(emptyBatchReason, escalationReason);
        Assert.Contains(escalationReason!, v => v.Contains("batch", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_spawn_failure_escalation_names_spawn_reason")]
    public void ConductorDriverWorkspaceReadySpawnFailureEscalationNamesSpawnReason()
    {
        var (_, goal) = SimpleGoal();
        string? escalationReason = null;
        const string spawnFailReason = "Dispatched 1 task(s) but no processes started (spawn failed)";

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ => DispatchStartOutcome.SpawnFailed(spawnFailReason),
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(spawnFailReason, escalationReason);
        Assert.Contains(escalationReason!, v => v.Contains("spawn", StringComparison.OrdinalIgnoreCase));
    }
}
