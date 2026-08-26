using Mcg.AgentOrchestrator.Core;

public sealed class GoalLifecycleTests
{
    [Xunit.Fact(DisplayName = "RetryTask_requires_an_explicit_typed_cause")]
    public void RetryTaskRequiresExplicitTypedCause()
    {
        var retryTask = typeof(AgentOrchestratorKernel)
            .GetMethods()
            .Single(method => method.Name == nameof(AgentOrchestratorKernel.RetryTask));
        var retryCause = retryTask.GetParameters().Single(parameter => parameter.Name == "retryCause");

        Assert.False(
            retryCause.HasDefaultValue,
            "RetryTask.retryCause must be required so a caller cannot silently classify a retry as Unknown.");
    }

    [Xunit.Theory(DisplayName = "RetryTask_persists_each_supported_typed_cause")]
    [Xunit.InlineData(RetryCause.NewSourceFinding)]
    [Xunit.InlineData(RetryCause.NewTestFinding)]
    [Xunit.InlineData(RetryCause.CriterionEvidenceOwnerMismatch)]
    [Xunit.InlineData(RetryCause.EnvironmentApparatusFailure)]
    [Xunit.InlineData(RetryCause.ContractClarification)]
    [Xunit.InlineData(RetryCause.MainDriftConflict)]
    [Xunit.InlineData(RetryCause.ProviderInterruption)]
    [Xunit.InlineData(RetryCause.UnchangedContextRepeat)]
    public void RetryTaskPersistsEachSupportedTypedCause(RetryCause retryCause)
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal(
            "Persist a classified retry cause",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();

        kernel.RetryTask(goal.Id, task.Id, "Retry with a classified cause.", retryCause);

        Assert.Equal(retryCause, task.PendingRetryCause);
    }

    [Xunit.Fact(DisplayName = "RetryTask_rejects_Unknown_for_a_prospective_retry")]
    public void RetryTaskRejectsUnknownForAProspectiveRetry()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal(
            "Reject an unclassified retry",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            kernel.RetryTask(goal.Id, task.Id, "Retry without a classified cause.", RetryCause.Unknown));

