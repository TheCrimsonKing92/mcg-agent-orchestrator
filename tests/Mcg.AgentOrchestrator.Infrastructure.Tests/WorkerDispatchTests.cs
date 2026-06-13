using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
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

    [Xunit.Fact(DisplayName = "WorkerCommandTemplate_rejects_unresolved_variables_before_worker_dispatch_mutation")]
    public void WorkerCommandTemplateRejectsUnresolvedVariablesBeforeWorkerDispatchMutation()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Do not record unresolved worker dispatches");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var previousDispatch = ReopenTaskWithRecoverableDispatchLimit(kernel, goal, task);
    var brief = kernel.BuildTaskBrief(goal.Id, task.Id, workingDirectory: workingDirectory);

    var ex = Assert.Throws<InvalidOperationException>(() => WorkerCommandTemplate.Prepare(
        brief,
        "manual-codex",
        "codex exec --model {subscriptionModelName} --cd {workingDirectory} (Get-Content -Raw {promptPath})",
        promptRoot,
        WorkerProfileDispatcher.BuildDispatchVariables(task.RequiredRole, workingDirectory, null)));

    Assert.Contains(ex.Message, text => text.Contains("{subscriptionModelName}", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("subscription-dispatch", StringComparison.Ordinal));
    Assert.False(Directory.Exists(promptRoot));
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.Equal(previousDispatch, task.LastDispatch);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_rejects_unresolved_profile_variables_before_state_mutation")]
    public void WorkerProfileDispatcherRejectsUnresolvedProfileVariablesBeforeStateMutation()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Reject cross-profile placeholder mismatch");
    var agent = new AgentDefinition(
        new AgentId("anthropic-developer"),
        "Anthropic Developer",
        AgentRole.Developer,
        new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var previousDispatch = ReopenTaskWithRecoverableDispatchLimit(kernel, goal, task);
    var profile = WorkerProfileCatalog.Default().GetRequired("codex-cli");

    var ex = Assert.Throws<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareTask(
        kernel,
        goal,
        task,
        profile,
        promptRoot,
        workingDirectory,
        dispatchedAt));

    Assert.Contains(ex.Message, text => text.Contains("codex-cli", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("{subscriptionModelName}", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("{subscriptionReasoningEffort}", StringComparison.Ordinal));
    Assert.False(Directory.Exists(promptRoot));
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.Equal(previousDispatch, task.LastDispatch);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_uses_agent_subscription_profile_and_template_variables")]
    public void WorkerProfileDispatcherUsesAgentSubscriptionProfileAndTemplateVariables()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(promptRoot);
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
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
    var contextDirectory = Path.Combine(workingDirectory, ".orchestrator-context", goal.Id.Value);
    var expectedPromptCharacters = kernel.BuildTaskBrief(
        goal.Id,
        task.Id,
        "OpenAI/gpt-5.3-codex",
        workingDirectory,
        contextDirectory).Content.Length;
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
    Assert.True(estimatedPromptCharacters < expectedPromptCharacters);
    Assert.Equal(File.ReadAllText(dispatchResult.PromptPath).Length, task.LastDispatch.PromptCharacterCount);
    Assert.Equal(expectedPromptCharacters, task.LastDispatch.PromptCharacterCount);
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
    Xunit.Assert.Null(risk);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_rejects_already_running_subscription_dispatch")]
    public void WorkerProfileDispatcherRejectsAlreadyRunningSubscriptionDispatch()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
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
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
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
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
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
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
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
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_includes_working_directory_in_dispatched_prompt")]
    public void WorkerProfileDispatcherIncludesWorkingDirectoryInDispatchedPrompt()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Dispatch with working directory context");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
    var profile = new WorkerProfile("codex-cli", "codex exec --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})");

    var result = WorkerProfileDispatcher.PrepareTask(kernel, goal, task, profile, promptRoot, workingDirectory, DateTimeOffset.UtcNow);

    var prompt = File.ReadAllText(result.PromptPath);
    Assert.Contains(prompt, text => text.Contains($"Working directory, use absolute paths: {workingDirectory}", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_keeps_assigned_agent_when_catalog_still_contains_it")]
    public void WorkerProfileDispatcherKeepsAssignedAgentWhenCatalogStillContainsIt()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Keep pinned assignment", [new TaskSpec(TaskId.New(), "Summarize context", AgentRole.Planner)]);
    var assignedAgent = new AgentDefinition(
        new AgentId("anthropic-planner"),
        "Anthropic planner",
        AgentRole.Planner,
        new ModelProfile("Anthropic", "claude-haiku-4-5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli"));
    var otherRoleAgent = new AgentDefinition(
        new AgentId("openai-planner"),
        "OpenAI planner",
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
    kernel.ActivateGoal(goal.Id, [assignedAgent]);
    var task = goal.Tasks.Single();

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [otherRoleAgent, assignedAgent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt);

    Assert.Equal(assignedAgent.Id, task.AssignedAgentId);
    Assert.Equal("claude-cli", task.LastDispatch!.WorkerName);
    Assert.Equal("Anthropic", task.LastDispatch.ProviderName);
    Assert.Equal("claude-haiku-4-5", task.LastDispatch.ModelName);
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_prepares_subscription_tasks_by_assigned_provider")]
    public void WorkerProfileDispatcherPreparesSubscriptionTasksByAssignedProvider()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
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
    Assert.Contains(developer.LastDispatch.Command, text => text.Contains($"--model '{AgentCatalog.OpenAiSubscriptionModelAlias}'", StringComparison.Ordinal));
    Assert.Contains(developer.LastDispatch.Command, text => text.Contains("model_reasoning_effort='low'", StringComparison.Ordinal));
    Assert.Contains(developer.LastDispatch.Command, text => text.Contains("--sandbox 'workspace-write'", StringComparison.Ordinal));
    Assert.Contains(developer.LastDispatch.Command, text => text.Contains($"--cd '{workingDirectory}'", StringComparison.Ordinal));
    Assert.False(developer.LastDispatch.Command.Contains("{workingDirectory}", StringComparison.Ordinal));
    Assert.Equal("codex-cli", researcher.LastDispatch!.WorkerName);
    Assert.Contains(researcher.LastDispatch.Command, text => text.Contains("codex exec", StringComparison.Ordinal));
    Assert.Contains(researcher.LastDispatch.Command, text => text.Contains($"--model '{AgentCatalog.OpenAiSubscriptionModelAlias}'", StringComparison.Ordinal));
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
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
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
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("claude --model 'claude-sonnet' --permission-mode 'plan' -p", StringComparison.Ordinal));
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

    var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles);

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

    var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles);

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

    var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles);

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

    var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles);

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

    var plan = SubscriptionPlanBuilder.Build(
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
    Xunit.Assert.Null(plan.ReadyStartCostRisk);
    Xunit.Assert.Null(plan.ReadyStartCostRecommendation);
    Xunit.Assert.Null(plan.ReadyStartPromptCharacterCount);
    Xunit.Assert.Empty(plan.ReadyStartCostRiskDetails);
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

    var plan = SubscriptionPlanBuilder.Build(
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

    var plan = SubscriptionPlanBuilder.Build(
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

    var plan = SubscriptionPlanBuilder.Build(
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

    var plan = SubscriptionPlanBuilder.Build(
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
    File.WriteAllText(Path.Combine(dispatchRoot, ".git"), "gitdir: ..");

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
    Xunit.Assert.Null(plan.ReadyStartCostRisk);
    Xunit.Assert.Empty(plan.ReadyStartCostRiskDetails);
    Xunit.Assert.Null(thresholdRisk);

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
    Xunit.Assert.Null(preparedRisk);
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

    var plan = SubscriptionPlanBuilder.Build(goal, agents, WorkerProfileCatalog.Default());

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
    Assert.False(risk.PromptExceedsBatchThreshold);
    Assert.True(risk.HasOversizedPrompt);
    Assert.False(risk.TaskCountExceedsThreshold);
    Assert.False(risk.UsesComplexPaidModel);
    Assert.Equal("large paid subscription start", SubscriptionPromptCostGuard.BuildInlineLabel(risk));
    Assert.Contains(ex.Message, text => text.Contains("--confirm-large-paid-subscription-start", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("thresholds 18000 chars or 3 task(s)", StringComparison.Ordinal));
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
    Assert.Contains(ex.Message, text => text.Contains("thresholds 18000 chars or 3 task(s)", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "SubscriptionPromptCostGuard_allows_complex_paid_start_under_size_threshold")]
    public void SubscriptionPromptCostGuardAllowsComplexPaidStartUnderSizeThreshold()
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

    Xunit.Assert.Null(risk);
}
    [Xunit.Fact(DisplayName = "SubscriptionPromptCostGuard_applies_prior_evidence_allowance_to_ready_prompt")]
    public void SubscriptionPromptCostGuardAppliesPriorEvidenceAllowanceToReadyPrompt()
{
    var kernel = new AgentOrchestratorKernel();
    var priorTasks = Enumerable.Range(1, 3)
        .Select(index => new TaskSpec(TaskId.New(), $"Report prior result {index}.", AgentRole.Researcher))
        .ToList();
    var nextTask = new TaskSpec(TaskId.New(), "Report next result.", AgentRole.Researcher);
    var goal = kernel.CreateGoal("Collect pipeline reports", [.. priorTasks, nextTask]);
    var agent = new AgentDefinition(
        new AgentId("researcher"),
        "Researcher",
        AgentRole.Researcher,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    foreach (var priorTask in priorTasks)
    {
        kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
            "manual-verification passed",
            "C:\\repo",
            0,
            new string('a', 900),
            string.Empty,
            DateTimeOffset.UtcNow));
    }

    var risk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        _ => 7600,
        nextTask);

    Assert.True(AgentOrchestratorKernel.EstimatePriorTaskEvidenceCharacterCount(goal, nextTask.Id) >= PaidPromptThresholds.PriorTaskEvidenceAllowance);
    Xunit.Assert.Null(risk);
}
    [Xunit.Fact(DisplayName = "SubscriptionPromptCostGuard_blocks_paid_batch_prompt_total")]
    public void SubscriptionPromptCostGuardBlocksPaidBatchPromptTotal()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Plan complex batch subscription work",
        [
            new TaskSpec(TaskId.New(), "Design and implement a production multi-tenant architecture with distributed rollback and data integrity checks for service one.", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Design and implement a production multi-tenant architecture with distributed rollback and data integrity checks for service two.", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Design and implement a production multi-tenant architecture with distributed rollback and data integrity checks for service three.", AgentRole.Developer)
        ]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini", "medium"),
        ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
    kernel.ActivateGoal(goal.Id, [agent]);

    var risk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        _ => 8000);
    var ex = Assert.Throws<InvalidOperationException>(() => SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(risk, confirmed: false));

    Assert.True(risk is not null);
    Assert.Equal(24000, risk!.PromptCharacterCount);
    Assert.True(risk.PromptExceedsBatchThreshold);
    Assert.False(risk.HasOversizedPrompt);
    Assert.False(risk.TaskCountExceedsThreshold);
    Assert.True(risk.UsesComplexPaidModel);
    Assert.Equal("large paid subscription start", SubscriptionPromptCostGuard.BuildInlineLabel(risk));
    Assert.Contains(ex.Message, text => text.Contains("--confirm-large-paid-subscription-start", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_skips_usage_limited_tasks_before_retry_time")]
    public void WorkerProfileDispatcherSkipsUsageLimitedTasksBeforeRetryTime()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
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
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
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

    var plan = SubscriptionPlanBuilder.Build(goal, agents, WorkerProfileCatalog.Default());
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

    kernel.AcknowledgeSubscriptionLimitReview(goal.Id, developer.Id, "Reviewed profile and provider timing.");
    var reviewedPlan = SubscriptionPlanBuilder.Build(goal, agents, WorkerProfileCatalog.Default());
    var reviewedItem = reviewedPlan.Items.First(item => item.Role == AgentRole.Developer);
    var result = WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        developer,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        retryWindowPassed);

    Assert.True(reviewedItem.CanPrepare);
    Assert.Equal(developer.Id, result.Task.Id);
    Assert.Equal(WorkTaskStatus.Running, developer.Status);
    Assert.Equal("Reviewed profile and provider timing.", developer.SubscriptionLimitReviewNote);
    Assert.Equal(2, developer.SubscriptionLimitReviewedFailureCount);
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_rejects_subscription_dispatch_for_developer_without_worktree")]
    public void WorkerProfileDispatcherRejectsSubscriptionDispatchForDeveloperWithoutWorktree()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-12T10:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Implement the feature without workspace");
    var agent = new AgentDefinition(
        new AgentId("openai-developer"),
        "OpenAI Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    var ex = Assert.Throws<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt));

    Assert.Contains(ex.Message, text => text.Contains("workspace create", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("Developer", StringComparison.Ordinal));
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.True(task.LastDispatch is null);
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_allows_subscription_dispatch_for_researcher_without_worktree")]
    public void WorkerProfileDispatcherAllowsSubscriptionDispatchForResearcherWithoutWorktree()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-12T10:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Survey the codebase without workspace");
    var agent = new AgentDefinition(
        new AgentId("openai-researcher"),
        "OpenAI Researcher",
        AgentRole.Researcher,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
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

    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.True(task.LastDispatch is not null);
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_allows_subscription_dispatch_for_developer_with_worktree")]
    public void WorkerProfileDispatcherAllowsSubscriptionDispatchForDeveloperWithWorktree()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-12T10:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Implement the feature with workspace");
    var agent = new AgentDefinition(
        new AgentId("openai-developer"),
        "OpenAI Developer",
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

    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.True(task.LastDispatch is not null);
    Assert.Equal(workingDirectory, task.LastDispatch!.WorkingDirectory);
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

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_refuses_process_start_when_disabled_for_environment")]
    public void BackgroundDispatchRunnerRefusesProcessStartWhenDisabledForEnvironment()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Refuse disabled dispatch start");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "Write-Output should-not-run", root, DateTimeOffset.UtcNow));
    var runner = new BackgroundDispatchRunner(disableProcessStart: true);

    var ex = Assert.Throws<InvalidOperationException>(() => runner.StartLatestDispatch(kernel, goal.Id, task.Id, Path.Combine(root, "logs")));

    Assert.Contains(ex.Message, text => text.Contains(BackgroundDispatchRunner.DisableDispatchStartVariable, StringComparison.Ordinal));
    Xunit.Assert.Null(task.LastProcess);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_defers_future_subscription_retry_after_before_start")]
    public void BackgroundDispatchRunnerDefersFutureSubscriptionRetryAfterBeforeStart()
{
    var root = CreateTempDirectory();
    var now = DateTimeOffset.UtcNow;
    var retryAfter = now.AddHours(1);
    var command = "Write-Output should-not-run";
    var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
        [
            new GoalSnapshot(
                "goal-deferred",
                "Defer recorded dispatch start",
                GoalStatus.Active,
                [
                    new TaskSnapshot(
                        "task-deferred",
                        "Do work",
                        AgentRole.Developer,
                        WorkTaskStatus.Running,
                        "developer",
                        null,
                        null,
                        [
                            new TaskVerificationSnapshot(
                                command,
                                root,
                                1,
                                string.Empty,
                                $"ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at {retryAfter:h:mm tt}.",
                                now)
                        ],
                        new TaskDispatchSnapshot("local", command, root, now),
                        null,
                        SubscriptionRetryAfter: retryAfter)
                ],
                [])
        ],
        []));
    var goal = kernel.Goals.Single();
    var task = goal.Tasks.Single();

    var ex = Assert.Throws<InvalidOperationException>(() => new BackgroundDispatchRunner().StartLatestDispatch(kernel, goal.Id, task.Id, Path.Combine(root, "logs")));

    Assert.Contains(ex.Message, text => text.Contains("retry after", StringComparison.Ordinal));
    Assert.True(ex.Message.Contains(retryAfter.ToString("u"), StringComparison.Ordinal));
    Xunit.Assert.Null(task.LastProcess);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_wrapper_shuts_down_dotnet_build_server_after_worker_command")]
    public void BackgroundDispatchRunnerWrapperShutsDownDotnetBuildServerAfterWorkerCommand()
{
    var wrapper = BackgroundDispatchRunner.BuildWrapper(
        "Write-Output ok",
        "C:\\logs\\out.log",
        "C:\\logs\\err.log",
        "C:\\logs\\exit.txt");

    Assert.Contains(wrapper, text => text.Contains("finally", StringComparison.Ordinal));
    Assert.Contains(wrapper, text => text.Contains("dotnet build-server shutdown", StringComparison.Ordinal));
    Assert.Contains(wrapper, text => text.Contains("*> $null", StringComparison.Ordinal));
    Assert.Contains(wrapper, text => text.Contains("$exitCodePath = 'C:\\logs\\exit.txt'", StringComparison.Ordinal));
    Assert.Contains(wrapper, text => text.Contains("[IO.File]::WriteAllText($exitCodePath, [string]$code)", StringComparison.Ordinal));
    Assert.Contains(wrapper, text => text.Contains("exit $code", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_wrapper_can_skip_dotnet_build_server_shutdown_for_local_dispatch")]
    public void BackgroundDispatchRunnerWrapperCanSkipDotnetBuildServerShutdownForLocalDispatch()
{
    var wrapper = BackgroundDispatchRunner.BuildWrapper(
        "Write-Output ok",
        "C:\\logs\\out.log",
        "C:\\logs\\err.log",
        "C:\\logs\\exit.txt",
        shutdownBuildServerOnExit: false,
        disableSharedCompilation: false);

    Assert.False(wrapper.Contains("dotnet build-server shutdown", StringComparison.Ordinal));
    Assert.False(wrapper.Contains("DOTNET_CLI_USE_MSBUILD_SERVER", StringComparison.Ordinal));
    Assert.False(wrapper.Contains("MSBUILDDISABLENODEREUSE", StringComparison.Ordinal));
    Assert.False(wrapper.Contains("UseSharedCompilation", StringComparison.Ordinal));
    Assert.Contains(wrapper, text => text.Contains("[IO.File]::WriteAllText($exitCodePath, [string]$code)", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_wrapper_disables_shared_compilation_by_default")]
    public void BackgroundDispatchRunnerWrapperDisablesSharedCompilationByDefault()
{
    var wrapper = BackgroundDispatchRunner.BuildWrapper(
        "Write-Output ok",
        "C:\\logs\\out.log",
        "C:\\logs\\err.log",
        "C:\\logs\\exit.txt");

    Assert.Contains(wrapper, text => text.Contains("$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'", StringComparison.Ordinal));
    Assert.Contains(wrapper, text => text.Contains("$env:MSBUILDDISABLENODEREUSE = '1'", StringComparison.Ordinal));
    Assert.Contains(wrapper, text => text.Contains("$env:UseSharedCompilation = 'false'", StringComparison.Ordinal));
    Assert.Contains(wrapper, text => text.Contains("Start-Heartbeat", StringComparison.Ordinal));
    Assert.Contains(wrapper, text => text.Contains("& { Write-Output ok } 1> $stdoutPath 2> $stderrPath", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_wrapper_can_skip_disabling_shared_compilation")]
    public void BackgroundDispatchRunnerWrapperCanSkipDisablingSharedCompilation()
{
    var wrapper = BackgroundDispatchRunner.BuildWrapper(
        "Write-Output ok",
        "C:\\logs\\out.log",
        "C:\\logs\\err.log",
        "C:\\logs\\exit.txt",
        disableSharedCompilation: false);

    Assert.False(wrapper.Contains("DOTNET_CLI_USE_MSBUILD_SERVER", StringComparison.Ordinal));
    Assert.False(wrapper.Contains("MSBUILDDISABLENODEREUSE", StringComparison.Ordinal));
    Assert.False(wrapper.Contains("UseSharedCompilation", StringComparison.Ordinal));
    Assert.Contains(wrapper, text => text.Contains("& { Write-Output ok } 1> $stdoutPath 2> $stderrPath", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_wrapper_writes_heartbeat_progress")]
    public void BackgroundDispatchRunnerWrapperWritesHeartbeatProgress()
{
    var wrapper = BackgroundDispatchRunner.BuildWrapper(
        "Write-Output ok",
        "C:\\logs\\out.log",
        "C:\\logs\\err.log",
        "C:\\logs\\exit.txt",
        "C:\\logs\\heartbeat.json");

    Assert.Contains(wrapper, text => text.Contains("$heartbeatPath = 'C:\\logs\\heartbeat.json'", StringComparison.Ordinal));
    Assert.Contains(wrapper, text => text.Contains("lastProgressAt", StringComparison.Ordinal));
    Assert.Contains(wrapper, text => text.Contains("stdoutBytes", StringComparison.Ordinal));
    Assert.Contains(wrapper, text => text.Contains("stderrBytes", StringComparison.Ordinal));
    Assert.Contains(wrapper, text => text.Contains("ConvertTo-Json -Compress", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_non_local_dispatch_runs_with_shared_compilation_disabled")]
    public void BackgroundDispatchRunnerNonLocalDispatchRunsWithSharedCompilationDisabled()
{
    var root = CreateTempDirectory();
    var logs = Path.Combine(root, "logs");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Disable shared compilation for worker dispatch");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "Write-Output $env:DOTNET_CLI_USE_MSBUILD_SERVER; Write-Output $env:MSBUILDDISABLENODEREUSE; Write-Output $env:UseSharedCompilation",
        root,
        DateTimeOffset.UtcNow));

    var process = new BackgroundDispatchRunner().StartLatestDispatch(kernel, goal.Id, task.Id, logs);
    WaitForExitFile(process.ExitCodePath);
    new BackgroundDispatchRunner().RefreshLatestProcess(kernel, goal.Id, task.Id);

    var output = File.ReadAllLines(process.StandardOutputPath);
    Assert.True(File.Exists(BackgroundDispatchRunner.GetHeartbeatPath(process)));
    Assert.Equal(3, output.Length);
    Assert.Equal("0", output[0]);
    Assert.Equal("1", output[1]);
    Assert.Equal("false", output[2]);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_refresh_completes_when_exit_file_exists_even_if_wrapper_is_running")]
    public void BackgroundDispatchRunnerRefreshCompletesWhenExitFileExistsEvenIfWrapperIsRunning()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "exit.txt");
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-11T16:00:00Z"));
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Complete background process from exit file");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, "done");
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "codex exec prompt", root, stdout, stderr, exit, clock.UtcNow, null, null));

    var completed = new BackgroundDispatchRunner(clock, isStillRunning: _ => true)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(0, completed.ExitCode);
    Assert.Equal(clock.UtcNow, completed.CompletedAt);
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal("done", task.LastVerification!.StandardOutput);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_refresh_fails_idle_codex_wrapper_after_final_output")]
    public void BackgroundDispatchRunnerRefreshFailsIdleCodexWrapperAfterFinalOutput()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "exit.txt");
    var now = DateTimeOffset.Parse("2026-06-11T16:10:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Detect hung codex wrapper");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, "Implemented the change.");
    File.WriteAllText(stderr, "Tokens used: input=123 output=45");
    File.SetLastWriteTimeUtc(stdout, now.AddMinutes(-3).UtcDateTime);
    File.SetLastWriteTimeUtc(stderr, now.AddMinutes(-3).UtcDateTime);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, now.AddMinutes(-5)));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "codex exec prompt", root, stdout, stderr, exit, now.AddMinutes(-5), null, null));

    var completed = new BackgroundDispatchRunner(clock, TimeSpan.FromMinutes(2), _ => true)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(1, completed.ExitCode);
    Assert.Equal(clock.UtcNow, completed.CompletedAt);
    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.True(File.Exists(exit));
    Assert.Contains(task.LastVerification!.StandardOutput, text => text.Contains("Implemented the change.", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("Tokens used", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("wrapper appears hung after codex final output", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_refresh_fails_provider_neutral_stall_after_heartbeat_progress_timeout")]
    public void BackgroundDispatchRunnerRefreshFailsProviderNeutralStallAfterHeartbeatProgressTimeout()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Detect silent subscription stall");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", root, now.AddMinutes(-30)));
    var process = new TaskProcessRecord(999999, "claude prompt", root, stdout, stderr, exit, now.AddMinutes(-30), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    WriteHeartbeat(process, now.AddMinutes(-20), now.AddMinutes(-20), "running", 0, 0);

    var completed = new BackgroundDispatchRunner(
            clock,
            isStillRunning: _ => true,
            progressStallTimeout: TimeSpan.FromMinutes(10))
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(1, completed.ExitCode);
    Assert.Equal(clock.UtcNow, completed.CompletedAt);
    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.True(File.Exists(exit));
    Assert.Contains(task.LastVerification!.StandardError, text => text.Contains("no observable progress", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("heartbeat state=running", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_refresh_keeps_recent_heartbeat_running")]
    public void BackgroundDispatchRunnerRefreshKeepsRecentHeartbeatRunning()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Keep active heartbeat running");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, "thinking");
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", root, now.AddMinutes(-30)));
    var process = new TaskProcessRecord(999999, "claude prompt", root, stdout, stderr, exit, now.AddMinutes(-30), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    WriteHeartbeat(process, now.AddMinutes(-1), now.AddMinutes(-1), "running", 8, 0);

    var refreshed = new BackgroundDispatchRunner(
            clock,
            isStillRunning: _ => true,
            progressStallTimeout: TimeSpan.FromMinutes(10))
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(process, refreshed);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.False(File.Exists(exit));
    Xunit.Assert.Null(task.LastVerification);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_refresh_keeps_stale_file_role_heartbeat_when_worktree_changed")]
    public void BackgroundDispatchRunnerRefreshKeepsStaleFileRoleHeartbeatWhenWorktreeChanged()
{
    var root = CreateSeededDispatchRepository();
    var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Do not kill worker after file progress");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    File.WriteAllText(Path.Combine(worktree, "dirty.txt"), "progress");
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", worktree, now.AddMinutes(-30)));
    var process = new TaskProcessRecord(999999, "claude prompt", worktree, stdout, stderr, exit, now.AddMinutes(-30), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    WriteHeartbeat(process, now.AddMinutes(-20), now.AddMinutes(-20), "running", 0, 0);

    var refreshed = new BackgroundDispatchRunner(
            clock,
            isStillRunning: _ => true,
            progressStallTimeout: TimeSpan.FromMinutes(10))
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(process, refreshed);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.False(File.Exists(exit));
    Xunit.Assert.Null(task.LastVerification);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_hung_wrapper_completes_when_Developer_worktree_evidence_passes")]
    public void BackgroundDispatchRunnerHungWrapperCompletesWhenDeveloperWorktreeEvidencePasses()
{
    var root = CreateSeededDispatchRepository();
    var now = DateTimeOffset.Parse("2026-06-12T10:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var taskSpec = new TaskSpec(TaskId.New(), "Developer task.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Hung wrapper with evidence", [taskSpec]);
    var agent = new AgentDefinition(
        new AgentId("codex-developer"),
        "Codex Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);

    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
    RunGit(worktree, ["add", "-A"], now.AddMinutes(-4));
    RunGit(worktree, ["commit", "-m", "Feature"], now.AddMinutes(-4));

    var logs = Path.Combine(root, "logs");
    Directory.CreateDirectory(logs);
    var stdout = Path.Combine(logs, "dev.out.log");
    var stderr = Path.Combine(logs, "dev.err.log");
    var exit = Path.Combine(logs, "dev.exit.txt");
    File.WriteAllText(stdout, "Implemented the change.");
    File.WriteAllText(stderr, "Tokens used: input=123 output=45");
    File.SetLastWriteTimeUtc(stdout, now.AddMinutes(-3).UtcDateTime);
    File.SetLastWriteTimeUtc(stderr, now.AddMinutes(-3).UtcDateTime);

    var task = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", worktree, now.AddMinutes(-5)));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "codex exec prompt", worktree, stdout, stderr, exit, now.AddMinutes(-5), null, null));

    var completed = new BackgroundDispatchRunner(clock, TimeSpan.FromMinutes(2), _ => true)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(0, completed.ExitCode);
    Assert.Equal(clock.UtcNow, completed.CompletedAt);
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.True(File.Exists(exit));
    Assert.Equal("0", File.ReadAllText(exit));
    Assert.Contains(task.LastVerification!.StandardError, text => text.Contains("Wrapper process reaped", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("commits_after_dispatch=1", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_hung_wrapper_fails_when_Developer_worktree_evidence_missing")]
    public void BackgroundDispatchRunnerHungWrapperFailsWhenDeveloperWorktreeEvidenceMissing()
{
    var root = CreateSeededDispatchRepository();
    var now = DateTimeOffset.Parse("2026-06-12T10:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var taskSpec = new TaskSpec(TaskId.New(), "Developer task.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Hung wrapper without evidence", [taskSpec]);
    var agent = new AgentDefinition(
        new AgentId("codex-developer"),
        "Codex Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);

    var worktree = GoalWorktrees.Ensure(root, goal.Id);

    var logs = Path.Combine(root, "logs");
    Directory.CreateDirectory(logs);
    var stdout = Path.Combine(logs, "dev.out.log");
    var stderr = Path.Combine(logs, "dev.err.log");
    var exit = Path.Combine(logs, "dev.exit.txt");
    File.WriteAllText(stdout, "Implemented the change.");
    File.WriteAllText(stderr, "Tokens used: input=123 output=45");
    File.SetLastWriteTimeUtc(stdout, now.AddMinutes(-3).UtcDateTime);
    File.SetLastWriteTimeUtc(stderr, now.AddMinutes(-3).UtcDateTime);

    var task = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", worktree, now.AddMinutes(-5)));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "codex exec prompt", worktree, stdout, stderr, exit, now.AddMinutes(-5), null, null));

    var completed = new BackgroundDispatchRunner(clock, TimeSpan.FromMinutes(2), _ => true)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(1, completed.ExitCode);
    Assert.Equal(clock.UtcNow, completed.CompletedAt);
    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.True(File.Exists(exit));
    Assert.Equal("1", File.ReadAllText(exit));
    Assert.Contains(task.LastVerification!.StandardError, text => text.Contains("wrapper appears hung after codex final output", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_developer_unchanged_dispatch_fails")]
    public void BackgroundDispatchRunnerDeveloperUnchangedDispatchFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the change.",
        string.Empty,
        clock);

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("did not produce required relevant file-change evidence", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("branch=goal/", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("worktree=clean", StringComparison.Ordinal));
    Assert.Equal("1", File.ReadAllText(process.ExitCodePath));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_developer_no_change_rationale_without_source_change_fails")]
    public void BackgroundDispatchRunnerDeveloperNoChangeRationaleWithoutSourceChangeFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "NO_CHANGE: Existing code already satisfies the request.",
        string.Empty,
        clock);

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("did not produce required relevant file-change evidence", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("changed_paths=none", StringComparison.Ordinal));
    Assert.Equal("1", File.ReadAllText(process.ExitCodePath));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_developer_generated_noise_only_dispatch_fails")]
    public void BackgroundDispatchRunnerDeveloperGeneratedNoiseOnlyDispatchFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the change.",
        string.Empty,
        clock,
        worktree =>
        {
            var qwenDirectory = Path.Combine(worktree, ".qwen");
            Directory.CreateDirectory(qwenDirectory);
            File.WriteAllText(Path.Combine(qwenDirectory, "settings.json"), "{}");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Qwen settings noise"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
        });

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("did not produce required relevant file-change evidence", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("commits_after_dispatch=1", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("changed_paths=.qwen/settings.json", StringComparison.Ordinal));
    Assert.Equal("1", File.ReadAllText(process.ExitCodePath));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_tester_verification_only_clean_dispatch_passes")]
    public void BackgroundDispatchRunnerTesterVerificationOnlyCleanDispatchPasses()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "dotnet test --filter OrchestratorHealthInspector\r\nPassed! - Failed: 0, Passed: 16, Skipped: 0, Total: 16.\r\nFull suite Core 172/172 + Infrastructure 326/326.",
        string.Empty,
        clock,
        taskDescription: "Verify behavior with automated and manual checks",
        verificationPlan: "Run or attempt exact automated tests or manual smoke checks and record pass/fail evidence.");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.False(task.LastVerification.StandardError.Contains("did not produce required relevant file-change evidence", StringComparison.Ordinal));
    Assert.Equal("0", File.ReadAllText(process.ExitCodePath));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_tester_clean_dispatch_without_passing_evidence_fails")]
    public void BackgroundDispatchRunnerTesterCleanDispatchWithoutPassingEvidenceFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "Ran dotnet test --filter OrchestratorHealthInspector.",
        string.Empty,
        clock,
        taskDescription: "Verify behavior with automated and manual checks",
        verificationPlan: "Run or attempt exact automated tests or manual smoke checks and record pass/fail evidence.");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("did not produce required relevant file-change evidence", StringComparison.Ordinal));
    Assert.Equal("1", File.ReadAllText(process.ExitCodePath));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_tester_expected_file_change_without_evidence_fails")]
    public void BackgroundDispatchRunnerTesterExpectedFileChangeWithoutEvidenceFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "Added focused regression tests and ran dotnet test --filter BackgroundDispatchRunner.\r\nPassed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3.",
        string.Empty,
        clock,
        taskDescription: "Add focused regression tests for the dispatch guard",
        verificationPlan: "Update tests and run the focused dispatch guard coverage.");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("did not produce required relevant file-change evidence", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("worktree=clean", StringComparison.Ordinal));
    Assert.Equal("1", File.ReadAllText(process.ExitCodePath));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_dirty_worktree_without_commit_fails")]
    public void BackgroundDispatchRunnerFileRoleDirtyWorktreeWithoutCommitFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "Verified and updated a test.",
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "dirty.txt"), "uncommitted"));

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("left the worktree dirty", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("worktree=dirty", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("status_short=?? dirty.txt", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_with_committed_change_passes")]
    public void BackgroundDispatchRunnerFileRoleWithCommittedChangePasses()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Committed implementation.",
        string.Empty,
        clock,
        worktree =>
        {
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Feature"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
        });

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_with_commit_and_dirty_worktree_fails")]
    public void BackgroundDispatchRunnerFileRoleWithCommitAndDirtyWorktreeFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Committed implementation.",
        string.Empty,
        clock,
        worktree =>
        {
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Feature"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            File.AppendAllText(Path.Combine(worktree, "seed.txt"), "leftover");
        });

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("left the worktree dirty", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("commits_after_dispatch=1", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("status_short=M seed.txt", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_with_no_change_rationale_and_clean_worktree_passes")]
    public void BackgroundDispatchRunnerFileRoleWithNoChangeRationaleAndCleanWorktreePasses()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "NO_CHANGE: Existing focused test already covers this behavior.",
        string.Empty,
        clock);

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_with_no_change_rationale_and_dirty_worktree_fails")]
    public void BackgroundDispatchRunnerFileRoleWithNoChangeRationaleAndDirtyWorktreeFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "NO_CHANGE: Existing focused test already covers this behavior.",
        string.Empty,
        clock,
        worktree => File.AppendAllText(Path.Combine(worktree, "seed.txt"), "leftover"));

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("left the worktree dirty", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("status_short=M seed.txt", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_non_file_role_completion_is_unchanged")]
    public void BackgroundDispatchRunnerNonFileRoleCompletionIsUnchanged()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Reviewer,
        "Reviewed implementation evidence.",
        string.Empty,
        clock,
        worktree => File.AppendAllText(Path.Combine(worktree, "seed.txt"), "leftover"));

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
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

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_researcher_dispatch_uses_read_only_codex_sandbox")]
    public void WorkerProfileDispatcherResearcherDispatchUsesReadOnlyCodexSandbox()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Survey the codebase configuration");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var researcher = goal.Tasks.First(task => task.RequiredRole == AgentRole.Researcher);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        researcher,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt);

    Assert.Contains(researcher.LastDispatch!.Command, text => text.Contains("--sandbox 'read-only'", StringComparison.Ordinal));
    Assert.True(!researcher.LastDispatch.Command.Contains("workspace-write", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_developer_dispatch_uses_workspace_write_codex_sandbox")]
    public void WorkerProfileDispatcherDeveloperDispatchUsesWorkspaceWriteCodexSandbox()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Implement the feature");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var developer = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        developer,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt);

    Assert.Contains(developer.LastDispatch!.Command, text => text.Contains("--sandbox 'workspace-write'", StringComparison.Ordinal));
    Assert.True(!developer.LastDispatch.Command.Contains("read-only", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_claude_resolves_plan_for_reviewer_and_bypassPermissions_for_developer")]
    public void WorkerProfileDispatcherClaudeResolvesPlanForReviewerAndBypassPermissionsForDeveloper()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var developerGoal = kernel.CreateGoal("Implement the change", [new TaskSpec(TaskId.New(), "Add the feature.", AgentRole.Developer)]);
    var reviewerGoal = kernel.CreateGoal("Review the change", [new TaskSpec(TaskId.New(), "Review the implementation.", AgentRole.Reviewer)]);
    var reviewerAgent = new AgentDefinition(
        new AgentId("anthropic-reviewer"),
        "Anthropic reviewer",
        AgentRole.Reviewer,
        new ModelProfile("Anthropic", "claude-sonnet-4-20250514", ModelCapability.Text, SubscriptionMode.ApiKey, MaxOutputTokens: AgentCatalog.RoutineApiMaxOutputTokens),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet"));
    var developerAgent = new AgentDefinition(
        new AgentId("anthropic-developer"),
        "Anthropic developer",
        AgentRole.Developer,
        new ModelProfile("Anthropic", "claude-sonnet-4-20250514", ModelCapability.Text, SubscriptionMode.ApiKey, MaxOutputTokens: AgentCatalog.RoutineApiMaxOutputTokens),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet"));
    kernel.ActivateGoal(developerGoal.Id, [developerAgent]);
    kernel.ActivateGoal(reviewerGoal.Id, [reviewerAgent]);
    var developerTask = developerGoal.Tasks.Single();
    var reviewerTask = reviewerGoal.Tasks.Single();

    WorkerProfileDispatcher.PrepareSubscriptionTask(kernel, developerGoal, developerTask, [developerAgent], WorkerProfileCatalog.Default(), promptRoot, workingDirectory, dispatchedAt);
    WorkerProfileDispatcher.PrepareSubscriptionTask(kernel, reviewerGoal, reviewerTask, [reviewerAgent], WorkerProfileCatalog.Default(), promptRoot, workingDirectory, dispatchedAt);

    Assert.Contains(developerTask.LastDispatch!.Command, text => text.Contains("--permission-mode 'bypassPermissions'", StringComparison.Ordinal));
    Assert.Contains(reviewerTask.LastDispatch!.Command, text => text.Contains("--permission-mode 'plan'", StringComparison.Ordinal));
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

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_writes_handoff_file_with_full_evidence")]
    public void WorkerProfileDispatcherWritesHandoffFileWithFullEvidence()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Fix the login bug.", AgentRole.Developer);
    var currentTask = new TaskSpec(TaskId.New(), "Add regression test for login.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Stabilize login flow", [priorTask, currentTask]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
    var stdoutHead = new string('a', 20000);
    var stdoutTail = new string('b', 10000);
    var fullStdout = stdoutHead + stdoutTail;
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "dotnet test", workingDirectory, 0, fullStdout, string.Empty, DateTimeOffset.UtcNow));
    var profile = new WorkerProfile("codex", "codex exec --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})");

    WorkerProfileDispatcher.PrepareTask(kernel, goal, currentTask, profile, Path.Combine(root, "prompts"), workingDirectory, DateTimeOffset.UtcNow);

    var handoffPath = Path.Combine(workingDirectory, ".orchestrator-handoff.md");
    Assert.True(File.Exists(handoffPath));
    var content = File.ReadAllText(handoffPath);
    Assert.Contains(content, text => text.Contains("Developer: Fix the login bug.", StringComparison.Ordinal));
    Assert.Contains(content, text => text.Contains(stdoutHead, StringComparison.Ordinal));
    Assert.Contains(content, text => text.Contains("[truncated 10000 chars]", StringComparison.Ordinal));
    Assert.True(!content.Contains(stdoutTail, StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_overwrites_handoff_file_on_each_dispatch")]
    public void WorkerProfileDispatcherOverwritesHandoffFileOnEachDispatch()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".orchestrator-handoff.md"), "STALE CONTENT FROM PREVIOUS DISPATCH");
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Prior completed work.", AgentRole.Developer);
    var currentTask = new TaskSpec(TaskId.New(), "Current work.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Test overwrite behavior", [priorTask, currentTask]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "dotnet test", workingDirectory, 0, "Tests passed.", string.Empty, DateTimeOffset.UtcNow));
    var profile = new WorkerProfile("codex", "codex exec --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})");

    WorkerProfileDispatcher.PrepareTask(kernel, goal, currentTask, profile, Path.Combine(root, "prompts"), workingDirectory, DateTimeOffset.UtcNow);

    var content = File.ReadAllText(Path.Combine(workingDirectory, ".orchestrator-handoff.md"));
    Assert.True(!content.Contains("STALE CONTENT", StringComparison.Ordinal));
    Assert.Contains(content, text => text.Contains("Prior completed work.", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_brief_contains_handoff_file_pointer_line")]
    public void WorkerProfileDispatcherBriefContainsHandoffFilePointerLine()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Fix the login bug.", AgentRole.Developer);
    var currentTask = new TaskSpec(TaskId.New(), "Add regression test.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Stabilize login", [priorTask, currentTask]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "dotnet test", workingDirectory, 0, "Tests passed.", string.Empty, DateTimeOffset.UtcNow));
    var profile = new WorkerProfile("codex", "codex exec --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})");

    var result = WorkerProfileDispatcher.PrepareTask(kernel, goal, currentTask, profile, Path.Combine(root, "prompts"), workingDirectory, DateTimeOffset.UtcNow);

    var brief = File.ReadAllText(result.PromptPath);
    Assert.Contains(brief, text => text.Contains(".orchestrator-context", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("manifest.md", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("digest.md", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("prior-task-summaries.md", StringComparison.Ordinal));
    Assert.True(brief.IndexOf("prior-task-summaries.md", StringComparison.Ordinal) < brief.IndexOf("prior-task-evidence.md", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("Full evidence available in the context files", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("## Prior Task Evidence", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_writes_context_artifacts_with_repo_guidance_and_fuller_prior_evidence")]
    public void WorkerProfileDispatcherWritesContextArtifactsWithRepoGuidanceAndFullerPriorEvidence()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, "AGENTS.md"), "Repo-local agent guidance.");
    File.WriteAllText(Path.Combine(workingDirectory, "BACKLOG.md"), "Open backlog item.");
    File.WriteAllText(Path.Combine(workingDirectory, "DOGFOOD_LOG.md"), "Recent dogfood note.");
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Plan implementation.", AgentRole.Planner);
    var currentTask = new TaskSpec(TaskId.New(), "Implement context artifacts.", AgentRole.Developer, "Run worker dispatch tests.");
    var goal = kernel.CreateGoal("Ship context artifact handoff", [priorTask, currentTask]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
    var inlineHead = new string('a', 800);
    var artifactOnlyTail = new string('z', 2000);
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "dotnet test", workingDirectory, 0, inlineHead + artifactOnlyTail, string.Empty, DateTimeOffset.UtcNow));
    var profile = new WorkerProfile("codex", "codex exec --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})");

    var result = WorkerProfileDispatcher.PrepareTask(kernel, goal, currentTask, profile, Path.Combine(root, "prompts"), workingDirectory, DateTimeOffset.UtcNow);

    var contextDirectory = Path.Combine(workingDirectory, ".orchestrator-context", goal.Id.Value);
    Assert.True(Directory.Exists(contextDirectory));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "manifest.md")));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "digest.md")));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "objective.md")));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "current-task.md")));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "prior-task-summaries.md")));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "prior-task-evidence.md")));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "AGENTS.md")));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "BACKLOG.md")));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "DOGFOOD_LOG.md")));
    var manifest = File.ReadAllText(Path.Combine(contextDirectory, "manifest.md"));
    var digest = File.ReadAllText(Path.Combine(contextDirectory, "digest.md"));
    var summaries = File.ReadAllText(Path.Combine(contextDirectory, "prior-task-summaries.md"));
    Assert.Contains(manifest, text => text.Contains("digest.md", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("prior-task-summaries.md", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("prior-task-evidence.md", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("Role Artifact Priorities", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("AGENTS.md", StringComparison.Ordinal));
    Assert.Contains(digest, text => text.Contains("## Current Task", StringComparison.Ordinal));
    Assert.Contains(digest, text => text.Contains("## Role Artifact Priorities", StringComparison.Ordinal));
    Assert.Contains(digest, text => text.Contains("## Prior Completed Outcomes", StringComparison.Ordinal));
    Assert.Contains(digest, text => text.Contains("prior-task-summaries.md", StringComparison.Ordinal));
    Assert.Contains(digest, text => text.Contains("prior-task-evidence.md", StringComparison.Ordinal));
    Assert.Contains(digest, text => text.Contains(".orchestrator-handoff.md", StringComparison.Ordinal));
    Assert.Contains(summaries, text => text.Contains("Changed files: Not reported.", StringComparison.Ordinal));
    Assert.Contains(summaries, text => text.Contains("Behavior changes: Not reported.", StringComparison.Ordinal));
    Assert.Contains(summaries, text => text.Contains("Verification: `dotnet test`", StringComparison.Ordinal));
    Assert.Contains(summaries, text => text.Contains("Model fit: Not reported.", StringComparison.Ordinal));
    Assert.Contains(File.ReadAllText(Path.Combine(contextDirectory, "current-task.md")), text => text.Contains("Run worker dispatch tests.", StringComparison.Ordinal));
    Assert.Contains(File.ReadAllText(Path.Combine(contextDirectory, "prior-task-evidence.md")), text => text.Contains(artifactOnlyTail, StringComparison.Ordinal));
    var prompt = File.ReadAllText(result.PromptPath);
    Assert.Contains(prompt, text => text.Contains(contextDirectory, StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("## Prior Task Evidence", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("Read prior-task-summaries.md first", StringComparison.Ordinal));
    Assert.True(prompt.IndexOf("prior-task-summaries.md", StringComparison.Ordinal) < prompt.IndexOf("prior-task-evidence.md", StringComparison.Ordinal));
    Assert.True(!prompt.Contains(inlineHead, StringComparison.Ordinal));
    Assert.True(!prompt.Contains(artifactOnlyTail, StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_writes_current_retry_evidence_for_collapsed_prompt_pointers")]
    public void WorkerContextArtifactsWritesCurrentRetryEvidenceForCollapsedPromptPointers()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var clock = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var currentTask = new TaskSpec(TaskId.New(), "Fix prompt budget retry.", AgentRole.Developer, "Run focused prompt tests.");
    var goal = kernel.CreateGoal("Carry retry evidence in context artifacts", [currentTask]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    kernel.RecordTaskDispatch(goal.Id, currentTask.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec retry-prompt.md",
        workingDirectory,
        clock));
    kernel.RecordTaskVerification(goal.Id, currentTask.Id, new TaskVerificationRecord(
        "dotnet test --filter TaskBriefTests",
        workingDirectory,
        1,
        "current retry stdout evidence",
        "current retry stderr evidence",
        clock));

    var contextDirectory = WorkerContextArtifacts.Write(goal, currentTask, workingDirectory);

    var currentTaskArtifact = File.ReadAllText(Path.Combine(contextDirectory, "current-task.md"));
    var manifest = File.ReadAllText(Path.Combine(contextDirectory, "manifest.md"));
    Assert.Contains(currentTaskArtifact, text => text.Contains("## Last Dispatch", StringComparison.Ordinal));
    Assert.Contains(currentTaskArtifact, text => text.Contains("codex exec retry-prompt.md", StringComparison.Ordinal));
    Assert.Contains(currentTaskArtifact, text => text.Contains("## Last Verification", StringComparison.Ordinal));
    Assert.Contains(currentTaskArtifact, text => text.Contains("current retry stdout evidence", StringComparison.Ordinal));
    Assert.Contains(currentTaskArtifact, text => text.Contains("current retry stderr evidence", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("retry evidence when present", StringComparison.Ordinal));
}

    [Xunit.Theory(DisplayName = "WorkerContextArtifacts_writes_role_specific_priorities_and_prior_summaries")]
    [Xunit.InlineData(AgentRole.Developer, "current-task.md: anchor implementation scope", "prior behavior")]
    [Xunit.InlineData(AgentRole.Tester, "identify changed files, behavior claims, risks, and verification gaps", "required checks")]
    [Xunit.InlineData(AgentRole.Reviewer, "review changed files, behavior changes, verification, risks, and model fit", "check open blockers")]
    public void WorkerContextArtifactsWritesRoleSpecificPrioritiesAndPriorSummaries(
        AgentRole role,
        string expectedPrimaryPriority,
        string expectedSecondaryPriority)
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Implement summary source.", AgentRole.Developer);
    var currentTask = new TaskSpec(TaskId.New(), "Use role bundle.", role, "Run focused checks.");
    var goal = kernel.CreateGoal("Bundle role context", [priorTask, currentTask]);
    kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "dotnet test --filter WorkerContextArtifacts",
        workingDirectory,
        0,
        """
        Changed files: src/Context.cs, tests/ContextTests.cs
        Behavior changes: context bundles summarize prior work before full evidence
        Verification result: passed focused tests
        Risks: none reported
        """,
        string.Empty,
        DateTimeOffset.UtcNow,
        "Model fit: OpenAI/gpt-5.5 - adequate - focused context bundle implementation."));

    var contextDirectory = WorkerContextArtifacts.Write(goal, currentTask, workingDirectory);

    var manifest = File.ReadAllText(Path.Combine(contextDirectory, "manifest.md"));
    var digest = File.ReadAllText(Path.Combine(contextDirectory, "digest.md"));
    var summaries = File.ReadAllText(Path.Combine(contextDirectory, "prior-task-summaries.md"));
    Assert.Contains(manifest, text => text.Contains("## Role Artifact Priorities", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains(expectedPrimaryPriority, StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains(expectedSecondaryPriority, StringComparison.Ordinal));
    Assert.Contains(digest, text => text.Contains("## Role Artifact Priorities", StringComparison.Ordinal));
    Assert.Contains(digest, text => text.Contains(expectedPrimaryPriority, StringComparison.Ordinal));
    Assert.Contains(summaries, text => text.Contains("Changed files: src/Context.cs, tests/ContextTests.cs", StringComparison.Ordinal));
    Assert.Contains(summaries, text => text.Contains("Behavior changes: context bundles summarize prior work before full evidence", StringComparison.Ordinal));
    Assert.Contains(summaries, text => text.Contains("Verification: `dotnet test --filter WorkerContextArtifacts`", StringComparison.Ordinal));
    Assert.Contains(summaries, text => text.Contains("Verification result: passed focused tests", StringComparison.Ordinal));
    Assert.Contains(summaries, text => text.Contains("Risks: none reported", StringComparison.Ordinal));
    Assert.Contains(summaries, text => text.Contains("Model fit: OpenAI/gpt-5.5 - adequate - focused context bundle implementation.", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_late_file_access_subscription_prompt_stays_below_large_paid_threshold")]
    public void WorkerProfileDispatcherLateFileAccessSubscriptionPromptStaysBelowLargePaidThreshold()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var priorTasks = Enumerable.Range(1, 4)
        .Select(index => new TaskSpec(TaskId.New(), $"Implement prior pipeline slice {index}.", AgentRole.Developer))
        .ToList();
    var currentTask = new TaskSpec(
        TaskId.New(),
        "Implement context digest support across API, CLI, dashboard, worker, tests, and docs with regression tests.",
        AgentRole.Developer);
    var goal = kernel.CreateGoal("Reduce late pipeline subscription prompt size.", [.. priorTasks, currentTask]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly);
    kernel.ActivateGoal(goal.Id, [agent]);
    var largePriorEvidence = $"prior-output-head {new string('p', 8000)} prior-output-tail";
    foreach (var priorTask in priorTasks)
    {
        kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
            "dotnet test",
            workingDirectory,
            0,
            largePriorEvidence,
            string.Empty,
            DateTimeOffset.UtcNow));
    }

    var result = WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        currentTask,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        DateTimeOffset.UtcNow);

    var prompt = File.ReadAllText(result.PromptPath);
    Assert.True(currentTask.LastDispatch!.PromptCharacterCount <= PaidPromptThresholds.PromptThreshold(
        currentTask.LastDispatch.TaskComplexity,
        currentTask.LastDispatch.UsesComplexModel));
    Assert.Contains(prompt, text => text.Contains("Read prior-task-summaries.md first", StringComparison.Ordinal));
    Assert.True(prompt.IndexOf("prior-task-summaries.md", StringComparison.Ordinal) < prompt.IndexOf("prior-task-evidence.md", StringComparison.Ordinal));
    Assert.True(!prompt.Contains("prior-output-head", StringComparison.Ordinal));
    Assert.True(!prompt.Contains("prior-output-tail", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_no_handoff_file_when_no_prior_completed_tasks")]
    public void WorkerProfileDispatcherNoHandoffFileWhenNoPriorCompletedTasks()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var kernel = new AgentOrchestratorKernel();
    var onlyTask = new TaskSpec(TaskId.New(), "First and only task.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Single task goal", [onlyTask]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var profile = new WorkerProfile("codex", "codex exec --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})");

    WorkerProfileDispatcher.PrepareTask(kernel, goal, onlyTask, profile, Path.Combine(root, "prompts"), workingDirectory, DateTimeOffset.UtcNow);

    Assert.False(File.Exists(Path.Combine(workingDirectory, ".orchestrator-handoff.md")));
    Assert.True(File.Exists(Path.Combine(workingDirectory, ".orchestrator-context", goal.Id.Value, "manifest.md")));
}

    private static TaskDispatchRecord ReopenTaskWithRecoverableDispatchLimit(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task)
{
    var previousDispatch = new TaskDispatchRecord(
        "previous-worker",
        "previous command",
        "C:\\repo",
        DateTimeOffset.Parse("2026-06-01T15:00:00Z"),
        "OpenAI",
        "gpt-5.5",
        "high",
        TaskComplexity.Simple,
        123);
    kernel.RecordTaskDispatch(goal.Id, task.Id, previousDispatch);
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        previousDispatch.Command,
        previousDispatch.WorkingDirectory,
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 4:58 PM.",
        DateTimeOffset.Parse("2026-06-01T15:01:00Z")));
    return previousDispatch;
}

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, TaskProcessRecord Process) CreateCompletedGoalWorktreeDispatch(
        string root,
        AgentRole role,
        string standardOutput,
        string standardError,
        IClock clock,
        Action<string>? mutateWorktree = null,
        string? taskDescription = null,
        string? verificationPlan = null)
{
    var kernel = new AgentOrchestratorKernel();
    var taskSpec = new TaskSpec(TaskId.New(), taskDescription ?? $"{role} task.", role, verificationPlan);
    var goal = kernel.CreateGoal("Dispatch evidence goal", [taskSpec]);
    var agent = new AgentDefinition(
        new AgentId(role.ToString().ToLowerInvariant()),
        role.ToString(),
        role,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);

    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    mutateWorktree?.Invoke(worktree);

    var logs = Path.Combine(root, "logs");
    Directory.CreateDirectory(logs);
    var stdout = Path.Combine(logs, $"{role}.out.log");
    var stderr = Path.Combine(logs, $"{role}.err.log");
    var exit = Path.Combine(logs, $"{role}.exit.txt");
    File.WriteAllText(stdout, standardOutput);
    File.WriteAllText(stderr, standardError);
    File.WriteAllText(exit, "0");

    var task = goal.Tasks.Single(candidate => candidate.RequiredRole == role);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", worktree, clock.UtcNow));
    var process = new TaskProcessRecord(999999, "codex exec prompt", worktree, stdout, stderr, exit, clock.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    return (kernel, goal, task, process);
}

    private static void WriteHeartbeat(
    TaskProcessRecord process,
    DateTimeOffset lastObservedAt,
    DateTimeOffset lastProgressAt,
    string state,
    long stdoutBytes,
    long stderrBytes)
{
    File.WriteAllText(
        BackgroundDispatchRunner.GetHeartbeatPath(process),
        "{" +
        "\"pid\":999999," +
        "\"childPid\":888888," +
        $"\"startedAt\":\"{process.StartedAt:O}\"," +
        $"\"lastObservedAt\":\"{lastObservedAt:O}\"," +
        $"\"lastProgressAt\":\"{lastProgressAt:O}\"," +
        $"\"state\":\"{state}\"," +
        $"\"stdoutBytes\":{stdoutBytes}," +
        $"\"stderrBytes\":{stderrBytes}," +
        "\"exitFileExists\":false" +
        "}");
}

    private static string CreateSeededDispatchRepository()
{
    var root = CreateTempDirectory();
    RunGit(root, ["init"], DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    RunGit(root, ["config", "user.email", "tests@example.com"], DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    RunGit(root, ["config", "user.name", "Dispatch Tests"], DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
    RunGit(root, ["add", "-A"], DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    RunGit(root, ["commit", "-m", "Seed"], DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    return root;
}

    private static void RunGit(string workingDirectory, string[] arguments, DateTimeOffset commitTime)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "git",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = workingDirectory
    };
    startInfo.Environment["GIT_AUTHOR_DATE"] = commitTime.ToString("O");
    startInfo.Environment["GIT_COMMITTER_DATE"] = commitTime.ToString("O");

    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start git.");
    process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit(60000);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
    }
}

    private static void WaitForExitFile(string path)
{
    var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (!File.Exists(path) && DateTimeOffset.UtcNow < deadline)
    {
        Thread.Sleep(50);
    }

    if (!File.Exists(path))
    {
        throw new TimeoutException($"Timed out waiting for exit file '{path}'.");
    }
}

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}

