using Mcg.AgentOrchestrator.Core;

public sealed class CancelledReviewerVerificationGateTests
{
    [Xunit.Fact]
    public void ProcessCancelledReviewerBlocksVerificationAndAcceptanceAfterRefresh()
    {
        var (kernel, goal, reviewer, clock) = GoalAwaitingReview();
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id,
            new TaskDispatchRecord("reviewer", "review", @"C:\repo", clock.UtcNow));
        var process = new TaskProcessRecord(1234, "review", @"C:\repo",
            "out.log", "err.log", "exit.txt", clock.UtcNow, null, null);
        kernel.RecordTaskProcessStarted(goal.Id, reviewer.Id, process);
        Assert.Equal(WorkTaskStatus.Running, reviewer.Status);
        Assert.All(goal.Tasks.Where(task => task.Id != reviewer.Id), task =>
        {
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.True(task.LastVerification!.Succeeded);
        });

        kernel.RecordTaskProcessCancelled(goal.Id, reviewer.Id,
            process with { CompletedAt = clock.UtcNow, WasCancelled = true });
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        kernel.RecordTaskVerification(goal.Id, tester.Id, PassedVerification(clock.UtcNow));

        Assert.Equal(WorkTaskStatus.Cancelled, reviewer.Status);
        Assert.NotEqual(GoalStatus.Verified, goal.Status);
        Assert.False(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
        Assert.Throws<InvalidOperationException>(() =>
            kernel.BeginGoalAcceptanceVerification(goal.Id, "Review is required."));
    }

    [Xunit.Fact]
    public void ProgressCancelledReviewerHasRerunGateAndCommands()
    {
        var (kernel, goal, reviewer, _) = GoalAwaitingReview();

        kernel.ReportTaskProgress(goal.Id, reviewer.Id, WorkTaskStatus.Cancelled, "Review interrupted.");

        Assert.NotEqual(GoalStatus.Verified, goal.Status);
        var gate = Assert.Single(kernel.BuildVerificationGate(goal.Id).Tasks,
            task => task.TaskId == reviewer.Id);
        Assert.Equal(VerificationGateStatus.NotReady, gate.GateStatus);
        Assert.Equal(VerificationGateReason.CancelledReviewNeedsRerun, gate.Reason);
        Assert.Contains(reviewer.Id.Value, gate.Message, StringComparison.Ordinal);
        Assert.Contains($"adjudicate --goal {goal.Id.Value} 3 route --cause <cause>", gate.Message, StringComparison.Ordinal);
        Assert.Contains($"retry --goal {goal.Id.Value} 3 --cause <cause>", gate.Message, StringComparison.Ordinal);
        var workItem = Assert.Single(kernel.BuildVerificationWorklist(goal.Id).Items,
            task => task.TaskId == reviewer.Id);
        Assert.Equal(gate.Message, workItem.SuggestedAction);
    }

    [Xunit.Fact]
    public void RetriedCancelledReviewerMustPassBeforeGoalBecomesVerified()
    {
        var (kernel, goal, reviewer, clock) = GoalAwaitingReview();
        kernel.ReportTaskProgress(goal.Id, reviewer.Id, WorkTaskStatus.Cancelled, "Review interrupted.");
        Assert.NotEqual(GoalStatus.Verified, goal.Status);

        kernel.RetryTask(goal.Id, reviewer.Id, "Re-run the cancelled review.", RetryCause.ProviderInterruption);
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        kernel.ReportTaskProgress(goal.Id, reviewer.Id, WorkTaskStatus.Completed, "Review finished.");
        Assert.NotEqual(GoalStatus.Verified, goal.Status);
        kernel.RecordTaskVerification(goal.Id, reviewer.Id, PassedVerification(clock.UtcNow));

        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.True(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
        kernel.BeginGoalAcceptanceVerification(goal.Id, "Review passed.");
        Assert.Equal(GoalStatus.Verifying, goal.Status);
    }

    [Xunit.Fact]
    public void PersistedVerifiedGoalWithCancelledReviewerDemotesOnRefresh()
    {
        var (kernel, goal, reviewer, clock) = GoalAwaitingReview();
        kernel.ReportTaskProgress(goal.Id, reviewer.Id, WorkTaskStatus.Cancelled, "Review interrupted.");
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [snapshot.Goals.Single() with { Status = GoalStatus.Verified }]
        });
        goal = kernel.GetGoal(goal.Id);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);

        kernel.RecordTaskVerification(goal.Id, tester.Id, PassedVerification(clock.UtcNow));

        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Throws<InvalidOperationException>(() =>
            kernel.BeginGoalAcceptanceVerification(goal.Id, "Legacy review is missing."));
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Planner)]
    [Xunit.InlineData(AgentRole.Ideation)]
    [Xunit.InlineData(AgentRole.Researcher)]
    [Xunit.InlineData(AgentRole.Developer)]
    [Xunit.InlineData(AgentRole.Tester)]
    public void CancelledNonReviewerStillPasses(AgentRole role)
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var task = new TaskSpec(TaskId.New(), "Deliberately descoped work", role);
        var goal = kernel.CreateGoal("Preserve deliberate descoping", [task]);

        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Cancelled, "Operator descoped task.");

        Assert.Equal(VerificationGateStatus.Passed, kernel.BuildVerificationGate(goal.Id).Tasks.Single().GateStatus);
        Assert.Equal(GoalStatus.Verified, goal.Status);
    }

    [Xunit.Theory]
    [Xunit.InlineData(GoalStatus.Completed)]
    [Xunit.InlineData(GoalStatus.Failed)]
    [Xunit.InlineData(GoalStatus.Cancelled)]
    [Xunit.InlineData(GoalStatus.Superseded)]
    public void TerminalGoalCancelledReviewerStillPasses(GoalStatus status)
    {
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot("terminal-review", "Preserve terminal reports", status,
                [new TaskSnapshot("cancelled-review", "Review", AgentRole.Reviewer,
                    WorkTaskStatus.Cancelled, null, null, null, [], null, null)], [])], []));
        var goal = kernel.Goals.Single();

        var gate = kernel.BuildVerificationGate(goal.Id);

        Assert.Equal(status, goal.Status);
        Assert.Equal(VerificationGateStatus.Passed, gate.Tasks.Single().GateStatus);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Reviewer, FakeClock Clock) GoalAwaitingReview()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Require a passed review",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer),
             new TaskSpec(TaskId.New(), "Test", AgentRole.Tester),
             new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Work finished.");
            kernel.RecordTaskVerification(goal.Id, task.Id, PassedVerification(clock.UtcNow));
        }
        return (kernel, goal, goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer), clock);
    }

    private static TaskVerificationRecord PassedVerification(DateTimeOffset at) =>
        new("manual", @"C:\repo", 0, "", "", at);
}
