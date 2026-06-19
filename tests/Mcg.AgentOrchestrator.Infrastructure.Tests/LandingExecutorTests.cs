using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class LandingExecutorTests
{
    [Xunit.Fact(DisplayName = "LandingExecutor_failed_count_excludes_auto_recovered_empty_output_flake")]
    public void LandingExecutorFailedCountExcludesAutoRecoveredEmptyOutputFlake()
    {
        var (kernel, goal) = CreateGoal(AgentRole.Developer);
        var task = goal.Tasks.Single();

        FlakeThenPass(kernel, goal, task);

        Assert.Equal(0, LandingExecutor.CountFailedVerifications(goal));
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_failed_count_includes_recovered_real_failure")]
    public void LandingExecutorFailedCountIncludesRecoveredRealFailure()
    {
        var (kernel, goal) = CreateGoal(AgentRole.Developer);
        var task = goal.Tasks.Single();

        FailThenPass(kernel, goal, task);

        Assert.Equal(1, LandingExecutor.CountFailedVerifications(goal));
    }

    [Xunit.Fact(DisplayName = "LandingDecision_escalates_when_two_genuine_failed_tasks_recovered")]
    public void LandingDecisionEscalatesWhenTwoGenuineFailedTasksRecovered()
    {
        var (kernel, goal) = CreateGoal(AgentRole.Developer, AgentRole.Tester);

        foreach (var task in goal.Tasks)
        {
            FailThenPass(kernel, goal, task);
        }

        var failedCount = LandingExecutor.CountFailedVerifications(goal);
        var decision = LandingDecisionEngine.Decide(new LandingInputs(
            RepositoryChangeClassifier.Classify(["README.md"]),
            AcceptancePassed: true,
            IntegrationToMainIsCleanFastForward: true,
            GoalFailureRetryCount: failedCount));

        Assert.Equal(LandingDecisionEngine.RepeatedFailureThreshold, failedCount);
        if (decision is not LandingDecision.Escalate escalation)
        {
            throw new InvalidOperationException("Expected landing escalation.");
        }

        Assert.True(escalation.Reason.Contains("repeated failures", StringComparison.OrdinalIgnoreCase));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateGoal(params AgentRole[] roles)
    {
        var kernel = new AgentOrchestratorKernel();
        var tasks = roles
            .Select(role => new TaskSpec(TaskId.New(), $"Run {role} task.", role))
            .ToArray();
        var goal = kernel.CreateGoal("Landing count test", tasks);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return (kernel, goal);
    }

    private static void FlakeThenPass(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        Dispatch(kernel, goal, task, "silent-agent");
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "silent-agent",
            "C:\\repo",
            0,
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow));
        kernel.RetryTask(goal.Id, task.Id, "Auto-retry transient empty-output dispatch flake.");
        Pass(kernel, goal, task);
    }

    private static void FailThenPass(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        Dispatch(kernel, goal, task, "failing-agent");
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "failing-agent",
            "C:\\repo",
            1,
            "attempted work",
            "test failed",
            DateTimeOffset.UtcNow));
        kernel.RetryTask(goal.Id, task.Id, "Fix real failure.");
        Pass(kernel, goal, task);
    }

    private static void Pass(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        Dispatch(kernel, goal, task, "passing-agent");
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "passing-agent",
            "C:\\repo",
            0,
            "WORKER_RESULT: tests pass",
            string.Empty,
            DateTimeOffset.UtcNow));
    }

    private static void Dispatch(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string command)
    {
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "test-worker",
            command,
            "C:\\repo",
            DateTimeOffset.UtcNow));
    }
}
