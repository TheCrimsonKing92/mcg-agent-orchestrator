using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsFailedGoalRecoveryInterpreter
{
    [Xunit.Fact]
    public void StaleContextDoesNotSpendRetryBudgetOrStartWorker()
    {
        var (kernel, goal, failedTask) = FailedLaunchGoal();
        var retryCalls = 0;
        var dispatchCalls = 0;
        var initialRetryCount = failedTask.EmptyOutputRetryCount;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (goalId, taskId, message) =>
            {
                retryCalls++;
                return kernel.RetryTask(goalId, taskId, message);
            },
            dispatchAndStart: _ =>
            {
                dispatchCalls++;
                return DispatchStartOutcome.Started();
            },
            beforeFailedGoalRecoveryEffect: (_, _) =>
                kernel.RecordTaskNote(goal.Id, failedTask.Id, "concurrent observation changed the recovery version"));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Contains("stale-recovery-facts", held.Reason, StringComparison.Ordinal);
        Assert.Equal(0, retryCalls);
        Assert.Equal(0, dispatchCalls);
        Assert.Equal(initialRetryCount, failedTask.EmptyOutputRetryCount);
    }

    [Xunit.Fact]
    public void SiblingStartingMidTickPreventsDuplicateWorkerStart()
    {
        var (kernel, goal) = SoftwareGoal();
        var failedTask = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        var sibling = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
        FailLaunchTwice(kernel, goal, failedTask);
        var retryCalls = 0;
        var dispatchCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (goalId, taskId, message) =>
            {
                retryCalls++;
                return kernel.RetryTask(goalId, taskId, message);
            },
            dispatchAndStart: _ =>
            {
                dispatchCalls++;
                return DispatchStartOutcome.Started();
            },
            beforeFailedGoalRecoveryEffect: (_, _) =>
            {
                DispatchTask(kernel, goal, sibling, "sibling-worker");
                kernel.RecordTaskProcessStarted(
                    goal.Id,
                    sibling.Id,
                    new TaskProcessRecord(
                        7788,
                        "sibling-worker",
                        "C:\\tmp",
                        "C:\\tmp\\sibling.out",
                        "C:\\tmp\\sibling.err",
                        "C:\\tmp\\sibling.exit",
                        DateTimeOffset.UtcNow,
                        CompletedAt: null,
                        ExitCode: null));
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Contains("stale-recovery-facts", held.Reason, StringComparison.Ordinal);
        Assert.True(sibling.LastProcess?.IsRunning);
        Assert.Equal(0, retryCalls);
        Assert.Equal(0, dispatchCalls);
    }

    [Xunit.Fact]
    public void OperatorDispositionMidTickSurvivesWithoutDriverOverride()
    {
        var (kernel, goal, failedTask) = FailedLaunchGoal();
        var retryCalls = 0;
        var dispatchCalls = 0;
        const string operatorReason = "operator selected a fresh retry disposition";
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (goalId, taskId, message) =>
            {
                retryCalls++;
                return kernel.RetryTask(goalId, taskId, message);
            },
            dispatchAndStart: _ =>
            {
                dispatchCalls++;
                return DispatchStartOutcome.Started();
            },
            beforeFailedGoalRecoveryEffect: (_, _) =>
                kernel.RetryTask(goal.Id, failedTask.Id, operatorReason));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Contains("stale-recovery-facts", held.Reason, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Assigned, failedTask.Status);
        Assert.Contains(goal.Timeline, item => item.Message.Contains(operatorReason, StringComparison.Ordinal));
        Assert.Equal(0, retryCalls);
        Assert.Equal(0, dispatchCalls);
    }

    [Xunit.Fact]
    public void MissingTypedCauseEscalatesWithoutSpendingCriterionRetryBudget()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                "test.exe",
                "C:\\tmp",
                1,
                string.Empty,
                "ParserError: Unexpected token in worker command.",
                DateTimeOffset.UtcNow));
        var feedbackCalls = 0;
        var retryCalls = 0;
        string? escalationReason = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            recordCriterionRetryFeedback: (_, _, _) =>
            {
                feedbackCalls++;
                return -1;
            },
            retryTask: (goalId, taskId, message) =>
            {
                retryCalls++;
                return kernel.RetryTask(goalId, taskId, message);
            },
            writeEscalation: (_, _, reason) => escalationReason = reason);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Contains("without a typed retry cause", escalationReason, StringComparison.Ordinal);
        Assert.Equal(0, feedbackCalls);
        Assert.Equal(0, retryCalls);
        Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
        Assert.Equal(0, task.CriterionRetryCount);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec FailedTask) FailedLaunchGoal()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        FailLaunchTwice(kernel, goal, task);
        return (kernel, goal, task);
    }

    private static void FailLaunchTwice(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            DispatchTask(kernel, goal, task);
            kernel.RecordDispatchExecutionResult(
                goal.Id,
                task.Id,
                new TaskVerificationRecord(
                    "test.exe",
                    "C:\\tmp",
                    1,
                    string.Empty,
                    string.Empty,
                    DateTimeOffset.UtcNow,
                    DispatchStartedAt: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30)));
            if (attempt == 0)
                kernel.RetryTask(goal.Id, task.Id, "seed transient retry");
        }
    }
}
