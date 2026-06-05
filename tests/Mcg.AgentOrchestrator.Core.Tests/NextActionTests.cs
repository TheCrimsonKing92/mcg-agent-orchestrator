using Mcg.AgentOrchestrator.Core;

public sealed class NextActionTests
{
    [Xunit.Fact(DisplayName = "NextActionAutomationPolicy_allows_only_safe_fully_specified_actions")]
    public void NextActionAutomationPolicyAllowsOnlySafeFullySpecifiedActions()
{
    var taskId = new TaskId("task-1");

    AssertAutomation(
        new NextActionItem(NextActionKind.RunAssignedTask, taskId, null, "Run it"),
        true,
        NextActionAutomationKind.RunAssignedTask,
        taskId);
    AssertAutomation(
        new NextActionItem(NextActionKind.RefreshRunningProcess, taskId, null, "Refresh it"),
        true,
        NextActionAutomationKind.RefreshRunningProcess,
        taskId);
    AssertAutomation(
        new NextActionItem(NextActionKind.ExecuteRecordedDispatch, taskId, null, "Start it"),
        true,
        NextActionAutomationKind.StartRecordedDispatch,
        taskId);
    AssertAutomation(
        new NextActionItem(NextActionKind.DelegatePendingTask, taskId, null, "Delegate it"),
        true,
        NextActionAutomationKind.DelegatePendingTask,
        taskId);

    AssertAutomation(
        new NextActionItem(NextActionKind.AnswerHumanInput, taskId, new HumanInputRequestId("input-1"), "Which branch?"),
        false,
        NextActionAutomationKind.None,
        taskId);
    AssertAutomation(
        new NextActionItem(NextActionKind.VerifyCompletedTask, taskId, null, "Verify"),
        false,
        NextActionAutomationKind.None,
        taskId);
    AssertAutomation(
        new NextActionItem(NextActionKind.InspectFailedTask, taskId, null, "Inspect"),
        false,
        NextActionAutomationKind.None,
        taskId);
    AssertAutomation(
        new NextActionItem(NextActionKind.MonitorGoal, null, null, "Monitor"),
        false,
        NextActionAutomationKind.None,
        null);
    AssertAutomation(
        new NextActionItem(NextActionKind.RunAssignedTask, null, null, "Missing task"),
        false,
        NextActionAutomationKind.None,
        null);
}

static void AssertAutomation(
    NextActionItem item,
    bool canExecute,
    NextActionAutomationKind kind,
    TaskId? taskId)
{
    var plan = NextActionAutomationPolicy.Build(item);

    Assert.Equal(canExecute, plan.CanExecute);
    Assert.Equal(kind, plan.Kind);
    Assert.Equal(taskId, plan.TaskId);
    Assert.True(plan.Message.Length > 0);
}
    [Xunit.Fact(DisplayName = "BuildNextActions_prioritizes_human_input_failures_dispatches_and_verification")]
    public void BuildNextActionsPrioritizesHumanInputFailuresDispatchesAndVerification()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Recommend next action");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var plannerTask = goal.Tasks.First(task => task.RequiredRole == AgentRole.Planner);
    var developerTask = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var testerTask = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
    var reviewerTask = goal.Tasks.First(task => task.RequiredRole == AgentRole.Reviewer);
    var request = kernel.RequestHumanInput(goal.Id, plannerTask.Id, "Which branch?");
    kernel.RecordTaskDispatch(goal.Id, developerTask.Id, new TaskDispatchRecord("local", "dotnet build", "C:\\repo", clock.UtcNow));
    kernel.RecordTaskVerification(goal.Id, testerTask.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, "", "failed", clock.UtcNow));
    kernel.ReportTaskProgress(goal.Id, reviewerTask.Id, WorkTaskStatus.Completed, "Reviewed.");

    var actions = kernel.BuildNextActions(goal.Id);

    Assert.Equal(goal.Id, actions.GoalId);
    Assert.Equal(NextActionKind.AnswerHumanInput, actions.Items[0].Kind);
    Assert.Equal(request.Id, actions.Items[0].HumanInputRequestId);
    Assert.Equal(plannerTask.Id, actions.Items[0].TaskId);
    Assert.Contains(actions.Items, item => item.Kind == NextActionKind.FixFailedVerification && item.TaskId == testerTask.Id);
    Assert.Contains(actions.Items, item => item.Kind == NextActionKind.ExecuteRecordedDispatch && item.TaskId == developerTask.Id);
    Assert.Contains(actions.Items, item => item.Kind == NextActionKind.VerifyCompletedTask && item.TaskId == reviewerTask.Id);
}
    [Xunit.Fact(DisplayName = "BuildNextActions_suggests_running_assigned_pending_and_monitor_states")]
    public void BuildNextActionsSuggestsRunningAssignedPendingAndMonitorStates()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Recommend simple states",
        [
            new TaskSpec(TaskId.New(), "Assigned work", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Pending work", AgentRole.Reviewer)
        ]);
    var developer = DefaultAgents().First(agent => agent.Role == AgentRole.Developer);
    kernel.ActivateGoal(goal.Id, [developer]);
    var assignedTask = goal.Tasks[0];
    var pendingTask = goal.Tasks[1];

    var actions = kernel.BuildNextActions(goal.Id);

    Assert.Contains(actions.Items, item => item.Kind == NextActionKind.RunAssignedTask && item.TaskId == assignedTask.Id);
    Assert.Contains(actions.Items, item => item.Kind == NextActionKind.DelegatePendingTask && item.TaskId == pendingTask.Id);

    kernel.ReportTaskProgress(goal.Id, assignedTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, assignedTask.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "ok", "", clock.UtcNow));
    kernel.ReportTaskProgress(goal.Id, pendingTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, pendingTask.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "ok", "", clock.UtcNow));

    var completedActions = kernel.BuildNextActions(goal.Id);

    Assert.Equal(NextActionKind.MonitorGoal, completedActions.Items.Single().Kind);
}
}

