using Mcg.AgentOrchestrator.Core;

// Parallel-safe: all goal state and the injected clock belong to each test.
public sealed class AssignedPassingVerificationReconciliationTests
{
    private const string ClearMarker = "passing verification was cleared";

    [Xunit.Fact]
    public void AssignTo_PassingVerification_ClearsBeforeKernelRefresh()
    {
        var (_, _, developer, _) = VerifiedDeveloper();
        var verification = developer.LastVerification;

        developer.AssignTo(SecondDeveloper().Id);

        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Null(developer.LastVerification);
        Assert.Contains(verification!, developer.VerificationHistory);
        developer.AssignTo(SecondDeveloper().Id);
        Assert.True(developer.ConsumePassingVerificationClearedOnAssignment());
        Assert.False(developer.ConsumePassingVerificationClearedOnAssignment());
    }

    [Xunit.Fact]
    public void ReassignTaskAgent_CompletedPassingTask_ClearsAndNotesOnce()
    {
        var (kernel, goal, developer, _) = VerifiedDeveloper();
        var verification = developer.LastVerification;
        var secondDeveloper = SecondDeveloper();

        kernel.ReassignTaskAgent(goal.Id, developer.Id, secondDeveloper);

        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Equal(secondDeveloper.Id, developer.AssignedAgentId);
        Assert.False(developer.Status == WorkTaskStatus.Assigned && developer.LastVerification?.Succeeded is true);
        Assert.Null(developer.LastVerification);
        Assert.Contains(verification!, developer.VerificationHistory);
        AssertClearNote(goal, developer);

        kernel.ReassignTaskAgent(goal.Id, developer.Id, secondDeveloper);

        AssertClearNote(goal, developer);
    }

    [Xunit.Fact]
    public void RefreshGoalStatus_PersistedContradiction_ClearsAndNotesOnce()
    {
        var (kernel, goal, developer, _) = VerifiedDeveloper();
        var verification = developer.LastVerification;
        developer.SetStatus(WorkTaskStatus.Assigned);
        Assert.True(developer.LastVerification?.Succeeded is true);

        RefreshThroughOtherTask(kernel, goal);

        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Null(developer.LastVerification);
        Assert.Contains(verification!, developer.VerificationHistory);
        AssertClearNote(goal, developer);

        RefreshThroughOtherTask(kernel, goal);

        Assert.Null(developer.LastVerification);
        AssertClearNote(goal, developer);
    }

    [Xunit.Fact]
    public void RecordTaskDispatch_ReconciledAssignedTask_AdmitsDispatch()
    {
        var (kernel, goal, developer, clock) = VerifiedDeveloper();
        developer.SetStatus(WorkTaskStatus.Assigned);
        Assert.True(developer.LastVerification?.Succeeded is true);
        RefreshThroughOtherTask(kernel, goal);
        var dispatch = new TaskDispatchRecord("codex", "implement feature", "C:\\repo", clock.UtcNow);

        var exception = Record.Exception(() => kernel.RecordTaskDispatch(goal.Id, developer.Id, dispatch));

        Assert.Null(exception);
        Assert.Equal(WorkTaskStatus.Running, developer.Status);
        Assert.NotNull(developer.LastDispatch);
        Assert.Equal(dispatch.Command, developer.LastDispatch.Command);
        Assert.Null(developer.LastVerification);
    }

    [Xunit.Theory]
    [Xunit.InlineData(1, null)]
    [Xunit.InlineData(0, "provider failed")]
    public void AssignmentAndRefresh_FailedVerification_KeepRecordWithoutNote(
        int exitCode, string? failureReason)
    {
        var (kernel, goal, developer, clock) = VerifiedDeveloper();
        kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
            "dotnet test", "C:\\repo", exitCode, "failed", string.Empty, clock.UtcNow,
            OrchestratorFailureReason: failureReason));
        var verification = developer.LastVerification;
        Assert.False(verification!.Succeeded);

        kernel.ReassignTaskAgent(goal.Id, developer.Id, SecondDeveloper());
        RefreshThroughOtherTask(kernel, goal);

        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Same(verification, developer.LastVerification);
        Assert.DoesNotContain(goal.Timeline, entry => entry.Message.Contains(ClearMarker, StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void RefreshGoalStatus_CompletedPassingTask_KeepsVerificationAndRefusesDispatch()
    {
        var (kernel, goal, developer, clock) = VerifiedDeveloper();
        var verification = developer.LastVerification;

        RefreshThroughOtherTask(kernel, goal);

        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Same(verification, developer.LastVerification);
        Assert.DoesNotContain(goal.Timeline, entry => entry.Message.Contains(ClearMarker, StringComparison.Ordinal));
        var exception = Assert.Throws<InvalidOperationException>(() => kernel.RecordTaskDispatch(
            goal.Id, developer.Id, new TaskDispatchRecord("codex", "implement feature", "C:\\repo", clock.UtcNow)));
        Assert.Contains("already has passing verification", exception.Message, StringComparison.Ordinal);
        Assert.Null(developer.LastDispatch);
    }

    [Xunit.Theory]
    [Xunit.InlineData(WorkTaskStatus.Pending)]
    [Xunit.InlineData(WorkTaskStatus.Running)]
    [Xunit.InlineData(WorkTaskStatus.Failed)]
    [Xunit.InlineData(WorkTaskStatus.Cancelled)]
    [Xunit.InlineData(WorkTaskStatus.WaitingForHuman)]
    public void RefreshGoalStatus_NonAssignedTask_KeepsPassingVerification(WorkTaskStatus status)
    {
        var (kernel, goal, developer, _) = VerifiedDeveloper();
        var verification = developer.LastVerification;
        developer.SetStatus(status);

        RefreshThroughOtherTask(kernel, goal);

        Assert.Equal(status, developer.Status);
        Assert.Same(verification, developer.LastVerification);
        Assert.DoesNotContain(goal.Timeline, entry => entry.Message.Contains(ClearMarker, StringComparison.Ordinal));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Developer, FakeClock Clock)
        VerifiedDeveloper()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Reconcile assigned verified work");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var developer = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        kernel.ReportTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Completed, "Implemented.");
        kernel.RecordTaskVerification(goal.Id, developer.Id,
            new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "ok", string.Empty, clock.UtcNow));
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.True(developer.LastVerification?.Succeeded is true);
        return (kernel, goal, developer, clock);
    }

    private static AgentDefinition SecondDeveloper() =>
        DefaultAgents().Single(agent => agent.Role == AgentRole.Developer) with
        {
            Id = new AgentId("second-developer"),
            Name = "Second Developer"
        };

    private static void RefreshThroughOtherTask(AgentOrchestratorKernel kernel, Goal goal)
    {
        var other = goal.Tasks.First(task => task.RequiredRole != AgentRole.Developer);
        kernel.ReportTaskProgress(goal.Id, other.Id, WorkTaskStatus.Running, "Refresh goal status.");
    }

    private static void AssertClearNote(Goal goal, TaskSpec developer)
    {
        var note = Assert.Single(goal.Timeline, entry => entry.Message.Contains(ClearMarker, StringComparison.Ordinal));
        Assert.Equal(developer.Id, note.TaskId);
        Assert.Equal(ProgressKind.TaskNote, note.Kind);
        Assert.Equal($"Task '{developer.Id.Value}' passing verification was cleared because the task was returned to Assigned.", note.Message);
    }
}
