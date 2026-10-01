using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each fixture uses its own in-memory kernel and starts no processes.
public sealed class NextActionControlsTests
{
    [Xunit.Fact(DisplayName = "NextActionControls_builds_direct_controls_for_safe_actions")]
    public void NextActionControlsBuildsDirectControlsForSafeActions()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Map direct next actions");
        var task = goal.Tasks[2];
        var goalPrefix = goal.Id.Value[..8];

        AssertControl(
            goal,
            new NextActionItem(NextActionKind.RunAssignedTask, task.Id, null, "Run it"),
            "Run task",
            "POST",
            $"/api/goals/{goalPrefix}/tasks/3/run?confirmTaskRun=true");
        AssertControl(
            goal,
            new NextActionItem(NextActionKind.RunAssignedTask, task.Id, null, "Run it"),
            "Prepare subscription handoff",
            "POST",
            $"/api/goals/{goalPrefix}/tasks/3/run?confirmTaskRun=true",
            [Validation(task.RequiredRole, AgentExecutionPolicy.PreferSubscription)],
            "paid subscription handoff");
        AssertControl(
            goal,
            new NextActionItem(NextActionKind.RunAssignedTask, task.Id, null, "Run it"),
            "Prepare subscription handoff",
            "POST",
            $"/api/goals/{goalPrefix}/tasks/3/run?confirmTaskRun=true",
            [Validation(task.RequiredRole, AgentExecutionPolicy.AnyAvailable)],
            "paid subscription handoff");
        AssertControl(
            goal,
            new NextActionItem(NextActionKind.RunAssignedTask, task.Id, null, "Run it"),
            "Run paid API task",
            "POST",
            $"/api/goals/{goalPrefix}/tasks/3/run?confirmTaskRun=true&confirmPaidApiRun=true",
            [Validation(task.RequiredRole, AgentExecutionPolicy.ApiOnly)],
            "paid API");
        AssertControl(
            goal,
            new NextActionItem(NextActionKind.RefreshRunningProcess, task.Id, null, "Refresh it"),
            "Refresh process",
            "POST",
            $"/api/goals/{goalPrefix}/tasks/3/refresh");
        AssertControl(
            goal,
            new NextActionItem(NextActionKind.ExecuteRecordedDispatch, task.Id, null, "Start it"),
            "Start prepared work",
            "POST",
            $"/api/goals/{goalPrefix}/tasks/3/start?confirmDispatchStart=true");
        AssertControl(
            goal,
            new NextActionItem(NextActionKind.DelegatePendingTask, null, null, "Delegate"),
            "Assign tasks",
            "POST",
            $"/api/goals/{goalPrefix}/delegate");
        AssertControl(
            goal,
            new NextActionItem(NextActionKind.InspectFailedTask, task.Id, null, "Inspect"),
            "Inspect task",
            "GET",
            $"/api/task/3?goal={goalPrefix}");
        AssertControl(
            goal,
            new NextActionItem(NextActionKind.FixFailedVerification, task.Id, null, "Fix verification"),
            "Verification records",
            "GET",
            $"/api/goals/{goalPrefix}/tasks/3/verifications");
        AssertControl(
            goal,
            new NextActionItem(NextActionKind.MonitorGoal, null, null, "Monitor"),
            "Monitor goal",
            "GET",
            $"/api/monitor?goal={goalPrefix}");

