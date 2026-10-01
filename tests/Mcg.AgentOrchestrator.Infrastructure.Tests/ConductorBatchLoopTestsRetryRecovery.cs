using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsRetryRecovery : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsRetryRecovery(ITestOutputHelper output)
        : base(output)
    {
    }

    // ── Auto-retry: transient acceptance flake recovers on retry ─────────

    [Xunit.Fact(DisplayName = "BatchLoop_AutoRetry_flakeOnFirstAttempt_recoverOnRetry")]
    public void BatchLoop_AutoRetry_FlakeOnFirstAttemptRecoverOnRetry()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single()); // goal → Completed → Verified state

        var attempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true), // IsMerged=false → Verified state
            runAcceptance: _ => { attempts++; return attempts > 1; }, // fail 1st, pass on retry
            writeEscalation: (_, _, _) => { });

        var stopFile = NoStopPath();
        // 1 tick with maxIterations=1: initial fail → 1 retry (passes) → Advanced
        var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile, maxIterations: 1);

        Assert.Equal(2, attempts);      // initial + 1 retry
        Assert.Equal(1, summary.Retried);
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(1, summary.Advanced);
    }

    // ── Auto-retry: persistent failure escalates after N retries ─────────

    [Xunit.Fact(DisplayName = "BatchLoop_AutoRetry_persistentFailureEscalatesAfterNRetries")]
    public void BatchLoop_AutoRetry_PersistentFailureEscalatesAfterNRetries()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());

        var attempts = 0;
        var escalationWritten = false;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptance: _ => { attempts++; return false; }, // always fail
            writeEscalation: (_, _, _) => { escalationWritten = true; });

        var stopFile = NoStopPath();
        // maxVerifyRetries=2 → 1 initial + 2 retries = 3 total acceptance calls before escalation
        var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative, stopFile,
            maxIterations: 1, maxVerifyRetries: 2);

        Assert.Equal(3, attempts);      // 1 initial + 2 retries
        Assert.Equal(2, summary.Retried);
        Assert.True(escalationWritten);
        Assert.Equal(1, summary.Escalated);
        var tick = Assert.Single(goal.Timeline, evt => evt.Kind == ProgressKind.GoalPolicyDecision
            && evt.TickOutcome?.EscalationKind == nameof(ConductorEscalationKind.AcceptanceVerificationFailed));
        Assert.Equal("Escalated", tick.TickOutcome!.OutcomeKind);
        Assert.Equal("Verified", tick.TickOutcome.LifecycleState);
        Assert.Contains("Acceptance verification failed", tick.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void BatchLoopSkipsRewordedTypedVerifiedAcceptanceEscalation()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        kernel.RecordGoalPolicyDecision(goal.Id, "Operator review is required.",
            new ConductorTickOutcomePayload("Escalated", "Verified", nameof(ConductorEscalationKind.AcceptanceVerificationFailed)));
        var acceptanceAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => { acceptanceAttempts++; return false; });

        var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
            NoStopPath(), maxIterations: 1, maxVerifyRetries: 2);

        Assert.Equal(0, acceptanceAttempts);
        Assert.Equal(0, summary.Retried);
        Assert.Equal(1, summary.Escalated);
    }

    [Xunit.Fact]
    public void BatchLoopIgnoresOldAcceptancePhrasesWhenTypedKindDiffers()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 1: escalated at Verified — Acceptance verification failed",
            new ConductorTickOutcomePayload("Escalated", "Verified", nameof(ConductorEscalationKind.Unspecified)));
        var acceptanceAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptance: _ => { acceptanceAttempts++; return true; });

        new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
            NoStopPath(), maxIterations: 1);

        Assert.Equal(1, acceptanceAttempts);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_skips_prior_verified_acceptance_escalation_without_spending_retry_budget")]
    public void BatchLoopSkipsPriorVerifiedAcceptanceEscalationWithoutSpendingRetryBudget()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 1: escalated at Verified — Acceptance verification failed");

        var acceptanceAttempts = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ =>
            {
                acceptanceAttempts++;
                return false;
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            maxVerifyRetries: 2);

        Assert.Equal(0, acceptanceAttempts);
        Assert.Equal(0, summary.Retried);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(0, summary.Advanced);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_treats_cleaned_up_goal_as_done_despite_prior_verified_acceptance_escalation")]
    public void BatchLoopTreatsCleanedUpGoalAsDoneDespitePriorVerifiedAcceptanceEscalation()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 1: escalated at Verified — Acceptance verification failed");

        var acceptanceAttempts = 0;
        var ticks = new List<BatchTickSummary>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(IsMerged: true, IsRecorded: true, IsCleanedUp: true),
            runAcceptance: _ =>
            {
                acceptanceAttempts++;
                return false;
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            maxVerifyRetries: 2,
            onTick: ticks.Add);

        Assert.Equal(0, acceptanceAttempts);
        Assert.Equal(0, summary.Retried);
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(0, summary.Advanced);
        Assert.Empty(ticks);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_operator_retry_clears_prior_verified_acceptance_escalation")]
    public void BatchLoopOperatorRetryClearsPriorVerifiedAcceptanceEscalation()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 1: escalated at Verified — Acceptance verification failed");
        kernel.RetryTask(goal.Id, task.Id, "Operator retry after fixing acceptance failure.");

        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: g =>
            {
                dispatches++;
                kernel.RecordTaskDispatch(g.Id, task.Id,
                    new TaskDispatchRecord("test-worker", "test.exe", "C:\\goal", DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(1, dispatches);
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(0, summary.Retried);
    }

    // ── Regression: flake recovery followed by ownership-blocked dispatch must not escalate ──
    // Before Fix 1+4, the conductor auto-recovered a watchdog-reaped Developer dispatch via
    // _retryTask (setting the task to Assigned), then on the NEXT tick called _dispatchAndStart
    // which returned EmptyBatch (high-risk ownership under Conservative policy). The empty-batch
    // path immediately escalated → SetAside(LifecycleEscalation) → permanently stuck, even though
    // `readiness` said "Proceed". Fix 1: dispatch in the SAME tick as recovery. Fix 4: on
    // EmptyBatch with assigned tasks, return Held instead of Escalate.

    [Xunit.Fact(DisplayName = "BatchLoop_flake_recovery_then_empty_batch_holds_not_escalates_no_paid_worker")]
    public void BatchLoopFlakeRecoveryThenEmptyBatchHoldsNotEscalatesNoPaidWorker()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("src/Mcg.AgentOrchestrator.Core/Fix something");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var planner = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Planner);
        var researcher = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Researcher);
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var now = DateTimeOffset.UtcNow;

        // Advance Planner/Researcher to Completed so Developer is the current stage.
        kernel.ReportTaskProgress(goal.Id, planner.Id, WorkTaskStatus.Completed, "Planner done.");
        kernel.ReportTaskProgress(goal.Id, researcher.Id, WorkTaskStatus.Completed, "Researcher done.");

        // Simulate a watchdog reap: Developer was dispatched but produced zero-byte stdout (empty flake).
        kernel.RecordTaskDispatch(goal.Id, developer.Id,
            new TaskDispatchRecord("test-worker", "dev.exe", "C:\\goal", now));
        kernel.RecordDispatchExecutionResult(goal.Id, developer.Id,
            new TaskVerificationRecord(
                "dev.exe",
                "C:\\goal",
                1,
                "",
                "",
                now,
                DispatchStartedAt: now - TimeSpan.FromSeconds(30)));

        Assert.Equal(WorkTaskStatus.Failed, kernel.GetTask(goal.Id, developer.Id).Status);
        Assert.Equal(1, developer.EmptyOutputRetryCount);

        var escalated = false;
        var dispatchAttempts = 0;
        var paidWorkerCount = 0;

        // _dispatchAndStart returns EmptyBatch to simulate high-risk ownership blocking
        // under a Conservative policy (the typical trigger for this bug).
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => paidWorkerCount,
            dispatchAndStart: _ =>
            {
                dispatchAttempts++;
                return DispatchStartOutcome.EmptyBatch(
                    "No tasks dispatched; all ready tasks require operator approval: high-risk ownership (src/Mcg.AgentOrchestrator.Core/)");
            },
            writeEscalation: (_, _, _) => { escalated = true; },
            retryTask: (gid, tid, msg) => kernel.RetryTask(gid, tid, msg));

        // Run one tick: Failed state → flake recovery → immediate dispatch attempt → EmptyBatch → Held.
        var summary = new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative,
            NoStopPath(), maxIterations: 1);

        Assert.False(escalated); // must not escalate when empty batch follows flake recovery
        Assert.Equal(0, paidWorkerCount); // no paid worker started (dispatch returned EmptyBatch)
        Assert.Equal(1, dispatchAttempts); // dispatch was attempted in the same tick as recovery (Fix 1)
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, developer.Id).Status);
    }
}
