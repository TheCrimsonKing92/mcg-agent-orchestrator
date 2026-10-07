using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsFailedGoalRecoveryDecision
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Xunit.Fact]
    public void LiveWorkerHoldCarriesPolicyDecisionAndUnchangedReasonAndOwner()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Keep live-worker failure hold",
        [
            new TaskSpec(TaskId.New(), "Failed review", AgentRole.Reviewer),
            new TaskSpec(TaskId.New(), "Live repair", AgentRole.Developer)
        ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var reviewer = goal.Tasks[0];
        var developer = goal.Tasks[1];
        kernel.ReportTaskProgress(goal.Id, reviewer.Id, WorkTaskStatus.Failed, "Open blocking finding.");
        DispatchTask(kernel, goal, developer);
        kernel.RecordTaskProcessStarted(goal.Id, developer.Id, Process(completedAt: null, exitCode: null));
        var contextVersion = ContextVersion(goal);
        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal($"Failure handling deferred while task {developer.Id.Value[..8]} still has a live worker process.", held.Reason);
        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        AssertDecision(held, held.Decision, "Hold", 1, "live-sibling-or-owned-attempt", held.Reason, goal, developer.Id, contextVersion);
    }

    [Xunit.Fact]
    public void MissingTypedCauseEscalationCarriesPolicyDecisionAndUnchangedReason()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", @"C:\tmp", 1, string.Empty,
                "ParserError: Unexpected token in worker command.", ObservedAt));
        var contextVersion = ContextVersion(goal);
        var feedbackCalls = 0;
        var retryCalls = 0;
        string? escalationReason = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            recordCriterionRetryFeedback: (_, _, _) => { feedbackCalls++; return -1; },
            retryTask: (goalId, taskId, message) =>
            {
                retryCalls++;
                return kernel.RetryTask(goalId, taskId, message);
            },
            writeEscalation: (_, _, reason) => escalationReason = reason);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        var expectedReason = $"Task {task.Id.Value[..8]} has a real failure without a typed retry cause; automatic redispatch was held for operator classification.";
        Assert.Equal(expectedReason, escalated.Reason);
        Assert.Equal(expectedReason, escalationReason);
        AssertDecision(escalated, escalated.Decision, "Escalate", 6,
            "real-failure-missing-typed-cause-no-budget-spend", expectedReason, goal, task.Id, contextVersion);
        Assert.Equal(0, feedbackCalls);
        Assert.Equal(0, retryCalls);
        Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
        Assert.Equal(0, task.CriterionRetryCount);
    }

    [Xunit.Fact]
    public void ChangedContextHoldCarriesDriverDecisionWithoutRetryOrDispatch()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        FailLaunchTwice(kernel, goal, task);
        var contextVersion = ContextVersion(goal);
        var initialRetryCount = task.EmptyOutputRetryCount;
        var retryCalls = 0;
        var dispatchCalls = 0;
        FailedGoalRecoveryDecision? policyDecision = null;
        string? actualVersion = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (goalId, taskId, message) =>
            {
                retryCalls++;
                return kernel.RetryTask(goalId, taskId, message);
            },
            dispatchAndStart: _ => { dispatchCalls++; return DispatchStartOutcome.Started(); },
            beforeFailedGoalRecoveryEffect: (_, decision) =>
            {
                policyDecision = decision;
                kernel.RecordTaskNote(goal.Id, task.Id, "concurrent observation changed the recovery version");
                actualVersion = ContextVersion(goal);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.NotNull(policyDecision);
        Assert.Equal(FailedGoalRecoveryAction.RetryTransient, policyDecision.Action);
        Assert.Equal(contextVersion, policyDecision.Identity.ContextVersion);
        Assert.NotEqual(contextVersion, actualVersion);
        var expectedReason = $"{policyDecision.Reason} Recovery effects were held because the goal/task/attempt authority changed before application " +
            $"(stale-recovery-facts; expected={contextVersion}; actual={actualVersion}; state=Failed).";
        Assert.Equal(expectedReason, held.Reason);
        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        AssertDecision(held, held.Decision, "Hold", 101, "stale-recovery-facts", expectedReason, goal, task.Id, contextVersion);
        Assert.Equal(0, retryCalls);
        Assert.Equal(0, dispatchCalls);
        Assert.Equal(initialRetryCount, task.EmptyOutputRetryCount);
    }

    [Xunit.Fact]
    public void ReconciledExitHoldCarriesDriverDecisionAndUnchangedReasonAndOwner()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task, hasCommittedChanges: task.RequiredRole == AgentRole.Developer);
        FailReviewerNeedsWork(kernel, goal, reviewer, "The candidate still has an open blocking finding.");
        kernel.RetryTask(goal.Id, developer.Id, "Route repair for the open finding.", invalidateDownstream: false);
        DispatchTask(kernel, goal, developer);
        var process = Process(ObservedAt);
        kernel.RecordTaskProcessStarted(goal.Id, developer.Id, process);
        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = snapshot.Goals.Single();
        var tasks = goalSnapshot.Tasks.Select(task => task.Id == developer.Id.Value
            ? task with { Status = WorkTaskStatus.Assigned } : task).ToArray();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [goalSnapshot with { Tasks = tasks }] });
        goal = kernel.GetGoal(goal.Id);
        developer = goal.Tasks.Single(task => task.Id == developer.Id);
        var contextVersion = ContextVersion(goal);
        var dispatchCalls = 0;
        var reconcileCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatchCalls++; return DispatchStartOutcome.Started(); },
            retryTask: (goalId, taskId, message) => kernel.RetryTask(goalId, taskId, message),
            reconcileExitedDispatch: (_, taskId) =>
            {
                reconcileCalls++;
                Assert.Equal(developer.Id, taskId);
                kernel.RecordTaskProcessRefreshed(goal.Id, developer.Id, process, SuccessfulDeveloperVerification());
                return true;
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        var expectedReason = $"Reconciled exited dispatch for task {developer.Id.Value[..8]} before failure handling (reconcile-before-failure-handling); deferring the retry decision to the next tick.";
        Assert.Equal(expectedReason, held.Reason);
        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        AssertDecision(held, held.Decision, "Hold", 104, "reconcile-before-failure-handling", expectedReason, goal, developer.Id, contextVersion);
        Assert.Equal(1, reconcileCalls);
        Assert.Equal(0, dispatchCalls);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Contains(developer.VerificationHistory, verification => verification.CompletedAt == ObservedAt && verification.ExitCode == 0);
    }

    private static void AssertDecision(
        ConductorAdvanceOutcome outcome, PolicyDecisionRecord? record, string action, int rung,
        string evidence, string reason, Goal goal, TaskId taskId, string contextVersion)
    {
        Assert.NotNull(record);
        Assert.Equal("failed-goal-recovery", record.Stage);
        Assert.Equal(action, record.Action);
        Assert.Equal(rung, record.Rung);
        Assert.Equal(evidence, record.DiscriminatingEvidence);
        Assert.Equal(reason, record.Reason);
        Assert.Equal(goal.Id.Value, Assert.Single(record.Facts, fact => fact.Name == "goalId").Value);
        Assert.Equal(taskId.Value, Assert.Single(record.Facts, fact => fact.Name == "targetTaskId").Value);
        Assert.Equal(contextVersion, Assert.Single(record.Facts, fact => fact.Name == "contextVersion").Value);
        Assert.Same(record, VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(outcome).Decision);
    }

    private static void FailLaunchTwice(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            DispatchTask(kernel, goal, task);
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
                new TaskVerificationRecord("test.exe", @"C:\tmp", 1, string.Empty, string.Empty,
                    ObservedAt.AddMinutes(attempt), DispatchStartedAt: ObservedAt.AddMinutes(attempt).AddSeconds(-30)));
            if (attempt == 0)
                kernel.RetryTask(goal.Id, task.Id, "seed transient retry");
        }
    }

    private static TaskProcessRecord Process(DateTimeOffset? completedAt, int? exitCode = 0) =>
        new(12345, "test.exe", @"C:\tmp", @"C:\tmp\stdout", @"C:\tmp\stderr", @"C:\tmp\exit",
            StartedAt: ObservedAt.AddMinutes(-1), CompletedAt: completedAt, ExitCode: exitCode);

    private static TaskVerificationRecord SuccessfulDeveloperVerification() =>
        new("test.exe", @"C:\tmp", 0, string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: src/Test.cs", "commands: focused verification",
            "tests: pass - focused verification passed", "commit: none", "blockers: none",
            "model_fit: fixture/model - adequate - regression fixture", "skills: none",
            "confidence: high", "END_WORKER_RESULT"), string.Empty, ObservedAt,
            HasCommittedChanges: true, WorkerResultPresent: true);

    // Exact context-version format from main 719fa28a9; timestamps are identity data, not time bounds.
    private static string ContextVersion(Goal goal)
    {
        var taskState = string.Join(";", goal.Tasks.Select(task => string.Join(":",
            task.Id.Value, task.Status, task.EmptyOutputRetryCount, task.CriterionRetryCount,
            task.LastProcess is { } process
                ? $"process:{process.ProcessId}:{process.StartedAt.UtcTicks}"
                : task.LastVerification is { } verification
                    ? $"verification:{verification.CompletedAt.UtcTicks}:{verification.ChildProcessId?.ToString() ?? "none"}"
                    : "unobserved-attempt",
            task.LastVerification?.CompletedAt.UtcTicks.ToString() ?? "none")));
        var lastEvent = goal.Timeline.LastOrDefault();
        return string.Join("|", goal.Id.Value, goal.Status, GoalLifecycleState.Failed,
            goal.AuthoritativeBrief.Version, goal.AutomaticAcceptanceRetryCount, goal.Timeline.Count,
            lastEvent?.Kind.ToString() ?? "none", lastEvent?.OccurredAt.UtcTicks.ToString() ?? "none", taskState);
    }
}
