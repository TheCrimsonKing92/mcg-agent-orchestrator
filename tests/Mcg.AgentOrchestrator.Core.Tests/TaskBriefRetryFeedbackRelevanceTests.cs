using Mcg.AgentOrchestrator.Core;

public sealed class TaskBriefRetryFeedbackRelevanceTests
{
    [Xunit.Fact]
    public void TesterBriefHeadlinesUpstreamDeveloperRetryNotLaterReviewerRetry()
    {
        var (clock, kernel, goal, developer, tester, reviewer) = CreateThreeTaskGoal();
        var developerRetryAt = RetryDeveloperTwice(clock, kernel, goal, developer);
        clock.Advance();
        kernel.ReportTaskProgress(goal.Id, reviewer.Id, WorkTaskStatus.Failed, "reviewer prior outcome");
        clock.Advance();
        kernel.RetryTask(goal.Id, reviewer.Id, "reviewer retry downstream");

        var feedback = FeedbackBlock(kernel.BuildTaskBrief(goal.Id, tester.Id).Content);

        Assert.Contains($"Most recent retry: Retry 2 of 2; {developerRetryAt:u}; Task 1 Developer.", feedback, StringComparison.Ordinal);
        Assert.Contains("Prior outcome:", feedback, StringComparison.Ordinal);
        Assert.Contains("TaskFailed: developer prior outcome", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry 3 of 3", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("Task 3 Reviewer", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewer retry downstream", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewer prior outcome", feedback, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void DeveloperBriefIgnoresDownstreamRetriesWhileReviewerBriefHeadlinesLatest()
    {
        var (clock, kernel, goal, developer, tester, reviewer) = CreateThreeTaskGoal();
        var developerRetryAt = RetryDeveloperTwice(clock, kernel, goal, developer);
        clock.Advance();
        kernel.ReportTaskProgress(goal.Id, tester.Id, WorkTaskStatus.Failed, "tester prior outcome");
        clock.Advance();
        kernel.RetryTask(goal.Id, tester.Id, "tester retry downstream");
        clock.Advance();
        kernel.ReportTaskProgress(goal.Id, reviewer.Id, WorkTaskStatus.Failed, "reviewer prior outcome");
        clock.Advance();
        kernel.RetryTask(goal.Id, reviewer.Id, "reviewer retry downstream");
        var reviewerRetryAt = clock.UtcNow;

        var developerFeedback = FeedbackBlock(kernel.BuildTaskBrief(goal.Id, developer.Id).Content);
        var reviewerFeedback = FeedbackBlock(kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content);

        Assert.Contains($"Most recent retry: Retry 2 of 2; {developerRetryAt:u}; Task 1 Developer.", developerFeedback, StringComparison.Ordinal);
        Assert.DoesNotContain("Task 2 Tester", developerFeedback, StringComparison.Ordinal);
        Assert.DoesNotContain("Task 3 Reviewer", developerFeedback, StringComparison.Ordinal);
        Assert.DoesNotContain("tester prior outcome", developerFeedback, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewer prior outcome", developerFeedback, StringComparison.Ordinal);
        Assert.DoesNotContain("tester retry downstream", developerFeedback, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewer retry downstream", developerFeedback, StringComparison.Ordinal);
        Assert.Contains($"Most recent retry: Retry 4 of 4; {reviewerRetryAt:u}; Task 3 Reviewer.", reviewerFeedback, StringComparison.Ordinal);
        Assert.Contains("TaskFailed: reviewer prior outcome", reviewerFeedback, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void RetryForTaskAbsentFromGoalIsNotHeadlinedAndDoesNotThrow()
    {
        var (clock, kernel, goal, developer, tester, reviewer) = CreateThreeTaskGoal();
        RetryDeveloperTwice(clock, kernel, goal, developer);
        clock.Advance();
        kernel.RetryTask(goal.Id, reviewer.Id, "reviewer retry");
        clock.Advance();
        kernel.GetGoal(goal.Id).Append(new ProgressEvent(
            goal.Id,
            TaskId.New(),
            ProgressKind.TaskRetried,
            "orphan retry outside goal",
            clock.UtcNow));

        foreach (var (task, expectedHeadline, expectedTask) in new[]
                 {
                     (developer, "Most recent retry: Retry 2 of 2", "Task 1 Developer"),
                     (tester, "Most recent retry: Retry 2 of 2", "Task 1 Developer"),
                     (reviewer, "Most recent retry: Retry 3 of 3", "Task 3 Reviewer")
                 })
        {
            var feedback = FeedbackBlock(kernel.BuildTaskBrief(goal.Id, task.Id).Content);
            Assert.Contains(expectedHeadline, feedback, StringComparison.Ordinal);
            Assert.Contains(expectedTask, feedback, StringComparison.Ordinal);
            Assert.DoesNotContain("orphan retry outside goal", feedback, StringComparison.Ordinal);
        }
    }

    private static (FakeClock Clock, AgentOrchestratorKernel Kernel, Goal Goal,
        TaskSpec Developer, TaskSpec Tester, TaskSpec Reviewer) CreateThreeTaskGoal()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var developer = new TaskSpec(TaskId.New(), "Implement retry feedback.", AgentRole.Developer);
        var tester = new TaskSpec(TaskId.New(), "Test retry feedback.", AgentRole.Tester);
        var reviewer = new TaskSpec(TaskId.New(), "Review retry feedback.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Scope retry feedback to upstream tasks", [developer, tester, reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        return (clock, kernel, goal, developer, tester, reviewer);
    }

    private static DateTimeOffset RetryDeveloperTwice(
        FakeClock clock,
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec developer)
    {
        kernel.RetryTask(goal.Id, developer.Id, "developer retry round one");
        clock.Advance();
        kernel.ReportTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Failed, "developer prior outcome");
        clock.Advance();
        kernel.RetryTask(goal.Id, developer.Id, "developer retry round two");
        return clock.UtcNow;
    }

    private static string FeedbackBlock(string brief)
    {
        const string startMarker = "<!-- ACCUMULATED_RETRY_FEEDBACK_START -->";
        const string endMarker = "<!-- ACCUMULATED_RETRY_FEEDBACK_END -->";
        var start = brief.IndexOf(startMarker, StringComparison.Ordinal);
        var end = brief.IndexOf(endMarker, start >= 0 ? start : 0, StringComparison.Ordinal);
        Assert.True(start >= 0, "Accumulated retry feedback start marker was absent.");
        Assert.True(end > start, "Accumulated retry feedback end marker was absent.");
        return brief[start..(end + endMarker.Length)];
    }
}