        Assert.Equal("retryCause", exception.ParamName);
    }

    [Xunit.Fact(DisplayName = "TaskSpec_retry_transition_rejects_Unknown_for_prospective_history")]
    public void TaskSpecRetryTransitionRejectsUnknownForProspectiveHistory()
    {
        var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            task.RecordRetry(DateTimeOffset.Parse("2026-08-25T12:00:00Z"), RetryCause.Unknown));

        Assert.Equal("retryCause", exception.ParamName);
    }

    [Xunit.Fact(DisplayName = "GoalLifecycle_identifies_active_goal_with_failed_task")]
    public void GoalLifecycleIdentifiesActiveGoalWithFailedTask()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal(
            "Expose failed work",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        Assert.False(GoalLifecycle.HasActiveFailedTask(goal));

        kernel.ReportTaskProgress(goal.Id, goal.Tasks.Single().Id, WorkTaskStatus.Failed, "failed");

        Assert.True(GoalLifecycle.HasActiveFailedTask(goal));
        Assert.Equal(GoalStatus.Active, goal.Status);
    }

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
    Assert.Equal(
        [AgentRole.Researcher, AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
        tasks.Select(task => task.RequiredRole));
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
    AssertBriefContains(kernel, goal, AgentRole.Reviewer, "Reviewer Requirements", "evidence_request:{selections:");
    AssertBriefContains(kernel, goal, AgentRole.Reviewer, "Reviewer Requirements", "Dashboard.Tests");
    AssertBriefContains(kernel, goal, AgentRole.Tester, "Tester Requirements", "evidence_request:{selections:");
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

    kernel.RecordOperatorTaskNote(goal.Id, task.Id, "Operator ruling remains visible on retry.");
    var operatorBrief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;
    Assert.Contains("OperatorTaskNote", operatorBrief, StringComparison.Ordinal);
    Assert.Contains("Operator ruling remains visible on retry.", operatorBrief, StringComparison.Ordinal);
}

    [Xunit.Fact]
    public void StructuredOperatorGateCanBeExplicitlySatisfiedAndRoundTrips()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Gate a conditional deliverable");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(candidate => candidate.RequiredRole == AgentRole.Developer);
    var request = kernel.RequestHumanInput(goal.Id, task.Id, "Should console suppression ship?");

    kernel.SubmitHumanInput(request.Id, "Gate it until the hypothesis is confirmed.", ["hidden-console-spawn"]);
    clock.Advance();
    kernel.MarkOperatorGateSatisfied(request.Id, "hidden-console-spawn", "operator confirmed the instrumented observation");

    var gate = Assert.Single(request.OperatorGates);
    Assert.False(gate.IsActive);
    Assert.Equal("operator confirmed the instrumented observation", gate.SatisfactionEvidence);
    Assert.Contains(goal.Timeline, evt =>
        evt.Kind == ProgressKind.OperatorGateSatisfied &&
        evt.Message.Contains("hidden-console-spawn", StringComparison.Ordinal));

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredGate = Assert.Single(restored.GetHumanInputRequest(request.Id).OperatorGates);
    Assert.Equal(gate.SatisfiedAt, restoredGate.SatisfiedAt);
    Assert.Equal(gate.SatisfactionEvidence, restoredGate.SatisfactionEvidence);
}

    [Xunit.Fact]
    public void OperatorTaskNoteGateCanBeExplicitlySatisfiedAndRoundTrips()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Gate a task-note deliverable");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(candidate => candidate.RequiredRole == AgentRole.Developer);
    kernel.RecordOperatorTaskNote(goal.Id, task.Id, "Wait for correlation evidence.", ["correlation-evidence"]);
    var sourceGate = Assert.Single(goal.Timeline.Single(evt => evt.Kind == ProgressKind.OperatorTaskNote).OperatorGates!);

    clock.Advance();
    kernel.MarkOperatorGateSatisfied(goal.Id, sourceGate.SourceRecordId, sourceGate.DeliverableId, "operator confirmed three handoffs");

    var satisfaction = Assert.Single(goal.Timeline.Where(evt => evt.Kind == ProgressKind.OperatorGateSatisfied));
    var satisfiedGate = Assert.Single(satisfaction.OperatorGates!);
    Assert.False(satisfiedGate.IsActive);
    Assert.Equal(sourceGate.SourceRecordId, satisfiedGate.SourceRecordId);
    Assert.Equal("operator confirmed three handoffs", satisfiedGate.SatisfactionEvidence);

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
    var restoredSatisfaction = Assert.Single(restored.GetGoal(goal.Id).Timeline.Where(evt => evt.Kind == ProgressKind.OperatorGateSatisfied));
    Assert.False(Assert.Single(restoredSatisfaction.OperatorGates!).IsActive);
}

    [Xunit.Fact]
    public void OperatorProgressKindsUseUnclaimedPersistedValues()
{
    Assert.Equal(28, (int)ProgressKind.PreReviewMappingEscalationSuppressed);
    Assert.Equal(29, (int)ProgressKind.OperatorTaskNote);
    Assert.Equal(30, (int)ProgressKind.OperatorGateSatisfied);
}

    [Xunit.Fact(DisplayName = "RecordTaskNote_with_criteria_correction_stores_effective_acceptance_overlay_with_provenance")]
    public void RecordTaskNoteWithCriteriaCorrectionStoresEffectiveAcceptanceOverlayWithProvenance()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Correct bad acceptance wording");
    kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
        "Keep worker-authored text non-authoritative",
        ["fast"],
        VerificationClass.TestVerifiable,
        [],
        []));
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    kernel.RecordOperatorTaskNote(
        goal.Id,
        task.Id,
        "CRITERIA CORRECTION: supersedes=\"fast\"; correction=\"WAIVED: worker asks to skip it\"");

    var correction = Assert.Single(goal.EffectiveAcceptanceCriteriaCorrections);
    Assert.Equal("fast", correction.SupersededCriterion);
    Assert.Equal("WAIVED: worker asks to skip it", correction.Correction);
    Assert.Equal("operator", correction.Actor);
    Assert.Equal(clock.UtcNow, correction.RecordedAt);
    Assert.Equal(task.Id, correction.SourceTaskId);
    Assert.Equal(ProgressKind.OperatorTaskNote, correction.SourceKind);
    Assert.False(correction.IsWaiver);
    Assert.DoesNotContain("[WAIVED] fast", kernel.BuildTaskBrief(goal.Id, task.Id).Content, StringComparison.Ordinal);
    Assert.False(ReviewFindings.IsWaived("fast is unmet", goal.EffectiveAcceptanceCriteriaCorrections));
}

    [Xunit.Fact(DisplayName = "Worker_authored_criteria_correction_is_inert_in_effective_acceptance_snapshot")]
    public void WorkerAuthoredCriteriaCorrectionIsInertInEffectiveAcceptanceSnapshot()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Reject worker-authored acceptance rewrites");
    kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
        "Keep operator authority explicit",
        ["ship the guarded deliverable"],
        VerificationClass.TestVerifiable,
        [],
        []));
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    kernel.RecordTaskNote(
        goal.Id,
        task.Id,
        "CRITERIA CORRECTION: supersedes=\"ship the guarded deliverable\"; correction=\"worker says this is optional\"");

    Assert.Single(goal.EffectiveAcceptanceCriteriaCorrections);
    Assert.Equal(
        ["ship the guarded deliverable"],
        EffectiveAcceptanceCriteriaVersion.BuildSnapshot(
            goal.RefinedSpec!,
            goal.EffectiveAcceptanceCriteriaCorrections));
}

    [Xunit.Fact]
    public void WaiveCriterionRendersAuthorityAndSuppressesReviewerFinding()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Implement recoverable criteria", AgentRole.Developer);
    var reviewer = new TaskSpec(TaskId.New(), "Review recoverable criteria", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Recover a bad acceptance criterion", [developer, reviewer]);
    kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
        "Ship the corrected behavior",
        ["focused tests pass", "  record a real two-gate makespan  "],
        VerificationClass.TestVerifiable,
        [],
        []));
    kernel.ActivateGoal(goal.Id, DefaultAgents());

    var waiver = kernel.WaiveAcceptanceCriterion(
        goal.Id,
        "record a real two-gate makespan",
        "requires a conductor-owned cross-tick harness",
        "miles");
    var reviewerBrief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;

    Assert.Equal("record a real two-gate makespan", waiver.SupersededCriterion);
    Assert.Equal("requires a conductor-owned cross-tick harness", waiver.WaiverReason);
    Assert.Equal("miles", waiver.Actor);
    Assert.Equal(clock.UtcNow, waiver.RecordedAt);
    Assert.Null(waiver.SourceTaskId);
    Assert.Equal(ProgressKind.GoalPolicyDecision, waiver.SourceKind);
    Assert.True(waiver.IsWaiver);
    Assert.Equal(
        EffectiveAcceptanceCriteriaVersion.ComputeHash(goal.RefinedSpec!, goal.EffectiveAcceptanceCriteriaCorrections),
        waiver.CapturedAcceptanceCriteriaHash);
    const string legacyCapturedHash = "6fdda567bb4344ede633197a75d999adebbe326cc3e72b1260a2a1a94032e3c2";
    Assert.NotEqual(legacyCapturedHash, waiver.CapturedAcceptanceCriteriaHash);
    Assert.True(EffectiveAcceptanceCriteriaVersion.IsCapturedHashCurrent(
        goal.RefinedSpec!,
        goal.EffectiveAcceptanceCriteriaCorrections,
        legacyCapturedHash));
    Assert.Contains("- [WAIVED] record a real two-gate makespan", reviewerBrief, StringComparison.Ordinal);
    Assert.Contains("Reason: requires a conductor-owned cross-tick harness", reviewerBrief, StringComparison.Ordinal);
    Assert.True(ReviewFindings.IsWaived("record a real two-gate makespan is unmet", goal.EffectiveAcceptanceCriteriaCorrections));
    Assert.Contains(goal.Timeline, evt =>
        evt.Kind == ProgressKind.GoalPolicyDecision &&
        evt.Message.Contains("Acceptance criterion waived", StringComparison.Ordinal));
    clock.Advance();
    var duplicate = Assert.Throws<InvalidOperationException>(() => kernel.WaiveAcceptanceCriterion(
        goal.Id,
        "record a real two-gate makespan",
        "a differently worded duplicate waiver",
        " MILES "));
    Assert.Contains("already has this waiver recorded", duplicate.Message, StringComparison.Ordinal);

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock).GetGoal(goal.Id);
    var restoredWaiver = Assert.Single(restored.EffectiveAcceptanceCriteriaCorrections);
    Assert.True(restoredWaiver.IsWaiver);
    Assert.Equal(waiver.CapturedAcceptanceCriteriaHash, restoredWaiver.CapturedAcceptanceCriteriaHash);
    var malformedRestoredWaiver = new EffectiveAcceptanceCriteriaCorrection(
        "legacy criterion",
        "legacy reason without prefix",
        "operator",
        clock.UtcNow,
        null,
        ProgressKind.GoalPolicyDecision,
        IsWaiver: true);
    Assert.Equal("legacy reason without prefix", malformedRestoredWaiver.WaiverReason);
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

    [Xunit.Fact(DisplayName = "ResolveState_returns_Verified_when_task_gates_pass")]
    public void ResolveStateReturnsVerifiedWhenTaskGatesPass()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Verified", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));

    Assert.Equal(GoalStatus.Verified, goal.Status);
    Assert.Equal(GoalLifecycleState.Verified, GoalLifecycle.ResolveState(goal));
}

    [Xunit.Fact(DisplayName = "BeginGoalAcceptanceVerification_moves_Verified_goal_to_Verifying")]
    public void BeginGoalAcceptanceVerificationMovesVerifiedGoalToVerifying()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Acceptance in flight", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));

    var changed = kernel.BeginGoalAcceptanceVerification(goal.Id, "gate record persisted and launched");

    Assert.True(changed);
    Assert.Equal(GoalStatus.Verifying, goal.Status);
    Assert.Equal(GoalLifecycleState.Verifying, GoalLifecycle.ResolveState(goal));
}

    [Xunit.Fact(DisplayName = "CompleteGoal_from_Verifying_requires_every_task_to_remain_terminal")]
    public void CompleteGoalFromVerifyingRequiresEveryTaskToRemainTerminal()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Ancestry landing", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));
    kernel.BeginGoalAcceptanceVerification(goal.Id, "gate record persisted and launched");

    var completed = kernel.CompleteGoal(goal.Id, "Branch tip is an ancestor of main.");

    Assert.Equal(GoalStatus.Completed, completed.Status);

    var invalidKernel = new AgentOrchestratorKernel();
    var invalidGoal = invalidKernel.CreateGoal("Invalid ancestry landing", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    invalidKernel.ActivateGoal(invalidGoal.Id, DefaultAgents());
    var invalidTask = invalidGoal.Tasks.Single();
    invalidKernel.ReportTaskProgress(invalidGoal.Id, invalidTask.Id, WorkTaskStatus.Completed, "Done.");
    invalidKernel.RecordTaskVerification(invalidGoal.Id, invalidTask.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));
    invalidKernel.BeginGoalAcceptanceVerification(invalidGoal.Id, "gate record persisted and launched");
    invalidTask.SetStatus(WorkTaskStatus.Assigned);

    var error = Assert.Throws<InvalidOperationException>(() =>
        invalidKernel.CompleteGoal(invalidGoal.Id, "Branch tip is an ancestor of main."));

    Assert.Contains("every task is terminal", error.Message, StringComparison.Ordinal);
    Assert.Equal(GoalStatus.Verifying, invalidGoal.Status);
}

    [Xunit.Fact(DisplayName = "ReconcileGoalAcceptanceVerified_moves_Verifying_goal_to_Verified")]
    public void ReconcileGoalAcceptanceVerifiedMovesVerifyingGoalToVerified()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Acceptance passed", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));
    kernel.BeginGoalAcceptanceVerification(goal.Id, "gate record persisted and launched");

    var changed = kernel.ReconcileGoalAcceptanceVerified(goal.Id, "exit artifact passed");

    Assert.True(changed);
    Assert.Equal(GoalStatus.Verified, goal.Status);
    Assert.Equal(GoalLifecycleState.Verified, GoalLifecycle.ResolveState(goal));
}

    [Xunit.Fact(DisplayName = "ReconcileGoalAcceptanceFailed_moves_Verifying_goal_to_AcceptanceFailed")]
    public void ReconcileGoalAcceptanceFailedMovesVerifyingGoalToAcceptanceFailed()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Acceptance failed", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));
    kernel.BeginGoalAcceptanceVerification(goal.Id, "gate record persisted and launched");

    var changed = kernel.ReconcileGoalAcceptanceFailed(
        goal.Id,
        ["TestClass.FailingCase"],
        "exit artifact failed",
        "branch-sha",
        "main-sha",
        [new AcceptanceCheckAttribution("TestClass.FailingCase", AcceptanceFailureOrigin.Inherited, "also failed elsewhere")],
        "attested-red; shared failure");

    Assert.True(changed);
    Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
    Assert.Equal(GoalLifecycleState.AcceptanceFailed, GoalLifecycle.ResolveState(goal));
    Assert.NotNull(goal.LatestAcceptanceFailure);
    Assert.Contains("TestClass.FailingCase", goal.LatestAcceptanceFailure!.FailedChecks);
    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
    var restoredFailure = restored.GetGoal(goal.Id).LatestAcceptanceFailure!;
    Assert.Equal(AcceptanceFailureOrigin.Inherited, Assert.Single(restoredFailure.CheckAttributions!).Origin);
    Assert.Equal("attested-red; shared failure", restoredFailure.BaselineAttestation);
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
    // Goal is Verified: a conduct tick here would run ACCEPTANCE, not re-dispatch.
    Assert.Equal(GoalLifecycleState.Verified, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(WorkspaceExists: true)));

    kernel.RetryTask(goal.Id, task.Id, "acceptance failed; fix the test compile errors");

    // The retried task is no longer complete: the goal drops back to Active and resolves to a
    // dispatch state (WorkspaceReady), so the conductor RE-DISPATCHES the worker with the feedback.
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(WorkTaskStatus.Assigned, goal.Tasks.Single().Status);
    Assert.Equal(GoalLifecycleState.WorkspaceReady, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(WorkspaceExists: true)));
}

    [Xunit.Fact(DisplayName = "RetryTask_on_a_Verifying_goal_requires_attempt_invalidation_before_redispatch")]
    public void RetryTaskOnVerifyingGoalRequiresAttemptInvalidationBeforeRedispatch()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Retry during acceptance", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));
    kernel.BeginGoalAcceptanceVerification(goal.Id, "gate record persisted and launched");
    Assert.Equal(GoalStatus.Verifying, goal.Status);

    kernel.RetryTask(goal.Id, task.Id, "acceptance result requires a Developer correction");

    Assert.Equal(GoalStatus.Verifying, goal.Status);
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.Equal(GoalLifecycleState.Verifying, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(WorkspaceExists: true)));

    var reopened = kernel.ReopenVerifyingGoalAfterAcceptanceAttemptInvalidated(
        goal.Id,
        "Acceptance attempt was invalidated before redispatch.");

    Assert.True(reopened);
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(GoalLifecycleState.WorkspaceReady, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(WorkspaceExists: true)));
    Assert.Contains(goal.Timeline, evt =>
        evt.Kind == ProgressKind.GoalPolicyDecision &&
        evt.Message.Contains("invalidated before redispatch", StringComparison.Ordinal));
}

    [Xunit.Fact]
    public void RetryTaskOnAcceptanceFailedGoalDefersCurrentFailureButRetainsRetryContext()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Retry failed acceptance", [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));
    kernel.BeginGoalAcceptanceVerification(goal.Id, "gate record persisted and launched");
    kernel.ReconcileGoalAcceptanceFailed(goal.Id, ["TestClass.FailingCase"], "exit artifact failed");
    Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
    Assert.NotNull(goal.LatestAcceptanceFailure);

    kernel.RetryTask(goal.Id, task.Id, "acceptance failed; fix the named check");

    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Null(goal.LatestAcceptanceFailure);
    Assert.NotNull(goal.RetainedAcceptanceFailure);
    Assert.Equal(WorkTaskStatus.Assigned, goal.Tasks.Single().Status);
    Assert.Equal(GoalLifecycleState.WorkspaceReady, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(WorkspaceExists: true)));

    var restoredGoal = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot()).GetGoal(goal.Id);
    Assert.Null(restoredGoal.LatestAcceptanceFailure);
    Assert.NotNull(restoredGoal.RetainedAcceptanceFailure);
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

    kernel.RetryTask(
        goal.Id,
        developer.Id,
        "Developer output needs revision.",
        retryCause: RetryCause.NewSourceFinding);

    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
    Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
    Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
    Assert.Equal(RetryCause.NewSourceFinding, developer.PendingRetryCause);
    Assert.Equal(RetryCause.NewSourceFinding, tester.PendingRetryCause);
    Assert.Equal(RetryCause.NewSourceFinding, reviewer.PendingRetryCause);
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

    [Xunit.Fact]
    public void RetryTask_preserves_downstream_completed_work_when_the_candidate_is_unchanged()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Retry without changing the candidate",
            [
                new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Test fix", AgentRole.Tester),
                new TaskSpec(TaskId.New(), "Review fix", AgentRole.Reviewer)
            ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        CompleteCandidateDispatch(kernel, goal, developer, "aaa111", "aaa111");
        CompleteCandidateDispatch(kernel, goal, tester, "aaa111", "aaa111");
        CompleteCandidateDispatch(kernel, goal, reviewer, "aaa111", "aaa111");
        var testerVerification = tester.LastVerification;
        var reviewerVerification = reviewer.LastVerification;

        kernel.RetryTask(goal.Id, developer.Id, "Attach focused evidence without changing code.");
        CompleteCandidateDispatch(kernel, goal, developer, "aaa111", "aaa111");

        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
        Assert.Same(testerVerification, tester.LastVerification);
        Assert.Same(reviewerVerification, reviewer.LastVerification);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId is { } taskId &&
            (taskId == tester.Id || taskId == reviewer.Id) &&
            evt.Kind == ProgressKind.TaskRetried);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == tester.Id &&
            evt.Kind == ProgressKind.TaskUpdated &&
            evt.Message.Contains("verification covers candidate aaa111", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void NoChangeRetry_PreservesPassedDownstreamAndExposesDisposition()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Verified no-change retry",
            [
                new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Test fix", AgentRole.Tester),
                new TaskSpec(TaskId.New(), "Review fix", AgentRole.Reviewer)
            ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        CompleteCandidateDispatch(kernel, goal, developer, "aaa111", "aaa111");
        CompleteCandidateDispatch(kernel, goal, tester, "aaa111", "aaa111");
        CompleteCandidateDispatch(kernel, goal, reviewer, "aaa111", "aaa111");

        kernel.RecordCriterionRetryFeedback(
            goal.Id,
            developer.Id,
            ["Out-of-scope acceptance check failed; inspect the current candidate."]);
        kernel.RetryTask(goal.Id, developer.Id, "Inspect the out-of-scope gate failure.");
        kernel.RecordTaskDispatch(
            goal.Id,
            developer.Id,
            new TaskDispatchRecord("Developer", "worker", "C:\\repo", DateTimeOffset.UtcNow));
        kernel.RecordDispatchBaseCommit(goal.Id, developer.Id, "aaa111");
        var output = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: dotnet test --filter Focused",
            "tests: pass - focused verification completed",
            "commit: none",
            "blockers: none",
            "model_fit: OpenAI/test - adequate - deterministic fixture",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        var diagnostics = DispatchRejectionDiagnosticMarker.Format(true, 0, "none") + Environment.NewLine +
            DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.RequiredFileChangeEvidenceMissing);

        kernel.RecordDispatchExecutionResult(
            goal.Id,
            developer.Id,
            new TaskVerificationRecord(
                "worker",
                "C:\\repo",
                1,
                output,
                diagnostics,
                DateTimeOffset.UtcNow,
                WorkerResultPresent: true,
                HeartbeatStandardOutputBytes: output.Length));

        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal("aaa111", developer.LastDispatch!.ResultCommit);
        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId is { } taskId &&
            (taskId == tester.Id || taskId == reviewer.Id) &&
            evt.Kind == ProgressKind.TaskRetried);
        Assert.Contains("rule=verified-no-change-round", kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void NoChangeRetry_WithBlocker_FailsAsBlocker()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Blocked no-change retry",
            [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var developer = goal.Tasks.Single();
        CompleteCandidateDispatch(kernel, goal, developer, "aaa111", "aaa111");
        kernel.RecordCriterionRetryFeedback(goal.Id, developer.Id, ["Inspect the current candidate."]);
        kernel.RetryTask(goal.Id, developer.Id, "Inspect the current candidate.");
        kernel.RecordTaskDispatch(
            goal.Id,
            developer.Id,
            new TaskDispatchRecord("Developer", "worker", "C:\\repo", DateTimeOffset.UtcNow));
        kernel.RecordDispatchBaseCommit(goal.Id, developer.Id, "aaa111");
        var output = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: none",
            "tests: pass - focused verification completed",
            "commit: none",
            "blockers: exact-blocker - dependency unavailable",
            "model_fit: OpenAI/test - adequate - deterministic fixture",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        var diagnostics = DispatchRejectionDiagnosticMarker.Format(true, 0, "none") + Environment.NewLine +
            DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.RequiredFileChangeEvidenceMissing);

        kernel.RecordDispatchExecutionResult(
            goal.Id,
            developer.Id,
            new TaskVerificationRecord(
                "worker",
                "C:\\repo",
                1,
                output,
                diagnostics,
                DateTimeOffset.UtcNow,
                WorkerResultPresent: true,
                HeartbeatStandardOutputBytes: output.Length));

        Assert.Equal(WorkTaskStatus.Failed, developer.Status);
        Assert.Null(developer.LastDispatch!.ResultCommit);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == developer.Id &&
            evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains("WORKER_RESULT reported blocker: exact-blocker - dependency unavailable", StringComparison.Ordinal));
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId is null &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("rule=verified-no-change-round", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void RetryTask_invalidates_downstream_when_the_retry_changed_the_candidate()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Retry and change the candidate",
            [
                new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Test fix", AgentRole.Tester)
            ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        CompleteCandidateDispatch(kernel, goal, developer, "aaa111", "aaa111");
        CompleteCandidateDispatch(kernel, goal, tester, "aaa111", "aaa111");

        kernel.RetryTask(goal.Id, developer.Id, "Revise the candidate.");
        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        CompleteCandidateDispatch(kernel, goal, developer, "bbb222", "bbb222");

        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Null(tester.LastVerification);
        Assert.Single(tester.VerificationHistory);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == tester.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("changed candidate from aaa111 to bbb222", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void RetryTask_invalidates_retained_downstream_when_requeued_attempt_matches_current_but_not_reviewed_candidate()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Retry through an interrupted changed attempt",
            [
                new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Test fix", AgentRole.Tester)
            ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        CompleteCandidateDispatch(kernel, goal, developer, "aaa111", "aaa111");
        CompleteCandidateDispatch(kernel, goal, tester, "aaa111", "aaa111");

        kernel.RetryTask(goal.Id, developer.Id, "Revise the candidate.");
        kernel.RecordTaskDispatch(
            goal.Id,
            developer.Id,
            new TaskDispatchRecord("Developer", "worker", "C:\\repo", DateTimeOffset.UtcNow));
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, "bbb222");
        kernel.RequeueInterruptedDispatch(goal.Id, developer.Id, "Retry the interrupted changed attempt.");
        Assert.Equal(RetryCause.ProviderInterruption, developer.PendingRetryCause);
        CompleteCandidateDispatch(kernel, goal, developer, "bbb222", "bbb222");

        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Null(tester.LastVerification);
        Assert.Single(tester.VerificationHistory);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == tester.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("changed candidate from aaa111 to bbb222", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void RetryTask_invalidates_downstream_when_candidate_identity_is_indeterminate()
    {
        var missingPriorKernel = new AgentOrchestratorKernel();
        var missingPriorGoal = missingPriorKernel.CreateGoal("Retry without a prior candidate",
            [
                new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Test fix", AgentRole.Tester)
            ]);
        missingPriorKernel.ActivateGoal(missingPriorGoal.Id, DefaultAgents());
        var missingPriorDeveloper = missingPriorGoal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var missingPriorTester = missingPriorGoal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        CompleteCandidateDispatch(missingPriorKernel, missingPriorGoal, missingPriorDeveloper, null, null);
        CompleteCandidateDispatch(missingPriorKernel, missingPriorGoal, missingPriorTester, "aaa111", "aaa111");

        missingPriorKernel.RetryTask(missingPriorGoal.Id, missingPriorDeveloper.Id, "Retry with unknown prior candidate.");

        Assert.Equal(WorkTaskStatus.Assigned, missingPriorTester.Status);
        Assert.Null(missingPriorTester.LastVerification);

        var missingCurrentKernel = new AgentOrchestratorKernel();
        var missingCurrentGoal = missingCurrentKernel.CreateGoal("Retry without a current candidate",
            [
                new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Test fix", AgentRole.Tester)
            ]);
        missingCurrentKernel.ActivateGoal(missingCurrentGoal.Id, DefaultAgents());
        var missingCurrentDeveloper = missingCurrentGoal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var missingCurrentTester = missingCurrentGoal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        CompleteCandidateDispatch(missingCurrentKernel, missingCurrentGoal, missingCurrentDeveloper, "aaa111", "aaa111");
        CompleteCandidateDispatch(missingCurrentKernel, missingCurrentGoal, missingCurrentTester, "aaa111", "aaa111");

        missingCurrentKernel.RetryTask(missingCurrentGoal.Id, missingCurrentDeveloper.Id, "Retry with unknown result candidate.");
        Assert.Equal(WorkTaskStatus.Completed, missingCurrentTester.Status);
        CompleteCandidateDispatch(missingCurrentKernel, missingCurrentGoal, missingCurrentDeveloper, null, null);

        Assert.Equal(WorkTaskStatus.Assigned, missingCurrentTester.Status);
        Assert.Null(missingCurrentTester.LastVerification);
        Assert.Contains(missingCurrentGoal.Timeline, evt =>
            evt.TaskId == missingCurrentTester.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("result unknown", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void RecordTaskVerification_completes_retried_task_without_result_commit_invalidates_retained_downstream()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Complete a retried task through direct verification",
            [
                new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Test fix", AgentRole.Tester)
            ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        CompleteCandidateDispatch(kernel, goal, developer, "aaa111", "aaa111");
        CompleteCandidateDispatch(kernel, goal, tester, "aaa111", "aaa111");

        kernel.RetryTask(goal.Id, developer.Id, "Attach verification without a dispatch result.");
        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        kernel.RecordTaskVerification(
            goal.Id,
            developer.Id,
            new TaskVerificationRecord(
                "manual",
                "C:\\repo",
                0,
                "passed",
                "",
                DateTimeOffset.UtcNow));

        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Null(tester.LastVerification);
        Assert.Single(tester.VerificationHistory);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == tester.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("result unknown", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void RecordTaskProcessCancelled_invalidates_retained_downstream_when_current_candidate_is_indeterminate()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Cancel a retried task with retained downstream evidence",
            [
                new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Test fix", AgentRole.Tester)
            ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        CompleteCandidateDispatch(kernel, goal, developer, "aaa111", "aaa111");
        CompleteCandidateDispatch(kernel, goal, tester, "aaa111", "aaa111");

        kernel.RetryTask(goal.Id, developer.Id, "Retry before cancellation.");
        kernel.RecordTaskDispatch(
            goal.Id,
            developer.Id,
            new TaskDispatchRecord("Developer", "worker", "C:\\repo", clock.UtcNow));
        var started = new TaskProcessRecord(
            1234,
            "worker",
            "C:\\repo",
            "out.log",
            "err.log",
            "exit.txt",
            clock.UtcNow,
            null,
            null);
        kernel.RecordTaskProcessStarted(goal.Id, developer.Id, started);
        kernel.RecordTaskProcessCancelled(
            goal.Id,
            developer.Id,
            started with { CompletedAt = clock.UtcNow, WasCancelled = true });

        Assert.Equal(WorkTaskStatus.Cancelled, developer.Status);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Null(tester.LastVerification);
        Assert.Single(tester.VerificationHistory);
        Assert.False(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == tester.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("result unknown; status Cancelled", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void CancelledUnchangedRetryPreservesDownstreamAndAcceptanceFailure()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Cancel a proven unchanged retry",
            [
                new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Test fix", AgentRole.Tester),
                new TaskSpec(TaskId.New(), "Review fix", AgentRole.Reviewer)
            ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        CompleteCandidateDispatch(kernel, goal, developer, "aaa111", "aaa111");
        CompleteCandidateDispatch(kernel, goal, tester, "aaa111", "aaa111");
        CompleteCandidateDispatch(kernel, goal, reviewer, "aaa111", "aaa111");
        var testerVerification = tester.LastVerification;
        var reviewerVerification = reviewer.LastVerification;
        kernel.BeginGoalAcceptanceVerification(goal.Id, "gate launched");
        kernel.ReconcileGoalAcceptanceFailed(goal.Id, ["Acceptance.Failed"], "candidate-independent apparatus failure");
        var acceptanceFailure = goal.LatestAcceptanceFailure;

        kernel.RetryTask(goal.Id, developer.Id, "Inspect the unchanged candidate.");
        kernel.RecordTaskDispatch(
            goal.Id,
            developer.Id,
            new TaskDispatchRecord(
                "Developer",
                "worker",
                "C:\\repo",
                clock.UtcNow,
                BaseCommit: "aaa111",
                WorktreeHeadSha: "aaa111",
                DirtyStateHash: "empty-status-hash"));
        var started = new TaskProcessRecord(
            1234, "worker", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null);
        kernel.RecordTaskProcessStarted(goal.Id, developer.Id, started);

        kernel.RecordTaskProcessCancelled(
            goal.Id,
            developer.Id,
            started with { CompletedAt = clock.UtcNow, WasCancelled = true },
            CancellationCandidateEvidence.ConfirmedUnchanged("aaa111", "head=aaa111; worktree=clean; commits_after_dispatch=0"));

        Assert.Equal("aaa111", developer.LastDispatch!.ResultCommit);
        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
        Assert.Same(testerVerification, tester.LastVerification);
        Assert.Same(reviewerVerification, reviewer.LastVerification);
        Assert.Same(acceptanceFailure, goal.LatestAcceptanceFailure);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == developer.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("kind=ConfirmedUnchanged", StringComparison.Ordinal) &&
            evt.Message.Contains("candidate=aaa111", StringComparison.Ordinal));
    }

    [Xunit.Theory]
    [Xunit.InlineData(CancellationCandidateEvidenceKind.Dirty)]
    [Xunit.InlineData(CancellationCandidateEvidenceKind.Changed)]
    [Xunit.InlineData(CancellationCandidateEvidenceKind.Unsafe)]
    [Xunit.InlineData(CancellationCandidateEvidenceKind.Unavailable)]
    public void CancelledRetryWithoutPositiveProofInvalidatesDownstream(
        CancellationCandidateEvidenceKind kind)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Cancel a retry without positive proof",
            [
                new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Test fix", AgentRole.Tester)
            ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        CompleteCandidateDispatch(kernel, goal, developer, "aaa111", "aaa111");
        CompleteCandidateDispatch(kernel, goal, tester, "aaa111", "aaa111");
        kernel.BeginGoalAcceptanceVerification(goal.Id, "gate launched");
        kernel.ReconcileGoalAcceptanceFailed(goal.Id, ["Acceptance.Failed"], "candidate-specific failure");
        var acceptanceFailure = goal.LatestAcceptanceFailure;
        kernel.RetryTask(goal.Id, developer.Id, "Inspect the candidate.");
        kernel.RecordTaskDispatch(
            goal.Id,
            developer.Id,
            new TaskDispatchRecord(
                "Developer", "worker", "C:\\repo", clock.UtcNow,
                BaseCommit: "aaa111", WorktreeHeadSha: "aaa111", DirtyStateHash: "empty-status-hash"));
        var started = new TaskProcessRecord(
            1234, "worker", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null);
        kernel.RecordTaskProcessStarted(goal.Id, developer.Id, started);
        var evidence = kind switch
        {
            CancellationCandidateEvidenceKind.Dirty => CancellationCandidateEvidence.Dirty("aaa111", "dirty paths", "status_short=M file.cs"),
            CancellationCandidateEvidenceKind.Changed => CancellationCandidateEvidence.Changed("bbb222", "head changed", "head=bbb222"),
            CancellationCandidateEvidenceKind.Unsafe => CancellationCandidateEvidence.Unsafe("branch-mismatch", "expected=goal; actual=main"),
            CancellationCandidateEvidenceKind.Unavailable => CancellationCandidateEvidence.Unavailable("git-inspection-failed", "exit_code=1"),
            _ => throw new InvalidOperationException()
        };

        kernel.RecordTaskProcessCancelled(
            goal.Id,
            developer.Id,
            started with { CompletedAt = clock.UtcNow, WasCancelled = true },
            evidence);

        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Null(tester.LastVerification);
        Assert.Same(acceptanceFailure, goal.LatestAcceptanceFailure);
        var restoredGoal = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot()).GetGoal(goal.Id);
        Assert.NotNull(restoredGoal.LatestAcceptanceFailure);
        Assert.Equal(acceptanceFailure!.FailedChecks, restoredGoal.LatestAcceptanceFailure!.FailedChecks);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == developer.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains($"kind={kind}", StringComparison.Ordinal) &&
            evt.Message.Contains(evidence.Reason, StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void ConductorCancelledDirtyRetryRetainsAcceptanceFailure()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Detach a dirty retry during conductor handoff",
            [new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var developer = goal.Tasks.Single();
        CompleteCandidateDispatch(kernel, goal, developer, "aaa111", "aaa111");
        kernel.BeginGoalAcceptanceVerification(goal.Id, "gate launched");
        kernel.ReconcileGoalAcceptanceFailed(goal.Id, ["Acceptance.Failed"], "candidate-specific failure");
        var acceptanceFailure = goal.LatestAcceptanceFailure;
        kernel.RetryTask(goal.Id, developer.Id, "Resume after conductor handoff.");
        kernel.RecordTaskDispatch(
            goal.Id,
            developer.Id,
            new TaskDispatchRecord(
                "Developer", "worker", "C:\\repo", clock.UtcNow,
                BaseCommit: "aaa111", WorktreeHeadSha: "aaa111", DirtyStateHash: "empty-status-hash"));
        var started = new TaskProcessRecord(
            1234, "worker", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null);
        kernel.RecordTaskProcessStarted(goal.Id, developer.Id, started);

        kernel.RecordTaskProcessCancelled(
            goal.Id,
            developer.Id,
            started with
            {
                CompletedAt = clock.UtcNow,
                WasCancelled = true,
                WasCancelledByConductor = true
            },
            CancellationCandidateEvidence.Dirty("aaa111", "dirty paths", "status_short=M file.cs"));

        Assert.Same(acceptanceFailure, goal.LatestAcceptanceFailure);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == developer.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("kind=Dirty", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void RetryTask_preserves_the_never_run_downstream_exemption()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Retry while preserving never-run downstream work",
            [
                new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Test fix", AgentRole.Tester),
                new TaskSpec(TaskId.New(), "Review fix", AgentRole.Reviewer)
            ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents().Where(agent => agent.Role != AgentRole.Reviewer).ToArray());
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        CompleteCandidateDispatch(kernel, goal, developer, "aaa111", "aaa111");
        CompleteCandidateDispatch(kernel, goal, tester, "bbb222", "bbb222");

        kernel.RetryTask(goal.Id, developer.Id, "Retry with stale Tester evidence.");

        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Equal(WorkTaskStatus.Pending, reviewer.Status);
        Assert.Null(reviewer.LastVerification);
        Assert.Null(reviewer.LastExecution);
        Assert.Null(reviewer.LastDispatch);
        Assert.Null(reviewer.LastProcess);
        Assert.Null(reviewer.SubscriptionRetryAfter);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id && evt.Kind == ProgressKind.TaskRetried);
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
}

static void CompleteCandidateDispatch(
    AgentOrchestratorKernel kernel,
    Goal goal,
    TaskSpec task,
    string? resultCommit,
    string? reviewedCommit)
{
    kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord(task.RequiredRole.ToString(), "worker", "C:\\repo", DateTimeOffset.UtcNow));
    if (!string.IsNullOrWhiteSpace(reviewedCommit))
    {
        kernel.RecordDispatchBaseCommit(goal.Id, task.Id, reviewedCommit);
    }
    if (!string.IsNullOrWhiteSpace(resultCommit))
    {
        kernel.RecordDispatchResultCommit(goal.Id, task.Id, resultCommit);
    }

    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, $"{task.RequiredRole} done.");
    kernel.RecordTaskVerification(
        goal.Id,
        task.Id,
        new TaskVerificationRecord(
            "dotnet test",
            "C:\\repo",
            0,
            "passed",
            "",
            DateTimeOffset.UtcNow,
            ReviewedCommit: reviewedCommit));
}

static AgentDefinition TestAgent(string id, string name, AgentRole role) =>
    new(
        new AgentId(id),
        name,
        role,
        new ModelProfile("OpenAI", OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey));

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

    [Xunit.Fact]
    public void FromSnapshot_MissingHumanRequest_ReconstructsOpenRequest()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal(
            "Repair a missing request",
            [new TaskSpec(TaskId.New(), "Ask before expanding scope", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        kernel.RequestHumanInput(goal.Id, task.Id, "Should scope expand?");
        var corrupt = kernel.ExportSnapshot() with { HumanInputRequests = [] };

        var restored = AgentOrchestratorKernel.FromSnapshot(corrupt, clock);

        var request = Assert.Single(restored.GetPendingHumanInput(goal.Id));
        Assert.Equal(task.Id, request.TaskId);
        Assert.Equal("Should scope expand?", request.Question);
        Assert.Contains(
            restored.GetGoal(goal.Id).Timeline,
            evt => evt.Kind == ProgressKind.TaskUpdated &&
                evt.Message.Contains("original kind and policy metadata were unavailable", StringComparison.Ordinal));
        var reloaded = AgentOrchestratorKernel.FromSnapshot(corrupt, clock);
        Assert.Equal(request.Id, Assert.Single(reloaded.GetPendingHumanInput(goal.Id)).Id);
        reloaded.SubmitHumanInput(request.Id, "Keep the existing scope.");
        Assert.Equal(WorkTaskStatus.Assigned, reloaded.GetTask(goal.Id, task.Id).Status);
    }

    [Xunit.Fact]
    public void RefreshTrackedGoals_NewHumanWait_DoesNotCreateDuplicate()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal(
            "Refresh an external request",
            [new TaskSpec(TaskId.New(), "Ask outside the conductor", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var external = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), new FakeClock());
        var expected = external.RequestHumanInput(goal.Id, goal.Tasks.Single().Id, "Proceed?");
        var snapshot = external.ExportSnapshot();

        kernel.RefreshTrackedGoals(snapshot);
        kernel.IngestNewGoals(snapshot);

        var actual = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.Equal(expected.Id, actual.Id);
    }

    [Xunit.Fact]
    public void FromSnapshot_CompletedHumanWait_RestoresTaskWithoutResurrection()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal(
            "Do not resurrect a resolved wait",
            [new TaskSpec(TaskId.New(), "Await an operator decision", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        kernel.RequestHumanInput(goal.Id, task.Id, "Proceed?");
        kernel.ParkGoal(goal.Id, "waiting for a decision");
        kernel.UnparkGoal(goal.Id, "decision recorded");

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), new FakeClock());

        Assert.Empty(restored.GetPendingHumanInput(goal.Id));
        Assert.Equal(WorkTaskStatus.Assigned, restored.GetTask(goal.Id, task.Id).Status);
    }

    [Xunit.Fact]
    public void RefreshParkedGoal_LegacyResolvedWait_RestoresTaskBeforePromotion()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal(
            "Repair a parked resolved wait",
            [new TaskSpec(TaskId.New(), "Await an operator decision", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Proceed?");
        kernel.SubmitHumanInput(request.Id, "Proceed with the existing scope.");
        var snapshot = kernel.ExportSnapshot();
        var answered = snapshot.Goals.Single();
        var resolvedAt = answered.Timeline.Last(evt => evt.Kind == ProgressKind.HumanInputReceived).OccurredAt;
        var legacyParked = answered with
        {
            Status = GoalStatus.Parked,
            Tasks = answered.Tasks
                .Select(candidate => candidate.Id == task.Id.Value
                    ? candidate with { Status = WorkTaskStatus.WaitingForHuman }
                    : candidate)
                .ToArray(),
            Timeline = answered.Timeline
                .Append(new ProgressEventSnapshot(
                    goal.Id.Value,
                    null,
                    ProgressKind.GoalPolicyDecision,
                    "Goal parked: waiting for a decision",
                    resolvedAt.AddTicks(-1)))
                .ToArray()
        };
        var restored = AgentOrchestratorKernel.FromSnapshot(
            snapshot with { Goals = [legacyParked] },
            clock);

        var promoted = restored.RefreshParkedGoalsWithResolvedHumanWaits();

        Assert.Equal(1, promoted);
        Assert.Equal(GoalStatus.Active, restored.GetGoal(goal.Id).Status);
        Assert.Equal(WorkTaskStatus.Assigned, restored.GetTask(goal.Id, task.Id).Status);
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
    [Xunit.Fact(DisplayName = "RecordFindingEvidenceOutcome_recovers_requesting_finding_from_unmerged_worker_output")]
    public void RecordFindingEvidenceOutcomeRecoversRequestingFindingFromUnmergedWorkerOutput()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var reviewer = new TaskSpec(TaskId.New(), "Review the change.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Preserve typed evidence outcomes after truncated output", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(
            goal.Id,
            reviewer.Id,
            new TaskDispatchRecord("reviewer", "review", "C:\\repo", DateTimeOffset.UtcNow));
        var output = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "blockers: exact-blocker - focused receipt required",
            """findings: [{"stable_id":"truncated-finding","state":"open","location":{"file":"tests/Test.cs","region":"Test.Run"},"description":"Focused receipt required.","evidence_request":{"selections":[{"test_project":"Core.Tests","test_class":"GoalLifecycleTests"}]}}]""",
            "touched_anchors: []",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            reviewer.Id,
            new TaskVerificationRecord(
                "review",
                "C:\\repo",
                1,
                output,
                "",
                DateTimeOffset.UtcNow,
                WorkerResultPresent: false));
        Assert.Null(reviewer.LastVerification!.MergedReviewFindings);
        var request = new FindingEvidenceRequest(
            [new FindingEvidenceSelection("Core.Tests", "GoalLifecycleTests")]);
        var receipt = new FindingEvidenceReceipt(
            "receipt-1",
            "abc1234",
            request,
            Accepted: true,
            Passed: true,
            "focused evidence passed");

        kernel.RecordFindingEvidenceOutcome(
            goal.Id,
            reviewer.Id,
            "truncated-finding",
            new FindingEvidenceOutcome(Honoured: true, ReceiptId: receipt.ReceiptId),
            receipt);

        var recordedFinding = Assert.Single(reviewer.LastVerification.MergedReviewFindings!);
        Assert.Equal("truncated-finding", recordedFinding.StableId);
        Assert.Equal(receipt.ReceiptId, recordedFinding.EvidenceOutcome?.ReceiptId);
        Assert.Same(receipt, Assert.Single(reviewer.LastVerification.FindingEvidenceReceipts!));
    }

    [Xunit.Fact(DisplayName = "RecordFindingEvidenceOutcome_resolves_canonicalized_requesting_finding_identity")]
    public void RecordFindingEvidenceOutcomeResolvesCanonicalizedRequestingFindingIdentity()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review the change.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Attach evidence after finding identity canonicalization", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        static string Result(string stableId, bool includeRequest)
        {
            var request = includeRequest
                ? ""","evidence_request":{"selections":[{"test_project":"Core.Tests","test_class":"GoalLifecycleTests"}]}"""
                : string.Empty;
            return string.Join(
                Environment.NewLine,
                "WORKER_RESULT:",
                "files: none",
                "commands: review",
                "tests: pass - deterministic fixture",
                "commit: none",
                "blockers: exact-blocker - focused receipt required",
                $"findings: [{{\"stable_id\":\"{stableId}\",\"state\":\"open\",\"location\":{{\"file\":\"tests/Test.cs\",\"region\":\"Test.Run\",\"hunk\":\"focused\"}},\"description\":\"Focused receipt required.\"{request}}}]",
                "touched_anchors: []",
                "criteria_verdicts: []",
                "verdict: needs-work",
                "model_fit: fixture/model - adequate - deterministic review",
                "skills: none",
                "confidence: high",
                "END_WORKER_RESULT");
        }

        kernel.RecordTaskDispatch(
            goal.Id,
            reviewer.Id,
            new TaskDispatchRecord("reviewer", "review-1", "C:\\repo", clock.UtcNow));
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            reviewer.Id,
            new TaskVerificationRecord(
                "review-1",
                "C:\\repo",
                0,
                Result("prior-finding", includeRequest: false),
                "",
                clock.UtcNow,
                WorkerResultPresent: true));

        clock.Advance();
        kernel.RetryTask(goal.Id, reviewer.Id, "recheck");
        kernel.RecordTaskDispatch(
            goal.Id,
            reviewer.Id,
            new TaskDispatchRecord("reviewer", "review-2", "C:\\repo", clock.UtcNow));
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            reviewer.Id,
            new TaskVerificationRecord(
                "review-2",
                "C:\\repo",
                0,
                Result("submitted-finding", includeRequest: true),
                "",
                clock.UtcNow,
                WorkerResultPresent: true));

        var canonicalized = Assert.Single(reviewer.LastVerification!.MergedReviewFindings!);
        Assert.Equal("prior-finding", canonicalized.StableId);
        Assert.NotNull(canonicalized.EvidenceRequest);
        var receipt = new FindingEvidenceReceipt(
            "receipt-canonical",
            "abc1234",
            canonicalized.EvidenceRequest!,
            Accepted: true,
            Passed: true,
            "focused evidence passed");

        kernel.RecordFindingEvidenceOutcome(
            goal.Id,
            reviewer.Id,
            "submitted-finding",
            new FindingEvidenceOutcome(Honoured: true, ReceiptId: receipt.ReceiptId),
            receipt);

        var recordedFinding = Assert.Single(reviewer.LastVerification.MergedReviewFindings!);
        Assert.Equal("prior-finding", recordedFinding.StableId);
        Assert.Equal(receipt.ReceiptId, recordedFinding.EvidenceOutcome?.ReceiptId);
        Assert.Same(receipt, Assert.Single(reviewer.LastVerification.FindingEvidenceReceipts!));
        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(
            reviewer.LastVerification, out var reportedRound, out _));
        var resolvedFromSubmittedIdentity = Assert.IsType<ReviewFinding>(
            ReviewFindingConvergence.ResolveMergedFinding(
                reviewer.LastVerification.MergedReviewFindings!, reportedRound, "submitted-finding"));
        Assert.Same(recordedFinding, resolvedFromSubmittedIdentity);
        Assert.Equal(receipt.ReceiptId, resolvedFromSubmittedIdentity.EvidenceOutcome?.ReceiptId);
    }

    [Xunit.Fact(DisplayName = "RecordFindingEvidenceOutcome_falls_back_to_reported_finding_when_anchor_is_ambiguous")]
    public void RecordFindingEvidenceOutcomeFallsBackToReportedFindingWhenAnchorIsAmbiguous()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var reviewer = new TaskSpec(TaskId.New(), "Review the change.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Attach evidence despite an ambiguous canonical anchor", [reviewer]);
        var location = new ReviewFindingLocation("tests/Test.cs", "Test.Run", "focused");
        var request = new FindingEvidenceRequest(
            [new FindingEvidenceSelection("Core.Tests", "GoalLifecycleTests")]);
        var reported = new ReviewFinding(
            "submitted-finding",
            ReviewFindingState.Open,
            location,
            "Focused receipt required.",
            EvidenceRequest: request);
        var merged = new[]
        {
            new ReviewFinding("resolved-at-anchor", ReviewFindingState.Resolved, location, "Prior issue resolved."),
            new ReviewFinding(
                "canonical-open-at-anchor",
                ReviewFindingState.Open,
                location,
                "Current issue remains open.",
                EvidenceRequest: request)
        };
        var output = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "blockers: exact-blocker - focused receipt required",
            $"findings: {System.Text.Json.JsonSerializer.Serialize(new[] { reported })}",
            "touched_anchors: []",
            "END_WORKER_RESULT");
        kernel.RecordTaskVerification(
            goal.Id,
            reviewer.Id,
            new TaskVerificationRecord(
                "review",
                "C:\\repo",
                1,
                output,
                "",
                DateTimeOffset.UtcNow,
                MergedReviewFindings: merged));

        kernel.RecordFindingEvidenceOutcome(
            goal.Id,
            reviewer.Id,
            reported.StableId,
            new FindingEvidenceOutcome(
                Honoured: false,
                Reason: FindingEvidenceNotHonouredReason.Unknown,
                Detail: "Ambiguous canonical anchor; attached to reported finding."));

        var recorded = Assert.Single(
            reviewer.LastVerification!.MergedReviewFindings!,
            finding => finding.StableId == reported.StableId);
        Assert.Equal(FindingEvidenceNotHonouredReason.Unknown, recorded.EvidenceOutcome?.Reason);
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

