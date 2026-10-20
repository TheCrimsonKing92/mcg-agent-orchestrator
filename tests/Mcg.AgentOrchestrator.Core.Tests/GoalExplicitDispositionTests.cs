using Mcg.AgentOrchestrator.Core;

// Parallel safe: each case owns its kernel and has no external resources.
public sealed class GoalExplicitDispositionTests
{
    [Fact]
    public void Reconcile_VisiblePark_RetainsFailureAndPendingRetry()
    {
        var (kernel, goal, task) = VerifyingGoal();
        kernel.ParkGoal(goal.Id, "Operator hold");
        Assert.True(kernel.ReconcileGoalAcceptanceFailed(
            goal.Id, ["focused RED"], "Terminal attempt", "branch", "main"));
        Assert.Equal(GoalStatus.Parked, goal.Status);
        Assert.Equal(GoalStatus.AcceptanceFailed, goal.LifecycleStatus);
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["Fix RED"]);
        kernel.RetryTask(goal.Id, task.Id, "Retry retained RED");
        Assert.Equal(GoalStatus.Parked, goal.Status);
        Assert.Equal(GoalStatus.Active, goal.LifecycleStatus);
        var snapshot = Assert.Single(kernel.ExportSnapshot().Goals);
        Assert.True(snapshot.AcceptanceFailureDeferredForRetry);
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        goal = kernel.GetGoal(goal.Id);
        Assert.Equal(GoalStatus.Parked, goal.Status);
        Assert.Equal(1, goal.AutomaticAcceptanceRetryCount);
        Assert.Equal(new[] { "focused RED" }, goal.RetainedAcceptanceFailure!.FailedChecks);
        kernel.UnparkGoal(goal.Id, "Resume");
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(WorkTaskStatus.Assigned, goal.Tasks.Single().Status);
        Assert.Equal(new GoalExplicitDisposition(2, GoalExplicitDispositionAction.Unpark), goal.ExplicitDisposition);
    }

    [Theory]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Failed)]
    public void Unpark_RetainedTerminalOutcome_DoesNotForceActive(GoalStatus outcome)
    {
        var (kernel, goal, _) = VerifyingGoal();
        kernel.ParkGoal(goal.Id, "Hold");
        if (outcome == GoalStatus.Completed)
            kernel.CompleteGoal(goal.Id, "Already owned completion");
        else
            goal.SetStatus(outcome);
        Assert.Equal(GoalStatus.Parked, goal.Status);
        if (outcome == GoalStatus.Completed)
            Assert.Throws<InvalidOperationException>(() =>
                kernel.RetryTask(goal.Id, goal.Tasks.Single().Id, "Cannot undo retained completion"));
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        goal = kernel.UnparkGoal(goal.Id, "Release");
        Assert.Equal(outcome, goal.Status);
        if (outcome == GoalStatus.Completed)
            Assert.Throws<InvalidOperationException>(() => kernel.ParkGoal(goal.Id, "Too late"));
    }

    [Theory]
    [InlineData(GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Superseded)]
    public void Stop_ExplicitlyParked_ExposesTerminalDisposition(GoalStatus stopped)
    {
        var (kernel, goal, _) = VerifyingGoal();
        kernel.ParkGoal(goal.Id, "Hold before stop");
        if (stopped == GoalStatus.Cancelled)
            kernel.CancelGoal(goal.Id, "Later operator cancel");
        else
            kernel.SupersedeGoal(goal.Id, "Later operator supersede");
        Assert.Equal(stopped, goal.Status);
        Assert.Equal(stopped, goal.LifecycleStatus);
        Assert.True(goal.IsTerminal);
        Assert.False(goal.IsExplicitlyParked);
        var kind = stopped == GoalStatus.Cancelled ? ProgressKind.GoalCancelled : ProgressKind.GoalSuperseded;
        Assert.Single(goal.Timeline.Where(e => e.Kind == kind));
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        goal = kernel.GetGoal(goal.Id);
        Assert.Equal(stopped, goal.Status);
        Assert.True(goal.IsTerminal);
        Assert.False(goal.IsExplicitlyParked);
        Assert.Throws<InvalidOperationException>(() => kernel.UnparkGoal(goal.Id, "Cannot undo stop"));
        goal.SetStatus(GoalStatus.Active); // A late ordinary callback cannot undo the stop.
        Assert.Equal(stopped, goal.Status);
        Assert.Equal(stopped, goal.LifecycleStatus);
    }

    [Theory]
    [InlineData(GoalStatus.Completed, GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Completed, GoalStatus.Superseded)]
    [InlineData(GoalStatus.Failed, GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Failed, GoalStatus.Superseded)]
    public void Stop_RetainedTerminalUnderPark_RejectsWithoutChangingState(
        GoalStatus outcome, GoalStatus stopped)
    {
        var (kernel, goal, _) = VerifyingGoal();
        kernel.ParkGoal(goal.Id, "Hold before terminal outcome");
        if (outcome == GoalStatus.Completed)
            kernel.CompleteGoal(goal.Id, "Already owned completion");
        else
            goal.SetStatus(outcome);
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        goal = kernel.GetGoal(goal.Id);
        Assert.Equal(GoalStatus.Parked, goal.Status);
        Assert.Equal(outcome, goal.LifecycleStatus);
        var before = System.Text.Json.JsonSerializer.Serialize(kernel.ExportSnapshot());

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            if (stopped == GoalStatus.Cancelled)
                kernel.CancelGoal(goal.Id, "Cannot change terminal verdict");
            else
                kernel.SupersedeGoal(goal.Id, "Cannot change terminal verdict");
        });

        Assert.Contains(outcome.ToString(), error.Message);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(kernel.ExportSnapshot()));
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        goal = kernel.UnparkGoal(goal.Id, "Expose retained outcome");
        Assert.Equal(outcome, goal.Status);
    }

    [Fact]
    public void Park_RejectedTransition_PreservesExactState()
    {
        var (kernel, goal, _) = VerifyingGoal();
        kernel.CompleteGoal(goal.Id, "Done");
        var before = kernel.ExportSnapshot();
        Assert.Throws<InvalidOperationException>(() => kernel.ParkGoal(goal.Id, "Rejected"));
        Assert.Null(goal.ExplicitDisposition);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before),
            System.Text.Json.JsonSerializer.Serialize(kernel.ExportSnapshot()));
    }

    [Fact]
    public void CompleteFromMerge_HeldCompletion_ReplayDoesNotAppendAgain()
    {
        var (kernel, goal, _) = VerifyingGoal();
        kernel.ParkGoal(goal.Id, "Hold");
        kernel.CompleteGoalFromMergeEvidence(goal.Id, "integrate-sha", "Landed");
        Assert.Equal(GoalStatus.Parked, goal.Status);
        Assert.Equal(GoalStatus.Completed, goal.LifecycleStatus);
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        goal = kernel.GetGoal(goal.Id);
        var count = goal.Timeline.Count;
        kernel.CompleteGoalFromMergeEvidence(goal.Id, "integrate-sha", "Replay");
        Assert.Equal(count, goal.Timeline.Count);
        kernel.UnparkGoal(goal.Id, "Release");
        Assert.Equal(GoalStatus.Completed, goal.Status);
    }

    [Fact]
    public void Refresh_ExplicitPark_CannotBeReleasedByHumanResolution()
    {
        var (kernel, goal, task) = VerifyingGoal();
        kernel.RequestHumanInput(goal.Id, task.Id, "Question");
        kernel.ParkGoal(goal.Id, "Explicit hold");
        Assert.Equal(0, kernel.RefreshParkedGoalsWithResolvedHumanWaits());
        Assert.Equal(GoalStatus.Parked, goal.Status);
        kernel.UnparkGoal(goal.Id, "Release");
        kernel.ParkGoal(goal.Id, "Fresh hold");
        Assert.Equal(3, goal.ExplicitDisposition!.Revision);
        Assert.Equal(GoalStatus.Parked, goal.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refresh_ResolvedHumanWait_PreservesTypedHoldAndLegacyRecovery(bool typed)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Resolved wait", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        if (typed) kernel.ParkGoal(goal.Id, "Explicit operator hold");
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "New question");
        if (!typed)
        {
            goal.SetStatus(GoalStatus.Parked);
            kernel.RecordGoalPolicyDecision(goal.Id, "Goal parked: automatic human wait");
        }
        clock.Advance();
        kernel.SubmitHumanInput(request.Id, "Proceed");
        var retained = goal.LifecycleStatus;
        Assert.Equal(GoalStatus.Parked, goal.Status);
        Assert.Equal(typed ? 0 : 1, kernel.RefreshParkedGoalsWithResolvedHumanWaits());
        Assert.Equal(typed ? GoalStatus.Parked : GoalStatus.Active, goal.Status);
        if (typed)
        {
            Assert.Equal(retained, goal.LifecycleStatus);
            Assert.Equal(GoalExplicitDispositionAction.Park, goal.ExplicitDisposition!.Action);
        }
        else Assert.Null(goal.ExplicitDisposition);
    }

    internal static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) VerifyingGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Explicit park", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done");
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "verify", "C:\\isolated", 0, "pass", "", DateTimeOffset.UnixEpoch));
        kernel.BeginGoalAcceptanceVerification(goal.Id, "Started");
        Assert.Equal(GoalStatus.Verifying, goal.Status);
        return (kernel, goal, task);
    }
}
