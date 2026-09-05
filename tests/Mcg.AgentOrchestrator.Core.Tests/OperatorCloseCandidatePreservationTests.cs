using Mcg.AgentOrchestrator.Core;

public sealed class OperatorCloseCandidatePreservationTests
{
    [Xunit.Fact]
    public void MatchingObservedCandidate_PreservesDownstreamVerdicts()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateThreeRoleGoal(kernel);
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        CompleteCandidateDispatch(kernel, goal, developer, "aaa111", "aaa111");
        CompleteCandidateDispatch(kernel, goal, tester, "aaa111", "aaa111");
        CompleteCandidateDispatch(kernel, goal, reviewer, "aaa111", "aaa111");
        var testerVerification = tester.LastVerification;
        var reviewerVerification = reviewer.LastVerification;

        kernel.RetryTask(goal.Id, developer.Id, "Close unchanged work mechanically.");
        // Against the code before the fix the same test fails because both verdicts are reset with the
        // "did not prove candidate aaa111 unchanged (result unknown; status Completed)" reason.
        kernel.ReportTaskProgress(
            goal.Id,
            developer.Id,
            WorkTaskStatus.Completed,
            "Operator closed unchanged work.",
            observedCandidate: "aaa111");

        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
        Assert.Same(testerVerification, tester.LastVerification);
        Assert.Same(reviewerVerification, reviewer.LastVerification);
        Assert.Contains(goal.Timeline, item =>
            item.Kind == ProgressKind.TaskUpdated &&
            item.Message.Contains("Preserved Tester task", StringComparison.Ordinal) &&
            item.Message.Contains("unchanged candidate aaa111", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void ChangedObservedCandidate_InvalidatesDownstreamVerdicts()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateThreeRoleGoal(kernel);
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        CompleteCandidateDispatch(kernel, goal, developer, "aaa111", "aaa111");
        CompleteCandidateDispatch(kernel, goal, tester, "aaa111", "aaa111");
        CompleteCandidateDispatch(kernel, goal, reviewer, "aaa111", "aaa111");

        kernel.RetryTask(goal.Id, developer.Id, "Close changed work mechanically.");
        kernel.ReportTaskProgress(
            goal.Id,
            developer.Id,
            WorkTaskStatus.Completed,
            "Operator closed changed work.",
            observedCandidate: "bbb222");

        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        Assert.Null(tester.LastVerification);
        Assert.Null(reviewer.LastVerification);
        Assert.Contains(goal.Timeline, item =>
            item.TaskId == tester.Id &&
            item.Kind == ProgressKind.TaskRetried &&
            item.Message.Contains("changed candidate from aaa111 to bbb222", StringComparison.Ordinal));
        Assert.Contains(goal.Timeline, item =>
            item.TaskId == reviewer.Id &&
            item.Kind == ProgressKind.TaskRetried &&
            item.Message.Contains("changed candidate from aaa111 to bbb222", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void MatchingDispatchResultCandidate_PreservesDownstreamVerdicts()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateThreeRoleGoal(kernel);
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        CompleteCandidateDispatch(kernel, goal, developer, "aaa111", "aaa111");
        CompleteCandidateDispatch(kernel, goal, tester, "aaa111", "aaa111");
        CompleteCandidateDispatch(kernel, goal, reviewer, "aaa111", "aaa111");
        var testerVerification = tester.LastVerification;
        var reviewerVerification = reviewer.LastVerification;

        kernel.RetryTask(goal.Id, developer.Id, "Run the unchanged candidate again.");
        CompleteCandidateDispatch(kernel, goal, developer, "aaa111", "aaa111");

        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
        Assert.Same(testerVerification, tester.LastVerification);
        Assert.Same(reviewerVerification, reviewer.LastVerification);
    }

    private static Goal CreateThreeRoleGoal(AgentOrchestratorKernel kernel)
    {
        var goal = kernel.CreateGoal(
            "Preserve verdicts for an unchanged candidate",
            [
                new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Test fix", AgentRole.Tester),
                new TaskSpec(TaskId.New(), "Review fix", AgentRole.Reviewer)
            ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        return goal;
    }

    private static void CompleteCandidateDispatch(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        string resultCommit,
        string reviewedCommit)
    {
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(task.RequiredRole.ToString(), "worker", "C:\\repo", DateTimeOffset.UtcNow));
        kernel.RecordDispatchBaseCommit(goal.Id, task.Id, reviewedCommit);
        kernel.RecordDispatchResultCommit(goal.Id, task.Id, resultCommit);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, $"{task.RequiredRole} done.");
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                "dotnet test",
                "C:\\repo",
                0,
                "passed",
                string.Empty,
                DateTimeOffset.UtcNow,
                ReviewedCommit: reviewedCommit));
    }
}
