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
        evt.Message.Contains("Added Developer task", StringComparison.Ordinal));
    Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Message.Contains(task.Description, StringComparison.Ordinal)));
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
    [Xunit.Fact(DisplayName = "RedelegateTask_reassigns_assigned_task_to_current_role_agent")]
    public void RedelegateTaskReassignsAssignedTaskToCurrentRoleAgent()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Recover orphaned assignment", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    var oldAgent = TestAgent("anthropic-developer", "Anthropic developer", AgentRole.Developer);
    var newAgent = TestAgent("openai-developer", "OpenAI developer", AgentRole.Developer);
    kernel.ActivateGoal(goal.Id, [oldAgent]);
    var task = goal.Tasks.Single();

    var updated = kernel.RedelegateTask(goal.Id, task.Id, [newAgent]);

    Assert.Equal(task, updated);
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.Equal(newAgent.Id, task.AssignedAgentId);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskRedelegated &&
        evt.Message.Contains(oldAgent.Id.Value, StringComparison.Ordinal) &&
        evt.Message.Contains(newAgent.Id.Value, StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "RedelegateTask_reassigns_failed_task_to_current_role_agent")]
    public void RedelegateTaskReassignsFailedTaskToCurrentRoleAgent()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Recover failed assignment", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    var oldAgent = TestAgent("anthropic-developer", "Anthropic developer", AgentRole.Developer);
    var newAgent = TestAgent("openai-developer", "OpenAI developer", AgentRole.Developer);
    kernel.ActivateGoal(goal.Id, [oldAgent]);
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Assigned agent was removed.");

    kernel.RedelegateTask(goal.Id, task.Id, [newAgent]);

    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.Equal(newAgent.Id, task.AssignedAgentId);
}
    [Xunit.Fact(DisplayName = "RedelegateTask_refuses_running_task_with_recovery_guidance")]
    public void RedelegateTaskRefusesRunningTaskWithRecoveryGuidance()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Avoid moving live work", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    var agent = TestAgent("developer", "Developer", AgentRole.Developer);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Started.");

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => kernel.RedelegateTask(goal.Id, task.Id, [agent]));

    Assert.Contains("cancel or refresh", ex.Message, StringComparison.Ordinal);
    Assert.Equal(agent.Id, task.AssignedAgentId);
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
        evt.Message.Contains("Verification plan updated", StringComparison.Ordinal));
    Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Message.Contains(task.VerificationPlan!, StringComparison.Ordinal)));
}
    [Xunit.Fact(DisplayName = "RecordTaskNote_preserves_status_and_flows_into_brief")]
    public void RecordTaskNotePreservesStatusAndFlowsIntoBrief()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Carry operator guidance");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var originalStatus = task.Status;

    var updated = kernel.RecordTaskNote(goal.Id, task.Id, "Use the existing CLI command style.");
    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Equal(task, updated);
    Assert.Equal(originalStatus, task.Status);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskNote &&
        evt.Message == "Use the existing CLI command style.");
    Assert.Contains("TaskNote", brief, StringComparison.Ordinal);
    Assert.Contains("Use the existing CLI command style.", brief, StringComparison.Ordinal);
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

    [Xunit.Fact(DisplayName = "ResolveState_returns_Created_for_draft_goal")]
    public void ResolveStateReturnsCreatedForDraftGoal()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Draft goal");

    Assert.Equal(GoalLifecycleState.Created, GoalLifecycle.ResolveState(goal));
}

    [Xunit.Fact(DisplayName = "ResolveState_returns_Created_for_active_goal_with_no_workspace")]
    public void ResolveStateReturnsCreatedForActiveGoalWithNoWorkspace()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Active no workspace");
    kernel.ActivateGoal(goal.Id, DefaultAgents());

    Assert.Equal(GoalLifecycleState.Created, GoalLifecycle.ResolveState(goal));
}

    [Xunit.Fact(DisplayName = "ResolveState_returns_WorkspaceReady_when_workspace_exists_and_no_dispatch")]
    public void ResolveStateReturnsWorkspaceReadyWhenWorkspaceExistsAndNoDispatch()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Workspace ready");
    kernel.ActivateGoal(goal.Id, DefaultAgents());

    Assert.Equal(GoalLifecycleState.WorkspaceReady, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(WorkspaceExists: true)));
}

    [Xunit.Fact(DisplayName = "ResolveState_returns_Dispatched_when_task_is_running_with_dispatch_but_no_process")]
    public void ResolveStateReturnsDispatchedWhenTaskIsRunningWithDispatchButNoProcess()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Dispatched", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", "C:\\repo", clock.UtcNow));

    Assert.Equal(GoalLifecycleState.Dispatched, GoalLifecycle.ResolveState(goal));
}

    [Xunit.Fact(DisplayName = "ResolveState_returns_Running_when_task_has_live_process")]
    public void ResolveStateReturnsRunningWhenTaskHasLiveProcess()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Running", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", "C:\\repo", clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(1234, "codex exec prompt.md", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null));

    Assert.Equal(GoalLifecycleState.Running, GoalLifecycle.ResolveState(goal));
}

    [Xunit.Fact(DisplayName = "ResolveState_returns_AwaitingVerification_when_all_tasks_completed_without_verification")]
    public void ResolveStateReturnsAwaitingVerificationWhenAllTasksCompletedWithoutVerification()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Awaiting verification", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");

    Assert.Equal(GoalLifecycleState.AwaitingVerification, GoalLifecycle.ResolveState(goal));
}

    [Xunit.Fact(DisplayName = "ResolveState_requires_executed_test_receipt_after_task_gates_pass")]
    public void ResolveStateRequiresExecutedTestReceiptAfterTaskGatesPass()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Verified", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));

    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(GoalLifecycleState.AwaitingVerification, GoalLifecycle.ResolveState(goal));
    var gate = kernel.BuildVerificationGate(goal.Id);
    Assert.False(gate.IsSatisfied);
    Assert.Equal(VerificationGateReason.MissingExecutedTestReceipt, gate.Reason);

    RecordPassingReceipt(kernel, goal);

    Assert.Equal(GoalStatus.Verified, goal.Status);
    Assert.Equal(GoalLifecycleState.Verified, GoalLifecycle.ResolveState(goal));
}

    [Xunit.Fact(DisplayName = "Failed_executed_test_receipt_blocks_Verified")]
    public void FailedExecutedTestReceiptBlocksVerified()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Red tests", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet build", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));

    kernel.RecordExecutedTestReceipt(
        goal.Id,
        "slot-path focused tests",
        ["tests/FooTests.cs"],
        ["FooTests"],
        passedCount: 0,
        failedCount: 1,
        failedChecks: ["FooTests.Fails"]);

    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(GoalLifecycleState.AwaitingVerification, GoalLifecycle.ResolveState(goal));
    Assert.Equal(["FooTests.Fails"], goal.LatestExecutedTestReceipt!.FailedChecks);
}

    [Xunit.Fact(DisplayName = "ResolveState_returns_Verified_for_completed_goal_without_integration_cleanup_facts")]
    public void ResolveStateReturnsVerifiedForCompletedGoalWithoutIntegrationCleanupFacts()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Completed before durable cleanup", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));
    RecordPassingReceipt(kernel, goal);
    kernel.CompleteGoal(goal.Id, "Legacy completion before journal facts.");

    Assert.Equal(GoalStatus.Completed, goal.Status);
    Assert.Equal(GoalLifecycleState.Verified, GoalLifecycle.ResolveState(goal));
}

    [Xunit.Fact(DisplayName = "RetryTask_on_a_Verified_goal_downgrades_to_Active_so_the_conductor_redispatches")]
    public void RetryTaskOnVerifiedGoalDowngradesToActiveForRedispatch()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Retry redispatch", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));
    RecordPassingReceipt(kernel, goal);
    // Goal is Verified: a conduct tick here would run ACCEPTANCE, not re-dispatch.
    Assert.Equal(GoalLifecycleState.Verified, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(WorkspaceExists: true)));

    kernel.RetryTask(goal.Id, task.Id, "acceptance failed; fix the test compile errors");

    // The retried task is no longer complete: the goal drops back to Active and resolves to a
    // dispatch state (WorkspaceReady), so the conductor RE-DISPATCHES the worker with the feedback.
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(WorkTaskStatus.Assigned, goal.Tasks.Single().Status);
    Assert.Equal(GoalLifecycleState.WorkspaceReady, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(WorkspaceExists: true)));
}

    [Xunit.Fact(DisplayName = "RetryTask_invalidates_downstream_completed_tasks_and_current_gate_evidence")]
    public void RetryTaskInvalidatesDownstreamCompletedTasksAndCurrentGateEvidence()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Retry invalidates stale downstream evidence",
        [
            new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Test fix", AgentRole.Tester),
            new TaskSpec(TaskId.New(), "Review fix", AgentRole.Reviewer)
        ]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
    var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
    var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
    CompleteWithVerification(kernel, goal, developer, "developer passed");
    CompleteWithVerification(kernel, goal, tester, "tester passed");
    CompleteWithVerification(kernel, goal, reviewer, "reviewer passed");
    Assert.Equal(GoalStatus.Verified, goal.Status);

    kernel.RetryTask(goal.Id, developer.Id, "Developer output needs revision.");

    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
    Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
    Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
    Assert.Null(developer.LastVerification);
    Assert.Null(tester.LastVerification);
    Assert.Null(reviewer.LastVerification);
    Assert.Single(tester.VerificationHistory);
    Assert.Single(reviewer.VerificationHistory);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == tester.Id &&
        evt.Kind == ProgressKind.TaskRetried &&
        evt.Message.Contains("Invalidated Tester task", StringComparison.Ordinal));
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == reviewer.Id &&
        evt.Kind == ProgressKind.TaskRetried &&
        evt.Message.Contains("Invalidated Reviewer task", StringComparison.Ordinal));

    var gate = kernel.BuildVerificationGate(goal.Id);
    Assert.False(gate.IsSatisfied);
    Assert.True(gate.Tasks.Any(task => task.TaskId == tester.Id && task.GateStatus == VerificationGateStatus.NotReady));
    Assert.True(gate.Tasks.Any(task => task.TaskId == reviewer.Id && task.GateStatus == VerificationGateStatus.NotReady));
}

    [Xunit.Fact(DisplayName = "RetryTask_refuses_running_downstream_before_mutating_upstream_task")]
    public void RetryTaskRefusesRunningDownstreamBeforeMutatingUpstreamTask()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Retry with running downstream",
        [
            new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Test fix", AgentRole.Tester)
        ]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
    var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
    CompleteWithVerification(kernel, goal, developer, "developer passed");
    kernel.RecordTaskDispatch(goal.Id, tester.Id, new TaskDispatchRecord("tester", "test.exe", "C:\\repo", DateTimeOffset.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, tester.Id, new TaskProcessRecord(1234, "test.exe", "C:\\repo", "out.log", "err.log", "exit.txt", DateTimeOffset.UtcNow, null, null));

    var ex = Assert.ThrowsAny<InvalidOperationException>(() =>
        kernel.RetryTask(goal.Id, developer.Id, "Retry while tester is running."));

    Assert.True(ex.Message.Contains("downstream Tester", StringComparison.Ordinal));
    Assert.Equal(WorkTaskStatus.Completed, developer.Status);
    Assert.NotNull(developer.LastVerification);
    Assert.Single(developer.VerificationHistory);
    Assert.Equal(WorkTaskStatus.Running, tester.Status);
    Assert.NotNull(tester.LastProcess);
}

    [Xunit.Fact(DisplayName = "NormalizeGoalLifecycleState_reopens_terminal_goal_with_nonterminal_task")]
    public void NormalizeGoalLifecycleStateReopensTerminalGoalWithNonterminalTask()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Normalize terminal desync", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    kernel.SupersedeGoal(goal.Id, "Temporarily terminal while task remains assigned.");

    var repaired = kernel.NormalizeGoalLifecycleState(goal.Id, "repair terminal/nonterminal desync");

    Assert.True(repaired);
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Contains(goal.Timeline, evt =>
        evt.Kind == ProgressKind.GoalPolicyDecision &&
        evt.Message.Contains("repair terminal/nonterminal desync", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "ResolveState_returns_Merged_when_goal_completed_and_merged")]
    public void ResolveStateReturnsMergedWhenGoalCompletedAndMerged()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Merged", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));
    RecordPassingReceipt(kernel, goal);

    Assert.Equal(GoalLifecycleState.Merged, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(IsMerged: true)));
}

    [Xunit.Fact(DisplayName = "ResolveState_returns_Recorded_when_goal_completed_merged_and_recorded")]
    public void ResolveStateReturnsRecordedWhenGoalCompletedMergedAndRecorded()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Recorded", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));
    RecordPassingReceipt(kernel, goal);

    Assert.Equal(GoalLifecycleState.Recorded, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(IsMerged: true, IsRecorded: true)));
}

    [Xunit.Fact(DisplayName = "ResolveState_returns_CleanedUp_when_fully_concluded")]
    public void ResolveStateReturnsCleanedUpWhenFullyConcluded()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("CleanedUp", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));
    RecordPassingReceipt(kernel, goal);

    Assert.Equal(GoalLifecycleState.CleanedUp, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(IsMerged: true, IsRecorded: true, IsCleanedUp: true)));
}

    [Xunit.Fact(DisplayName = "ResolveState_does_not_treat_cleanup_only_as_landed")]
    public void ResolveStateDoesNotTreatCleanupOnlyAsLanded()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Cleanup only is not landed", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));
    RecordPassingReceipt(kernel, goal);

    Assert.Equal(GoalLifecycleState.Verified, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(IsCleanedUp: true)));
    Assert.Equal(GoalLifecycleState.Verified, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(IsRecorded: true, IsCleanedUp: true)));
    Assert.Equal(GoalLifecycleState.Merged, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(IsMerged: true, IsCleanedUp: true)));
}

    [Xunit.Fact(DisplayName = "ResolveState_returns_Failed_when_task_has_failed_status")]
    public void ResolveStateReturnsFailedWhenTaskHasFailedStatus()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Failed task", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Started.");
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Build error.");

    Assert.Equal(GoalLifecycleState.Failed, GoalLifecycle.ResolveState(goal));
}

    [Xunit.Fact(DisplayName = "ResolveState_returns_Failed_for_cancelled_goal")]
    public void ResolveStateReturnsFailedForCancelledGoal()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Cancelled", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
    kernel.SupersedeGoal(goal.Id, "Replaced by a cleaner goal.");

    Assert.Equal(GoalLifecycleState.Failed, GoalLifecycle.ResolveState(goal));
}

    [Xunit.Fact(DisplayName = "ResolveState_returns_Blocked_when_IsBlocked_fact_is_set")]
    public void ResolveStateReturnsBlockedWhenIsBlockedFactIsSet()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Blocked", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());

    Assert.Equal(GoalLifecycleState.Blocked, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(IsBlocked: true)));
}

    [Xunit.Fact(DisplayName = "ResolveState_returns_AwaitingHumanInput_when_goal_is_waiting_for_human")]
    public void ResolveStateReturnsAwaitingHumanInputWhenGoalIsWaitingForHuman()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Human input");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Planner);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Planner started.");
    kernel.RequestHumanInput(goal.Id, task.Id, "Which repository should I target?");

    Assert.Equal(GoalLifecycleState.AwaitingHumanInput, GoalLifecycle.ResolveState(goal));
}

    [Xunit.Fact(DisplayName = "ResolveState_does_not_treat_parked_goal_as_awaiting_human_input")]
    public void ResolveStateDoesNotTreatParkedGoalAsAwaitingHumanInput()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Parked goal");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var request = kernel.RequestHumanInput(goal.Id, null, "Can this wait?");

    kernel.ParkGoal(goal.Id, "deferred");

    Assert.True(request.IsCompleted);
    Assert.DoesNotContain(kernel.HumanInputRequests, candidate => candidate.GoalId == goal.Id && !candidate.IsCompleted);
    Assert.Equal(GoalStatus.Parked, goal.Status);
    Assert.NotEqual(GoalLifecycleState.AwaitingHumanInput, GoalLifecycle.ResolveState(goal));
    Assert.Equal(GoalLifecycleState.Created, GoalLifecycle.ResolveState(goal));
}

