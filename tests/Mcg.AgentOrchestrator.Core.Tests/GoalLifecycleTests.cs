using Mcg.AgentOrchestrator.Core;

public sealed class GoalLifecycleTests
{
    [Xunit.Fact(DisplayName = "CreateGoal_records_goal_and_initial_progress_event")]
    public void CreateGoalRecordsGoalAndInitialProgressEvent()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());

    var goal = kernel.CreateGoal("Build a Windows agent orchestrator");

    Assert.Equal(GoalStatus.Draft, goal.Status);
    Assert.Equal("Build a Windows agent orchestrator", goal.Objective);
    Assert.True(goal.Tasks.Count >= 5, "Default SDLC task decomposition should be present.");
    Assert.Single(goal.Timeline);
    Assert.Equal(ProgressKind.GoalCreated, goal.Timeline[0].Kind);
}
    [Xunit.Fact(DisplayName = "ActivateGoal_delegates_tasks_to_matching_agents")]
    public void ActivateGoalDelegatesTasksToMatchingAgents()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Ship a feature");
    var agents = DefaultAgents();

    var plan = kernel.ActivateGoal(goal.Id, agents);

    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(goal.Tasks.Count, plan.Assignments.Count);

    foreach (var task in goal.Tasks)
    {
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.True(task.AssignedAgentId is not null, $"Task {task.Description} should have an assigned agent.");
        var assignedAgent = agents.Single(agent => agent.Id == task.AssignedAgentId);
        Assert.Equal(task.RequiredRole, assignedAgent.Role);
    }

    Assert.Equal(goal.Tasks.Count, goal.Timeline.Count(evt => evt.Kind == ProgressKind.TaskDelegated));
}
    [Xunit.Fact(DisplayName = "AddTask_adds_pending_task_and_timeline_event")]
    public void AddTaskAddsPendingTaskAndTimelineEvent()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Expand the plan");

    var task = kernel.AddTask(goal.Id, AgentRole.Developer, "Implement a custom integration");

    Assert.Equal(6, goal.Tasks.Count);
    Assert.Equal(task, goal.Tasks.Last());
    Assert.Equal(WorkTaskStatus.Pending, task.Status);
    Assert.Equal(AgentRole.Developer, task.RequiredRole);
    Assert.Equal("Implement a custom integration", task.Description);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskAdded &&
        evt.Message.Contains("Implement a custom integration", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "AddTask_delegates_to_matching_agent_when_available")]
    public void AddTaskDelegatesToMatchingAgentWhenAvailable()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Expand and delegate the plan");
    var agents = DefaultAgents();

    var task = kernel.AddTask(goal.Id, AgentRole.Reviewer, "Review the custom integration", agents);

    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.Equal(agents.Single(agent => agent.Role == AgentRole.Reviewer).Id, task.AssignedAgentId);
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskAdded);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskDelegated);
}
    [Xunit.Fact(DisplayName = "CreateDefaultSoftwareDevelopmentTasks_include_verification_plans")]
    public void CreateDefaultSoftwareDevelopmentTasksIncludeVerificationPlans()
{
    var tasks = AgentOrchestratorKernel.CreateDefaultSoftwareDevelopmentTasks();

    Assert.Equal(5, tasks.Count);
    Assert.True(tasks.All(task => !string.IsNullOrWhiteSpace(task.VerificationPlan)), "Each default SDLC task should include a verification plan.");
    Assert.Contains(tasks, task => task.RequiredRole == AgentRole.Planner && task.VerificationPlan!.Contains("likely files", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(tasks, task => task.RequiredRole == AgentRole.Researcher && task.VerificationPlan!.Contains("commands", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(tasks, task => task.RequiredRole == AgentRole.Developer && task.VerificationPlan!.Contains("changed files", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(tasks, task => task.RequiredRole == AgentRole.Tester && task.VerificationPlan!.Contains("concrete pass/fail evidence", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(tasks, task => task.RequiredRole == AgentRole.Reviewer && task.VerificationPlan!.Contains("test gaps", StringComparison.OrdinalIgnoreCase));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_includes_role_specific_quality_requirements")]
    public void BuildTaskBriefIncludesRoleSpecificQualityRequirements()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Tighten prompts");

    AssertBriefContains(kernel, goal, AgentRole.Planner, "Planner Requirements", "likely files or modules");
    AssertBriefContains(kernel, goal, AgentRole.Researcher, "Researcher Requirements", "commands or file inspections");
    AssertBriefContains(kernel, goal, AgentRole.Researcher, "Researcher Requirements", "**/bin/**");
    AssertBriefContains(kernel, goal, AgentRole.Tester, "Tester Requirements", "concrete evidence");
    AssertBriefContains(kernel, goal, AgentRole.Tester, "Tester Requirements", "avoid treating bin/obj output as changed source");
    AssertBriefContains(kernel, goal, AgentRole.Reviewer, "Reviewer Requirements", "Challenge generic summaries");
    AssertBriefContains(kernel, goal, AgentRole.Reviewer, "Reviewer Requirements", "Ignore generated bin/obj output");
}
    [Xunit.Fact(DisplayName = "SetTaskVerificationPlan_persists_plan_and_timeline_event")]
    public void SetTaskVerificationPlanPersistsPlanAndTimelineEvent()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Plan verification");
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    var updated = kernel.SetTaskVerificationPlan(goal.Id, task.Id, "Run dotnet build and focused tests.");

    Assert.Equal(task, updated);
    Assert.Equal("Run dotnet build and focused tests.", task.VerificationPlan);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskVerificationPlanUpdated &&
        evt.Message.Contains("focused tests", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "Snapshot_roundtrip_preserves_added_task")]
    public void SnapshotRoundtripPreservesAddedTask()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Persist added task");
    var task = kernel.AddTask(goal.Id, AgentRole.Tester, "Verify a custom scenario", DefaultAgents());

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredGoal = restored.GetGoal(goal.Id);
    var restoredTask = restored.GetTask(goal.Id, task.Id);

    Assert.Equal(goal.Tasks.Count, restoredGoal.Tasks.Count);
    Assert.Equal("Verify a custom scenario", restoredTask.Description);
    Assert.Equal(AgentRole.Tester, restoredTask.RequiredRole);
    Assert.Equal(WorkTaskStatus.Assigned, restoredTask.Status);
    Assert.Contains(restoredGoal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskAdded);
}

static void AssertBriefContains(AgentOrchestratorKernel kernel, Goal goal, AgentRole role, string heading, string detail)
{
    var task = goal.Tasks.First(task => task.RequiredRole == role);
    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains(heading, StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains(detail, StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "Snapshot_roundtrip_preserves_verification_plan")]
    public void SnapshotRoundtripPreservesVerificationPlan()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Persist verification plan");
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.SetTaskVerificationPlan(goal.Id, task.Id, "Run dotnet test after implementation.");

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredTask = restored.GetTask(goal.Id, task.Id);

    Assert.Equal("Run dotnet test after implementation.", restoredTask.VerificationPlan);
    Assert.Contains(restored.GetGoal(goal.Id).Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskVerificationPlanUpdated);
}
    [Xunit.Fact(DisplayName = "Task_progress_updates_goal_timeline")]
    public void TaskProgressUpdatesGoalTimeline()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Implement progress tracking");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    clock.Advance();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Developer started implementation.");
    clock.Advance();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Developer finished implementation.");

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskStarted);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
    Assert.True(goal.Timeline.SequenceEqual(goal.Timeline.OrderBy(evt => evt.OccurredAt)), "Timeline should stay ordered.");
}
    [Xunit.Fact(DisplayName = "Human_input_request_pauses_task_and_goal")]
    public void HumanInputRequestPausesTaskAndGoal()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Need clarification");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Planner);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Planner started.");

    var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which repository should I target?");

    Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
    Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
    Assert.False(request.IsCompleted);
    Assert.Equal(request.Id, kernel.GetPendingHumanInput(goal.Id).Single().Id);
    Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.HumanInputRequested && evt.TaskId == task.Id);
}
    [Xunit.Fact(DisplayName = "Submitting_human_input_resumes_waiting_task")]
    public void SubmittingHumanInputResumesWaitingTask()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Resume after clarification");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Planner);
    var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which branch?");

    kernel.SubmitHumanInput(request.Id, "Use main.");

    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
    Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.HumanInputReceived && evt.TaskId == task.Id);
}
    [Xunit.Fact(DisplayName = "Snapshot_roundtrip_preserves_timeline_and_pending_input")]
    public void SnapshotRoundtripPreservesTimelineAndPendingInput()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Persist orchestrator state");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
    var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which verification command should run?");

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), new FakeClock());
    var restoredGoal = restored.GetGoal(goal.Id);
    var restoredRequest = restored.GetPendingHumanInput(goal.Id).Single();

    Assert.Equal(goal.Status, restoredGoal.Status);
    Assert.Equal(goal.Tasks.Count, restoredGoal.Tasks.Count);
    Assert.Equal(goal.Timeline.Count, restoredGoal.Timeline.Count);
    Assert.Equal(request.Id, restoredRequest.Id);
    Assert.Equal(task.Id, restoredRequest.TaskId);
    Assert.Equal(WorkTaskStatus.WaitingForHuman, restoredGoal.Tasks.First(item => item.Id == task.Id).Status);
}
}

