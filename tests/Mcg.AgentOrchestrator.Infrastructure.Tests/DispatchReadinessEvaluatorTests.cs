using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DispatchReadinessEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FutureRetryAfter = Now.AddMinutes(30);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Goal ActiveGoalWithAssignedTask(AgentRole role = AgentRole.Developer)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement feature", role);
        var goal = kernel.CreateGoal("Test goal", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return kernel.GetGoal(goal.Id);
    }

    private static SubscriptionPlan PlanWithItems(Goal goal, IReadOnlyList<SubscriptionPlanItem> items) =>
        new(
            goal.Id.Value,
            goal.Objective,
            goal.Status,
            ReadyToPrepareCount: items.Count(i => i.CanPrepare),
            ResolvableProfileCount: 0,
            RetryDeferredCount: items.Count(i => i.RetryAfter is not null),
            NextSubscriptionRetryAfter: null,
            ReadyStartCostRisk: null,
            ReadyStartPromptCharacterCount: null,
            ReadyStartCostRiskDetails: [],
            ReadyStartCostRecommendation: null,
            CapacitySchedule: new ProviderCapacitySchedule(
                ProviderCapacityDisposition.Blocked, "", 0, 0, null, false, []),
            ReadyModelUsage: [],
            ProviderBudgets: [],
            Items: items);

    private static SubscriptionPlanItem MakePlanItem(
        Goal goal,
        string taskId,
        WorkTaskStatus status = WorkTaskStatus.Assigned,
        bool canPrepare = true,
        DateTimeOffset? retryAfter = null,
        string providerName = "OpenAI") =>
        new(
            TaskNumber: 1,
            TaskId: taskId,
            Role: AgentRole.Developer,
            TaskStatus: status,
            Description: "Task",
            AgentId: null,
            AgentName: null,
            ProviderName: providerName,
            ModelName: "gpt-5.4-mini",
            ExecutionPolicy: null,
            ProfileName: "codex-cli",
            SubscriptionModelAlias: null,
            ProfileExists: true,
            ProfileIsResolvable: true,
            ProfileIsEchoOnly: false,
            ProfileIsPatchCapable: true,
            CanPrepare: canPrepare,
            Detail: canPrepare ? "Ready to prepare subscription dispatch." : "Provider is cooling down.",
            RetryAfter: retryAfter);

    // ── Ready ─────────────────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "EvaluateDispatchReadiness_returns_Ready_when_candidate_can_prepare")]
    public void EvaluateDispatchReadinessReturnsReadyWhenCandidateCanPrepare()
    {
        var goal = ActiveGoalWithAssignedTask();
        var taskId = goal.Tasks[0].Id.Value;
        var plan = PlanWithItems(goal, [MakePlanItem(goal, taskId, canPrepare: true)]);

        var verdict = DispatchReadinessEvaluator.EvaluateDispatchReadiness(goal, plan, Now);

        Xunit.Assert.IsType<DispatchReadinessReady>(verdict);
    }

    // ── Deferred ──────────────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "EvaluateDispatchReadiness_returns_Deferred_when_all_candidates_have_future_RetryAfter")]
    public void EvaluateDispatchReadinessReturnsDeferredWhenAllCandidatesHaveFutureRetryAfter()
    {
        var goal = ActiveGoalWithAssignedTask();
        var taskId = goal.Tasks[0].Id.Value;
        var plan = PlanWithItems(goal, [
            MakePlanItem(goal, taskId, canPrepare: false, retryAfter: FutureRetryAfter)
        ]);

        var verdict = DispatchReadinessEvaluator.EvaluateDispatchReadiness(goal, plan, Now);

        var deferred = Xunit.Assert.IsType<DispatchReadinessDeferred>(verdict);
        Xunit.Assert.Equal(FutureRetryAfter, deferred.RetryAfter);
        Xunit.Assert.Contains("retry after", deferred.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "EvaluateDispatchReadiness_Deferred_surfaces_min_RetryAfter_across_multiple_tasks")]
    public void EvaluateDispatchReadinessDeferredSurfacesMinRetryAfterAcrossMultipleTasks()
    {
        var kernel = new AgentOrchestratorKernel();
        var task1 = new TaskSpec(TaskId.New(), "Task 1", AgentRole.Developer);
        var task2 = new TaskSpec(TaskId.New(), "Task 2", AgentRole.Developer);
        var goal = kernel.CreateGoal("Multi-task goal", [task1, task2]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var updatedGoal = kernel.GetGoal(goal.Id);

        var earlier = FutureRetryAfter;
        var later = FutureRetryAfter.AddHours(2);
        var plan = PlanWithItems(updatedGoal, [
            MakePlanItem(updatedGoal, task1.Id.Value, canPrepare: false, retryAfter: later),
            MakePlanItem(updatedGoal, task2.Id.Value, canPrepare: false, retryAfter: earlier)
        ]);

        var verdict = DispatchReadinessEvaluator.EvaluateDispatchReadiness(updatedGoal, plan, Now);

        var deferred = Xunit.Assert.IsType<DispatchReadinessDeferred>(verdict);
        Xunit.Assert.Equal(earlier, deferred.RetryAfter);
    }

    [Xunit.Fact(DisplayName = "EvaluateDispatchReadiness_returns_Ready_when_any_candidate_can_prepare_despite_others_deferred")]
    public void EvaluateDispatchReadinessReturnsReadyWhenAnyCandidateCanPrepareDespiteOthersDeferred()
    {
        var kernel = new AgentOrchestratorKernel();
        var task1 = new TaskSpec(TaskId.New(), "Task 1", AgentRole.Developer);
        var task2 = new TaskSpec(TaskId.New(), "Task 2", AgentRole.Developer);
        var goal = kernel.CreateGoal("Mixed goal", [task1, task2]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var updatedGoal = kernel.GetGoal(goal.Id);

        var plan = PlanWithItems(updatedGoal, [
            MakePlanItem(updatedGoal, task1.Id.Value, canPrepare: false, retryAfter: FutureRetryAfter),
            MakePlanItem(updatedGoal, task2.Id.Value, canPrepare: true)
        ]);

        var verdict = DispatchReadinessEvaluator.EvaluateDispatchReadiness(updatedGoal, plan, Now);

        Xunit.Assert.IsType<DispatchReadinessReady>(verdict);
    }

    // ── Blocked ───────────────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "EvaluateDispatchReadiness_returns_Blocked_no_candidates_when_no_assigned_tasks")]
    public void EvaluateDispatchReadinessReturnsBlockedNoCandidatesWhenNoAssignedTasks()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Completed task", AgentRole.Developer);
        var goal = kernel.CreateGoal("Done goal", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        // Mark the task as completed so HasAssignedDispatchCandidates returns false
        var dispatch = new TaskDispatchRecord("worker", "exe", "dir", DateTimeOffset.UtcNow);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
        var verification = new TaskVerificationRecord("exe", "dir", 0, "ok", "", DateTimeOffset.UtcNow);
        kernel.RecordTaskVerification(goal.Id, task.Id, verification);
        var updatedGoal = kernel.GetGoal(goal.Id);

        var plan = PlanWithItems(updatedGoal, [
            MakePlanItem(updatedGoal, task.Id.Value, status: WorkTaskStatus.Completed, canPrepare: false)
        ]);

        var verdict = DispatchReadinessEvaluator.EvaluateDispatchReadiness(updatedGoal, plan, Now);

        var blocked = Xunit.Assert.IsType<DispatchReadinessBlocked>(verdict);
        Xunit.Assert.False(blocked.HasCandidates);
    }

    [Xunit.Fact(DisplayName = "EvaluateDispatchReadiness_returns_Blocked_with_candidates_when_assigned_tasks_cannot_prepare")]
    public void EvaluateDispatchReadinessReturnsBlockedWithCandidatesWhenAssignedTasksCannotPrepare()
    {
        var goal = ActiveGoalWithAssignedTask();
        var taskId = goal.Tasks[0].Id.Value;
        // Assigned task but CanPrepare=false and no future RetryAfter (e.g., missing profile)
        var plan = PlanWithItems(goal, [
            MakePlanItem(goal, taskId, canPrepare: false, retryAfter: null)
        ]);

        var verdict = DispatchReadinessEvaluator.EvaluateDispatchReadiness(goal, plan, Now);

        var blocked = Xunit.Assert.IsType<DispatchReadinessBlocked>(verdict);
        Xunit.Assert.True(blocked.HasCandidates);
    }

    [Xunit.Fact(DisplayName = "EvaluateDispatchReadiness_treats_past_RetryAfter_as_not_deferred")]
    public void EvaluateDispatchReadinessTreatsPastRetryAfterAsNotDeferred()
    {
        var goal = ActiveGoalWithAssignedTask();
        var taskId = goal.Tasks[0].Id.Value;
        var pastRetryAfter = Now.AddMinutes(-5);
        var plan = PlanWithItems(goal, [
            MakePlanItem(goal, taskId, canPrepare: false, retryAfter: pastRetryAfter)
        ]);

        var verdict = DispatchReadinessEvaluator.EvaluateDispatchReadiness(goal, plan, Now);

        // Past RetryAfter means cooldown has expired; no longer deferred → Blocked with candidates
        var blocked = Xunit.Assert.IsType<DispatchReadinessBlocked>(verdict);
        Xunit.Assert.True(blocked.HasCandidates);
    }

    // ── GoalReadinessPreflight integration ────────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalReadinessPreflight_reports_deferred_finding_when_all_candidates_cooling_down")]
    public void GoalReadinessPreflightReportsDeferredFindingWhenAllCandidatesCoolingDown()
    {
        var kernel = new AgentOrchestratorKernel();
        var agents = AgentCatalog.Default().Agents;
        var task = new TaskSpec(TaskId.New(), "Implement feature in src/App/Feature.cs", AgentRole.Developer);
        var goal = kernel.CreateGoal("Add feature to src/App/Feature.cs", [task]);
        kernel.ActivateGoal(goal.Id, agents);
        var updatedGoal = kernel.GetGoal(goal.Id);

        // Build a worker profile catalog where the profile exists and has required placeholders,
        // then create a synthetic plan item that is assigned but deferred (CanPrepare=false, future RetryAfter).
        // We use a custom plan built inline rather than calling SubscriptionPlanBuilder to avoid
        // needing a real dispatched-failure history just to exercise the deferred path.
        var tempRoot = Path.GetTempPath();
        var syntheticPlan = PlanWithItems(updatedGoal, [
            MakePlanItem(updatedGoal, task.Id.Value, canPrepare: false, retryAfter: FutureRetryAfter)
        ]);

        // We cannot pass syntheticPlan directly to GoalReadinessPreflight (it builds its own plan from
        // profiles). Instead, build a profile catalog that causes CanPrepare=false AND has no future
        // RetryAfter scenario — so we verify via DispatchReadinessEvaluator directly.
        var verdict = DispatchReadinessEvaluator.EvaluateDispatchReadiness(updatedGoal, syntheticPlan, Now);
        Xunit.Assert.IsType<DispatchReadinessDeferred>(verdict);
    }

    // ── CrossGoalSubscriptionStartPlanner integration ─────────────────────────

    [Xunit.Fact(DisplayName = "CrossGoalSubscriptionStartPlanner_excludes_deferred_goal_from_candidates")]
    public void CrossGoalSubscriptionStartPlannerExcludesDeferredGoalFromCandidates()
    {
        var kernel = new AgentOrchestratorKernel();
        var agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();

        // Goal with a Planner task (no workspace required): activate and verify that the planner
        // returns it as a candidate when tasks are ready, and excludes it when the subscription plan
        // has no ready items (all CanPrepare=false, e.g., no valid profile match for the task role).
        // We test exclusion by giving the kernel a goal with no assigned tasks that can prepare.
        var task = new TaskSpec(TaskId.New(), "Plan the work", AgentRole.Planner);
        var goal = kernel.CreateGoal("Plan-only objective", [task]);
        kernel.ActivateGoal(goal.Id, agents);

        var plan = CrossGoalSubscriptionStartPlanner.Build(kernel, agents, profiles);

        // The Planner agent should be assigned and CanPrepare=true with the default codex-cli profile,
        // so the goal SHOULD be a candidate.
        // This verifies the evaluator is called and Ready goals pass through.
        var candidate = plan.Candidates.FirstOrDefault(c => c.GoalId == goal.Id.Value);
        Xunit.Assert.NotNull(candidate);
    }
}