static void AssertBriefContains(AgentOrchestratorKernel kernel, Goal goal, AgentRole role, string heading, string detail)
{
    var task = goal.Tasks.First(task => task.RequiredRole == role);
    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(heading, brief, StringComparison.Ordinal);
    Assert.Contains(detail, brief, StringComparison.Ordinal);
}

static void CompleteWithVerification(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string standardOutput)
{
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, $"{task.RequiredRole} done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, standardOutput, "", DateTimeOffset.UtcNow));
    if (goal.Tasks.All(candidate => candidate.Status == WorkTaskStatus.Completed && candidate.LastVerification is { Succeeded: true }))
    {
        RecordPassingReceipt(kernel, goal);
    }
}

static void RecordPassingReceipt(AgentOrchestratorKernel kernel, Goal goal)
{
    kernel.RecordExecutedTestReceipt(
        goal.Id,
        "slot-path focused tests",
        ["src/Foo.cs", "tests/FooTests.cs"],
        ["FooTests"],
        passedCount: 1,
        failedCount: 0,
        branchHeadSha: "branch",
        mainHeadSha: "main");
}

static AgentDefinition TestAgent(string id, string name, AgentRole role) =>
    new(
        new AgentId(id),
        name,
        role,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey));

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

    [Xunit.Fact(DisplayName = "Snapshot_roundtrip_preserves_executed_test_receipt")]
    public void SnapshotRoundtripPreservesExecutedTestReceipt()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Persist executed test receipt");

    RecordPassingReceipt(kernel, goal);

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var receipt = restored.GetGoal(goal.Id).LatestExecutedTestReceipt;

    Assert.NotNull(receipt);
    Assert.Equal("slot-path focused tests", receipt.RunContext);
    Assert.Equal(1, receipt.PassedCount);
    Assert.Equal(0, receipt.FailedCount);
    Assert.Equal(["FooTests"], receipt.CoveredChecks);
    Assert.Equal("branch", receipt.BranchHeadSha);
}

    [Xunit.Fact(DisplayName = "Snapshot_roundtrip_preserves_criterion_retry_state")]
    public void SnapshotRoundtripPreservesCriterionRetryState()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Persist criterion retry state");
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["file-exists docs/usage.md: missing file"]);

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredTask = restored.GetTask(goal.Id, task.Id);

    Assert.Equal(1, restoredTask.CriterionRetryCount);
    Assert.True(restoredTask.CriterionRetryFeedback.Any(item => item.Contains("docs/usage.md", StringComparison.Ordinal)));
}

    [Xunit.Fact(DisplayName = "Snapshot_roundtrip_preserves_latest_retry_epoch")]
    public void SnapshotRoundtripPreservesLatestRetryEpoch()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Persist retry epoch");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    clock.Advance();
    kernel.RetryTask(goal.Id, task.Id, "Retry after failed acceptance.");
    var latestRetryAt = task.LatestRetryAt;

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredTask = restored.GetTask(goal.Id, task.Id);

    Assert.Equal(latestRetryAt, restoredTask.LatestRetryAt);
    Assert.Equal(WorkTaskStatus.Assigned, restoredTask.Status);
}

    [Xunit.Fact(DisplayName = "CancelGoal_marks_active_assigned_goal_cancelled_with_reason")]
    public void CancelGoalMarksActiveAssignedGoalCancelledWithReason()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Abandon validation", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, [TestAgent("developer", "Developer", AgentRole.Developer)]);

    var cancelled = kernel.CancelGoal(goal.Id, "Superseded by a cleaner validation goal.");

    Assert.Equal(goal, cancelled);
    Assert.Equal(GoalStatus.Cancelled, goal.Status);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId is null &&
        evt.Kind == ProgressKind.GoalCancelled &&
        evt.Message == "Superseded by a cleaner validation goal.");
    var next = kernel.BuildNextActions(goal.Id);
    Assert.Single(next.Items);
    Assert.True(next.Items[0].Message.Contains("Cancelled", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "SupersedeGoal_persists_terminal_status_and_timeline_reason")]
    public void SupersedeGoalPersistsTerminalStatusAndTimelineReason()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Replace validation", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);

    kernel.SupersedeGoal(goal.Id, "Replacement goal has narrower evidence.");

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredGoal = restored.GetGoal(goal.Id);

    Assert.Equal(GoalStatus.Superseded, restoredGoal.Status);
    Assert.Contains(restoredGoal.Timeline, evt =>
        evt.TaskId is null &&
        evt.Kind == ProgressKind.GoalSuperseded &&
        evt.Message == "Replacement goal has narrower evidence.");
}
    [Xunit.Fact(DisplayName = "CancelGoal_preserves_completed_goal")]
    public void CancelGoalPreservesCompletedGoal()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Already accepted", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("manual", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));
    RecordPassingReceipt(kernel, goal);
    kernel.CompleteGoal(goal.Id, "Test completed after cleanup evidence.");

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => kernel.CancelGoal(goal.Id, "No longer needed."));

    Assert.True(ex.Message.Contains("Completed", StringComparison.Ordinal));
    Assert.Equal(GoalStatus.Completed, goal.Status);
    Assert.False(goal.Timeline.Any(evt => evt.Kind == ProgressKind.GoalCancelled));
}
    [Xunit.Fact(DisplayName = "CancelGoal_refuses_goal_with_live_running_process")]
    public void CancelGoalRefusesGoalWithLiveRunningProcess()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Do not abandon live dispatch", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, [TestAgent("developer", "Developer", AgentRole.Developer)]);
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", "C:\\repo", clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(1234, "codex exec prompt.md", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null));

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => kernel.CancelGoal(goal.Id, "Abandon."));

    Assert.True(ex.Message.Contains("running dispatch", StringComparison.OrdinalIgnoreCase));
    Assert.True(ex.Message.Contains("1234", StringComparison.Ordinal));
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.False(goal.Timeline.Any(evt => evt.Kind == ProgressKind.GoalCancelled));
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
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
    Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.HumanInputReceived && evt.TaskId == task.Id);
    Assert.Contains(goal.Timeline, evt =>
        evt.Kind == ProgressKind.TaskUpdated &&
        evt.TaskId == task.Id &&
        evt.Message.Contains("restored task status to Assigned", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "SourceBacklogItemId_roundtrips_through_snapshot")]
    public void SourceBacklogItemIdRoundtripsThoughSnapshot()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Add a feature from backlog", [new TaskSpec(TaskId.New(), "Do it", AgentRole.Developer)]);
        kernel.SetGoalSourceBacklogItemId(goal.Id, "my-backlog-item-slug");

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), new FakeClock());
        var restoredGoal = restored.GetGoal(goal.Id);

        Assert.Equal("my-backlog-item-slug", restoredGoal.SourceBacklogItemId);
    }

    [Xunit.Fact(DisplayName = "SourceBacklogItemId_null_when_not_set_roundtrips_through_snapshot")]
    public void SourceBacklogItemIdNullRoundtripsThoughSnapshot()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        kernel.CreateGoal("Plain goal", [new TaskSpec(TaskId.New(), "Do it", AgentRole.Developer)]);

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), new FakeClock());
        var restoredGoal = restored.Goals.Single();

        Assert.True(restoredGoal.SourceBacklogItemId is null);
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

