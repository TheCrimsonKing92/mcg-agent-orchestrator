using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BudgetAwareRoutingTests
{
    private static readonly WorkerProfileCatalog DefaultProfiles = WorkerProfileCatalog.Default();

    // Ollama developer agent with subscription execution allowed
    private static AgentDefinition OllamaDeveloperAgent() =>
        new(
            new AgentId("ollama-developer-test"),
            "Ollama Developer",
            AgentRole.Developer,
            new ModelProfile("Ollama", "qwen3:8b", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.LocalBridge),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            ComplexModel: new ModelProfile("Ollama", "qwen3:14b", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.LocalBridge));

    [Xunit.Fact(DisplayName = "BudgetAwareRouting_simple_task_confirms_local_provider_as_cost_optimal")]
    public void BudgetAwareRoutingSimpleTaskConfirmsLocalProviderAsCostOptimal()
    {
        var kernel = new AgentOrchestratorKernel();
        var ollamaAgent = OllamaDeveloperAgent();
        // "verify " prefix triggers IsLowImpactTestOrVerificationTask → Simple complexity
        var task = new TaskSpec(TaskId.New(), "Verify test output for the deployment check", AgentRole.Developer);
        var goal = kernel.CreateGoal("Simple maintenance work", [task]);
        kernel.ActivateGoal(goal.Id, [ollamaAgent]);
        var updatedGoal = kernel.GetGoal(goal.Id);

        var plan = SubscriptionPlanBuilder.Build(updatedGoal, [ollamaAgent], DefaultProfiles);
        var item = plan.Items.Single(i => i.Role == AgentRole.Developer);

        Assert.Equal(WorkerRouteDisposition.Selected, item.Route!.Disposition);
        Assert.True(
            item.Route.Reasons.Any(r =>
                r.Contains("simple", StringComparison.OrdinalIgnoreCase) &&
                r.Contains("local", StringComparison.OrdinalIgnoreCase)));
    }

    [Xunit.Fact(DisplayName = "BudgetAwareRouting_simple_task_prefers_local_over_paid_when_both_available")]
    public void BudgetAwareRoutingSimpleTaskPrefersLocalOverPaidWhenBothAvailable()
    {
        var kernel = new AgentOrchestratorKernel();
        var ollamaAgent = OllamaDeveloperAgent();
        var openAiAgents = AgentCatalog.Default().Agents;
        // Both local and paid agents available
        var allAgents = openAiAgents.Concat(new[] { ollamaAgent }).ToList();

        var task = new TaskSpec(TaskId.New(), "Verify test output for the deployment check", AgentRole.Developer);
        var goal = kernel.CreateGoal("Simple maintenance work", [task]);
        kernel.ActivateGoal(goal.Id, allAgents);
        var updatedGoal = kernel.GetGoal(goal.Id);

        var plan = SubscriptionPlanBuilder.Build(updatedGoal, allAgents, DefaultProfiles);
        var item = plan.Items.Single(i => i.Role == AgentRole.Developer);

        // The qwen-code harness resolves to its LlamaCpp backend; the Simple task must stay off the paid OpenAI lane.
        Assert.Equal(WorkerRouteDisposition.Selected, item.Route!.Disposition);
        Assert.True(string.Equals("LlamaCpp", item.ProviderName, StringComparison.OrdinalIgnoreCase));
        Assert.True(
            item.Route.Reasons.Any(r =>
                r.Contains("simple", StringComparison.OrdinalIgnoreCase) &&
                r.Contains("local", StringComparison.OrdinalIgnoreCase)));
    }

    [Xunit.Fact(DisplayName = "BudgetAwareRouting_no_local_available_selects_paid_lane")]
    public void BudgetAwareRoutingNoLocalAvailableSelectsPaidLane()
    {
        var kernel = new AgentOrchestratorKernel();
        // OpenAI-only catalog — no local agents
        var agents = AgentCatalog.Default().Agents;

        var task = new TaskSpec(TaskId.New(), "Verify test output for the deployment check", AgentRole.Developer);
        var goal = kernel.CreateGoal("Simple maintenance work", [task]);
        kernel.ActivateGoal(goal.Id, agents);
        var updatedGoal = kernel.GetGoal(goal.Id);

        var plan = SubscriptionPlanBuilder.Build(updatedGoal, agents, DefaultProfiles);
        var item = plan.Items.Single(i => i.Role == AgentRole.Developer);

        // No local agent available → paid lane is selected as the only option
        Assert.Equal(WorkerRouteDisposition.Selected, item.Route!.Disposition);
        Assert.False(string.Equals("Ollama", item.ProviderName, StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "BudgetAwareRouting_scorecard_avoid_overrides_cheap_lane")]
    public void BudgetAwareRoutingScorecardAvoidOverridesCheapLane()
    {
        var kernel = new AgentOrchestratorKernel();
        // The Developer default is the scoped Sol subscription lane; Ideation remains on the legacy default alias.
        var agents = AgentCatalog.Default().Agents;
        var task = new TaskSpec(TaskId.New(), "Update docs for the new API endpoint", AgentRole.Developer);
        var goal = kernel.CreateGoal("Documentation update", [task]);
        kernel.ActivateGoal(goal.Id, agents);
        var updatedGoal = kernel.GetGoal(goal.Id);

        // Scorecard says Avoid for the selected spark cheap lane.
        var scorecard = new[]
        {
            new ModelOutcomeRecord(
                "OpenAI",
                "gpt-5.3-codex-spark",
                Completed: 1,
                Failed: 2,
                SelfRatedAdequate: 2,
                SelfRatedOverkill: 0,
                SelfRatedUnderpowered: 0,
                Divergence: 2,
                Recommendation: ModelOutcomeRecommendation.Avoid,
                Reason: "2/3 recent dispatches failed. 2 self-rated adequate dispatch(es) failed.",
                DispatchLane: "codex-spark")
        };

        var plan = SubscriptionPlanBuilder.Build(updatedGoal, agents, DefaultProfiles, scorecard: scorecard);
        var item = plan.Items.Single(i => i.Role == AgentRole.Developer);

        // Scorecard Avoid must BLOCK the route even when canPrepare would otherwise be true
        Assert.Equal(WorkerRouteDisposition.Blocked, item.Route!.Disposition);
        Assert.True(
            item.Route.Reasons.Any(r => r.Contains("scorecard=Avoid", StringComparison.OrdinalIgnoreCase)));
        Assert.True(
            item.Route.Alternatives.Any(a =>
                a.Contains("Scorecard says Avoid", StringComparison.OrdinalIgnoreCase) &&
                a.Contains("route to a different provider", StringComparison.OrdinalIgnoreCase)));
    }

    [Xunit.Fact(DisplayName = "BudgetAwareRouting_scorecard_lookup_accepts_same_model_in_multiple_lanes")]
    public void BudgetAwareRoutingScorecardLookupAcceptsSameModelInMultipleLanes()
    {
        var kernel = new AgentOrchestratorKernel();
        var agents = AgentCatalog.Default().Agents;
        var task = new TaskSpec(TaskId.New(), "Implement high-risk multi-scope persistence migration", AgentRole.Developer);
        var goal = kernel.CreateGoal("Complex dispatch planning", [task]);
        kernel.RecordGoalPolicyDecision(
            goal.Id,
            "Intake pipeline decision (auto): developer-reviewer; reasons: high-risk objective needs pre-acceptance review; risk labels: complex, high-risk.");
        kernel.ActivateGoal(goal.Id, agents);
        var updatedGoal = kernel.GetGoal(goal.Id);
        var scorecard = new[]
        {
            new ModelOutcomeRecord(
                "OpenAI",
                AgentCatalog.OpenAiSolSubscriptionModelAlias,
                Completed: 3,
                Failed: 0,
                SelfRatedAdequate: 3,
                SelfRatedOverkill: 0,
                SelfRatedUnderpowered: 0,
                Divergence: 0,
                Recommendation: ModelOutcomeRecommendation.Prefer,
                Reason: "3/3 recent dispatches completed.",
                DispatchLane: "codex-cli"),
            new ModelOutcomeRecord(
                "OpenAI",
                AgentCatalog.OpenAiSolSubscriptionModelAlias,
                Completed: 0,
                Failed: 3,
                SelfRatedAdequate: 3,
                SelfRatedOverkill: 0,
                SelfRatedUnderpowered: 0,
                Divergence: 3,
                Recommendation: ModelOutcomeRecommendation.Avoid,
                Reason: "3/3 recent dispatches failed.",
                DispatchLane: "codex-spark")
        };

        var plan = SubscriptionPlanBuilder.Build(updatedGoal, agents, DefaultProfiles, scorecard: scorecard);
        var item = plan.Items.Single(i => i.Role == AgentRole.Developer);

        Assert.Equal("codex-cli", item.ProfileName);
        Assert.Equal(WorkerRouteDisposition.Selected, item.Route!.Disposition);
        Assert.Contains(item.Route.Reasons, reason => reason.Contains("scorecard=Prefer", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "BudgetAwareRouting_budget_exhausted_suggests_ollama_fallback")]
    public void BudgetAwareRoutingBudgetExhaustedSuggestsOllamaFallback()
    {
        var kernel = new AgentOrchestratorKernel();
        var agents = AgentCatalog.Default().Agents;
        var task = new TaskSpec(TaskId.New(), "Implement the new caching layer", AgentRole.Developer);
        var goal = kernel.CreateGoal("Caching feature", [task]);
        kernel.ActivateGoal(goal.Id, agents);
        var updatedGoal = kernel.GetGoal(goal.Id);
        var assignedTask = updatedGoal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);

        // Record a recoverable limit failure with a future retry-after so the task enters retry-deferred state
        var command = $"codex exec --skip-git-repo-check --model {AgentCatalog.OpenAiSubscriptionModelAlias}";
        var workDir = "C:\\work";
        var dispatch = new TaskDispatchRecord("codex-cli", command, workDir, DateTimeOffset.UtcNow, "OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias);
        kernel.RecordTaskDispatch(goal.Id, assignedTask.Id, dispatch);

        var futureRetryAt = DateTimeOffset.UtcNow.AddHours(2);
        var timeStr = BuildRetryTimeString(futureRetryAt);
        var limitError = $"ERROR: You've hit your usage limit. Purchase more credits or try again at {timeStr}.";
        kernel.RecordDispatchExecutionResult(goal.Id, assignedTask.Id, new TaskVerificationRecord(
            command, workDir, 1, string.Empty, limitError, DateTimeOffset.UtcNow));

        // After a recoverable limit failure, task is Assigned again with SubscriptionRetryAfter set
        updatedGoal = kernel.GetGoal(goal.Id);
        var plan = SubscriptionPlanBuilder.Build(updatedGoal, agents, DefaultProfiles);
        var item = plan.Items.Single(i => i.Role == AgentRole.Developer);

        Assert.Equal(WorkerRouteDisposition.Deferred, item.Route!.Disposition);
        Assert.True(
            item.Route.Alternatives.Any(a => a.Contains("Ollama", StringComparison.OrdinalIgnoreCase)));
    }

    [Xunit.Fact(DisplayName = "BudgetAwareRouting_complex_task_recommends_paid_capable_model")]
    public void BudgetAwareRoutingComplexTaskRecommendsPaidCapableModel()
    {
        var kernel = new AgentOrchestratorKernel();
        var agents = AgentCatalog.Default().Agents;
        // Task description with strong complexity signals to trigger Complex complexity estimate
        var task = new TaskSpec(TaskId.New(),
            "Implement comprehensive authentication system with schema migration, distributed state management, and end-to-end integration tests for all new service endpoints",
            AgentRole.Developer);
        var goal = kernel.CreateGoal("Security hardening initiative", [task]);
        kernel.ActivateGoal(goal.Id, agents);
        var updatedGoal = kernel.GetGoal(goal.Id);

        var plan = SubscriptionPlanBuilder.Build(updatedGoal, agents, DefaultProfiles);
        var item = plan.Items.Single(i => i.Role == AgentRole.Developer);

        Assert.Equal(WorkerRouteDisposition.Selected, item.Route!.Disposition);
        Assert.True(
            item.Route.Reasons.Any(r =>
                r.Contains("complex", StringComparison.OrdinalIgnoreCase) &&
                (r.Contains("capable", StringComparison.OrdinalIgnoreCase) || r.Contains("complexity", StringComparison.OrdinalIgnoreCase))));
    }

    [Xunit.Fact(DisplayName = "BudgetAwareRouting_complex_task_prefers_paid_over_local_when_both_available")]
    public void BudgetAwareRoutingComplexTaskPrefersPaidOverLocalWhenBothAvailable()
    {
        var kernel = new AgentOrchestratorKernel();
        var ollamaAgent = OllamaDeveloperAgent();
        var openAiAgents = AgentCatalog.Default().Agents;
        var allAgents = openAiAgents.Concat(new[] { ollamaAgent }).ToList();

        var task = new TaskSpec(TaskId.New(),
            "Implement comprehensive authentication system with schema migration, distributed state management, and end-to-end integration tests for all new service endpoints",
            AgentRole.Developer);
        var goal = kernel.CreateGoal("Security hardening initiative", [task]);
        kernel.ActivateGoal(goal.Id, allAgents);
        var updatedGoal = kernel.GetGoal(goal.Id);

        var plan = SubscriptionPlanBuilder.Build(updatedGoal, allAgents, DefaultProfiles);
        var item = plan.Items.Single(i => i.Role == AgentRole.Developer);

        // Complex task should prefer the capable paid lane
        Assert.Equal(WorkerRouteDisposition.Selected, item.Route!.Disposition);
        Assert.False(string.Equals("Ollama", item.ProviderName, StringComparison.OrdinalIgnoreCase));
        Assert.True(
            item.Route.Reasons.Any(r =>
                r.Contains("complex", StringComparison.OrdinalIgnoreCase) &&
                (r.Contains("capable", StringComparison.OrdinalIgnoreCase) || r.Contains("complexity", StringComparison.OrdinalIgnoreCase))));
    }

    // Formats a DateTimeOffset as a 12-hour time string suitable for the usage-limit error message parser.
    // Produces "h:mm AM/PM" so TimeOnly.TryParse can parse it back.
    private static string BuildRetryTimeString(DateTimeOffset time)
    {
        var hour = time.Hour % 12;
        if (hour == 0) hour = 12;
        var ampm = time.Hour < 12 ? "AM" : "PM";
        return $"{hour}:{time.Minute:D2} {ampm}";
    }
}
