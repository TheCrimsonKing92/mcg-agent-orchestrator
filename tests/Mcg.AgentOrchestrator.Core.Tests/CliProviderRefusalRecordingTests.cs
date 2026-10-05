using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: no external dispatch runs; time advances only through FakeClock.
public sealed class CliProviderRefusalRecordingTests
{
    [Fact]
    public void WeeklyLimitReassignsOnlyReviewerWithAbsoluteReset()
    {
        var clock = CompletionClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Hold reviewer on weekly limit",
        [
            new TaskSpec(TaskId.New(), "Implement behavior", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Review behavior", AgentRole.Reviewer)
        ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var agentId = reviewer.AssignedAgentId;
        Assert.NotNull(agentId);
        var otherStatuses = goal.Tasks.Where(task => task.Id != reviewer.Id)
            .ToDictionary(task => task.Id, task => task.Status);
        Assert.Single(otherStatuses);
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, ClaudeDispatch("claude review", clock));

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "claude review", "C:\\repo", 1,
            "You've hit your weekly limit \u00B7 resets Oct 7, 12pm (America/Chicago)",
            string.Empty, clock.UtcNow));

        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        Assert.Equal(AgentRole.Reviewer, reviewer.RequiredRole);
        Assert.Equal(agentId, reviewer.AssignedAgentId);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 17, 0, 0, TimeSpan.Zero), reviewer.SubscriptionRetryAfter);
        Assert.NotEqual(GoalStatus.Failed, goal.Status);
        Assert.All(goal.Tasks.Where(task => task.Id != reviewer.Id),
            task => Assert.Equal(otherStatuses[task.Id], task.Status));
        Assert.DoesNotContain(goal.Timeline,
            evt => evt.TaskId == reviewer.Id && evt.Kind == ProgressKind.TaskFailed);
        Assert.Contains(goal.Timeline,
            evt => evt.TaskId == reviewer.Id && evt.Kind == ProgressKind.TaskRetried);
    }

    [Fact]
    public void FourthConsecutive529FailsThroughExistingConnectivityCap()
    {
        var clock = CompletionClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Cap overload retries",
            [new TaskSpec(TaskId.New(), "Implement behavior", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = Assert.Single(goal.Tasks);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var command = $"claude implement attempt {attempt}";
            kernel.RecordTaskDispatch(goal.Id, task.Id, ClaudeDispatch(command, clock));
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
                command, "C:\\repo", 1, "API Error 529 Overloaded", string.Empty, clock.UtcNow));

            if (attempt <= 3)
            {
                Assert.Equal(WorkTaskStatus.Assigned, task.Status);
                Assert.NotEqual(GoalStatus.Failed, goal.Status);
            }

            clock.Advance(TimeSpan.FromMinutes(attempt));
        }

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.NotNull(task.LastVerification);
        Assert.Equal(4, DispatchFailureClassifier.CountRecoverableProviderConnectivityFailures(task));
        var failure = Assert.Single(goal.Timeline.Where(
            evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskFailed));
        Assert.Contains("provider connectivity failed after 3 automatic retry attempt", failure.Message, StringComparison.Ordinal);
        Assert.Contains("529", failure.Message, StringComparison.Ordinal);
    }

    private static FakeClock CompletionClock()
    {
        var clock = new FakeClock();
        clock.Advance(new DateTimeOffset(2026, 10, 5, 10, 12, 56, TimeSpan.Zero) - clock.UtcNow);
        return clock;
    }

    private static TaskDispatchRecord ClaudeDispatch(string command, FakeClock clock) =>
        new("claude-cli", command, "C:\\repo", clock.UtcNow, WorkerProviderKind: ProviderKind.AnthropicClaudeCli);
}
