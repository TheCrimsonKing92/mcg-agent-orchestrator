using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

public sealed class WorkerDispatchTests
{
    [Xunit.Fact(DisplayName = "WorkerCommandTemplate_expands_profile_template_and_writes_prompt")]
    public void WorkerCommandTemplateExpandsProfileTemplateAndWritesPrompt()
{
    var root = CreateTempDirectory();
    var brief = new TaskBrief(
        new GoalId("goal123456789"),
        new TaskId("task123456789"),
        AgentRole.Developer,
        "Developer: implement",
        "brief content");
    var profile = new WorkerProfile("agent", "agent-cli --prompt {promptPath} --role {role}");

    var preparation = WorkerCommandTemplate.Prepare(brief, profile.Name, profile.CommandTemplate, root);

    Assert.True(File.Exists(preparation.PromptPath));
    Assert.Equal("brief content", File.ReadAllText(preparation.PromptPath));
    Assert.Equal("brief content".Length, preparation.PromptCharacterCount);
    Assert.Contains(preparation.Command, text => text.Contains("agent-cli --prompt", StringComparison.Ordinal));
    Assert.Contains(preparation.Command, text => text.Contains("--role Developer", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_uses_agent_subscription_profile_and_template_variables")]
    public void WorkerProfileDispatcherUsesAgentSubscriptionProfileAndTemplateVariables()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Dispatch configured subscription worker");
    var agent = new AgentDefinition(
        new AgentId("configured-developer"),
        "Configured Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("custom-codex", "gpt-5.3-codex", "medium"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("custom-codex", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --api-reasoning {apiReasoningEffort} --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})")
    ]);
    var expectedPromptCharacters = kernel.BuildTaskBrief(goal.Id, task.Id, "OpenAI/gpt-5.3-codex").Content.Length;
    var estimatedPromptCharacters = WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(kernel, goal, task, [agent]);

    var dispatchResult = WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        profiles,
        promptRoot,
        workingDirectory,
        dispatchedAt);