        Assert.Equal(null, NextActionControls.Build(goal, new NextActionItem(NextActionKind.VerifyCompletedTask, task.Id, null, "Verify"), WorkerProfileCatalog.Default()));
    }

    [Xunit.Fact(DisplayName = "NextActionControls_surface_prior_subscription_model_fit_before_handoff")]
    public void NextActionControlsSurfacePriorSubscriptionModelFitBeforeHandoff()
    {
        var kernel = new AgentOrchestratorKernel();
        var priorTask = new TaskSpec(TaskId.New(), "Update the old label.", AgentRole.Developer);
        var nextTask = new TaskSpec(TaskId.New(), "Update the next label.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Avoid repeating overkill paid subscription handoff", [priorTask, nextTask]);
        var agent = new AgentDefinition(
            new AgentId("cost-aware-developer"),
            "Cost-aware Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini", "low"));
        kernel.ActivateGoal(goal.Id, [agent]);
        kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
            "manual-verification passed",
            "C:\\repo",
            0,
            "Evidence checked.\nModel fit: OpenAI/gpt-5-mini - overkill - label-only change.",
            string.Empty,
            DateTimeOffset.UtcNow));
        var action = kernel.BuildNextActions(goal.Id).Items.Single();

        var control = NextActionControls.Build(goal, action, WorkerProfileCatalog.Default(), agentDefinitions: [agent]);

        Assert.Equal(NextActionKind.RunAssignedTask, action.Kind);
        Assert.Equal(nextTask.Id.Value, action.TaskId!.Value);
        Assert.Equal("prior overkill subscription model", control!.CostRisk);
        Assert.True(control.CostRecommendation?.Contains("try local Ollama/qwen3:8b", StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "NextActionControls_allow_complex_paid_prepared_dispatch_under_size_threshold")]
    public void NextActionControlsAllowComplexPaidPreparedDispatchUnderSizeThreshold()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Start complex prepared dispatch",
            [new TaskSpec(TaskId.New(), "Run prepared complex paid work", AgentRole.Developer)]);
        var agent = Agent(AgentRole.Developer, AgentExecutionPolicy.PreferSubscription);
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli",
            "codex exec prompt.md",
            "C:\\repo",
            DateTimeOffset.UtcNow,
            "OpenAI",
            AgentCatalog.OpenAiSubscriptionModelAlias,
            "high",
            TaskComplexity.Complex,
            500));
        var action = kernel.BuildNextActions(goal.Id).Items.Single();
        var goalPrefix = goal.Id.Value[..8];

        var control = NextActionControls.Build(goal, action, WorkerProfileCatalog.Default());

        Assert.Equal(NextActionKind.ExecuteRecordedDispatch, action.Kind);
        Assert.Equal($"/api/goals/{goalPrefix}/tasks/1/start?confirmDispatchStart=true", control!.Url);
        Xunit.Assert.Null(control.CostRisk);
        Xunit.Assert.Null(control.CostRecommendation);
    }

    [Fact]
    public void PaidSubscriptionHandoffPreservesControlAndCostValues()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Map direct next actions");
        var task = goal.Tasks[2];
        var validation = new AgentConfigurationValidation(
            task.RequiredRole, task.RequiredRole.ToString(), "OpenAI", "test", "medium",
            AgentCatalog.RoutineApiMaxOutputTokens, AgentExecutionPolicy.PreferSubscription,
            null, null, null, false, "none", true, "valid");

        var control = NextActionControls.Build(goal,
            new NextActionItem(NextActionKind.RunAssignedTask, task.Id, null, "Run it"),
            WorkerProfileCatalog.Default(), [validation]);

        Assert.Equal(new NextActionControl(
            "Prepare subscription handoff", "POST",
            $"/api/goals/{goal.Id.Value[..8]}/tasks/3/run?confirmTaskRun=true",
            "paid subscription handoff",
            $"Review subscription-plan first; {CostRecommendationText.LocalModelSwitchAction} before paid subscription handoff when the task is routine."), control);
    }

    [Fact]
    public void PreparedPaidDispatchPreservesControlAndCostValues()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Start costly prepared dispatch",
            [new TaskSpec(TaskId.New(), "Run prepared paid work", AgentRole.Developer)]);
        var agent = new AgentDefinition(AgentId.New(), AgentRole.Developer.ToString(), AgentRole.Developer,
            new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey, ReasoningEffort: "medium"),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription);
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec prompt.md", "C:\\repo", DateTimeOffset.UtcNow,
            "OpenAI", AgentCatalog.StaleOpenAiCodexSubscriptionModelAlias, "medium", TaskComplexity.Complex, 20000));
        var action = kernel.BuildNextActions(goal.Id).Items.Single();

        Assert.Equal(NextActionKind.ExecuteRecordedDispatch, action.Kind);
        var control = NextActionControls.Build(goal, action, WorkerProfileCatalog.Default());

        Assert.Equal(new NextActionControl(
            "Start prepared work", "POST",
            $"/api/goals/{goal.Id.Value[..8]}/tasks/1/start?confirmDispatchStart=true",
            "large paid subscription start",
            "Inspect the generated prompt before paid subscription start; it exceeds the paid prompt threshold."), control);
    }

    [Fact]
    public void InspectPreservesControlAndHasNoCostRecommendation()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Map direct next actions");

        var control = NextActionControls.Build(goal,
            new NextActionItem(NextActionKind.InspectFailedTask, goal.Tasks[2].Id, null, "Inspect"),
            WorkerProfileCatalog.Default());

        Assert.Equal(new NextActionControl("Inspect task", "GET",
            $"/api/task/3?goal={goal.Id.Value[..8]}", null, null), control);
    }

    static void AssertControl(
        Goal goal,
        NextActionItem item,
        string label,
        string method,
        string url,
        IReadOnlyList<AgentConfigurationValidation>? agents = null,
        string? costRisk = null)
    {
        var control = NextActionControls.Build(goal, item, WorkerProfileCatalog.Default(), agents);

        Assert.True(control is not null);
        Assert.Equal(label, control!.Label);
        Assert.Equal(method, control.Method);
        Assert.Equal(url, control.Url);
        Assert.Equal(costRisk, control.CostRisk);
    }

    static AgentDefinition Agent(AgentRole role, AgentExecutionPolicy policy)
    {
        return new AgentDefinition(
            AgentId.New(),
            role.ToString(),
            role,
            new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey, ReasoningEffort: "medium"),
            ExecutionPolicy: policy);
    }

    static AgentConfigurationValidation Validation(AgentRole role, AgentExecutionPolicy policy)
    {
        return new AgentConfigurationValidation(
            role,
            role.ToString(),
            "OpenAI",
            "test",
            "medium",
            AgentCatalog.RoutineApiMaxOutputTokens,
            policy,
              null,
              null,
              null,
              false,
              "none",
              true,
              "valid");
    }
}
