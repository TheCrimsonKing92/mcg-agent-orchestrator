using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalObjectivePlannerDurationTests
{
    private const string WorkDir = "C:\\work";
    private const string Command = "codex exec prompt.md";

    [Xunit.Fact(DisplayName = "GoalObjectivePlanner_estimate_text_uses_legitimate_runtime_not_wall_time")]
    public void GoalObjectivePlannerEstimateTextUsesLegitimateRuntimeNotWallTime()
    {
        var stats = new[]
        {
            new TaskDurationStatsRecord(
                AgentRole.Developer,
                TaskComplexity.Complex,
                ProviderName: null,
                ModelName: null,
                TaskCount: 3,
                AttemptCount: 4,
                FailedAttemptCount: 1,
                MedianLegitimateRuntime: TimeSpan.FromMinutes(10),
                P90LegitimateRuntime: TimeSpan.FromMinutes(10),
                MedianFailureInterventionOverhead: TimeSpan.FromMinutes(43),
                FailureRate: 0.5)
        };

        var plan = GoalObjectivePlanner.Build(
            "Design and implement production architecture across src/Mcg.AgentOrchestrator.App/Reports.cs tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ReportsTests.cs with integration tests",
            simple: true,
            stats);

        Xunit.Assert.NotNull(plan.HistoricalTimeEstimate);
        Xunit.Assert.Contains("~10 min legitimate runtime", plan.HistoricalTimeEstimate);
        Xunit.Assert.Contains("excludes 43 min median failure/intervention overhead", plan.HistoricalTimeEstimate);
        Xunit.Assert.Contains("Historical estimate:", plan.Recommendation);
    }

    [Xunit.Fact(DisplayName = "Cli_durations_prints_legitimate_runtime_and_overhead")]
    public void CliDurationsPrintsLegitimateRuntimeAndOverhead()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var clock = new TestClock(new DateTimeOffset(2026, 06, 19, 12, 00, 00, TimeSpan.Zero));
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Implement a complex report", [new TaskSpec(TaskId.New(), "Implement src/App.cs with tests and integration coverage", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.Single();

        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(5));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Verification(exitCode: 1, clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(15));
        kernel.RetryTask(goal.Id, task.Id, "retry after recovery");
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(10));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Verification(exitCode: 0, clock.UtcNow));
        for (var index = 0; index < 2; index++)
        {
            var sampleGoal = kernel.CreateGoal($"Implement another complex report {index}", [new TaskSpec(TaskId.New(), "Implement src/Other.cs with tests and integration coverage", AgentRole.Developer)]);
            kernel.ActivateGoal(sampleGoal.Id, agents);
            var sampleTask = sampleGoal.Tasks.Single();
            kernel.RecordTaskDispatch(sampleGoal.Id, sampleTask.Id, Dispatch(clock.UtcNow));
            clock.Advance(TimeSpan.FromMinutes(5));
            kernel.RecordDispatchExecutionResult(sampleGoal.Id, sampleTask.Id, Verification(exitCode: 1, clock.UtcNow));
            clock.Advance(TimeSpan.FromMinutes(15));
            kernel.RetryTask(sampleGoal.Id, sampleTask.Id, "retry after recovery");
            kernel.RecordTaskDispatch(sampleGoal.Id, sampleTask.Id, Dispatch(clock.UtcNow));
            clock.Advance(TimeSpan.FromMinutes(11 + index));
            kernel.RecordDispatchExecutionResult(sampleGoal.Id, sampleTask.Id, Verification(exitCode: 0, clock.UtcNow));
        }

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["durations"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("Task duration stats", output);
        Xunit.Assert.Contains("Developer/Complex", output);
        Xunit.Assert.Contains("legit median=11m", output);
        Xunit.Assert.Contains("overhead median=20m", output);
        Xunit.Assert.Contains("failureRate=", output);
    }

    [Xunit.Fact(DisplayName = "SimpleGoal_preflight_uses_history_estimate_without_starting_paid_worker")]
    public void SimpleGoalPreflightUsesHistoryEstimateWithoutStartingPaidWorker()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var clock = new TestClock(new DateTimeOffset(2026, 06, 19, 12, 00, 00, TimeSpan.Zero));
        var kernel = new AgentOrchestratorKernel(clock);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        for (var index = 0; index < TaskDurationReport.MinSamplesForPublishedStats; index++)
        {
            var sampleGoal = kernel.CreateGoal($"Implement historical complex report {index}", [new TaskSpec(TaskId.New(), "Implement src/History.cs with tests and integration coverage", AgentRole.Developer)]);
            kernel.ActivateGoal(sampleGoal.Id, agents);
            var sampleTask = sampleGoal.Tasks.Single();
            kernel.RecordTaskDispatch(sampleGoal.Id, sampleTask.Id, Dispatch(clock.UtcNow));
            clock.Advance(TimeSpan.FromMinutes(10));
            kernel.RecordDispatchExecutionResult(sampleGoal.Id, sampleTask.Id, Verification(exitCode: 0, clock.UtcNow));
        }

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["simple-goal", "Design and implement production architecture across src/Mcg.AgentOrchestrator.App/Reports.cs tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ReportsTests.cs with integration tests"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("Historical estimate:", output);
        Xunit.Assert.Contains("~10 min legitimate runtime", output);
        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.All(currentGoal!.Tasks, task => Xunit.Assert.Null(task.LastDispatch));
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

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        private DateTimeOffset _utcNow = utcNow;

        public DateTimeOffset UtcNow => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