    Assert.Equal("custom-codex", task.LastDispatch!.WorkerName);
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("--model 'gpt-5.3-codex'", StringComparison.Ordinal));
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("model_reasoning_effort='medium'", StringComparison.Ordinal));
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("--api-reasoning 'high'", StringComparison.Ordinal));
    Assert.Contains(task.LastDispatch.Command, text => text.Contains($"--cd '{workingDirectory}'", StringComparison.Ordinal));
    Assert.Equal("OpenAI", task.LastDispatch.ProviderName);
    Assert.Equal("gpt-5.3-codex", task.LastDispatch.ModelName);
    Assert.Equal("medium", task.LastDispatch.ReasoningEffort);
    Assert.Equal(TaskComplexity.Simple, task.LastDispatch.TaskComplexity);
    Assert.Equal(expectedPromptCharacters, estimatedPromptCharacters);
    Assert.Equal(File.ReadAllText(dispatchResult.PromptPath).Length, task.LastDispatch.PromptCharacterCount);
    Assert.Equal(estimatedPromptCharacters, task.LastDispatch.PromptCharacterCount);
    Assert.Contains(File.ReadAllText(dispatchResult.PromptPath), text => text.Contains("Model fit: OpenAI/gpt-5.3-codex - adequate|overkill|underpowered - <task shape> - <short reason>", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "ProfileDispatchTask_enriches_matching_subscription_profile_metadata")]
    public void ProfileDispatchTaskEnrichesMatchingSubscriptionProfileMetadata()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Plan architecture work",
        [new TaskSpec(TaskId.New(), "Design and implement a production multi-tenant architecture.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini-codex", "low"),
        ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var profile = WorkerProfileCatalog.Default().GetRequired("codex-cli");

    var dispatch = GoalManagementCommandService.ProfileDispatchTask(
        kernel,
        workspace,
        goal,
        task,
        profile,
        [agent]);
    var risk = SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(goal, task);

    Assert.True(File.Exists(dispatch.PromptPath));
    Assert.Contains(task.LastDispatch!.Command, text => text.Contains("--model 'gpt-5.5'", StringComparison.Ordinal));
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("model_reasoning_effort='high'", StringComparison.Ordinal));
    Assert.Equal("OpenAI", task.LastDispatch.ProviderName);
    Assert.Equal("gpt-5.5", task.LastDispatch.ModelName);
    Assert.Equal("high", task.LastDispatch.ReasoningEffort);
    Assert.Equal(TaskComplexity.Complex, task.LastDispatch.TaskComplexity);
    Assert.True(task.LastDispatch.PromptCharacterCount > 0);
    Assert.Equal("complex paid subscription model", SubscriptionPromptCostGuard.BuildInlineLabel(risk!));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_rejects_already_running_subscription_dispatch")]
    public void WorkerProfileDispatcherRejectsAlreadyRunningSubscriptionDispatch()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Avoid duplicate subscription handoff");
    var agent = new AgentDefinition(
        new AgentId("configured-developer"),
        "Configured Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt);

    var ex = Assert.Throws<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt.AddMinutes(1)));

    Assert.Contains(ex.Message, text => text.Contains("status is Running", StringComparison.Ordinal));
    Assert.Equal(WorkTaskStatus.Running, task.Status);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_rejects_verified_task_dispatch")]
    public void WorkerProfileDispatcherRejectsVerifiedTaskDispatch()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Avoid repeated worker dispatch");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", root, 0, "ok", string.Empty, DateTimeOffset.UtcNow));
    var profile = new WorkerProfile("custom", "codex exec --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})");

    var ex = Assert.Throws<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareTask(
        kernel,
        goal,
        task,
        profile,
        Path.Combine(root, "prompts"),
        root,
        DateTimeOffset.UtcNow));

    Assert.Contains(ex.Message, text => text.Contains("already has passing verification", StringComparison.Ordinal));
    Assert.True(task.LastDispatch is null);
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_uses_complex_model_only_for_complex_subscription_tasks")]
    public void WorkerProfileDispatcherUsesComplexModelOnlyForComplexSubscriptionTasks()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var agent = new AgentDefinition(
        new AgentId("cost-aware-developer"),
        "Cost-aware Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini-codex", "low"),
        ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --api-model {apiModelName} --api-reasoning {apiReasoningEffort} --complexity {taskComplexity} --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})")
    ]);
    var simpleGoal = kernel.CreateGoal(
        "Fix a dashboard typo",
        [new TaskSpec(TaskId.New(), "Update the button label copy.", AgentRole.Developer)]);
    kernel.ActivateGoal(simpleGoal.Id, [agent]);
    var simpleTask = simpleGoal.Tasks.Single();
    var complexGoal = kernel.CreateGoal(
        "Design and implement a production multi-tenant architecture",
        [new TaskSpec(TaskId.New(), "Build an end-to-end distributed integration with horizontal scaling.", AgentRole.Developer)]);
    kernel.ActivateGoal(complexGoal.Id, [agent]);
    var complexTask = complexGoal.Tasks.Single();

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        simpleGoal,
        simpleTask,
        [agent],
        profiles,
        promptRoot,
        workingDirectory,
        dispatchedAt);
    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        complexGoal,
        complexTask,
        [agent],
        profiles,
        promptRoot,
        workingDirectory,
        dispatchedAt);

    Assert.Contains(simpleTask.LastDispatch!.Command, text => text.Contains("--model 'gpt-5-mini-codex'", StringComparison.Ordinal));
    Assert.Contains(simpleTask.LastDispatch.Command, text => text.Contains("model_reasoning_effort='low'", StringComparison.Ordinal));
    Assert.Contains(simpleTask.LastDispatch.Command, text => text.Contains("--api-model 'gpt-5-mini'", StringComparison.Ordinal));
    Assert.Contains(simpleTask.LastDispatch.Command, text => text.Contains("--api-reasoning 'low'", StringComparison.Ordinal));
    Assert.Contains(simpleTask.LastDispatch.Command, text => text.Contains("--complexity 'Simple'", StringComparison.Ordinal));
    Assert.Equal("OpenAI", simpleTask.LastDispatch.ProviderName);
    Assert.Equal("gpt-5-mini-codex", simpleTask.LastDispatch.ModelName);
    Assert.Equal("low", simpleTask.LastDispatch.ReasoningEffort);
    Assert.Equal(TaskComplexity.Simple, simpleTask.LastDispatch.TaskComplexity);
    Assert.Contains(complexTask.LastDispatch!.Command, text => text.Contains("--model 'gpt-5.5'", StringComparison.Ordinal));
    Assert.Contains(complexTask.LastDispatch.Command, text => text.Contains("model_reasoning_effort='high'", StringComparison.Ordinal));
    Assert.Contains(complexTask.LastDispatch.Command, text => text.Contains("--api-model 'gpt-5.5'", StringComparison.Ordinal));
    Assert.Contains(complexTask.LastDispatch.Command, text => text.Contains("--api-reasoning 'high'", StringComparison.Ordinal));
    Assert.Contains(complexTask.LastDispatch.Command, text => text.Contains("--complexity 'Complex'", StringComparison.Ordinal));
    Assert.Equal("OpenAI", complexTask.LastDispatch.ProviderName);
    Assert.Equal("gpt-5.5", complexTask.LastDispatch.ModelName);
    Assert.Equal("high", complexTask.LastDispatch.ReasoningEffort);
    Assert.Equal(TaskComplexity.Complex, complexTask.LastDispatch.TaskComplexity);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_keeps_test_only_surface_tasks_on_routine_subscription_model")]
    public void WorkerProfileDispatcherKeepsTestOnlySurfaceTasksOnRoutineSubscriptionModel()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Make the orchestrator less costly to run without sacrificing accuracy.",
        [new TaskSpec(
            TaskId.New(),
            "Add regression tests for provider smoke behavior across CLI, dashboard API, subscription worker state, and docs.",
            AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("cost-aware-developer"),
        "Cost-aware Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini-codex", "low"),
        ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --complexity {taskComplexity} --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})")
    ]);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        profiles,
        promptRoot,
        workingDirectory,
        dispatchedAt);

    Assert.Contains(task.LastDispatch!.Command, text => text.Contains("--model 'gpt-5-mini-codex'", StringComparison.Ordinal));
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("model_reasoning_effort='low'", StringComparison.Ordinal));
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("--complexity 'Simple'", StringComparison.Ordinal));
    Assert.Equal("gpt-5-mini-codex", task.LastDispatch.ModelName);
    Assert.Equal("low", task.LastDispatch.ReasoningEffort);
    Assert.Equal(TaskComplexity.Simple, task.LastDispatch.TaskComplexity);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_rejects_verified_subscription_dispatch")]
    public void WorkerProfileDispatcherRejectsVerifiedSubscriptionDispatch()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Avoid repeated subscription dispatch");
    var agent = new AgentDefinition(
        new AgentId("verified-developer"),
        "Verified Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", root, 0, "ok", string.Empty, DateTimeOffset.UtcNow));

    var ex = Assert.Throws<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        Path.Combine(root, "prompts"),
        root,
        DateTimeOffset.UtcNow));

    Assert.Contains(ex.Message, text => text.Contains("already has passing verification", StringComparison.Ordinal));
    Assert.True(task.LastDispatch is null);
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_keeps_subscription_alias_when_complex_model_is_absent")]
    public void WorkerProfileDispatcherKeepsSubscriptionAliasWhenComplexModelIsAbsent()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Design and implement a production multi-tenant architecture",
        [new TaskSpec(TaskId.New(), "Build an end-to-end distributed integration with horizontal scaling.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("aliased-developer"),
        "Aliased Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini-codex", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --api-model {apiModelName} --complexity {taskComplexity} --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})")
    ]);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        profiles,
        promptRoot,
        workingDirectory,
        dispatchedAt);

    Assert.Contains(task.LastDispatch!.Command, text => text.Contains("--model 'gpt-5-mini-codex'", StringComparison.Ordinal));
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("--api-model 'gpt-5-mini'", StringComparison.Ordinal));
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("--complexity 'Complex'", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_prepares_ready_tasks_and_returns_prompt_paths")]
    public void WorkerProfileDispatcherPreparesReadyTasksAndReturnsPromptPaths()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Dispatch local subscription workers");
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var profile = new WorkerProfile("codex-cli", "codex exec (Get-Content -Raw {promptPath})");

    var results = WorkerProfileDispatcher.PrepareReadyTasks(kernel, goal, profile, promptRoot, workingDirectory, dispatchedAt);

    Assert.Equal(1, results.Count);
    var result = results.Single();
    Assert.Equal(goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer).Id, result.Task.Id);
    Assert.True(File.Exists(result.PromptPath));
    var prompt = File.ReadAllText(result.PromptPath);
    Assert.Contains(prompt, text => text.Contains("Dispatch local subscription workers", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains(result.Task.VerificationPlan!, StringComparison.Ordinal));
    Assert.True(result.Task.LastDispatch is not null);
    Assert.Equal("codex-cli", result.Task.LastDispatch!.WorkerName);
    Assert.Equal(workingDirectory, result.Task.LastDispatch.WorkingDirectory);
    Assert.Equal(dispatchedAt, result.Task.LastDispatch.DispatchedAt);
    Assert.Contains(result.Task.LastDispatch.Command, text => text.Contains(result.PromptPath, StringComparison.Ordinal));
    Assert.Equal(WorkTaskStatus.Running, result.Task.Status);
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_prepares_subscription_tasks_by_assigned_provider")]
    public void WorkerProfileDispatcherPreparesSubscriptionTasksByAssignedProvider()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Dispatch subscription-backed workers");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);

    var results = WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(
        kernel,
        goal,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt);

    Assert.Equal(5, results.Count);
    var developer = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var researcher = goal.Tasks.First(task => task.RequiredRole == AgentRole.Researcher);
    Assert.Equal("codex-cli", developer.LastDispatch!.WorkerName);
    Assert.Contains(developer.LastDispatch.Command, text => text.Contains("codex exec", StringComparison.Ordinal));
    Assert.Contains(developer.LastDispatch.Command, text => text.Contains("--model 'gpt-5.3-codex'", StringComparison.Ordinal));
    Assert.Contains(developer.LastDispatch.Command, text => text.Contains("model_reasoning_effort='low'", StringComparison.Ordinal));
    Assert.Contains(developer.LastDispatch.Command, text => text.Contains("--sandbox workspace-write", StringComparison.Ordinal));
    Assert.Contains(developer.LastDispatch.Command, text => text.Contains($"--cd '{workingDirectory}'", StringComparison.Ordinal));
    Assert.False(developer.LastDispatch.Command.Contains("{workingDirectory}", StringComparison.Ordinal));
    Assert.Equal("codex-cli", researcher.LastDispatch!.WorkerName);
    Assert.Contains(researcher.LastDispatch.Command, text => text.Contains("codex exec", StringComparison.Ordinal));
    Assert.Contains(researcher.LastDispatch.Command, text => text.Contains("--model 'gpt-5.3-codex'", StringComparison.Ordinal));
    Assert.Contains(researcher.LastDispatch.Command, text => text.Contains("model_reasoning_effort='low'", StringComparison.Ordinal));
    Assert.True(File.Exists(results.Single(result => result.Task.Id == developer.Id).PromptPath));
    Assert.Equal(WorkTaskStatus.Running, developer.Status);
    Assert.Equal(workingDirectory, developer.LastDispatch.WorkingDirectory);
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_escalates_default_openai_agents_for_complex_subscription_tasks")]
    public void WorkerProfileDispatcherEscalatesDefaultOpenAiAgentsForComplexSubscriptionTasks()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Design and implement a production multi-tenant architecture",
        [new TaskSpec(TaskId.New(), "Build an end-to-end distributed integration with horizontal scaling.", AgentRole.Developer)]);
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var developer = goal.Tasks.Single();

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        developer,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt);

    Assert.Contains(developer.LastDispatch!.Command, text => text.Contains("--model 'gpt-5.5'", StringComparison.Ordinal));
    Assert.Contains(developer.LastDispatch.Command, text => text.Contains("model_reasoning_effort='high'", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_pins_anthropic_subscription_model")]
    public void WorkerProfileDispatcherPinsAnthropicSubscriptionModel()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Dispatch Anthropic subscription model",
        [new TaskSpec(TaskId.New(), "Review the implementation notes.", AgentRole.Reviewer)]);
    var agent = new AgentDefinition(
        new AgentId("anthropic-reviewer"),
        "Anthropic reviewer",
        AgentRole.Reviewer,
        new ModelProfile("Anthropic", "claude-sonnet-4-20250514", ModelCapability.Text, SubscriptionMode.ApiKey, MaxOutputTokens: AgentCatalog.RoutineApiMaxOutputTokens),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt);

    Assert.Equal("claude-cli", task.LastDispatch!.WorkerName);
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("claude --model 'claude-sonnet' -p", StringComparison.Ordinal));
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("Get-Content -Raw", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_rejects_subscription_profiles_without_reasoning_pinning")]
    public void WorkerProfileDispatcherRejectsSubscriptionProfilesWithoutReasoningPinning()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Reject unpinned subscription reasoning");
    var agent = new AgentDefinition(
        new AgentId("openai-reviewer"),
        "OpenAI reviewer",
        AgentRole.Reviewer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("custom-agent", "gpt-5.3-codex", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Reviewer);
    var profiles = new WorkerProfileCatalog([new WorkerProfile("custom-agent", "agent-cli --model {subscriptionModelName} {promptPath}")]);

    var ex = Assert.Throws<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        profiles,
        Path.Combine(root, "prompts"),
        root,
        DateTimeOffset.UtcNow));

    Assert.Contains(ex.Message, text => text.Contains("{subscriptionReasoningEffort}", StringComparison.Ordinal));
    Assert.True(task.LastDispatch is null);
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_falls_back_to_api_model_settings_for_default_openai_subscription_profile")]
    public void WorkerProfileDispatcherFallsBackToApiModelSettingsForDefaultOpenAiSubscriptionProfile()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Dispatch default OpenAI subscription profile");
    var agent = new AgentDefinition(
        new AgentId("openai-researcher"),
        "OpenAI researcher",
        AgentRole.Researcher,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Researcher);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt);

    Assert.Equal("codex-cli", task.LastDispatch!.WorkerName);
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("--model 'gpt-5.5'", StringComparison.Ordinal));
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("model_reasoning_effort='high'", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_rejects_echo_only_subscription_profiles")]
    public void WorkerProfileDispatcherRejectsEchoOnlySubscriptionProfiles()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Reject echo subscription profile");
    var agent = new AgentDefinition(
        new AgentId("openai-developer"),
        "OpenAI developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var profiles = WorkerProfileCatalog.Default().Upsert(new WorkerProfile("codex-cli", "Write-Output {promptPath}"));

    var ex = Assert.Throws<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        profiles,
        Path.Combine(root, "prompts"),
        root,
        DateTimeOffset.UtcNow));

    Assert.Contains(ex.Message, text => text.Contains("only echoes the prompt path", StringComparison.Ordinal));
    Assert.True(task.LastDispatch is null);
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_rejects_subscription_profiles_without_model_pinning")]
    public void WorkerProfileDispatcherRejectsSubscriptionProfilesWithoutModelPinning()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Reject unpinned subscription model");
    var agent = new AgentDefinition(
        new AgentId("openai-reviewer"),
        "OpenAI reviewer",
        AgentRole.Reviewer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("custom-agent", "gpt-5.3-codex"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Reviewer);
    var profiles = new WorkerProfileCatalog([new WorkerProfile("custom-agent", "agent-cli {promptPath}")]);

    var ex = Assert.Throws<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        profiles,
        Path.Combine(root, "prompts"),
        root,
        DateTimeOffset.UtcNow));

    Assert.Contains(ex.Message, text => text.Contains("{subscriptionModelName}", StringComparison.Ordinal));
    Assert.True(task.LastDispatch is null);
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_rejects_developer_subscription_profiles_that_cannot_patch")]
    public void WorkerProfileDispatcherRejectsDeveloperSubscriptionProfilesThatCannotPatch()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Reject non-patching subscription profile");
    var agent = new AgentDefinition(
        new AgentId("openai-developer"),
        "OpenAI developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var profiles = WorkerProfileCatalog.Default().Upsert(new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} {promptPath}"));

    var ex = Assert.Throws<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        profiles,
        Path.Combine(root, "prompts"),
        root,
        DateTimeOffset.UtcNow));

    Assert.Contains(ex.Message, text => text.Contains("not patch-capable", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("--sandbox workspace-write", StringComparison.Ordinal));
    Assert.True(task.LastDispatch is null);
}
    [Xunit.Fact(DisplayName = "SubscriptionPlan_marks_echo_only_profiles_not_preparable")]
    public void SubscriptionPlanMarksEchoOnlyProfilesNotPreparable()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Plan echo subscription profile");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var profiles = WorkerProfileCatalog.Default().Upsert(new WorkerProfile("codex-cli", "Write-Output {promptPath}"));

    var plan = DashboardResponseMapper.BuildSubscriptionPlan(goal, agents, profiles);

    var developer = plan.Items.First(item => item.Role == AgentRole.Developer);
    Assert.True(developer.ProfileExists);
    Assert.True(developer.ProfileIsEchoOnly);
    Assert.False(developer.ProfileIsPatchCapable);
    Assert.False(developer.CanPrepare);
    Assert.Contains(developer.Detail, text => text.Contains("only echoes the prompt path", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "SubscriptionPlan_marks_unpinned_model_profiles_not_preparable")]
    public void SubscriptionPlanMarksUnpinnedModelProfilesNotPreparable()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Plan unpinned subscription model");
    var agents =
        AgentCatalog.Default()
            .UpsertRole(new AgentDefinition(
                new AgentId("openai-reviewer"),
                "OpenAI reviewer",
                AgentRole.Reviewer,
                new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("custom-agent", "gpt-5.3-codex")))
            .Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var profiles = WorkerProfileCatalog.Default().Upsert(new WorkerProfile("custom-agent", "agent-cli {promptPath}"));

    var plan = DashboardResponseMapper.BuildSubscriptionPlan(goal, agents, profiles);

    var reviewer = plan.Items.First(item => item.Role == AgentRole.Reviewer);
    Assert.True(reviewer.ProfileExists);
    Assert.False(reviewer.CanPrepare);
    Assert.Contains(reviewer.Detail, text => text.Contains("{subscriptionModelName}", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "SubscriptionPlan_marks_unpinned_reasoning_profiles_not_preparable")]
    public void SubscriptionPlanMarksUnpinnedReasoningProfilesNotPreparable()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Plan unpinned subscription reasoning");
    var agents =
        AgentCatalog.Default()
            .UpsertRole(new AgentDefinition(
                new AgentId("openai-reviewer"),
                "OpenAI reviewer",
                AgentRole.Reviewer,
                new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("custom-agent", "gpt-5.3-codex", "low")))
            .Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var profiles = WorkerProfileCatalog.Default().Upsert(new WorkerProfile("custom-agent", "agent-cli --model {subscriptionModelName} {promptPath}"));

    var plan = DashboardResponseMapper.BuildSubscriptionPlan(goal, agents, profiles);

    var reviewer = plan.Items.First(item => item.Role == AgentRole.Reviewer);
    Assert.True(reviewer.ProfileExists);
    Assert.False(reviewer.CanPrepare);
    Assert.Contains(reviewer.Detail, text => text.Contains("{subscriptionReasoningEffort}", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "SubscriptionPlan_marks_non_patching_developer_profiles_not_preparable")]
    public void SubscriptionPlanMarksNonPatchingDeveloperProfilesNotPreparable()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Plan non-patching subscription profile");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var profiles = WorkerProfileCatalog.Default().Upsert(new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} {promptPath}"));

    var plan = DashboardResponseMapper.BuildSubscriptionPlan(goal, agents, profiles);

    var developer = plan.Items.First(item => item.Role == AgentRole.Developer);
    var planner = plan.Items.First(item => item.Role == AgentRole.Planner);
    Assert.True(developer.ProfileExists);
    Assert.False(developer.ProfileIsPatchCapable);
    Assert.False(developer.CanPrepare);
    Assert.Contains(developer.Detail, text => text.Contains("not patch-capable", StringComparison.Ordinal));
    Assert.True(planner.CanPrepare);
}
    [Xunit.Fact(DisplayName = "SubscriptionPlan_reports_effective_models_before_subscription_dispatch")]
    public void SubscriptionPlanReportsEffectiveModelsBeforeSubscriptionDispatch()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Maintain dashboard views",
        [
            new TaskSpec(TaskId.New(), "Update a tooltip label.", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.", AgentRole.Developer)
        ]);
    var agent = new AgentDefinition(
        new AgentId("cost-aware-developer"),
        "Cost-aware Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        ComplexModel: new ModelProfile("Anthropic", "claude-opus-4.1", ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
    kernel.ActivateGoal(goal.Id, [agent]);

    var plan = DashboardResponseMapper.BuildSubscriptionPlan(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        task => kernel.BuildTaskBrief(goal.Id, task.Id).Content.Length);

    var simple = plan.Items.First(item => item.Description.Contains("tooltip", StringComparison.Ordinal));
    var complex = plan.Items.First(item => item.Description.Contains("multi-tenant", StringComparison.Ordinal));
    var simpleTask = goal.Tasks.First(task => task.Description.Contains("tooltip", StringComparison.Ordinal));
    var complexTask = goal.Tasks.First(task => task.Description.Contains("multi-tenant", StringComparison.Ordinal));
    var simplePromptCharacters = kernel.BuildTaskBrief(goal.Id, simpleTask.Id).Content.Length;
    var complexPromptCharacters = kernel.BuildTaskBrief(goal.Id, complexTask.Id).Content.Length;
    Assert.Equal(TaskComplexity.Simple, simple.TaskComplexity);
    Assert.Equal("OpenAI", simple.ProviderName);
    Assert.Equal("gpt-5-mini", simple.ModelName);
    Assert.Equal("codex-cli", simple.ProfileName);
    Assert.Equal("gpt-5-mini", simple.SubscriptionModelName);
    Assert.Equal("low", simple.SubscriptionReasoningEffort);
    Assert.Equal(simplePromptCharacters, simple.EstimatedPromptCharacterCount);
    Assert.Equal(TaskComplexity.Complex, complex.TaskComplexity);
    Assert.Equal("Anthropic", complex.ProviderName);
    Assert.Equal("claude-opus-4.1", complex.ModelName);
    Assert.Equal("claude-cli", complex.ProfileName);
    Assert.Equal("claude-opus-4.1", complex.SubscriptionModelName);
    Assert.Equal("high", complex.SubscriptionReasoningEffort);
    Assert.Equal(complexPromptCharacters, complex.EstimatedPromptCharacterCount);
    Assert.Equal(2, plan.ReadyModelUsage.Count);
    var simpleSummary = plan.ReadyModelUsage.Single(item => item.TaskComplexity == TaskComplexity.Simple);
    Assert.Equal("OpenAI", simpleSummary.ProviderName);
    Assert.Equal("gpt-5-mini", simpleSummary.ModelName);
    Assert.Equal("low", simpleSummary.ReasoningEffort);
    Assert.Equal(1, simpleSummary.ReadyCount);
    Assert.Equal(simplePromptCharacters, simpleSummary.EstimatedPromptCharacterCount);
    Assert.True(simpleSummary.IsPotentiallyPaidProvider);
    var complexSummary = plan.ReadyModelUsage.Single(item => item.TaskComplexity == TaskComplexity.Complex);
    Assert.Equal("Anthropic", complexSummary.ProviderName);
    Assert.Equal("claude-opus-4.1", complexSummary.ModelName);
    Assert.Equal("high", complexSummary.ReasoningEffort);
    Assert.Equal(1, complexSummary.ReadyCount);
    Assert.Equal(complexPromptCharacters, complexSummary.EstimatedPromptCharacterCount);
    Assert.True(complexSummary.IsPotentiallyPaidProvider);
    Assert.Equal("complex paid subscription model", plan.ReadyStartCostRisk);
    Assert.True(plan.ReadyStartCostRecommendation?.Contains("Confirm this task needs the complex paid subscription model", StringComparison.Ordinal) == true);
    Assert.Equal(simplePromptCharacters + complexPromptCharacters, plan.ReadyStartPromptCharacterCount);
    Assert.True(plan.ReadyStartCostRiskDetails.Any(detail => detail.Contains("uses complex paid model selection", StringComparison.Ordinal)));
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_surfaces_prior_model_fit_for_ready_models")]
    public void SubscriptionPlanSurfacesPriorModelFitForReadyModels()
{
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Update the old button label.", AgentRole.Developer);
    var nextTask = new TaskSpec(TaskId.New(), "Update the next button label.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Tune model choice from fit evidence", [priorTask, nextTask]);
    var agent = new AgentDefinition(
        new AgentId("cost-aware-developer"),
        "Cost-aware Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "manual-verification passed",
        "C:\\repo",
        0,
        "Evidence checked.\nModel fit: OpenAI/gpt-5-mini - overkill - copy-only change.",
        string.Empty,
        DateTimeOffset.UtcNow));

    var plan = DashboardResponseMapper.BuildSubscriptionPlan(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        task => kernel.BuildTaskBrief(goal.Id, task.Id).Content.Length);

    var summary = plan.ReadyModelUsage.Single();
    Assert.Equal("OpenAI", summary.ProviderName);
    Assert.Equal("gpt-5-mini", summary.ModelName);
    Assert.Equal(1, summary.ReadyCount);
    Assert.Equal(1, summary.PreviousModelFitNoteCount);
    Assert.Equal(0, summary.PreviousAdequateCount);
    Assert.Equal(1, summary.PreviousOverkillCount);
    Assert.Equal(0, summary.PreviousUnderpoweredCount);
    Assert.Equal(0, summary.PreviousUnknownFitCount);
    Assert.True(summary.PreviousTaskShapes?.Contains("copy-only change") == true);
    Assert.True(summary.ModelFitRecommendation?.Contains("try local Ollama/qwen3:8b", StringComparison.Ordinal) == true);
    Assert.Equal("prior overkill model", plan.ReadyStartCostRisk);
    Assert.True(plan.ReadyStartCostRiskDetails.Any(detail => detail.Contains("prior overkill model-fit note", StringComparison.Ordinal)));
    Assert.True(plan.ReadyStartCostRiskDetails.Any(detail => detail.Contains("shapes copy-only change", StringComparison.Ordinal)));
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_retains_earlier_model_fit_attempts_for_ready_models")]
    public void SubscriptionPlanRetainsEarlierModelFitAttemptsForReadyModels()
{
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Update the old button label.", AgentRole.Developer);
    var nextTask = new TaskSpec(TaskId.New(), "Update the next button label.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Tune model choice from all fit evidence", [priorTask, nextTask]);
    var agent = new AgentDefinition(
        new AgentId("cost-aware-developer"),
        "Cost-aware Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
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
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "manual-verification passed",
        "C:\\repo",
        0,
        "Evidence checked.\nModel fit: OpenAI/gpt-5.4-mini - adequate - focused parser fix.",
        string.Empty,
        DateTimeOffset.UtcNow.AddMinutes(1)));

    var plan = DashboardResponseMapper.BuildSubscriptionPlan(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        task => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(kernel, goal, task, [agent]));

    var summary = plan.ReadyModelUsage.Single();
    Assert.Equal("OpenAI", summary.ProviderName);
    Assert.Equal("gpt-5-mini", summary.ModelName);
    Assert.Equal(1, summary.PreviousModelFitNoteCount);
    Assert.Equal(1, summary.PreviousOverkillCount);
    Assert.True(summary.PreviousTaskShapes?.Contains("label-only change") == true);
    Assert.True(summary.ModelFitRecommendation?.Contains("try local Ollama/qwen3:8b", StringComparison.Ordinal) == true);
    Assert.Equal("prior overkill model", plan.ReadyStartCostRisk);
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_flags_prior_underpowered_model_fit_for_ready_models")]
    public void SubscriptionPlanFlagsPriorUnderpoweredModelFitForReadyModels()
{
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Fix failed parser behavior.", AgentRole.Developer);
    var nextTask = new TaskSpec(TaskId.New(), "Fix another parser behavior.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Avoid repeating underpowered model choice", [priorTask, nextTask]);
    var agent = new AgentDefinition(
        new AgentId("cost-aware-developer"),
        "Cost-aware Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "manual-verification failed",
        "C:\\repo",
        1,
        "Evidence checked.\nModel fit: OpenAI/gpt-5-mini - underpowered - missed regression path.",
        string.Empty,
        DateTimeOffset.UtcNow));

    var plan = DashboardResponseMapper.BuildSubscriptionPlan(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        task => kernel.BuildTaskBrief(goal.Id, task.Id).Content.Length);

    var summary = plan.ReadyModelUsage.Single();
    Assert.Equal(1, summary.PreviousUnderpoweredCount);
    Assert.True(summary.PreviousTaskShapes?.Contains("missed regression path") == true);
    Assert.True(summary.ModelFitRecommendation?.Contains("choose a stronger model", StringComparison.Ordinal) == true);
    Assert.Equal("prior underpowered model", plan.ReadyStartCostRisk);
    Assert.True(plan.ReadyStartCostRiskDetails.Any(detail => detail.Contains("prior underpowered model-fit note", StringComparison.Ordinal)));
    Assert.True(plan.ReadyStartCostRiskDetails.Any(detail => detail.Contains("shapes missed regression path", StringComparison.Ordinal)));
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_escalates_underpowered_routine_model_to_complex_model")]
    public void SubscriptionPlanEscalatesUnderpoweredRoutineModelToComplexModel()
{
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Fix failed parser behavior.", AgentRole.Developer);
    var nextTask = new TaskSpec(TaskId.New(), "Fix another parser behavior.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Avoid repeating underpowered model choice", [priorTask, nextTask]);
    var agent = new AgentDefinition(
        new AgentId("cost-aware-developer"),
        "Cost-aware Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini", "low"),
        ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
    kernel.ActivateGoal(goal.Id, [agent]);
    kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "manual-verification failed",
        "C:\\repo",
        1,
        "Evidence checked.\nModel fit: OpenAI/gpt-5-mini - underpowered - missed regression path.",
        string.Empty,
        DateTimeOffset.UtcNow));

    var plan = DashboardResponseMapper.BuildSubscriptionPlan(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        task => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(kernel, goal, task, [agent]));
    var thresholdRisk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        _ => 5000);
    var dispatchRoot = CreateTempDirectory();

    var item = plan.Items.Single(candidate => candidate.TaskId == nextTask.Id.Value);
    var summary = plan.ReadyModelUsage.Single();
    Assert.Equal(TaskComplexity.Simple, item.TaskComplexity);
    Assert.True(item.UsesComplexModel);
    Assert.Equal("OpenAI", item.ProviderName);
    Assert.Equal("gpt-5.5", item.ModelName);
    Assert.Equal("gpt-5.5", item.SubscriptionModelName);
    Assert.Equal("high", item.SubscriptionReasoningEffort);
    Assert.True(summary.UsesComplexModel);
    Assert.Equal("gpt-5.5", summary.ModelName);
    Assert.Equal("complex paid subscription model", plan.ReadyStartCostRisk);
    Assert.True(plan.ReadyStartCostRiskDetails.Any(detail => detail.Contains("uses complex paid model selection", StringComparison.Ordinal)));
    Assert.True(thresholdRisk is not null);
    Assert.False(thresholdRisk!.HasOversizedPrompt);
    Assert.True(thresholdRisk.UsesComplexPaidModel);
    Assert.Equal("complex paid subscription model", SubscriptionPromptCostGuard.BuildInlineLabel(thresholdRisk));
    Assert.False(thresholdRisk.Details.Any(detail => detail.Contains("exceeds 4000", StringComparison.Ordinal)));

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        nextTask,
        [agent],
        WorkerProfileCatalog.Default(),
        Path.Combine(dispatchRoot, "prompts"),
        dispatchRoot,
        DateTimeOffset.UtcNow);
    var preparedRisk = SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(goal, nextTask);

    Assert.True(nextTask.LastDispatch!.UsesComplexModel);
    Assert.Equal(TaskComplexity.Simple, nextTask.LastDispatch.TaskComplexity);
    Assert.Equal("gpt-5.5", nextTask.LastDispatch.ModelName);
    Assert.Equal("complex paid subscription model", SubscriptionPromptCostGuard.BuildInlineLabel(preparedRisk!));
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_marks_usage_limited_tasks_not_preparable_until_retry_time")]
    public void SubscriptionPlanMarksUsageLimitedTasksNotPreparableUntilRetryTime()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Plan retry-later subscription task");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var developer = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var now = DateTimeOffset.UtcNow;
    var retryTime = now.AddHours(1);
    kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord("codex-cli", "codex exec", "C:\\repo", now));
    kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord(
        "codex exec",
        "C:\\repo",
        1,
        string.Empty,
        $"ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at {retryTime:h:mm tt}.",
        now));
    kernel.RetryTask(goal.Id, developer.Id, "Retry after provider window.");
    kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord("codex-cli", "codex exec retry", "C:\\repo", now.AddMinutes(5)));
    kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord(
        "codex exec retry",
        "C:\\repo",
        1,
        string.Empty,
        $"ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at {retryTime:h:mm tt}.",
        now.AddMinutes(5)));

    var plan = DashboardResponseMapper.BuildSubscriptionPlan(goal, agents, WorkerProfileCatalog.Default());

    var item = plan.Items.First(item => item.Role == AgentRole.Developer);
    Assert.Equal(1, plan.RetryDeferredCount);
    Assert.Equal(developer.SubscriptionRetryAfter, plan.NextSubscriptionRetryAfter);
    Assert.False(item.CanPrepare);
    Assert.Equal(developer.SubscriptionRetryAfter, item.RetryAfter);
    Assert.True(item.RetryDelaySeconds is > 0);
    Assert.Equal(2, item.RecoverableSubscriptionLimitFailureCount);
    Assert.Contains(item.Detail, text => text.Contains("Recoverable subscription usage limit", StringComparison.Ordinal));
    Assert.Contains(item.Detail, text => text.Contains("2 previous recoverable subscription usage limit failures", StringComparison.Ordinal));
    Assert.Contains(item.Detail, text => text.Contains("retry after", StringComparison.Ordinal));
    Assert.Equal(developer.SubscriptionRetryAfter, DashboardResponseMapper.ToTaskSummaryDto(goal, developer).SubscriptionRetryAfter);
}
    [Xunit.Fact(DisplayName = "SubscriptionPromptCostGuard_blocks_large_paid_ready_subscription_start_before_dispatch")]
    public void SubscriptionPromptCostGuardBlocksLargePaidReadySubscriptionStartBeforeDispatch()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Plan expensive subscription start",
        [new TaskSpec(TaskId.New(), "Do paid subscription work.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-codex"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();

    var risk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        _ => 12001);
    var ex = Assert.Throws<InvalidOperationException>(() => SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(risk, confirmed: false));

    Assert.True(risk is not null);
    Assert.Equal(12001, risk!.PromptCharacterCount);
    Assert.True(risk.PromptExceedsBatchThreshold);
    Assert.True(risk.HasOversizedPrompt);
    Assert.False(risk.TaskCountExceedsThreshold);
    Assert.False(risk.UsesComplexPaidModel);
    Assert.Equal("large paid subscription start", SubscriptionPromptCostGuard.BuildInlineLabel(risk));
    Assert.Contains(ex.Message, text => text.Contains("--confirm-large-paid-subscription-start", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("Paid subscription start requires explicit confirmation", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("Inspect the generated prompt before paid subscription start", StringComparison.Ordinal));
    Assert.True(task.LastDispatch is null);
}
    [Xunit.Fact(DisplayName = "SubscriptionPromptCostGuard_blocks_paid_batch_fanout_with_small_prompts")]
    public void SubscriptionPromptCostGuardBlocksPaidBatchFanoutWithSmallPrompts()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Plan paid batch subscription start");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);

    var risk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
        goal,
        agents,
        WorkerProfileCatalog.Default(),
        _ => 500);
    var ex = Assert.Throws<InvalidOperationException>(() => SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(risk, confirmed: false));

    Assert.True(risk is not null);
    Assert.Equal(5, risk!.TaskCount);
    Assert.Equal(2500, risk.PromptCharacterCount);
    Assert.False(risk.PromptExceedsBatchThreshold);
    Assert.False(risk.HasOversizedPrompt);
    Assert.True(risk.TaskCountExceedsThreshold);
    Assert.False(risk.UsesComplexPaidModel);
    Assert.Equal("paid subscription fanout", SubscriptionPromptCostGuard.BuildInlineLabel(risk));
    Assert.True(risk.Details.Any(detail => detail.Contains("Paid task count 5 exceeds 3", StringComparison.Ordinal)));
    Assert.Contains(ex.Message, text => text.Contains("thresholds 12000 chars or 3 task(s)", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "SubscriptionPromptCostGuard_blocks_complex_paid_start_with_small_prompt")]
    public void SubscriptionPromptCostGuardBlocksComplexPaidStartWithSmallPrompt()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Plan complex paid subscription start",
        [new TaskSpec(TaskId.New(), "Design and implement a production multi-tenant architecture with distributed rollback and data integrity checks.", AgentRole.Developer)]);
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);

    var risk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
        goal,
        agents,
        WorkerProfileCatalog.Default(),
        _ => 500);

    Assert.True(risk is not null);
    Assert.Equal(1, risk!.TaskCount);
    Assert.Equal(500, risk.PromptCharacterCount);
    Assert.False(risk.PromptExceedsBatchThreshold);
    Assert.False(risk.HasOversizedPrompt);
    Assert.False(risk.TaskCountExceedsThreshold);
    Assert.True(risk.UsesComplexPaidModel);
    Assert.Equal("complex paid subscription model", SubscriptionPromptCostGuard.BuildInlineLabel(risk));
    Assert.True(risk.Details.Any(detail => detail.Contains("uses complex paid model selection", StringComparison.Ordinal)));
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_skips_usage_limited_tasks_before_retry_time")]
    public void WorkerProfileDispatcherSkipsUsageLimitedTasksBeforeRetryTime()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var failureAt = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Skip retry-later subscription dispatch");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var developer = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord("codex-cli", "codex exec", workingDirectory, failureAt));
    kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord(
        "codex exec",
        workingDirectory,
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 4:58 PM.",
        failureAt));

    var results = WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(
        kernel,
        goal,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        failureAt.AddMinutes(30));

    Assert.False(results.Any(result => result.Task.Id == developer.Id));
    Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
    Assert.True(developer.LastVerification is null);

    var ex = Assert.Throws<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        developer,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        failureAt.AddMinutes(30)));
    Assert.Contains(ex.Message, text => text.Contains("retry after", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_requires_review_after_repeated_usage_limits")]
    public void WorkerProfileDispatcherRequiresReviewAfterRepeatedUsageLimits()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var firstFailureAt = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
    var secondFailureAt = DateTimeOffset.Parse("2026-06-01T13:00:00Z");
    var retryWindowPassed = DateTimeOffset.Parse("2026-06-01T18:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Review repeated subscription usage limits");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var developer = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord("codex-cli", "codex exec attempt 1", workingDirectory, firstFailureAt));
    kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord(
        "codex exec attempt 1",
        workingDirectory,
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 4:58 PM.",
        firstFailureAt));
    kernel.RetryTask(goal.Id, developer.Id, "Retry after first usage limit.");
    kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord("codex-cli", "codex exec attempt 2", workingDirectory, secondFailureAt));
    kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord(
        "codex exec attempt 2",
        workingDirectory,
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 4:58 PM.",
        secondFailureAt));

    var plan = DashboardResponseMapper.BuildSubscriptionPlan(goal, agents, WorkerProfileCatalog.Default());
    var item = plan.Items.First(item => item.Role == AgentRole.Developer);
    var results = WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(
        kernel,
        goal,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        retryWindowPassed);

    Assert.Equal(2, item.RecoverableSubscriptionLimitFailureCount);
    Assert.False(item.CanPrepare);
    Assert.Contains(item.Detail, text => text.Contains("Repeated recoverable subscription usage limit", StringComparison.Ordinal));
    Assert.Contains(item.Detail, text => text.Contains("inspect model, profile, or timing", StringComparison.Ordinal));
    Assert.False(results.Any(result => result.Task.Id == developer.Id));

    var ex = Assert.Throws<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        developer,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        retryWindowPassed));
    Assert.Contains(ex.Message, text => text.Contains("recoverable subscription usage limit 2 time", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("inspect model, profile, or timing", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_rejects_duplicate_process_start")]
    public void BackgroundDispatchRunnerRejectsDuplicateProcessStart()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Avoid duplicate process start");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "Write-Output ok", root, DateTimeOffset.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(1234, "Write-Output ok", root, "out.log", "err.log", "exit.txt", DateTimeOffset.UtcNow, null, null));

    var ex = Assert.Throws<InvalidOperationException>(() => new BackgroundDispatchRunner().StartLatestDispatch(kernel, goal.Id, task.Id, Path.Combine(root, "logs")));

    Assert.Contains(ex.Message, text => text.Contains("already has a dispatch process record", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "LocalDispatchRunner_rejects_inactive_dispatch_execution")]
    public async Task LocalDispatchRunnerRejectsInactiveDispatchExecution()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Avoid duplicate local dispatch execution");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "Write-Output should-not-run", root, DateTimeOffset.UtcNow));
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Already handled.");

    var ex = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(async () => await new LocalDispatchRunner().ExecuteLatestDispatchAsync(kernel, goal.Id, task.Id));

    Assert.Contains(ex.Message, text => text.Contains("status is Completed", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_rejects_subscription_dispatch_without_supported_assignment")]
    public void WorkerProfileDispatcherRejectsSubscriptionDispatchWithoutSupportedAssignment()
{
    var kernel = new AgentOrchestratorKernel();
    var unassignedGoal = kernel.CreateGoal("Reject unassigned", [new TaskSpec(TaskId.New(), "Unassigned", AgentRole.Developer)]);
    Assert.Throws<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        unassignedGoal,
        unassignedGoal.Tasks.Single(),
        AgentCatalog.Default().Agents,
        WorkerProfileCatalog.Default(),
        CreateTempDirectory(),
        Environment.CurrentDirectory,
        DateTimeOffset.UtcNow));

    var unsupported = new AgentDefinition(
        AgentId.New(),
        "Local",
        AgentRole.Developer,
        new ModelProfile("Local", "local", ModelCapability.Text, SubscriptionMode.LocalBridge));
    var unsupportedGoal = kernel.CreateGoal("Reject unsupported", [new TaskSpec(TaskId.New(), "Unsupported", AgentRole.Developer)]);
    kernel.ActivateGoal(unsupportedGoal.Id, [unsupported]);
    Assert.Throws<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        unsupportedGoal,
        unsupportedGoal.Tasks.Single(),
        [unsupported],
        WorkerProfileCatalog.Default(),
        CreateTempDirectory(),
        Environment.CurrentDirectory,
        DateTimeOffset.UtcNow));
}
}

