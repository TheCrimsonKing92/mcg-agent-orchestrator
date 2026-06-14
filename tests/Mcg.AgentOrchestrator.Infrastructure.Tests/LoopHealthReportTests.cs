using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class LoopHealthReportTests
{
    private static readonly IReadOnlyList<AgentDefinition> DefaultAgents =
        AgentCatalog.Default().Agents;

    private const string WorkDir = "C:\\work";
    private const string DispatchCommand = "worker-cli run";

    // Three-goal fixture used by most tests:
    //   Goal 1 – retry goal (1 task): dispatch fails → RetryTask → dispatch passes → Goal Completed
    //   Goal 2 – false-completion goal (2 tasks): Task1 exits 0 but file-change guard fires the rejection
    //            marker in StandardError; Task2 stays pending so goal remains Active
    //   Goal 3 – clean one-shot goal (1 task): single dispatch passes → Goal Completed

    [Xunit.Fact(DisplayName = "LoopHealth_dispatches_per_successful_merge_counts_dispatch_events_over_completed_goals")]
    public void LoopHealthDispatchesPerSuccessfulMergeCountsDispatchEventsOverCompletedGoals()
    {
        var (kernel, _, _, _) = BuildFixture();

        var report = kernel.BuildLoopHealthReport();

        // Dispatches: retry=2, false-completion=1, clean=1 → 4 total
        // Completed goals: retry goal + clean goal = 2 (false-completion goal is Active)
        Assert.Equal(4, report.TotalDispatchCount);
        Assert.Equal(2, report.CompletedGoalCount);
        Assert.Equal(2.0, report.DispatchesPerSuccessfulMerge);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_false_completion_catch_rate_counts_tasks_with_false_positive_rejection_marker")]
    public void LoopHealthFalseCompletionCatchRateCountsTasksWithFalsePositiveRejectionMarker()
    {
        var (kernel, _, _, _) = BuildFixture();

        var report = kernel.BuildLoopHealthReport();

        // 4 total tasks (1+2+1); Task2 of Goal2 has no verifications → 3 tasks have any verification.
        // Only Goal2's Task1 has a verification whose StandardError contains the rejection marker → rate = 1/3.
        Assert.Equal(4, report.TotalTaskCount);
        var expectedRate = 1.0 / 3.0;
        Assert.True(Math.Abs(report.FalseCompletionCatchRate - expectedRate) < 0.01);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_false_completion_catch_rate_does_not_count_plain_pass_then_fail_without_marker")]
    public void LoopHealthFalseCompletionCatchRateDoesNotCountPlainPassThenFailWithoutMarker()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Unrelated fail goal", [MakeTask()]);
        kernel.ActivateGoal(goal.Id, DefaultAgents);
        var task = goal.Tasks[0];

        // First verification: passes
        RecordCompletedDispatch(kernel, goal, task, "Anthropic", "claude-sonnet-4-6");

        // Second verification: fails but NOT due to the false-positive rejection guard
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                "dotnet test",
                WorkDir,
                1,
                "Test failed.",
                "Error: 3 tests failed — not a false-positive rejection",
                DateTimeOffset.UtcNow));

        var report = kernel.BuildLoopHealthReport();

        // One task has verifications; plain pass→fail without the marker must NOT be counted.
        Assert.Equal(0.0, report.FalseCompletionCatchRate);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_rework_retry_rate_counts_tasks_with_TaskRetried_timeline_events")]
    public void LoopHealthReworkRetryRateCountsTasksWithTaskRetriedTimelineEvents()
    {
        var (kernel, _, _, _) = BuildFixture();

        var report = kernel.BuildLoopHealthReport();

        // Only Goal1's task has a TaskRetried event; 4 total tasks → rate = 1/4
        var expectedRate = 1.0 / 4.0;
        Assert.True(Math.Abs(report.ReworkRetryRate - expectedRate) < 0.01);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_operator_prompts_per_goal_counts_human_input_requests")]
    public void LoopHealthOperatorPromptsPerGoalCountsHumanInputRequests()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal1 = kernel.CreateGoal("Goal A", [MakeTask()]);
        var goal2 = kernel.CreateGoal("Goal B", [MakeTask()]);
        kernel.ActivateGoal(goal1.Id, DefaultAgents);
        kernel.ActivateGoal(goal2.Id, DefaultAgents);

        kernel.RequestHumanInput(goal1.Id, null, "Is this the right approach?");

        var report = kernel.BuildLoopHealthReport();

        Assert.Equal(2, report.GoalCount);
        Assert.Equal(0.5, report.OperatorPromptsPerGoal);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_median_time_to_acceptance_is_null_when_no_completed_goals")]
    public void LoopHealthMedianTimeToAcceptanceIsNullWhenNoCompletedGoals()
    {
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal("Incomplete goal", [MakeTask()]);

        var report = kernel.BuildLoopHealthReport();

        Assert.True(report.MedianTimeToAcceptanceHours is null);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_median_time_to_acceptance_returns_value_for_completed_goals")]
    public void LoopHealthMedianTimeToAcceptanceReturnsValueForCompletedGoals()
    {
        var (kernel, _, _, _) = BuildFixture();

        var report = kernel.BuildLoopHealthReport();

        // retry goal and clean goal are both Completed; median should be non-null and >= 0
        Assert.Equal(2, report.CompletedGoalCount);
        Assert.True(report.MedianTimeToAcceptanceHours.HasValue);
        Assert.True(report.MedianTimeToAcceptanceHours!.Value >= 0.0);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_per_model_outcome_mix_delegates_to_ModelOutcomeScorecard")]
    public void LoopHealthPerModelOutcomeMixDelegatesToModelOutcomeScorecard()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal1 = kernel.CreateGoal("Feature X", [MakeTask()]);
        var goal2 = kernel.CreateGoal("Feature Y", [MakeTask()]);
        kernel.ActivateGoal(goal1.Id, DefaultAgents);
        kernel.ActivateGoal(goal2.Id, DefaultAgents);

        RecordCompletedDispatch(kernel, goal1, goal1.Tasks[0], "Anthropic", "claude-sonnet-4-6");
        RecordCompletedDispatch(kernel, goal2, goal2.Tasks[0], "Anthropic", "claude-sonnet-4-6");

        var report = kernel.BuildLoopHealthReport();

        var record = report.ModelOutcomeMix.Single(r =>
            r.ProviderName == "Anthropic" && r.ModelName == "claude-sonnet-4-6");
        Assert.Equal(ModelOutcomeRecommendation.Prefer, record.Recommendation);
        Assert.Equal(2, record.Completed);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_lastN_window_restricts_to_most_recent_N_goals")]
    public void LoopHealthLastNWindowRestrictsToMostRecentNGoals()
    {
        var kernel = new AgentOrchestratorKernel();
        for (var i = 0; i < 5; i++)
        {
            kernel.CreateGoal($"Goal {i}", [MakeTask()]);
        }

        var allReport = kernel.BuildLoopHealthReport();
        var windowedReport = kernel.BuildLoopHealthReport(lastN: 3);

        Assert.Equal(5, allReport.GoalCount);
        Assert.Equal(3, windowedReport.GoalCount);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_empty_state_returns_zero_metrics_without_exception")]
    public void LoopHealthEmptyStateReturnsZeroMetricsWithoutException()
    {
        var kernel = new AgentOrchestratorKernel();

        var report = kernel.BuildLoopHealthReport();

        Assert.Equal(0, report.GoalCount);
        Assert.Equal(0, report.CompletedGoalCount);
        Assert.Equal(0, report.TotalTaskCount);
        Assert.Equal(0.0, report.DispatchesPerSuccessfulMerge);
        Assert.Equal(0.0, report.FalseCompletionCatchRate);
        Assert.Equal(0.0, report.OperatorPromptsPerGoal);
        Assert.Equal(0.0, report.ReworkRetryRate);
        Assert.True(report.MedianTimeToAcceptanceHours is null);
    }

    // Builds the canonical three-goal fixture.
    // Goal 1 (1 task)  – retry: dispatch fails, RetryTask, dispatch passes → Completed
    // Goal 2 (2 tasks) – false-completion: Task1 exits 0, file-change guard fires rejection marker;
    //                    Task2 stays pending so goal remains Active
    // Goal 3 (1 task)  – clean one-shot: dispatch passes → Completed
    private static (AgentOrchestratorKernel, Goal RetryGoal, Goal FalseCompletionGoal, Goal CleanGoal) BuildFixture()
    {
        var kernel = new AgentOrchestratorKernel();

        // Goal 1: retry goal
        var retryGoal = kernel.CreateGoal("Retry goal", [MakeTask()]);
        kernel.ActivateGoal(retryGoal.Id, DefaultAgents);
        var retryTask = retryGoal.Tasks[0];
        RecordFailedDispatch(kernel, retryGoal, retryTask);
        kernel.RetryTask(retryGoal.Id, retryTask.Id, "Retrying after build failure.");
        RecordCompletedDispatch(kernel, retryGoal, retryTask, "Anthropic", "claude-sonnet-4-6");

        // Goal 2: false-completion goal — two tasks so goal stays Active while Task1 has a caught false completion
        var fcGoal = kernel.CreateGoal("False-completion goal", [MakeTask(), MakeTask()]);
        kernel.ActivateGoal(fcGoal.Id, DefaultAgents);
        var fcTask1 = fcGoal.Tasks[0];
        RecordCompletedDispatch(kernel, fcGoal, fcTask1, "Anthropic", "claude-sonnet-4-6");
        // False-positive rejection: the file-change guard fires and emits the rejection marker in StandardError.
        kernel.RecordTaskVerification(
            fcGoal.Id,
            fcTask1.Id,
            new TaskVerificationRecord(
                "worker-cli run",
                WorkDir,
                1,
                string.Empty,
                "Developer/Tester dispatch exited 0 but did not produce required relevant file-change evidence. Blocking completion.",
                DateTimeOffset.UtcNow));

        // Goal 3: clean one-shot
        var cleanGoal = kernel.CreateGoal("Clean goal", [MakeTask()]);
        kernel.ActivateGoal(cleanGoal.Id, DefaultAgents);
        RecordCompletedDispatch(kernel, cleanGoal, cleanGoal.Tasks[0], "Anthropic", "claude-sonnet-4-6");

        return (kernel, retryGoal, fcGoal, cleanGoal);
    }

    private static TaskSpec MakeTask() =>
        new(TaskId.New(), "Developer implementation task", AgentRole.Developer);

    private static void RecordCompletedDispatch(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        string providerName = "Anthropic",
        string modelName = "claude-sonnet-4-6")
    {
        var dispatch = new TaskDispatchRecord(
            "worker-cli",
            DispatchCommand,
            WorkDir,
            DateTimeOffset.UtcNow,
            ProviderName: providerName,
            ModelName: modelName);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);

        var verification = new TaskVerificationRecord(
            DispatchCommand,
            WorkDir,
            0,
            "Task completed successfully.",
            string.Empty,
            DateTimeOffset.UtcNow);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);
    }

    private static void RecordFailedDispatch(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task)
    {
        var dispatch = new TaskDispatchRecord(
            "worker-cli",
            DispatchCommand,
            WorkDir,
            DateTimeOffset.UtcNow);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);

        var verification = new TaskVerificationRecord(
            DispatchCommand,
            WorkDir,
            1,
            "Task failed.",
            "Error: build failed",
            DateTimeOffset.UtcNow);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);
    }
}
