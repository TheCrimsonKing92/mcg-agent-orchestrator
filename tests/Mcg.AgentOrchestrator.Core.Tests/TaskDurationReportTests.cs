using Mcg.AgentOrchestrator.Core;

public sealed class TaskDurationReportTests
{
    private const string WorkDir = "C:\\work";
    private const string Command = "codex exec prompt.md";

    [Xunit.Fact(DisplayName = "TaskDurationReport_separates_successful_runtime_from_failure_and_retry_gap")]
    public void TaskDurationReportSeparatesSuccessfulRuntimeFromFailureAndRetryGap()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Implement a complex CLI report", [new TaskSpec(TaskId.New(), "Implement src/App.cs with tests", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();

        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(5));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Verification(exitCode: 1, clock.UtcNow));

        clock.Advance(TimeSpan.FromMinutes(15));
        kernel.RetryTask(goal.Id, task.Id, "retry after operator recovery");
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(10));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Verification(exitCode: 0, clock.UtcNow));

        var record = TaskDurationReport.BuildByRoleAndComplexity(kernel.Goals).Single();

        Xunit.Assert.Equal(AgentRole.Developer, record.Role);
        Xunit.Assert.Equal(TaskComplexity.Complex, record.Complexity);
        Xunit.Assert.Equal(2, record.AttemptCount);
        Xunit.Assert.Equal(1, record.FailedAttemptCount);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(10), record.MedianLegitimateRuntime);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(10), record.P90LegitimateRuntime);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(20), record.MedianFailureInterventionOverhead);
        Xunit.Assert.Equal(0.5, record.FailureRate);
    }

    private static TaskDispatchRecord Dispatch(DateTimeOffset at) =>
        new(
            "codex-cli",
            Command,
            WorkDir,
            at,
            ProviderName: "OpenAI",
            ModelName: "gpt-5.5",
            TaskComplexity: TaskComplexity.Complex);

    private static TaskVerificationRecord Verification(int exitCode, DateTimeOffset at) =>
        new(
            Command,
            WorkDir,
            exitCode,
            exitCode == 0 ? "ok" : "failed",
            exitCode == 0 ? string.Empty : "error",
            at);
}
