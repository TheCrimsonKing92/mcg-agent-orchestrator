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
        Xunit.Assert.False(record.HasPublishedStats);
        Xunit.Assert.Null(TaskDurationReport.FindEstimate([record], AgentRole.Developer, TaskComplexity.Complex));
    }

    [Xunit.Fact(DisplayName = "TaskDurationReport_uses_single_successful_attempt_as_legitimate_runtime")]
    public void TaskDurationReportUsesSingleSuccessfulAttemptAsLegitimateRuntime()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Implement a complex CLI report", [new TaskSpec(TaskId.New(), "Implement src/App.cs with tests", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();

        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(6));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Verification(exitCode: 0, clock.UtcNow));
        kernel.RetryTask(goal.Id, task.Id, "retry should make a separate successful attempt");
        clock.Advance(TimeSpan.FromMinutes(2));
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(9));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Verification(exitCode: 0, clock.UtcNow));

        var record = TaskDurationReport.BuildByRoleAndComplexity(kernel.Goals).Single();

        Xunit.Assert.Equal(TimeSpan.FromMinutes(9), record.MedianLegitimateRuntime);
        Xunit.Assert.Equal(TimeSpan.Zero, record.MedianFailureInterventionOverhead);
        Xunit.Assert.Equal(0, record.FailedAttemptCount);
    }

    [Xunit.Fact(DisplayName = "TaskDurationReport_counts_watchdog_reap_and_recovery_gap_as_overhead")]
    public void TaskDurationReportCountsWatchdogReapAndRecoveryGapAsOverhead()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Implement a complex CLI report", [new TaskSpec(TaskId.New(), "Implement src/App.cs with tests", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();

        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, Process(clock.UtcNow, completedAt: null, wasCancelled: false));
        clock.Advance(TimeSpan.FromMinutes(38));
        kernel.RecordTaskProcessCancelled(goal.Id, task.Id, Process(clock.UtcNow.AddMinutes(-38), clock.UtcNow, wasCancelled: true));
        clock.Advance(TimeSpan.FromMinutes(7));
        kernel.RetryTask(goal.Id, task.Id, "retry after watchdog recovery");
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(10));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Verification(exitCode: 0, clock.UtcNow));

        var record = TaskDurationReport.BuildByRoleAndComplexity(kernel.Goals).Single();

        Xunit.Assert.Equal(2, record.AttemptCount);
        Xunit.Assert.Equal(1, record.FailedAttemptCount);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(10), record.MedianLegitimateRuntime);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(45), record.MedianFailureInterventionOverhead);
    }

    [Xunit.Fact(DisplayName = "TaskDurationReport_publishes_estimate_only_after_minimum_samples")]
    public void TaskDurationReportPublishesEstimateOnlyAfterMinimumSamples()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        for (var index = 0; index < TaskDurationReport.MinSamplesForPublishedStats; index++)
        {
            var goal = kernel.CreateGoal($"Implement complex report {index}", [new TaskSpec(TaskId.New(), "Implement src/App.cs with integration tests", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, DefaultAgents());
            var task = goal.Tasks.Single();
            kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
            clock.Advance(TimeSpan.FromMinutes(10 + index));
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Verification(exitCode: 0, clock.UtcNow));
        }

        var record = TaskDurationReport.BuildByRoleAndComplexity(kernel.Goals).Single();

        Xunit.Assert.True(record.HasPublishedStats);
        Xunit.Assert.Same(record, TaskDurationReport.FindEstimate([record], AgentRole.Developer, TaskComplexity.Complex));
    }

    [Xunit.Fact(DisplayName = "TaskDurationReport_since_excludes_out_of_window_attempts")]
    public void TaskDurationReportSinceExcludesOutOfWindowAttempts()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Implement a complex CLI report", [new TaskSpec(TaskId.New(), "Implement src/App.cs with tests", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();

        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(5));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Verification(exitCode: 1, clock.UtcNow));

        clock.Advance(TimeSpan.FromDays(2));
        var since = clock.UtcNow;
        kernel.RetryTask(goal.Id, task.Id, "retry after old failed attempt");
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(10));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Verification(exitCode: 0, clock.UtcNow));

        var record = TaskDurationReport.BuildByRoleAndComplexity(kernel.Goals, since).Single();

        Xunit.Assert.Equal(1, record.TaskCount);
        Xunit.Assert.Equal(1, record.AttemptCount);
        Xunit.Assert.Equal(0, record.FailedAttemptCount);
        Xunit.Assert.Equal(1.0, record.AttemptsPerTask);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(10), record.MedianLegitimateRuntime);
        Xunit.Assert.Equal(TimeSpan.Zero, record.MedianFailureInterventionOverhead);
    }

    [Xunit.Fact(DisplayName = "TaskDurationReport_attempts_per_task_counts_redundancy")]
    public void TaskDurationReportAttemptsPerTaskCountsRedundancy()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Implement a complex CLI report", [new TaskSpec(TaskId.New(), "Implement src/App.cs with tests", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();

        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(5));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Verification(exitCode: 1, clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(5));
        kernel.RetryTask(goal.Id, task.Id, "retry");
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(5));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Verification(exitCode: 0, clock.UtcNow));

        var record = TaskDurationReport.BuildByRoleAndComplexity(kernel.Goals).Single();

        Xunit.Assert.Equal(2, record.AttemptCount);
        Xunit.Assert.Equal(1, record.TaskCount);
        Xunit.Assert.Equal(2.0, record.AttemptsPerTask);
    }

    [Xunit.Fact(DisplayName = "TaskDurationReport_segments_failed_attempts_by_classifier_rule_class")]
    public void TaskDurationReportSegmentsFailedAttemptsByClassifierRuleClass()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        AddFailedThenSuccessfulTask(kernel, clock, "succeeded-worker-result-failing-tests");
        AddFailedThenSuccessfulTask(kernel, clock, "provider-connectivity");
        AddFailedThenSuccessfulTask(kernel, clock, "retry-round-produced-no-commit-and-no-deferral");

        var record = TaskDurationReport.BuildByRoleAndComplexity(kernel.Goals).Single();

        Xunit.Assert.Equal(6, record.AttemptCount);
        Xunit.Assert.Equal(3, record.FailedAttemptCount);
        Xunit.Assert.Equal(1, record.RealFailureAttemptCount);
        Xunit.Assert.Equal(1, record.EnvironmentalFailureAttemptCount);
        Xunit.Assert.Equal(1, record.ManufacturedFixedFailureAttemptCount);
        Xunit.Assert.Equal(0, record.UnknownEraFailureAttemptCount);
        Xunit.Assert.Equal(0.5, record.FailureRate);
        Xunit.Assert.Equal(1.0 / 6.0, record.RealFailureRate);
        Xunit.Assert.Equal(1.0 / 6.0, record.EnvironmentalFailureRate);
        Xunit.Assert.Equal(1.0 / 6.0, record.ManufacturedFixedFailureRate);
        Xunit.Assert.Equal(0, record.UnknownEraFailureRate);
    }

    [Xunit.Fact(DisplayName = "TaskDurationReport_daily_trend_rows_are_ordered")]
    public void TaskDurationReportDailyTrendRowsAreOrdered()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        AddSuccessfulTask(kernel, clock, "Implement day one report");
        clock.Advance(TimeSpan.FromDays(1));
        AddSuccessfulTask(kernel, clock, "Implement day two report");

        var trend = TaskDurationReport.BuildDailyTrend(kernel.Goals);

        Xunit.Assert.Collection(
            trend,
            first =>
            {
                Xunit.Assert.Equal(new DateOnly(2026, 06, 01), first.Day);
                Xunit.Assert.Equal(1, first.TaskCount);
                Xunit.Assert.Equal(1, first.AttemptCount);
            },
            second =>
            {
                Xunit.Assert.Equal(new DateOnly(2026, 06, 02), second.Day);
                Xunit.Assert.Equal(1, second.TaskCount);
                Xunit.Assert.Equal(1, second.AttemptCount);
            });
    }

    private static TaskDispatchRecord Dispatch(DateTimeOffset at) =>
        new(
            "codex-cli",
            Command,
            WorkDir,
            at,
            ProviderName: "OpenAI",
            ModelName: OpenAiSubscriptionModelAlias,
            TaskComplexity: TaskComplexity.Complex);

    private static TaskVerificationRecord Verification(int exitCode, DateTimeOffset at) =>
        new(
            Command,
            WorkDir,
            exitCode,
            exitCode == 0 ? "ok" : "failed",
            exitCode == 0 ? string.Empty : "error",
            at);

    private static TaskProcessRecord Process(DateTimeOffset startedAt, DateTimeOffset? completedAt, bool wasCancelled) =>
        new(
            1234,
            Command,
            WorkDir,
            "out.log",
            "err.log",
            "exit.txt",
            startedAt,
            completedAt,
            wasCancelled ? null : 1,
            wasCancelled);

    private static void AddSuccessfulTask(AgentOrchestratorKernel kernel, FakeClock clock, string objective)
    {
        var goal = kernel.CreateGoal(objective, [new TaskSpec(TaskId.New(), "Implement src/App.cs with tests", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(5));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Verification(exitCode: 0, clock.UtcNow));
    }

    private static void AddFailedThenSuccessfulTask(AgentOrchestratorKernel kernel, FakeClock clock, string rule)
    {
        var goal = kernel.CreateGoal($"Implement classified report {rule}", [new TaskSpec(TaskId.New(), "Implement src/App.cs with tests", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(5));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Verification(exitCode: 1, clock.UtcNow));
        kernel.RecordTaskNote(goal.Id, task.Id, $"CLASSIFIER rule={rule}; verdict=UnknownFailure");
        clock.Advance(TimeSpan.FromMinutes(1));
        kernel.RetryTask(goal.Id, task.Id, "retry after classified failure");
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(10));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Verification(exitCode: 0, clock.UtcNow));
    }
}
