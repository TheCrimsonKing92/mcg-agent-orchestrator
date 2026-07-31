using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Text.Json;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class WorkerDispatchTestsModelSelection : WorkerDispatchTestSupport
{
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_falls_back_and_persists_when_assigned_agent_is_missing")]
    public void WorkerProfileDispatcherFallsBackAndPersistsWhenAssignedAgentIsMissing()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Plan the fallback repair.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Repair stale agent pin", [task]);
        var oldAgent = SubscriptionPlannerAgent("old-planner", "Old Planner");
        var newAgent = SubscriptionPlannerAgent("new-planner", "New Planner");
        kernel.ActivateGoal(goal.Id, [oldAgent]);

        var stderr = CaptureConsoleError(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
            kernel,
            goal,
            task,
            [newAgent],
            DispatchTestProfiles(),
            Path.Combine(root, "prompts"),
            root,
            DateTimeOffset.UtcNow,
            sandboxOptions: new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget)));

        Assert.Equal(newAgent.Id, task.AssignedAgentId);
        Assert.Equal("claude-cli", task.LastDispatch!.WorkerName);
        Assert.Contains("Warning: assigned agent 'old-planner'", stderr);
        Assert.Contains("Planner", stderr);
        Assert.Contains("new-planner", stderr);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskRedelegated &&
            evt.Message.Contains("old-planner", StringComparison.Ordinal) &&
            evt.Message.Contains("new-planner", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_uses_valid_assigned_agent_without_warning")]
    public void WorkerProfileDispatcherUsesValidAssignedAgentWithoutWarning()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Plan direct assignment.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Keep valid agent pin", [task]);
        var agent = SubscriptionPlannerAgent("planner", "Planner");
        kernel.ActivateGoal(goal.Id, [agent]);

        var stderr = CaptureConsoleError(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
            kernel,
            goal,
            task,
            [agent],
            DispatchTestProfiles(),
            Path.Combine(root, "prompts"),
            root,
            DateTimeOffset.UtcNow,
            sandboxOptions: new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget)));

        Assert.Equal(agent.Id, task.AssignedAgentId);
        Assert.Equal("claude-cli", task.LastDispatch!.WorkerName);
        Assert.DoesNotContain("Warning:", stderr);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskRedelegated);
    }

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_replace_midflight_role_agent_dispatches_with_repaired_assignment")]
    public void WorkerProfileDispatcherReplaceMidflightRoleAgentDispatchesWithRepairedAssignment()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Plan after provider replacement.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Replace role agent mid-flight", [task]);
        var oldAgent = SubscriptionPlannerAgent("old-planner", "Old Planner");
        var agents = new AgentCatalog([oldAgent]).Agents;
        kernel.ActivateGoal(goal.Id, agents);
        var newAgent = SubscriptionPlannerAgent("new-planner", "New Planner");
        agents = new AgentCatalog(agents).UpsertRole(newAgent).Agents;

        WorkerProfileDispatcher.PrepareSubscriptionTask(
            kernel,
            goal,
            task,
            agents,
            DispatchTestProfiles(),
            Path.Combine(root, "prompts"),
            root,
            DateTimeOffset.UtcNow,
            sandboxOptions: new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget));

        Assert.Equal(newAgent.Id, task.AssignedAgentId);
        Assert.Equal("claude-cli", task.LastDispatch!.WorkerName);
        Assert.Equal("Anthropic", task.LastDispatch.ProviderName);
    }

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
    Assert.Contains("agent-cli --prompt", preparation.Command, StringComparison.Ordinal);
    Assert.Contains("--role Developer", preparation.Command, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_resolves_subscription_model_name_matrix")]
    public void WorkerProfileDispatcherResolvesSubscriptionModelNameMatrix()
{
    var baseModel = new ModelProfile("OpenAI", "api-base", ModelCapability.Text, SubscriptionMode.ApiKey, "low");
    var complexModel = new ModelProfile("OpenAI", "api-complex", ModelCapability.Text, SubscriptionMode.ApiKey, "high");
    var agent = new AgentDefinition(
        new AgentId("sentinel-developer"),
        "Sentinel Developer",
        AgentRole.Developer,
        baseModel,
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "sub-alias", "medium"),
        ComplexModel: complexModel);
    var agentWithoutSubscription = agent with { Subscription = null };
    var cases = new[]
    {
        new
        {
            Name = "complex subscription launch profile",
            Agent = agent,
            Selection = new WorkerProfileDispatcher.SubscriptionModelSelection(
                TaskComplexity.Complex,
                complexModel,
                UsesComplexModel: true,
                UsesSubscriptionLaunchProfile: true),
            Expected = "sub-alias"
        },
        new
        {
            Name = "simple subscription launch profile",
            Agent = agent,
            Selection = new WorkerProfileDispatcher.SubscriptionModelSelection(
                TaskComplexity.Simple,
                baseModel,
                UsesComplexModel: false,
                UsesSubscriptionLaunchProfile: true),
            Expected = "sub-alias"
        },
        new
        {
            Name = "complex non-subscription launch profile",
            Agent = agent,
            Selection = new WorkerProfileDispatcher.SubscriptionModelSelection(
                TaskComplexity.Complex,
                complexModel,
                UsesComplexModel: true,
                UsesSubscriptionLaunchProfile: false),
            Expected = "api-complex"
        },
        new
        {
            Name = "simple non-subscription launch profile",
            Agent = agent,
            Selection = new WorkerProfileDispatcher.SubscriptionModelSelection(
                TaskComplexity.Simple,
                baseModel,
                UsesComplexModel: false,
                UsesSubscriptionLaunchProfile: false),
            Expected = "api-base"
        },
        new
        {
            Name = "null subscription launch profile",
            Agent = agentWithoutSubscription,
            Selection = new WorkerProfileDispatcher.SubscriptionModelSelection(
                TaskComplexity.Complex,
                complexModel,
                UsesComplexModel: true,
                UsesSubscriptionLaunchProfile: true),
            Expected = "api-complex"
        }
    };

    foreach (var testCase in cases)
    {
        var actual = WorkerProfileDispatcher.ResolveEffectiveSubscriptionModelName(testCase.Agent, testCase.Selection);

        if (testCase.Name == "complex subscription launch profile")
        {
            Assert.True(
                actual == testCase.Expected,
                "pre-fix code returned 'api-complex' for complex subscription launch profiles; fixed code returns 'sub-alias'.");
        }
        else
        {
            Assert.Equal(testCase.Expected, actual);
        }
    }
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_renders_latest_retry_feedback_into_fresh_prompt_before_dispatch")]
    public void WorkerProfileDispatcherRendersLatestRetryFeedbackIntoFreshPromptBeforeDispatch()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var kernel = new AgentOrchestratorKernel(new TestClock(DateTimeOffset.Parse("2026-06-28T12:00:00Z")));
    var developer = new TaskSpec(TaskId.New(), "Implement retry prompt regeneration.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Fix stale retry dispatch prompts", [developer]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    kernel.RetryTask(goal.Id, developer.Id, "old retry feedback that should not be the current blocker");
    var latestFeedback = "latest retry feedback: fix goals subscribe routing before redispatch";
    kernel.RetryTask(goal.Id, developer.Id, latestFeedback);
    var profile = new WorkerProfile("codex-cli", "codex exec --cd {workingDirectory}");

    var result = WorkerProfileDispatcher.PrepareTask(
        kernel,
        goal,
        developer,
        profile,
        promptRoot,
        workingDirectory,
        DateTimeOffset.Parse("2026-06-28T12:01:00Z"),
        providerName: "OpenAI",
        modelName: "gpt-5.5");

    var prompt = File.ReadAllText(result.PromptPath);
    Assert.Contains(latestFeedback, prompt, StringComparison.Ordinal);
    Assert.Equal(result.PromptPath, developer.LastDispatch!.PromptPath);
    Assert.DoesNotContain(result.PromptPath, developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.True(File.Exists(developer.LastDispatch.PromptPath));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_repeated_dispatches_do_not_reuse_same_prompt_path")]
    public void WorkerProfileDispatcherRepeatedDispatchesDoNotReuseSamePromptPath()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var kernel = new AgentOrchestratorKernel(new TestClock(DateTimeOffset.Parse("2026-06-28T13:00:00Z")));
    var developer = new TaskSpec(TaskId.New(), "Implement retry prompt regeneration.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Fix stale retry dispatch prompts", [developer]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var profile = new WorkerProfile("codex-cli", "codex exec --cd {workingDirectory}");

    var first = WorkerProfileDispatcher.PrepareTask(
        kernel,
        goal,
        developer,
        profile,
        promptRoot,
        workingDirectory,
        DateTimeOffset.Parse("2026-06-28T13:01:00Z"),
        providerName: "OpenAI",
        modelName: "gpt-5.5");
    kernel.ReportTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Failed, "Synthetic first dispatch failed before retry.");
    kernel.RetryTask(goal.Id, developer.Id, "latest retry feedback for second prompt");
    var second = WorkerProfileDispatcher.PrepareTask(
        kernel,
        goal,
        developer,
        profile,
        promptRoot,
        workingDirectory,
        DateTimeOffset.Parse("2026-06-28T13:01:00Z"),
        providerName: "OpenAI",
        modelName: "gpt-5.5");

    Assert.NotEqual(first.PromptPath, second.PromptPath);
    Assert.True(File.Exists(first.PromptPath));
    Assert.True(File.Exists(second.PromptPath));
    Assert.Equal(second.PromptPath, developer.LastDispatch!.PromptPath);
    Assert.Contains("latest retry feedback for second prompt", File.ReadAllText(second.PromptPath), StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_rejects_over_limit_subscription_prompt_before_dispatch_mutation")]
    public void WorkerProfileDispatcherRejectsOverLimitSubscriptionPromptBeforeDispatchMutation()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Reject irreducible over-budget subscription prompt",
        [new TaskSpec(TaskId.New(), "Research prompt budget behavior", AgentRole.Researcher)]);
    kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
        "Fixed refined spec content " + new string('x', 20000),
        ["Prompt budget failure is reported before dispatch."],
        VerificationClass.TestVerifiable,
        [],
        []));
    var agent = new AgentDefinition(
        new AgentId("ollama-researcher"),
        "Ollama Researcher",
        AgentRole.Researcher,
        new ModelProfile("Ollama", "qwen3:8b", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();

    var ex = Assert.ThrowsAny<WorkerPromptInputBudgetExceededException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt,
        new DispatchModelOverride("qwen-code-cli", "qwen3:8b", null)));

    Assert.Equal(task.Id, ex.TaskId);
    Assert.False(Directory.Exists(promptRoot));
    Assert.Null(task.LastDispatch);
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
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
    CompleteResearcherAndPlannerArtifacts(kernel, goal);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var previousDispatch = ReopenTaskWithRecoverableDispatchLimit(kernel, goal, task);
    var profile = WorkerProfileCatalog.Default().GetRequired("codex-cli");

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareTask(
        kernel,
        goal,
        task,
        profile,
        promptRoot,
        workingDirectory,
        dispatchedAt));

    Assert.Contains("codex-cli", ex.Message, StringComparison.Ordinal);
    Assert.Contains("{subscriptionModelName}", ex.Message, StringComparison.Ordinal);
    Assert.Contains("{subscriptionReasoningEffort}", ex.Message, StringComparison.Ordinal);
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
    CompleteResearcherAndPlannerArtifacts(kernel, goal);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("custom-codex", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --api-reasoning {apiReasoningEffort} --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})")
    ]);
    var contextDirectory = Path.Combine(workingDirectory, ".orchestrator-context", goal.Id.Value);
    new WorkerArtifactWriter().Write(goal, task, workingDirectory);
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
    Assert.Contains("--model 'gpt-5.3-codex'", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("model_reasoning_effort='medium'", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--api-reasoning 'high'", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains($"--cd '{workingDirectory}'", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Equal("OpenAI", task.LastDispatch.ProviderName);
    Assert.Equal("gpt-5.3-codex", task.LastDispatch.ModelName);
    Assert.Equal("medium", task.LastDispatch.ReasoningEffort);
    Assert.Equal(TaskComplexity.Simple, task.LastDispatch.TaskComplexity);
    Assert.True(estimatedPromptCharacters < expectedPromptCharacters);
    var prompt = File.ReadAllText(dispatchResult.PromptPath);
    Assert.Equal(prompt.Length, task.LastDispatch.PromptCharacterCount);
    Assert.Equal(expectedPromptCharacters, task.LastDispatch.PromptCharacterCount);
    Assert.Contains("Model fit: OpenAI/gpt-5.3-codex - adequate|overkill|underpowered - <task shape> - <short reason>", prompt, StringComparison.Ordinal);
    Assert.Contains("WORKER_RESULT", prompt, StringComparison.Ordinal);
    var preflightPath = Path.Combine(contextDirectory, "subscription-preflight.md");
    Assert.True(File.Exists(preflightPath));
    Assert.Contains("ready: profile, sandbox, worktree, and retry state passed deterministic preflight", File.ReadAllText(preflightPath), StringComparison.Ordinal);
    Assert.Contains("subscription-preflight.md", File.ReadAllText(Path.Combine(contextDirectory, "digest.md")), StringComparison.Ordinal);
    Assert.Contains("deterministic profile, sandbox, worktree", File.ReadAllText(Path.Combine(contextDirectory, "manifest.md")), StringComparison.Ordinal);
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
    kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
        "Plan architecture work",
        ["Profile dispatch metadata is recorded from the selected subscription model."],
        VerificationClass.TestVerifiable,
        [],
        []));
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
    // Subscription launch profiles always pin the configured alias; complexity only changes API-side model/effort.
    Assert.Contains("--model 'gpt-5-mini-codex'", task.LastDispatch!.Command, StringComparison.Ordinal);
    Assert.Contains("model_reasoning_effort='high'", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Equal("OpenAI", task.LastDispatch.ProviderName);
    // Subscription launch profiles always pin the configured alias; complexity only changes API-side model/effort.
    Assert.Equal("gpt-5-mini-codex", task.LastDispatch.ModelName);
    Assert.Equal("high", task.LastDispatch.ReasoningEffort);
    Assert.Equal("base", task.LastDispatch.ReasoningEffortReason);
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
    CompleteResearcherAndPlannerArtifacts(kernel, goal);
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

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt.AddMinutes(1)));

    Assert.Contains("status is Running", ex.Message, StringComparison.Ordinal);
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

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareTask(
        kernel,
        goal,
        task,
        profile,
        Path.Combine(root, "prompts"),
        root,
        DateTimeOffset.UtcNow));

    Assert.Contains("already has passing verification", ex.Message, StringComparison.Ordinal);
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
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --api-model {apiModelName} --api-reasoning {apiReasoningEffort} --complexity {taskComplexity} --sandbox workspace-write --cd {workingDirectory}")
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

    Assert.Contains("--model 'gpt-5-mini-codex'", simpleTask.LastDispatch!.Command, StringComparison.Ordinal);
    Assert.Contains("model_reasoning_effort='low'", simpleTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--api-model 'gpt-5-mini'", simpleTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--api-reasoning 'low'", simpleTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--complexity 'Simple'", simpleTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Equal("OpenAI", simpleTask.LastDispatch.ProviderName);
    Assert.Equal("gpt-5-mini-codex", simpleTask.LastDispatch.ModelName);
    Assert.Equal("low", simpleTask.LastDispatch.ReasoningEffort);
    Assert.Equal(TaskComplexity.Simple, simpleTask.LastDispatch.TaskComplexity);
    // Subscription launch profiles always pin the configured alias; complexity only changes API-side model/effort.
    Assert.Contains("--model 'gpt-5-mini-codex'", complexTask.LastDispatch!.Command, StringComparison.Ordinal);
    Assert.Contains("model_reasoning_effort='high'", complexTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--api-model 'gpt-5.5'", complexTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--api-reasoning 'high'", complexTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--complexity 'Complex'", complexTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Equal("OpenAI", complexTask.LastDispatch.ProviderName);
    // Subscription launch profiles always pin the configured alias; complexity only changes API-side model/effort.
    Assert.Equal("gpt-5-mini-codex", complexTask.LastDispatch.ModelName);
    Assert.Equal("high", complexTask.LastDispatch.ReasoningEffort);
    Assert.Equal(TaskComplexity.Complex, complexTask.LastDispatch.TaskComplexity);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_selects_reasoning_effort_from_policy_signals")]
    public void WorkerProfileDispatcherSelectsReasoningEffortFromPolicySignals()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var dispatchedAt = DateTimeOffset.Parse("2026-07-14T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var agent = new AgentDefinition(
        new AgentId("adaptive-developer"),
        "Adaptive Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "medium"),
        ReasoningEffortPolicy: new ReasoningEffortPolicy(RetryDepthThreshold: 3, RetryDepthEffort: "high", ComplexityEffort: "high", ClassFindingEffort: "high"));
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --reason {reasoningEffortSelectionReason} --sandbox workspace-write --cd {workingDirectory}")
    ]);
    var simpleGoal = kernel.CreateGoal("Fix a dashboard typo", [new TaskSpec(TaskId.New(), "Update the button label copy.", AgentRole.Developer)]);
    kernel.ActivateGoal(simpleGoal.Id, [agent]);
    var complexGoal = kernel.CreateGoal("Implement high-risk multi-scope persistence migration across every store", [new TaskSpec(TaskId.New(), "Implement the scoped slice.", AgentRole.Developer)]);
    kernel.ActivateGoal(complexGoal.Id, [agent]);
    var retryGoal = kernel.CreateGoal("Fix a dashboard typo after review", [new TaskSpec(TaskId.New(), "Update the button label copy.", AgentRole.Developer)]);
    kernel.ActivateGoal(retryGoal.Id, [agent]);
    var retryTask = retryGoal.Tasks.Single();
    kernel.RecordCriterionRetryFeedback(retryGoal.Id, retryTask.Id, ["first miss"]);
    kernel.RecordCriterionRetryFeedback(retryGoal.Id, retryTask.Id, ["second miss"]);
    var classGoal = kernel.CreateGoal("Fix a dashboard typo after class review", [new TaskSpec(TaskId.New(), "Update the button label copy.", AgentRole.Developer)]);
    kernel.ActivateGoal(classGoal.Id, [agent]);
    var classTask = classGoal.Tasks.Single();
    kernel.RecordCriterionRetryFeedback(classGoal.Id, classTask.Id, ["The fix missed every call site in the persistence path."]);

    WorkerProfileDispatcher.PrepareSubscriptionTask(kernel, simpleGoal, simpleGoal.Tasks.Single(), [agent], profiles, promptRoot, workingDirectory, dispatchedAt);
    WorkerProfileDispatcher.PrepareSubscriptionTask(kernel, complexGoal, complexGoal.Tasks.Single(), [agent], profiles, promptRoot, workingDirectory, dispatchedAt);
    WorkerProfileDispatcher.PrepareSubscriptionTask(kernel, retryGoal, retryTask, [agent], profiles, promptRoot, workingDirectory, dispatchedAt);
    WorkerProfileDispatcher.PrepareSubscriptionTask(kernel, classGoal, classTask, [agent], profiles, promptRoot, workingDirectory, dispatchedAt);

    Assert.Equal("medium", simpleGoal.Tasks.Single().LastDispatch!.ReasoningEffort);
    Assert.Equal("base", simpleGoal.Tasks.Single().LastDispatch!.ReasoningEffortReason);
    Assert.Contains("--reason 'base'", simpleGoal.Tasks.Single().LastDispatch!.Command, StringComparison.Ordinal);
    Assert.Equal("high", complexGoal.Tasks.Single().LastDispatch!.ReasoningEffort);
    Assert.Equal("complexity", complexGoal.Tasks.Single().LastDispatch!.ReasoningEffortReason);
    Assert.Contains("--reason 'complexity'", complexGoal.Tasks.Single().LastDispatch!.Command, StringComparison.Ordinal);
    Assert.Equal("high", retryTask.LastDispatch!.ReasoningEffort);
    Assert.Equal("retry-depth", retryTask.LastDispatch!.ReasoningEffortReason);
    Assert.Contains("--reason 'retry-depth'", retryTask.LastDispatch!.Command, StringComparison.Ordinal);
    Assert.Equal("high", classTask.LastDispatch!.ReasoningEffort);
    Assert.Equal("class-finding", classTask.LastDispatch!.ReasoningEffortReason);
    Assert.Contains("--reason 'class-finding'", classTask.LastDispatch!.Command, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_persists_reasoning_effort_reason_in_snapshot")]
    public void WorkerProfileDispatcherPersistsReasoningEffortReasonInSnapshot()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var agent = new AgentDefinition(
        new AgentId("adaptive-developer"),
        "Adaptive Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "medium"));
    var goal = kernel.CreateGoal("Implement high-risk multi-scope persistence migration", [new TaskSpec(TaskId.New(), "Implement the scoped slice.", AgentRole.Developer)]);
    kernel.RecordGoalPolicyDecision(
        goal.Id,
        "Intake pipeline decision (auto): developer-reviewer; reasons: high-risk objective needs pre-acceptance review; risk labels: complex, high-risk, multi-scope.");
    kernel.ActivateGoal(goal.Id, [agent]);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        goal.Tasks.Single(),
        [agent],
        WorkerProfileCatalog.Default(),
        Path.Combine(root, "prompts"),
        workingDirectory,
        DateTimeOffset.Parse("2026-07-14T12:00:00Z"));

    var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
    var restoredTask = restored.GetGoal(goal.Id).Tasks.Single();
    Assert.Equal("high", restoredTask.LastDispatch!.ReasoningEffort);
    Assert.Equal("complexity", restoredTask.LastDispatch!.ReasoningEffortReason);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_small_developer_task_can_route_to_codex_spark_provider_by_typed_selection")]
    public void WorkerProfileDispatcherSmallDeveloperTaskCanRouteToCodexSparkProviderByTypedSelection()
    {
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Fix a typo",
        [new TaskSpec(TaskId.New(), "Update one label.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("spark-developer"),
        "Spark Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-spark", "gpt-5.3-codex-spark", "low"));
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
        DateTimeOffset.Parse("2026-06-02T12:00:00Z"));

    var provider = WorkerProviderCatalog.Default().ResolveProfile(task.LastDispatch!.WorkerName);
    Assert.Equal(ProviderKind.OpenAICodexSpark, provider.Identity.Kind);
    Assert.Equal(ProviderKind.OpenAICodexSpark, task.LastDispatch.WorkerProviderKind);
    Assert.Equal(TaskComplexity.Simple, task.LastDispatch.TaskComplexity);
    Assert.Equal("gpt-5.3-codex-spark", task.LastDispatch.ModelName);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_routes_small_default_developer_task_to_codex_spark")]
    public void WorkerProfileDispatcherRoutesSmallDefaultDeveloperTaskToCodexSpark()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Fix a typo",
        [new TaskSpec(TaskId.New(), "Update one label.", AgentRole.Developer)]);
    var agents = AgentCatalog.Default().Agents;
    kernel.RecordGoalPolicyDecision(
        goal.Id,
        "Intake pipeline decision (auto): developer-only; reasons: simple code objective; risk labels: small-task, low-risk.");
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        DateTimeOffset.Parse("2026-07-17T12:00:00Z"));

    Assert.Equal("codex-spark", task.LastDispatch!.WorkerName);
    Assert.Equal(ProviderKind.OpenAICodexSpark, task.LastDispatch.WorkerProviderKind);
    Assert.Equal("OpenAI", task.LastDispatch.ProviderName);
    Assert.Equal("gpt-5.3-codex-spark", task.LastDispatch.ModelName);
    Assert.Equal("codex-spark", task.LastDispatch.DispatchLane);
    Assert.Contains("cheap-lane: Developer small-task", task.LastDispatch.ModelSelectionReason, StringComparison.Ordinal);
    Assert.Contains("--model 'gpt-5.3-codex-spark'", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("model_reasoning_effort='low'", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskDispatchRecorded &&
        evt.Message.Contains("lane=codex-spark", StringComparison.Ordinal) &&
        evt.Message.Contains("cheap-lane: Developer small-task", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_ready_batch_routes_small_default_developer_task_to_codex_spark")]
    public void WorkerProfileDispatcherReadyBatchRoutesSmallDefaultDeveloperTaskToCodexSpark()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Fix a typo",
        [new TaskSpec(TaskId.New(), "Update one label.", AgentRole.Developer)]);
    var agents = AgentCatalog.Default().Agents;
    kernel.RecordGoalPolicyDecision(
        goal.Id,
        "Intake pipeline decision (auto): developer-only; reasons: simple code objective; risk labels: small-task, low-risk.");
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();

    var batch = WorkerProfileDispatcher.PrepareSubscriptionReadyBatch(
        kernel,
        goal,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        DateTimeOffset.Parse("2026-07-17T12:00:00Z"));

    Assert.Empty(batch.Blocked);
    Assert.Single(batch.Dispatches);
    Assert.Equal(task.Id, batch.Dispatches.Single().Task.Id);
    Assert.Equal("codex-spark", task.LastDispatch!.WorkerName);
    Assert.Equal(ProviderKind.OpenAICodexSpark, task.LastDispatch.WorkerProviderKind);
    Assert.Equal("gpt-5.3-codex-spark", task.LastDispatch.ModelName);
    Assert.Equal("codex-spark", task.LastDispatch.DispatchLane);
    Assert.Contains("cheap-lane: Developer small-task", task.LastDispatch.ModelSelectionReason, StringComparison.Ordinal);
    Assert.Contains("--model 'gpt-5.3-codex-spark'", task.LastDispatch.Command, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_does_not_override_constrained_developer_alias_with_cheap_lane")]
    public void WorkerProfileDispatcherDoesNotOverrideConstrainedDeveloperAliasWithCheapLane()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Fix a typo",
        [new TaskSpec(TaskId.New(), "Update one label.", AgentRole.Developer)]);
    var agent = AgentCatalog.Default().GetRequired(AgentRole.Developer) with
    {
        Subscription = new SubscriptionLaunchProfile(
            "codex-cli",
            AgentCatalog.OpenAiSolSubscriptionModelAlias,
            AgentCatalog.RoutineSubscriptionReasoningEffort),
        IsProviderRoutingConstrained = true
    };
    kernel.RecordGoalPolicyDecision(
        goal.Id,
        "Intake pipeline decision (auto): developer-only; reasons: simple code objective; risk labels: small-task, low-risk.");
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
        DateTimeOffset.Parse("2026-07-29T12:00:00Z"));

    Assert.Equal("codex-cli", task.LastDispatch!.WorkerName);
    Assert.Equal("OpenAI", task.LastDispatch.ProviderName);
    Assert.Equal(AgentCatalog.OpenAiSolSubscriptionModelAlias, task.LastDispatch.ModelName);
    Assert.Equal("codex-cli", task.LastDispatch.DispatchLane);
    Assert.DoesNotContain("codex-spark", task.LastDispatch.Command, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("provider-constrained: Developer remains on OpenAI", task.LastDispatch.ModelSelectionReason, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_keeps_complex_high_risk_developer_on_default_lane")]
    public void WorkerProfileDispatcherKeepsComplexHighRiskDeveloperOnDefaultLane()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Implement high-risk multi-scope persistence migration",
        [new TaskSpec(TaskId.New(), "Build an end-to-end distributed integration with horizontal scaling.", AgentRole.Developer)]);
    var agents = AgentCatalog.Default().Agents;
    kernel.RecordGoalPolicyDecision(
        goal.Id,
        "Intake pipeline decision (auto): developer-reviewer; reasons: high-risk objective needs pre-acceptance review; risk labels: complex, high-risk, multi-scope.");
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        DateTimeOffset.Parse("2026-07-17T12:00:00Z"));

    Assert.Equal("codex-cli", task.LastDispatch!.WorkerName);
    // Subscription launch profiles always pin the configured alias; complexity only changes API-side model/effort.
    Assert.Equal(AgentCatalog.OpenAiSubscriptionModelAlias, task.LastDispatch.ModelName);
    Assert.Equal(TaskComplexity.Complex, task.LastDispatch.TaskComplexity);
    Assert.Equal("codex-cli", task.LastDispatch.DispatchLane);
    Assert.DoesNotContain("gpt-5.3-codex-spark", task.LastDispatch.Command, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_routes_mechanical_retry_to_codex_spark_even_when_complex")]
    public void WorkerProfileDispatcherRoutesMechanicalRetryToCodexSparkEvenWhenComplex()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Implement high-risk multi-scope persistence migration",
        [new TaskSpec(TaskId.New(), "Build an end-to-end distributed integration with horizontal scaling.", AgentRole.Developer)]);
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    kernel.RetryTask(goal.Id, task.Id, "Mechanical retry: rerun named commands and quote receipts; commit nothing.", retryRoundKind: RetryRoundKind.Mechanical);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        DateTimeOffset.Parse("2026-07-17T12:00:00Z"));

    Assert.Equal(TaskComplexity.Complex, task.LastDispatch!.TaskComplexity);
    Assert.Equal("codex-spark", task.LastDispatch.WorkerName);
    Assert.Equal("gpt-5.3-codex-spark", task.LastDispatch.ModelName);
    Assert.Equal("codex-spark", task.LastDispatch.DispatchLane);
    Assert.Contains("cheap-lane: Developer mechanical-retry", task.LastDispatch.ModelSelectionReason, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_routes_mechanical_tester_retry_to_codex_spark_even_when_complex")]
    public void WorkerProfileDispatcherRoutesMechanicalTesterRetryToCodexSparkEvenWhenComplex()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Implement high-risk multi-scope persistence migration",
        [new TaskSpec(
            TaskId.New(),
            "Build comprehensive production integration tests for API CLI dashboard provider subscription worker persistence state, schema migration, rollback, deadlock, and authorization behavior.",
            AgentRole.Tester)]);
    var agents = AgentCatalog.Default().Agents;
    kernel.RecordGoalPolicyDecision(
        goal.Id,
        "Intake pipeline decision (auto): developer-reviewer; reasons: high-risk objective needs pre-acceptance review; risk labels: complex, high-risk, multi-scope.");
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    kernel.RetryTask(goal.Id, task.Id, "Mechanical retry: rerun named commands and quote receipts; commit nothing.", retryRoundKind: RetryRoundKind.Mechanical);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        DateTimeOffset.Parse("2026-07-17T12:00:00Z"));

    Assert.Equal(TaskComplexity.Complex, task.LastDispatch!.TaskComplexity);
    Assert.Equal("codex-spark", task.LastDispatch.WorkerName);
    Assert.Equal("gpt-5.3-codex-spark", task.LastDispatch.ModelName);
    Assert.Equal("codex-spark", task.LastDispatch.DispatchLane);
    Assert.Contains("cheap-lane: Tester mechanical-retry", task.LastDispatch.ModelSelectionReason, StringComparison.Ordinal);
    Assert.Contains("--model 'gpt-5.3-codex-spark'", task.LastDispatch.Command, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_falls_back_to_default_lane_when_codex_spark_is_unconfigured")]
    public void WorkerProfileDispatcherFallsBackToDefaultLaneWhenCodexSparkIsUnconfigured()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Fix a typo",
        [new TaskSpec(TaskId.New(), "Update one label.", AgentRole.Developer)]);
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox workspace-write --cd {workingDirectory}")
    ]);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        agents,
        profiles,
        promptRoot,
        workingDirectory,
        DateTimeOffset.Parse("2026-07-17T12:00:00Z"));

    Assert.Equal("codex-cli", task.LastDispatch!.WorkerName);
    // Subscription launch profiles always pin the configured alias; fallback from spark still uses the configured default alias.
    Assert.Equal(AgentCatalog.OpenAiSubscriptionModelAlias, task.LastDispatch.ModelName);
    Assert.Equal("codex-cli", task.LastDispatch.DispatchLane);
    Assert.Contains("fallback-default-lane: spark unavailable", task.LastDispatch.ModelSelectionReason, StringComparison.Ordinal);
    // Subscription launch profiles always pin the configured alias; fallback from spark still uses the configured default alias.
    Assert.Contains($"--model '{AgentCatalog.OpenAiSubscriptionModelAlias}'", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain("gpt-5.3-codex-spark", task.LastDispatch.Command, StringComparison.Ordinal);
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
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --complexity {taskComplexity} --sandbox workspace-write --cd {workingDirectory}")
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

    Assert.Contains("--model 'gpt-5-mini-codex'", task.LastDispatch!.Command, StringComparison.Ordinal);
    Assert.Contains("model_reasoning_effort='low'", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--complexity 'Simple'", task.LastDispatch.Command, StringComparison.Ordinal);
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

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        Path.Combine(root, "prompts"),
        root,
        DateTimeOffset.UtcNow));

    Assert.Contains("already has passing verification", ex.Message, StringComparison.Ordinal);
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
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --api-model {apiModelName} --complexity {taskComplexity} --sandbox workspace-write --cd {workingDirectory}")
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

    Assert.Contains("--model 'gpt-5-mini-codex'", task.LastDispatch!.Command, StringComparison.Ordinal);
    Assert.Contains("--api-model 'gpt-5-mini'", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--complexity 'Complex'", task.LastDispatch.Command, StringComparison.Ordinal);
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
    CompleteResearcherAndPlannerArtifacts(kernel, goal);
    var profile = new WorkerProfile("codex-cli", "codex exec");

    var results = WorkerProfileDispatcher.PrepareReadyTasks(kernel, goal, profile, promptRoot, workingDirectory, dispatchedAt);

    Assert.Equal(1, results.Count);
    var result = results.Single();
    Assert.Equal(goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer).Id, result.Task.Id);
    Assert.True(File.Exists(result.PromptPath));
    var prompt = File.ReadAllText(result.PromptPath);
    Assert.Contains("Dispatch local subscription workers", prompt, StringComparison.Ordinal);
    Assert.Contains(result.Task.VerificationPlan!, prompt, StringComparison.Ordinal);
    Assert.True(result.Task.LastDispatch is not null);
    Assert.Equal("codex-cli", result.Task.LastDispatch!.WorkerName);
    Assert.Equal(workingDirectory, result.Task.LastDispatch.WorkingDirectory);
    Assert.Equal(dispatchedAt, result.Task.LastDispatch.DispatchedAt);
    Assert.DoesNotContain(result.PromptPath, result.Task.LastDispatch.Command, StringComparison.Ordinal);
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
    CompleteResearcherAndPlannerArtifacts(kernel, goal);
    var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
    var profile = new WorkerProfile("codex-cli", "codex exec --sandbox workspace-write --cd {workingDirectory}");

    var result = WorkerProfileDispatcher.PrepareTask(kernel, goal, task, profile, promptRoot, workingDirectory, DateTimeOffset.UtcNow);

    var prompt = File.ReadAllText(result.PromptPath);
    Assert.Contains($"Working directory, use absolute paths: {workingDirectory}", prompt, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_includes_current_branch_and_head_in_dispatched_prompt")]
    public void WorkerProfileDispatcherIncludesCurrentBranchAndHeadInDispatchedPrompt()
{
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var kernel = new AgentOrchestratorKernel(new TestClock(DateTimeOffset.Parse("2026-06-27T12:00:00Z")));
    var developer = new TaskSpec(TaskId.New(), "Implement retry prompt regeneration.", AgentRole.Developer);
    var tester = new TaskSpec(TaskId.New(), "Test retry prompt regeneration.", AgentRole.Tester);
    var reviewer = new TaskSpec(TaskId.New(), "Review retry prompt regeneration.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Fix retry prompt regeneration", [developer, tester, reviewer]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var branch = ReadGit(worktree, ["branch", "--show-current"]);
    var head = ReadGit(worktree, ["rev-parse", "HEAD"]);
    kernel.RetryTask(goal.Id, developer.Id, "latest developer retry feedback");
    var profile = new WorkerProfile("codex-cli", "codex exec --sandbox workspace-write --cd {workingDirectory}");

    var firstDispatch = WorkerProfileDispatcher.PrepareTask(
        kernel,
        goal,
        tester,
        profile,
        promptRoot,
        worktree,
        DateTimeOffset.Parse("2026-06-27T12:01:00Z"));

    var prompt = File.ReadAllText(firstDispatch.PromptPath);
    Assert.Contains("Current target context:", prompt, StringComparison.Ordinal);
    Assert.Contains($"- Branch: {branch}", prompt, StringComparison.Ordinal);
    Assert.Contains($"- HEAD commit: {head}", prompt, StringComparison.Ordinal);
    Assert.Contains("latest developer retry feedback", prompt, StringComparison.Ordinal);

    var reviewerDispatch = WorkerProfileDispatcher.PrepareTask(
        kernel,
        goal,
        reviewer,
        profile,
        promptRoot,
        worktree,
        DateTimeOffset.Parse("2026-06-27T12:01:30Z"));

    var reviewerPrompt = File.ReadAllText(reviewerDispatch.PromptPath);
    Assert.Contains("Current target context:", reviewerPrompt, StringComparison.Ordinal);
    Assert.Contains($"- Branch: {branch}", reviewerPrompt, StringComparison.Ordinal);
    Assert.Contains($"- HEAD commit: {head}", reviewerPrompt, StringComparison.Ordinal);
    Assert.Contains("latest developer retry feedback", reviewerPrompt, StringComparison.Ordinal);

    kernel.RecordTaskVerification(goal.Id, tester.Id, new TaskVerificationRecord(
        "dotnet test",
        worktree,
        1,
        string.Empty,
        "failed before redispatch",
        DateTimeOffset.Parse("2026-06-27T12:02:00Z")));
    kernel.ReportTaskProgress(goal.Id, tester.Id, WorkTaskStatus.Failed, "Tester dispatch failed before redispatch.");
    File.WriteAllText(Path.Combine(worktree, "redispatch.txt"), "redispatch head");
    RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-27T12:03:00Z"));
    RunGit(worktree, ["commit", "-m", "Advance redispatch head"], DateTimeOffset.Parse("2026-06-27T12:03:00Z"));
    var redispatchHead = ReadGit(worktree, ["rev-parse", "HEAD"]);
    Assert.NotEqual(head, redispatchHead);
    kernel.RetryTask(goal.Id, tester.Id, "tester redispatch should use current head");

    var redispatch = WorkerProfileDispatcher.PrepareTask(
        kernel,
        goal,
        tester,
        profile,
        promptRoot,
        worktree,
        DateTimeOffset.Parse("2026-06-27T12:04:00Z"));

    var redispatchPrompt = File.ReadAllText(redispatch.PromptPath);
    Assert.Contains($"- Branch: {branch}", redispatchPrompt, StringComparison.Ordinal);
    Assert.Contains($"- HEAD commit: {redispatchHead}", redispatchPrompt, StringComparison.Ordinal);
    Assert.True(!redispatchPrompt.Contains($"- HEAD commit: {head}", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_retry_prompt_includes_failed_verification_receipt")]
    public void WorkerProfileDispatcherRetryPromptIncludesFailedVerificationReceipt()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var clock = new MutableClock(DateTimeOffset.Parse("2026-06-27T12:00:00Z"));
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Implement retry prompt regeneration.", AgentRole.Developer);
    var tester = new TaskSpec(TaskId.New(), "Test retry prompt regeneration.", AgentRole.Tester);
    var goal = kernel.CreateGoal("Fix retry prompt regeneration", [developer, tester]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
        "dotnet test --filter DispatchReceipt",
        workingDirectory,
        1,
        "Failed Mcg.AgentOrchestrator.Infrastructure.Tests.DispatchReceiptTests.PromptContainsReceipt",
        "exit code 1",
        clock.UtcNow,
        StandardOutputPath: Path.Combine(root, "logs", "developer.out.log"),
        StandardErrorPath: Path.Combine(root, "logs", "developer.err.log")));
    clock.Advance();
    kernel.RetryTask(goal.Id, developer.Id, "Operator steer stays short.");
    var profile = new WorkerProfile("codex-cli", "codex exec --sandbox workspace-write --cd {workingDirectory}");

    var dispatch = WorkerProfileDispatcher.PrepareTask(
        kernel,
        goal,
        tester,
        profile,
        promptRoot,
        workingDirectory,
        clock.UtcNow);

    var prompt = File.ReadAllText(dispatch.PromptPath);
    Assert.Contains("Operator steer stays short.", prompt, StringComparison.Ordinal);
    Assert.Contains("Structured failure receipt (bounded):", prompt, StringComparison.Ordinal);
    Assert.Contains("Verification command: dotnet test --filter DispatchReceipt", prompt, StringComparison.Ordinal);
    Assert.Contains("Verification exit code: 1", prompt, StringComparison.Ordinal);
    Assert.Contains("DispatchReceiptTests.PromptContainsReceipt", prompt, StringComparison.Ordinal);
    Assert.Contains(Path.Combine(root, "logs", "developer.out.log"), prompt, StringComparison.Ordinal);
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
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [otherRoleAgent, assignedAgent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt,
        sandboxOptions: sandbox,
        commandExists: RealClaudeLauncherExists);

    Assert.Equal(assignedAgent.Id, task.AssignedAgentId);
    Assert.Equal("claude-cli", task.LastDispatch!.WorkerName);
    Assert.Equal("Anthropic", task.LastDispatch.ProviderName);
    Assert.Equal("claude-haiku-4-5", task.LastDispatch.ModelName);
}

    [Xunit.Theory(DisplayName = "WorkerProfileDispatcher_operator_OpenAI_selection_constrains_all_light_roles")]
    [Xunit.InlineData(AgentRole.Planner)]
    [Xunit.InlineData(AgentRole.Researcher)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void WorkerProfileDispatcherOperatorOpenAiSelectionConstrainsAllLightRoles(AgentRole role)
{
    var agent = DashboardRequestParser.CreateAgentDefinition(new AgentSubmissionDto(
        role.ToString(),
        "OpenAI",
        "gpt-5.4-mini",
        null));
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Inspect the requested change.", role);
    var goal = kernel.CreateGoal("Keep the explicit OpenAI assignment", [task]);
    kernel.ActivateGoal(goal.Id, [agent]);
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    var planItem = SubscriptionPlanBuilder.Build(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        sandboxOptions: sandbox,
        commandExists: _ => true).Items.Single();

    Assert.True(agent.IsProviderRoutingConstrained);
    Assert.Equal("OpenAI", planItem.ProviderName);
    Assert.Equal("codex-cli", planItem.ProfileName);
    Assert.Equal(AgentCatalog.OpenAiSubscriptionModelAlias, planItem.SubscriptionModelName);
}

    [Xunit.Theory(DisplayName = "WorkerProfileDispatcher_operator_Anthropic_selection_constrains_all_light_roles")]
    [Xunit.InlineData(AgentRole.Planner)]
    [Xunit.InlineData(AgentRole.Researcher)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void WorkerProfileDispatcherOperatorAnthropicSelectionConstrainsAllLightRoles(AgentRole role)
{
    var agent = DashboardRequestParser.CreateAgentDefinition(new AgentSubmissionDto(
        role.ToString(),
        "Anthropic",
        "claude-sonnet-4-6",
        null,
        SubscriptionModelAlias: "claude-sonnet-4-6"));
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Inspect the requested change.", role);
    var goal = kernel.CreateGoal("Keep the explicit Anthropic assignment", [task]);
    kernel.ActivateGoal(goal.Id, [agent]);
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    var planItem = SubscriptionPlanBuilder.Build(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        sandboxOptions: sandbox,
        commandExists: _ => true).Items.Single();

    Assert.True(agent.IsProviderRoutingConstrained);
    Assert.Equal("Anthropic", planItem.ProviderName);
    Assert.Equal("claude-cli", planItem.ProfileName);
    Assert.Equal("claude-sonnet-4-6", planItem.SubscriptionModelName);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_OpenAI_constraint_aligns_plan_preflight_command_and_receipt")]
    public void WorkerProfileDispatcherOpenAiConstraintAlignsPlanPreflightCommandAndReceipt()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var dispatchedAt = DateTimeOffset.Parse("2026-07-29T12:00:00Z");
    var agent = DashboardRequestParser.CreateAgentDefinition(new AgentSubmissionDto(
        AgentRole.Planner.ToString(),
        "OpenAI",
        "gpt-5.4-mini",
        null));
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Plan the requested source change.", AgentRole.Planner);
    var goal = kernel.CreateGoal("Dispatch an explicitly OpenAI-only planner", [task]);
    var profiles = WorkerProfileCatalog.Default();
    kernel.ActivateGoal(goal.Id, [agent]);
    var planItem = SubscriptionPlanBuilder.Build(goal, [agent], profiles).Items.Single();
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        profiles,
        promptRoot,
        workingDirectory,
        dispatchedAt,
        sandboxOptions: sandbox);

    var dispatch = task.LastDispatch!;
    var preflight = File.ReadAllText(Path.Combine(
        workingDirectory,
        ".orchestrator-context",
        goal.Id.Value,
        "subscription-preflight.md"));
    Assert.Equal(planItem.ProfileName, dispatch.WorkerName);
    Assert.Equal(planItem.ProviderName, dispatch.ProviderName);
    Assert.Equal(planItem.SubscriptionModelName, dispatch.ModelName);
    Assert.StartsWith("codex exec", dispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain("claude", dispatch.Command, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("profile: codex-cli", preflight, StringComparison.Ordinal);
    Assert.Contains("model: OpenAI/gpt-5.5", preflight, StringComparison.Ordinal);
    Assert.Contains("model-selection: provider-constrained: Planner remains on OpenAI", preflight, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "AgentCatalogStore_stale_catalog_repair_preserves_explicit_provider_constraint")]
    public void AgentCatalogStoreStaleCatalogRepairPreservesExplicitProviderConstraint()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "agents.json");
    var legacyAgent = AgentCatalog.Default().GetRequired(AgentRole.Researcher) with
    {
        Name = "Pinned OpenAI researcher",
        Subscription = new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSolSubscriptionModelAlias),
        IsProviderRoutingConstrained = null
    };
    AgentCatalogStore.Save(path, new AgentCatalog([legacyAgent]));

    AgentCatalog repairedCatalog = null!;
    var warning = CaptureConsoleError(() => repairedCatalog = AgentCatalogStore.Load(path));
    var repairedAgent = repairedCatalog.GetRequired(AgentRole.Researcher);
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Research the requested source change.", AgentRole.Researcher);
    var goal = kernel.CreateGoal("Route a repaired explicit catalog", [task]);
    kernel.ActivateGoal(goal.Id, [repairedAgent]);
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
    var planItem = SubscriptionPlanBuilder.Build(
        goal,
        [repairedAgent],
        WorkerProfileCatalog.Default(),
        sandboxOptions: sandbox,
        commandExists: _ => true).Items.Single();

    Assert.True(repairedAgent.IsProviderRoutingConstrained);
    Assert.Contains("1 customized assignment(s) are provider-constrained", warning, StringComparison.Ordinal);
    Assert.Equal("OpenAI", planItem.ProviderName);
    Assert.Equal("codex-cli", planItem.ProfileName);
    Assert.Equal(AgentCatalog.OpenAiSolSubscriptionModelAlias, planItem.SubscriptionModelName);
}

    [Xunit.Fact(DisplayName = "AgentCatalogStore_legacy_built_in_catalog_routing_is_fallback_independent")]
    public void AgentCatalogStoreLegacyBuiltInCatalogRoutingIsFallbackIndependent()
{
    var root = CreateTempDirectory();
    var legacyCatalogs = new[]
    {
        ("openai", AgentCatalog.Default()),
        ("ollama", AgentCatalog.OllamaDefault())
    };

    foreach (var (name, builtInCatalog) in legacyCatalogs)
    {
        var path = Path.Combine(root, $"{name}-agents.json");
        var legacyCatalog = new AgentCatalog(builtInCatalog.Agents
            .Select(agent => agent with { IsProviderRoutingConstrained = null })
            .ToList());
        AgentCatalogStore.Save(path, legacyCatalog);

        AgentCatalog defaultLoad = null!;
        AgentCatalog ollamaFallbackLoad = null!;
        var defaultWarning = CaptureConsoleError(() => defaultLoad = AgentCatalogStore.Load(path));
        var ollamaFallbackWarning = CaptureConsoleError(
            () => ollamaFallbackLoad = AgentCatalogStore.Load(path, AgentCatalog.OllamaDefault()));

        Assert.All(defaultLoad.Agents, agent => Assert.False(agent.IsProviderRoutingConstrained));
        Assert.Equal(
            defaultLoad.Agents.Select(agent => agent.IsProviderRoutingConstrained),
            ollamaFallbackLoad.Agents.Select(agent => agent.IsProviderRoutingConstrained));
        Assert.Contains("6 built-in-compatible assignment(s) remain automatic", defaultWarning, StringComparison.Ordinal);
        Assert.Contains("6 built-in-compatible assignment(s) remain automatic", ollamaFallbackWarning, StringComparison.Ordinal);
    }
}

    [Xunit.Fact(DisplayName = "AgentCatalog_Ollama_defaults_explicitly_enable_automatic_routing")]
    public void AgentCatalogOllamaDefaultsExplicitlyEnableAutomaticRouting()
{
    Assert.All(AgentCatalog.OllamaDefault().Agents, agent => Assert.False(agent.IsProviderRoutingConstrained));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_constrained_high_risk_reviewer_preserves_risk_reason")]
    public void WorkerProfileDispatcherConstrainedHighRiskReviewerPreservesRiskReason()
{
    var agent = AgentCatalog.Default().GetRequired(AgentRole.Reviewer) with
    {
        IsProviderRoutingConstrained = true
    };
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Review the high-risk change.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Review a high-risk migration", [task]);
    kernel.RecordGoalPolicyDecision(
        goal.Id,
        "Intake pipeline decision (auto): developer-reviewer; risk labels: complex, high-risk.");
    kernel.ActivateGoal(goal.Id, [agent]);
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    var variables = WorkerProfileDispatcher.BuildSubscriptionTemplateVariables(
        agent,
        goal,
        task,
        WorkerProfileCatalog.Default(),
        sandboxOptions: sandbox,
        commandExists: _ => true);

    Assert.Equal(
        "full-profile: Reviewer high-risk/complex intake labels require exhaustive review",
        variables["modelSelectionReason"]);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_light_readonly_role_resolves_haiku_with_recorded_reason")]
    public void WorkerProfileDispatcherLightReadonlyRoleResolvesHaikuWithRecordedReason()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var dispatchedAt = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Research repository-local evidence for the change.", AgentRole.Researcher);
    var goal = kernel.CreateGoal("Route light read-only role", [task]);
    var agents = AgentCatalog.Default().Agents;
    Assert.All(agents, agent => Assert.False(agent.IsProviderRoutingConstrained));
    kernel.ActivateGoal(goal.Id, agents);
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt,
        sandboxOptions: sandbox,
        commandExists: RealClaudeLauncherExists);

    var preflight = File.ReadAllText(Path.Combine(workingDirectory, ".orchestrator-context", goal.Id.Value, "subscription-preflight.md"));

    Assert.Equal("claude-cli", task.LastDispatch!.WorkerName);
    Assert.Equal("Anthropic", task.LastDispatch.ProviderName);
    Assert.Equal("claude-haiku-4-5", task.LastDispatch.ModelName);
    Assert.Contains("model-selection: light-role: Researcher uses claude-cli/claude-haiku-4-5", preflight, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_echo_stub_claude_profile_falls_back_from_light_role_with_reason")]
    public void WorkerProfileDispatcherEchoStubClaudeProfileFallsBackFromLightRoleWithReason()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var dispatchedAt = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Research repository-local evidence for the change.", AgentRole.Researcher);
    var goal = kernel.CreateGoal("Fallback from fake Claude light role", [task]);
    var agents = AgentCatalog.Default().Agents;
    var profiles = WorkerProfileCatalog.Default()
        .Upsert(new WorkerProfile("claude-cli", "Write-Output {subscriptionModelName}; Write-Output {promptPath}"));
    kernel.ActivateGoal(goal.Id, agents);
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        agents,
        profiles,
        promptRoot,
        workingDirectory,
        dispatchedAt,
        sandboxOptions: sandbox,
        commandExists: _ => true);

    var preflight = File.ReadAllText(Path.Combine(workingDirectory, ".orchestrator-context", goal.Id.Value, "subscription-preflight.md"));
    Assert.Equal("codex-cli", task.LastDispatch!.WorkerName);
    Assert.Equal("OpenAI", task.LastDispatch.ProviderName);
    Assert.Equal(AgentCatalog.OpenAiSubscriptionModelAlias, task.LastDispatch.ModelName);
    Assert.Contains("model-selection: full-profile: light-role profile unavailable", preflight, StringComparison.Ordinal);
    Assert.Contains("not the expected claude CLI", preflight, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_default_path_dispatch_is_identical_when_no_light_role_applies")]
    public void WorkerProfileDispatcherDefaultPathDispatchIsIdenticalWhenNoLightRoleApplies()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var dispatchedAt = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
    var codexProfile = new WorkerProfile(
        "codex-cli",
        "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox {sandboxMode} --cd {workingDirectory}");
    var codexOnlyProfiles = new WorkerProfileCatalog([codexProfile]);
    var codexWithLightProfile = new WorkerProfileCatalog(
    [
        codexProfile,
        new WorkerProfile("claude-cli", "claude --model {subscriptionModelName} --permission-mode {permissionMode}")
    ]);
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    var codexOnlyDispatch = PrepareDeveloperDispatch(codexOnlyProfiles);
    var codexWithLightProfileDispatch = PrepareDeveloperDispatch(codexWithLightProfile);

    Assert.Equal(codexOnlyDispatch.WorkerName, codexWithLightProfileDispatch.WorkerName);
    Assert.Equal(codexOnlyDispatch.Command, codexWithLightProfileDispatch.Command);
    Assert.Equal(codexOnlyDispatch.WorkingDirectory, codexWithLightProfileDispatch.WorkingDirectory);
    Assert.Equal(codexOnlyDispatch.ProviderName, codexWithLightProfileDispatch.ProviderName);
    Assert.Equal(codexOnlyDispatch.ModelName, codexWithLightProfileDispatch.ModelName);
    Assert.Equal(codexOnlyDispatch.ReasoningEffort, codexWithLightProfileDispatch.ReasoningEffort);
    Assert.Equal(codexOnlyDispatch.TaskComplexity, codexWithLightProfileDispatch.TaskComplexity);
    Assert.Equal(codexOnlyDispatch.UsesComplexModel, codexWithLightProfileDispatch.UsesComplexModel);

    TaskDispatchRecord PrepareDeveloperDispatch(WorkerProfileCatalog profiles)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement the requested source change.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Preserve developer dispatch route", [task]);
        var agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(goal.Id, agents);

        WorkerProfileDispatcher.PrepareSubscriptionTask(
            kernel,
            goal,
            task,
            agents,
            profiles,
            promptRoot,
            workingDirectory,
            dispatchedAt,
            sandboxOptions: sandbox);

        return task.LastDispatch!;
    }
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_template_conforming_light_role_result_keeps_haiku")]
    public void WorkerProfileDispatcherTemplateConformingLightRoleResultKeepsHaiku()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var dispatchedAt = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Research repository-local evidence for the change.", AgentRole.Researcher);
    var goal = kernel.CreateGoal("Keep light model after contract-conforming research", [task]);
    var agents = AgentCatalog.AnthropicDefault().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "worker refresh",
        workingDirectory,
        1,
        """
        WORKER_RESULT:
        files: none
        commands: rg -n WorkerProfileDispatcher src tests
        tests: fail - retry requested for unrelated operator feedback
        commit: none
        blockers: none
        citations: src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs; tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTests.cs
        model_fit: Anthropic/claude-haiku-4-5 - adequate - research shape - cited repo evidence
        skills: none
        confidence: high
        END_WORKER_RESULT
        """,
        string.Empty,
        dispatchedAt.AddMinutes(-1)));
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt,
        sandboxOptions: sandbox,
        commandExists: RealClaudeLauncherExists);

    var preflight = File.ReadAllText(Path.Combine(workingDirectory, ".orchestrator-context", goal.Id.Value, "subscription-preflight.md"));
    Assert.Equal("claude-cli", task.LastDispatch!.WorkerName);
    Assert.Equal("Anthropic", task.LastDispatch.ProviderName);
    Assert.Equal("claude-haiku-4-5", task.LastDispatch.ModelName);
    Assert.Contains("model-selection: light-role: Researcher uses claude-cli/claude-haiku-4-5", preflight, StringComparison.Ordinal);
    Assert.DoesNotContain("fallback-full-profile", preflight, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_guardrail_failure_falls_back_to_full_profile_with_recorded_reason")]
    public void WorkerProfileDispatcherGuardrailFailureFallsBackToFullProfileWithRecordedReason()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var dispatchedAt = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Research repository-local evidence for the change.", AgentRole.Researcher);
    var goal = kernel.CreateGoal("Fallback after weak light-role output", [task]);
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "worker refresh",
        workingDirectory,
        1,
        """
        WORKER_RESULT:
        files: none
        commands: none
        tests: fail - missing required citations
        blockers: none
        model_fit: Anthropic/claude-haiku-4-5 - underpowered - research shape - omitted citations
        skills: none
        confidence: low
        END_WORKER_RESULT
        """,
        string.Empty,
        dispatchedAt.AddMinutes(-1)));
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt,
        sandboxOptions: sandbox);

    var preflight = File.ReadAllText(Path.Combine(workingDirectory, ".orchestrator-context", goal.Id.Value, "subscription-preflight.md"));
    Assert.Equal("codex-cli", task.LastDispatch!.WorkerName);
    Assert.Equal("OpenAI", task.LastDispatch.ProviderName);
    Assert.Equal(AgentCatalog.OpenAiSubscriptionModelAlias, task.LastDispatch.ModelName);
    Assert.Contains("model-selection: fallback-full-profile: prior Researcher WORKER_RESULT missing field(s): citations", preflight, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_plan_readiness_and_dispatch_agree_for_light_role_fallback")]
    public void WorkerProfileDispatcherPlanReadinessAndDispatchAgreeForLightRoleFallback()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var dispatchedAt = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Research repository-local evidence for the change.", AgentRole.Researcher);
    var goal = kernel.CreateGoal("Fallback route agreement", [task]);
    var agents = AgentCatalog.Default().Agents;
    var profiles = WorkerProfileCatalog.Default();
    kernel.ActivateGoal(goal.Id, agents);
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "worker refresh",
        workingDirectory,
        1,
        """
        WORKER_RESULT:
        files: none
        commands: none
        tests: fail - missing required citations
        blockers: none
        model_fit: Anthropic/claude-haiku-4-5 - underpowered - research shape - omitted citations
        skills: none
        confidence: low
        END_WORKER_RESULT
        """,
        string.Empty,
        dispatchedAt.AddMinutes(-1)));
    var plan = SubscriptionPlanBuilder.Build(
        goal,
        agents,
        profiles,
        _ => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(kernel, goal, task, agents));
    var planItem = plan.Items.Single();
    var readiness = DispatchReadinessEvaluator.EvaluateDispatchReadiness(goal, plan, dispatchedAt);
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        agents,
        profiles,
        promptRoot,
        workingDirectory,
        dispatchedAt,
        sandboxOptions: sandbox,
        commandExists: RealClaudeLauncherExists);

    Assert.IsType<DispatchReadinessReady>(readiness);
    Assert.True(planItem.CanPrepare);
    Assert.Equal("codex-cli", planItem.ProfileName);
    Assert.Equal("OpenAI", planItem.ProviderName);
    Assert.Equal(AgentCatalog.OpenAiSubscriptionModelAlias, planItem.SubscriptionModelName);
    Assert.Equal(planItem.ProfileName, task.LastDispatch!.WorkerName);
    Assert.Equal(planItem.ProviderName, task.LastDispatch.ProviderName);
    Assert.Equal(planItem.SubscriptionModelName, task.LastDispatch.ModelName);
    Assert.Contains(planItem.Route!.Reasons, reason => reason.Contains("fallback-full-profile", StringComparison.OrdinalIgnoreCase));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_source_call_sites_use_catalog_aware_model_resolution")]
    public void WorkerProfileDispatcherSourceCallSitesUseCatalogAwareModelResolution()
{
    var repoRoot = InfrastructureTestSupport.FindRepositoryRoot();
    var srcRoot = Path.Combine(repoRoot, "src");
    var bypasses = new List<string>();
    var callPattern = new Regex(
        @"WorkerProfileDispatcher\.(?:BuildSubscriptionTemplateVariables|ResolveSubscriptionProfileName)\((?<args>.*?)\)",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);
    var advanceLoopDefaultCatalogPattern = new Regex(
        @"Advance(?:Goal)?UntilBlockedAsync\s*\([^)]*WorkerProfileCatalog\?\s+\w+\s*=\s*null",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);
    var catalogAwareDefaultParameterPattern = new Regex(
        @"(?:DashboardNextActionControls\.Build|ToNextActionsDto|ToNextActionDto|ToNextActionControlDto|ToGoalWorkSummaryDto|ToTaskWorkContextDto|GoalTranscriptRenderer\.Render|PrintNextActions)\s*\([^)]*WorkerProfileCatalog\?\s+\w+\s*=\s*null",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);
    var catalogAwareDeclarations = new (string RelativePath, string MethodName)[]
    {
        (Path.Combine("Mcg.AgentOrchestrator.App", "Dashboard", "Rendering", "DashboardNextActionControls.cs"), "Build"),
        (Path.Combine("Mcg.AgentOrchestrator.App", "Dashboard", "Api", "DashboardResponseMapper.Reports.cs"), "ToNextActionsDto"),
        (Path.Combine("Mcg.AgentOrchestrator.App", "Dashboard", "Api", "DashboardResponseMapper.Reports.cs"), "ToNextActionDto"),
        (Path.Combine("Mcg.AgentOrchestrator.App", "Dashboard", "Api", "DashboardResponseMapper.Reports.cs"), "ToNextActionControlDto"),
        (Path.Combine("Mcg.AgentOrchestrator.App", "Dashboard", "Api", "DashboardResponseMapper.Reports.cs"), "ToGoalWorkSummaryDto"),
        (Path.Combine("Mcg.AgentOrchestrator.App", "Dashboard", "Api", "DashboardResponseMapper.Reports.cs"), "ToTaskWorkContextDto"),
        (Path.Combine("Mcg.AgentOrchestrator.App", "Dashboard", "Rendering", "GoalTranscriptRenderer.cs"), "Render"),
        (Path.Combine("Mcg.AgentOrchestrator.App", "Cli", "ConsoleViews.DispatchAndNextActions.cs"), "PrintNextActions")
    };

    foreach (var path in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
    {
        if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(Path.Combine("Workers", "WorkerProfileDispatcher.cs"), StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var text = File.ReadAllText(path);
        foreach (Match match in callPattern.Matches(text))
        {
            var args = match.Groups["args"].Value;
            if (args.Contains("goal", StringComparison.Ordinal) &&
                args.Contains("task", StringComparison.Ordinal) &&
                !args.Contains("profiles", StringComparison.Ordinal) &&
                !args.Contains("WorkerProfileCatalog.Default()", StringComparison.Ordinal) &&
                !args.Contains("resolvedProfiles", StringComparison.Ordinal))
            {
                var line = text[..match.Index].Count(ch => ch == '\n') + 1;
                bypasses.Add($"{Path.GetRelativePath(repoRoot, path)}:{line}: {match.Value.ReplaceLineEndings(" ")}");
            }
        }

        foreach (Match match in advanceLoopDefaultCatalogPattern.Matches(text))
        {
            var line = text[..match.Index].Count(ch => ch == '\n') + 1;
            bypasses.Add($"{Path.GetRelativePath(repoRoot, path)}:{line}: advance-loop entry point defaults WorkerProfileCatalog");
        }

        foreach (Match match in catalogAwareDefaultParameterPattern.Matches(text))
        {
            var line = text[..match.Index].Count(ch => ch == '\n') + 1;
            bypasses.Add($"{Path.GetRelativePath(repoRoot, path)}:{line}: catalog-aware next-action surface defaults WorkerProfileCatalog");
        }
    }

    foreach (var declaration in catalogAwareDeclarations)
    {
        var path = Path.Combine(srcRoot, declaration.RelativePath);
        var text = File.ReadAllText(path);
        var optionalPattern = new Regex(
            $@"\b{Regex.Escape(declaration.MethodName)}\s*\([^)]*WorkerProfileCatalog\?\s+\w+\s*=\s*null",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        foreach (Match match in optionalPattern.Matches(text))
        {
            var line = text[..match.Index].Count(ch => ch == '\n') + 1;
            bypasses.Add($"{Path.GetRelativePath(repoRoot, path)}:{line}: catalog-aware next-action surface defaults WorkerProfileCatalog");
        }
    }

    Assert.Empty(bypasses);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_template_conforming_reviewer_result_keeps_haiku")]
    public void WorkerProfileDispatcherTemplateConformingReviewerResultKeepsHaiku()
{
    var workingDirectory = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(workingDirectory, "prompts");
    var dispatchedAt = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Review implementation output and risks.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Keep light model after contract-conforming review", [task]);
    var agents = AgentCatalog.AnthropicDefault().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "worker refresh",
        workingDirectory,
        1,
        """
        WORKER_RESULT:
        files: none
        commands: git diff --stat
        tests: fail - retry requested for unrelated operator feedback
        commit: none
        blockers: retry requested for unrelated operator feedback
        findings: [{"stable_id":"F-RETRY","state":"open","location":{"file":"src/Test.cs","region":"Test.Run"},"description":"Retry requested for unrelated operator feedback."}]
        touched_anchors: []
        criteria_verdicts: []
        verdict: needs-work
        model_fit: Anthropic/claude-haiku-4-5 - adequate - review shape - returned verdict and blocker status
        skills: none
        confidence: high
        END_WORKER_RESULT
        """,
        string.Empty,
        dispatchedAt.AddMinutes(-1)));
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt,
        sandboxOptions: sandbox,
        commandExists: RealClaudeLauncherExists);

    var preflight = File.ReadAllText(Path.Combine(workingDirectory, ".orchestrator-context", goal.Id.Value, "subscription-preflight.md"));
    Assert.Equal("claude-cli", task.LastDispatch!.WorkerName);
    Assert.Equal("Anthropic", task.LastDispatch.ProviderName);
    Assert.Equal("claude-haiku-4-5", task.LastDispatch.ModelName);
    Assert.Contains("model-selection: light-role: Reviewer uses claude-cli/claude-haiku-4-5", preflight, StringComparison.Ordinal);
    Assert.DoesNotContain("fallback-full-profile", preflight, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_reviewer_guardrail_requires_verdict_and_blockers_before_light_retry")]
    public void WorkerProfileDispatcherReviewerGuardrailRequiresVerdictAndBlockersBeforeLightRetry()
{
    var workingDirectory = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(workingDirectory, "prompts");
    var dispatchedAt = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Review implementation output and risks.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Fallback after weak reviewer output", [task]);
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "worker refresh",
        workingDirectory,
        1,
        """
        WORKER_RESULT:
        files: none
        commands: none
        tests: fail - missing review blockers
        findings: []
        touched_anchors: []
        criteria_verdicts: []
        verdict: fail
        model_fit: Anthropic/claude-haiku-4-5 - underpowered - review shape - omitted blockers
        skills: none
        confidence: low
        END_WORKER_RESULT
        """,
        string.Empty,
        dispatchedAt.AddMinutes(-1)));
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt,
        sandboxOptions: sandbox);

    var preflight = File.ReadAllText(Path.Combine(workingDirectory, ".orchestrator-context", goal.Id.Value, "subscription-preflight.md"));
    Assert.Equal("codex-cli", task.LastDispatch!.WorkerName);
    Assert.Equal("OpenAI", task.LastDispatch.ProviderName);
    Assert.Contains("model-selection: fallback-full-profile: prior Reviewer WORKER_RESULT invalid (missing field(s): blockers.)", preflight, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_developer_dispatch_model_unchanged")]
    public void WorkerProfileDispatcherDeveloperDispatchModelUnchanged()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var dispatchedAt = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Build an end-to-end distributed integration with horizontal scaling.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Keep complex developer model unchanged", [task]);
    var agents = AgentCatalog.Default().Agents;
    kernel.RecordGoalPolicyDecision(
        goal.Id,
        "Intake pipeline decision (auto): developer-reviewer; reasons: high-risk objective needs pre-acceptance review; risk labels: complex, high-risk.");
    kernel.ActivateGoal(goal.Id, agents);
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt,
        sandboxOptions: sandbox);

    var preflight = File.ReadAllText(Path.Combine(workingDirectory, ".orchestrator-context", goal.Id.Value, "subscription-preflight.md"));
    Assert.Equal("codex-cli", task.LastDispatch!.WorkerName);
    Assert.Equal("OpenAI", task.LastDispatch.ProviderName);
    Assert.Equal(AgentCatalog.OpenAiSubscriptionModelAlias, task.LastDispatch.ModelName);
    Assert.Contains("model-selection: full-profile: role is write-capable or gate-heavy", preflight, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "Reviewer_subscription_dispatch_uses_xhigh_reasoning_only_for_high_risk_intake_labels")]
    public void ReviewerSubscriptionDispatchUsesXhighReasoningOnlyForHighRiskIntakeLabels()
{
    var highRiskWorkingDirectory = CreateSeededDispatchRepository();
    var normalWorkingDirectory = CreateSeededDispatchRepository();
    WriteSkill(highRiskWorkingDirectory, "orchestrator-worker-verification");
    WriteSkill(normalWorkingDirectory, "orchestrator-worker-verification");
    var highRiskPromptRoot = Path.Combine(highRiskWorkingDirectory, "prompts");
    var normalPromptRoot = Path.Combine(normalWorkingDirectory, "prompts");
    var dispatchedAt = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox read-only --cd {workingDirectory}")
    ]);
    var agents = AgentCatalog.Default().Agents;
    var highRiskKernel = new AgentOrchestratorKernel();
    var normalKernel = new AgentOrchestratorKernel();
    var highRiskTask = new TaskSpec(TaskId.New(), "Review implementation output and risks.", AgentRole.Reviewer);
    var normalTask = new TaskSpec(TaskId.New(), "Review implementation output and risks.", AgentRole.Reviewer);
    var highRiskGoal = highRiskKernel.CreateGoal("Review stored intake labels", [highRiskTask]);
    var normalGoal = normalKernel.CreateGoal("Review stored intake labels", [normalTask]);
    highRiskKernel.RecordGoalPolicyDecision(
        highRiskGoal.Id,
        "Intake pipeline decision (auto): developer-reviewer; reasons: high-risk objective needs pre-acceptance review; risk labels: high-risk, multi-scope.");
    normalKernel.RecordGoalPolicyDecision(
        normalGoal.Id,
        "Intake pipeline decision (auto): developer-only; reasons: simple code objective; risk labels: low-risk.");
    highRiskKernel.ActivateGoal(highRiskGoal.Id, agents);
    normalKernel.ActivateGoal(normalGoal.Id, agents);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        highRiskKernel,
        highRiskGoal,
        highRiskTask,
        agents,
        profiles,
        highRiskPromptRoot,
        highRiskWorkingDirectory,
        dispatchedAt);
    WorkerProfileDispatcher.PrepareSubscriptionTask(
        normalKernel,
        normalGoal,
        normalTask,
        agents,
        profiles,
        normalPromptRoot,
        normalWorkingDirectory,
        dispatchedAt);

    Assert.Equal("codex-cli", highRiskTask.LastDispatch!.WorkerName);
    Assert.Equal("xhigh", highRiskTask.LastDispatch.ReasoningEffort);
    Assert.Contains("model_reasoning_effort='xhigh'", highRiskTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Equal("intake-risk", highRiskTask.LastDispatch.ReasoningEffortReason);
    Assert.Equal("codex-cli", normalTask.LastDispatch!.WorkerName);
    Assert.Equal("low", normalTask.LastDispatch.ReasoningEffort);
    Assert.Contains("model_reasoning_effort='low'", normalTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Equal("base", normalTask.LastDispatch.ReasoningEffortReason);
}

    [Xunit.Fact(DisplayName = "SubscriptionDispatch_explicit_codex_oss_profile_uses_codex_stdin_delivery")]
    public void SubscriptionDispatchExplicitCodexOssProfileUsesCodexStdinDelivery()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var dispatchedAt = DateTimeOffset.Parse("2026-07-01T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Dispatch Codex OSS write worker",
        [new TaskSpec(TaskId.New(), "Implement the focused change.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("openai-developer"),
        "OpenAI Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var profileOverride = new DispatchModelOverride("codex-oss-cli", "qwen3:8b", null);

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        workingDirectory,
        dispatchedAt,
        profileOverride);
    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt,
        profileOverride);
    var provider = WorkerProviderCatalog.Default().ResolveProfile("codex-oss-cli");
    var sandboxProvider = BackgroundDispatchRunner.ResolveSandboxProvider(provider);

    Assert.True(preflight.Allowed, string.Join("\n", preflight.Findings));
    Assert.Equal(ProviderKind.OpenAICodexOssCli, provider.Identity.Kind);
    Assert.Equal(WorkerSandboxProvider.Codex, sandboxProvider);
    Assert.Equal("codex-oss-cli", task.LastDispatch!.WorkerName);
    Assert.Equal(ProviderKind.OpenAICodexOssCli, task.LastDispatch.WorkerProviderKind);
    Assert.Equal("Ollama", task.LastDispatch.ProviderName);
    Assert.Contains("codex exec --skip-git-repo-check --oss --local-provider ollama", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain(" -p", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain("Get-Content -Raw", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain(task.LastDispatch.PromptPath!, task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.True(DispatchProcessHost.ShouldWritePromptToStdin(new DispatchProcessHost.DispatchRunParameters(
        task.LastDispatch.Command,
        workingDirectory,
        Path.Combine(root, "stdout.log"),
        Path.Combine(root, "stderr.log"),
        Path.Combine(root, "exit.txt"),
        null,
        ShutdownBuildServerOnExit: false,
        DisableSharedCompilation: false,
        Provider: sandboxProvider,
        PromptPath: task.LastDispatch.PromptPath)));
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

    // Subscription launch profiles always pin the configured alias; complexity only changes API-side model/effort.
    Assert.Contains($"--model '{AgentCatalog.OpenAiSubscriptionModelAlias}'", developer.LastDispatch!.Command, StringComparison.Ordinal);
    Assert.Equal("OpenAI", developer.LastDispatch.ProviderName);
    // Subscription launch profiles always pin the configured alias; complexity only changes API-side model/effort.
    Assert.Equal(AgentCatalog.OpenAiSubscriptionModelAlias, developer.LastDispatch.ModelName);
    Assert.Contains("model_reasoning_effort='high'", developer.LastDispatch.Command, StringComparison.Ordinal);
    var dispatchEvent = goal.Timeline.Single(evt =>
        evt.TaskId == developer.Id &&
        evt.Kind == ProgressKind.TaskDispatchRecorded);
    // Subscription launch profiles always pin the configured alias in dispatch metadata.
    Assert.Contains($"OpenAI/{AgentCatalog.OpenAiSubscriptionModelAlias}", dispatchEvent.Message, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "SubscriptionDispatch_override_model_beats_complex_path")]
    public void SubscriptionDispatchOverrideModelBeatsComplexPath()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-16T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Design and implement a production multi-tenant architecture",
        [new TaskSpec(TaskId.New(), "Build an end-to-end distributed integration with horizontal scaling.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("openai-developer"),
        "OpenAI Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini-codex", "low"),
        ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox workspace-write --cd {workingDirectory}")
    ]);
    var modelOverride = new DispatchModelOverride(null, "gpt-5.3-codex-spark", null);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel, goal, task, [agent], profiles, promptRoot, workingDirectory, dispatchedAt, modelOverride);

    Assert.Equal(TaskComplexity.Complex, task.LastDispatch!.TaskComplexity);
    Assert.Equal("OpenAI", task.LastDispatch.ProviderName);
    Assert.Equal("gpt-5.3-codex-spark", task.LastDispatch.ModelName);
    Assert.Contains("--model 'gpt-5.3-codex-spark'", task.LastDispatch.Command, StringComparison.Ordinal);
    var dispatchEvent = goal.Timeline.Single(evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskDispatchRecorded);
    Assert.Contains("OpenAI/gpt-5.3-codex-spark", dispatchEvent.Message, StringComparison.Ordinal);
    Assert.Contains("Model fit: OpenAI/gpt-5.3-codex-spark - adequate|overkill|underpowered", File.ReadAllText(task.LastDispatch.PromptPath!), StringComparison.Ordinal);

    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        task.LastDispatch.Command,
        workingDirectory,
        0,
        "WORKER_RESULT\nModel fit: OpenAI/gpt-5.3-codex-spark - adequate - override dispatch drill - effective label\nEND_WORKER_RESULT",
        string.Empty,
        dispatchedAt.AddMinutes(1)));
    var outcome = kernel.BuildModelOutcomeScorecard().Single(record =>
        record.ProviderName == "OpenAI" &&
        record.ModelName == "gpt-5.3-codex-spark");
    Assert.Equal(1, outcome.Completed);
}

    [Xunit.Fact(DisplayName = "SubscriptionDispatch_override_profile_sets_dispatch_provider_label")]
    public void SubscriptionDispatchOverrideProfileSetsDispatchProviderLabel()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-16T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Fix a worker dispatch label",
        [new TaskSpec(TaskId.New(), "Implement the focused dispatch label change.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("anthropic-developer"),
        "Anthropic Developer",
        AgentRole.Developer,
        new ModelProfile("Anthropic", "claude-haiku-4-5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-haiku-4-5"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox workspace-write --cd {workingDirectory}"),
        new WorkerProfile("claude-cli", "claude --model {subscriptionModelName} --permission-mode bypassPermissions")
    ]);
    var modelOverride = new DispatchModelOverride("codex-cli", "gpt-5.3-codex-spark", null);

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal, task, [agent], profiles, workingDirectory, dispatchedAt, modelOverride);
    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel, goal, task, [agent], profiles, promptRoot, workingDirectory, dispatchedAt, modelOverride);

    Assert.True(preflight.Allowed);
    Assert.Contains(preflight.Findings, text => text.Equals("profile: codex-cli", StringComparison.Ordinal));
    Assert.Contains(preflight.Findings, text => text.Equals("model: OpenAI/gpt-5.3-codex-spark", StringComparison.Ordinal));
    Assert.Equal("codex-cli", task.LastDispatch!.WorkerName);
    Assert.Equal("OpenAI", task.LastDispatch.ProviderName);
    Assert.Equal("gpt-5.3-codex-spark", task.LastDispatch.ModelName);
    Assert.Contains("codex exec", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--model 'gpt-5.3-codex-spark'", task.LastDispatch.Command, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "SubscriptionDispatch_override_profile_replaces_agent_default")]
    public void SubscriptionDispatchOverrideProfileReplacesAgentDefault()
{
    var workingDirectory = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(workingDirectory, "prompts");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-16T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Review the implementation",
        [new TaskSpec(TaskId.New(), "Review the code.", AgentRole.Reviewer)]);
    var agent = new AgentDefinition(
        new AgentId("openai-reviewer"),
        "OpenAI Reviewer",
        AgentRole.Reviewer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox read-only --cd {workingDirectory}"),
        new WorkerProfile("alt-profile", "alt-cli --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --cd {workingDirectory} (Get-Content -Raw {promptPath})")
    ]);
    var profileOverride = new DispatchModelOverride("alt-profile", null, null);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel, goal, task, [agent], profiles, promptRoot, workingDirectory, dispatchedAt, profileOverride);

    Assert.Equal("alt-profile", task.LastDispatch!.WorkerName);
}

    [Xunit.Fact(DisplayName = "SubscriptionDispatch_override_reasoning_beats_agent_default")]
    public void SubscriptionDispatchOverrideReasoningBeatsAgentDefault()
{
    var workingDirectory = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(workingDirectory, "prompts");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-16T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Review the implementation",
        [new TaskSpec(TaskId.New(), "Review the code.", AgentRole.Reviewer)]);
    var agent = new AgentDefinition(
        new AgentId("openai-reviewer"),
        "OpenAI Reviewer",
        AgentRole.Reviewer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "high"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox read-only --cd {workingDirectory}")
    ]);
    var reasoningOverride = new DispatchModelOverride(null, null, "low");

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel, goal, task, [agent], profiles, promptRoot, workingDirectory, dispatchedAt, reasoningOverride);

    Assert.Equal("low", task.LastDispatch!.ReasoningEffort);
    Assert.Contains("model_reasoning_effort='low'", task.LastDispatch.Command, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "SubscriptionDispatch_emits_first_class_subscription_alias_and_reasoning")]
    public void SubscriptionDispatchEmitsFirstClassSubscriptionAliasAndReasoning()
{
    var workingDirectory = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(workingDirectory, "prompts");
    var dispatchedAt = DateTimeOffset.Parse("2026-07-15T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Review the implementation",
        [new TaskSpec(TaskId.New(), "Review the code.", AgentRole.Reviewer)]);
    var agent = new AgentDefinition(
        new AgentId("openai-reviewer"),
        "OpenAI Reviewer",
        AgentRole.Reviewer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiLunaSubscriptionModelAlias, "medium"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox read-only --cd {workingDirectory}")
    ]);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel, goal, task, [agent], profiles, promptRoot, workingDirectory, dispatchedAt);

    Assert.Equal("codex-cli", task.LastDispatch!.WorkerName);
    Assert.Equal("OpenAI", task.LastDispatch.ProviderName);
    Assert.Equal(AgentCatalog.OpenAiLunaSubscriptionModelAlias, task.LastDispatch.ModelName);
    Assert.Equal("medium", task.LastDispatch.ReasoningEffort);
    Assert.Contains($"--model '{AgentCatalog.OpenAiLunaSubscriptionModelAlias}'", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("model_reasoning_effort='medium'", task.LastDispatch.Command, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "SubscriptionDispatch_no_override_preserves_complex_model_default")]
    public void SubscriptionDispatchNoOverridePreservesComplexModelDefault()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-16T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Design and implement a production multi-tenant architecture",
        [new TaskSpec(TaskId.New(), "Build an end-to-end distributed integration with horizontal scaling.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("openai-developer"),
        "OpenAI Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini-codex", "low"),
        ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox workspace-write --cd {workingDirectory}")
    ]);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel, goal, task, [agent], profiles, promptRoot, workingDirectory, dispatchedAt);

    Assert.Equal(TaskComplexity.Complex, task.LastDispatch!.TaskComplexity);
    // Subscription launch profiles always pin the configured alias; complexity only changes API-side model/effort.
    Assert.Equal("gpt-5-mini-codex", task.LastDispatch.ModelName);
    // Subscription launch profiles always pin the configured alias; complexity only changes API-side model/effort.
    Assert.Contains("--model 'gpt-5-mini-codex'", task.LastDispatch.Command, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_pins_anthropic_subscription_model")]
    public void WorkerProfileDispatcherPinsAnthropicSubscriptionModel()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Dispatch Anthropic subscription model",
        [new TaskSpec(TaskId.New(), "Implement the requested change.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("anthropic-developer"),
        "Anthropic developer",
        AgentRole.Developer,
        new ModelProfile("Anthropic", "claude-sonnet-4-20250514", ModelCapability.Text, SubscriptionMode.ApiKey, MaxOutputTokens: AgentCatalog.RoutineApiMaxOutputTokens),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt,
        sandboxOptions: sandbox);

    Assert.Equal("claude-cli", task.LastDispatch!.WorkerName);
    Assert.Contains("claude -p --model 'claude-sonnet' --permission-mode 'bypassPermissions'", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains(" -p ", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain("Get-Content -Raw", task.LastDispatch.Command, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_rejects_subscription_profiles_without_reasoning_pinning")]
    public void WorkerProfileDispatcherRejectsSubscriptionProfilesWithoutReasoningPinning()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Reject unpinned subscription reasoning", [new TaskSpec(TaskId.New(), "Implement the change.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("openai-developer"),
        "OpenAI developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("custom-agent", "gpt-5.3-codex", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var profiles = new WorkerProfileCatalog([new WorkerProfile("custom-agent", "agent-cli --model {subscriptionModelName} {promptPath}")]);

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        profiles,
        Path.Combine(root, "prompts"),
        root,
        DateTimeOffset.UtcNow));

    Assert.Contains("{subscriptionReasoningEffort}", ex.Message, StringComparison.Ordinal);
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
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var goal = kernel.CreateGoal(
        "Dispatch default OpenAI subscription profile",
        [new TaskSpec(TaskId.New(), "Build an end-to-end distributed integration with horizontal scaling.", AgentRole.Developer)]);
    kernel.RecordGoalPolicyDecision(
        goal.Id,
        "Intake pipeline decision (auto): developer-reviewer; reasons: complex objective; risk labels: complex, high-risk.");
    var agent = new AgentDefinition(
        new AgentId("openai-developer"),
        "OpenAI developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription);
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

    Assert.Equal("codex-cli", task.LastDispatch!.WorkerName);
    Assert.Contains("--model 'gpt-5.5'", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("model_reasoning_effort='high'", task.LastDispatch.Command, StringComparison.Ordinal);
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
    var profiles = new WorkerProfileCatalog([new WorkerProfile("codex-cli", "Write-Output {promptPath}")]);

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        profiles,
        Path.Combine(root, "prompts"),
        root,
        DateTimeOffset.UtcNow));

    Assert.Contains("Subscription preflight failed", ex.Message, StringComparison.Ordinal);
    Assert.Contains("only echoes prompt path", ex.Message, StringComparison.Ordinal);
    Assert.True(task.LastDispatch is null);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_rejects_subscription_profiles_without_model_pinning")]
    public void WorkerProfileDispatcherRejectsSubscriptionProfilesWithoutModelPinning()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Reject unpinned subscription model", [new TaskSpec(TaskId.New(), "Implement the change.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("openai-developer"),
        "OpenAI developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("custom-agent", "gpt-5.3-codex"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var profiles = new WorkerProfileCatalog([new WorkerProfile("custom-agent", "agent-cli {promptPath}")]);

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        profiles,
        Path.Combine(root, "prompts"),
        root,
        DateTimeOffset.UtcNow));

    Assert.Contains("{subscriptionModelName}", ex.Message, StringComparison.Ordinal);
    Assert.True(task.LastDispatch is null);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_rejects_developer_subscription_profiles_that_cannot_patch")]
public void WorkerProfileDispatcherRejectsDeveloperSubscriptionProfilesThatCannotPatch()
{
    var root = CreateTempDirectory();
    File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ..");
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
    var profiles = new WorkerProfileCatalog([new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} {promptPath}")]);

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        profiles,
        Path.Combine(root, "prompts"),
        root,
        DateTimeOffset.UtcNow));

    Assert.Contains("Subscription preflight failed", ex.Message, StringComparison.Ordinal);
    Assert.Contains("not patch-capable", ex.Message, StringComparison.Ordinal);
    Assert.Contains("missing", ex.Message, StringComparison.Ordinal);
    Assert.True(task.LastDispatch is null);
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
    CompleteResearcherAndPlannerArtifacts(kernel, goal);
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

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_rejects_subscription_dispatch_without_supported_assignment")]
    public void WorkerProfileDispatcherRejectsSubscriptionDispatchWithoutSupportedAssignment()
{
    var kernel = new AgentOrchestratorKernel();
    var unassignedGoal = kernel.CreateGoal("Reject unassigned", [new TaskSpec(TaskId.New(), "Unassigned", AgentRole.Developer)]);
    Assert.ThrowsAny<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
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
    Assert.ThrowsAny<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
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
    var fullStdout = new string('a', 20000) + new string('b', 10000);
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "dotnet test", workingDirectory, 0, fullStdout, string.Empty, DateTimeOffset.UtcNow));
    var profile = new WorkerProfile("codex", "codex exec --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})");

    WorkerProfileDispatcher.PrepareTask(kernel, goal, currentTask, profile, Path.Combine(root, "prompts"), workingDirectory, DateTimeOffset.UtcNow);

    var handoffPath = Path.Combine(workingDirectory, ".orchestrator-handoff.md");
    Assert.True(File.Exists(handoffPath));
    var content = File.ReadAllText(handoffPath);
    Assert.Contains("Developer: Fix the login bug.", content, StringComparison.Ordinal);
    Assert.Contains(new string('a', VerificationTextBounds.PreviewHeadChars), content, StringComparison.Ordinal);
    Assert.Contains("full output path not recorded", content, StringComparison.Ordinal);
    Assert.Contains(new string('b', VerificationTextBounds.PreviewTailChars), content, StringComparison.Ordinal);
    Assert.True(!content.Contains(new string('a', VerificationTextBounds.PreviewHeadChars + 1), StringComparison.Ordinal));
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
    Assert.Contains("Prior completed work.", content, StringComparison.Ordinal);
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
    Assert.Contains(".orchestrator-context", brief, StringComparison.Ordinal);
    Assert.Contains("manifest.md", brief, StringComparison.Ordinal);
    Assert.Contains("digest.md", brief, StringComparison.Ordinal);
    Assert.Contains("prior-task-summaries.md", brief, StringComparison.Ordinal);
    Assert.True(brief.IndexOf("prior-task-summaries.md", StringComparison.Ordinal) < brief.IndexOf("prior-task-evidence.md", StringComparison.Ordinal));
    Assert.Contains("Full evidence available in the context files", brief, StringComparison.Ordinal);
    Assert.Contains("## Prior Task Evidence", brief, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_writes_context_artifacts_with_repo_guidance_and_fuller_prior_evidence")]
    public void WorkerProfileDispatcherWritesContextArtifactsWithRepoGuidanceAndFullerPriorEvidence()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, "AGENTS.md"), "Repo-local agent guidance.");
    File.WriteAllText(Path.Combine(workingDirectory, "BACKLOG.md"), "Open backlog item.");
    File.WriteAllText(Path.Combine(workingDirectory, "DOGFOOD_LOG.md"), "Compatibility pointer only.");
    File.WriteAllText(Path.Combine(workingDirectory, "TestRepo.sln"), ""); // mark as dotnet for toolchain detection
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Research implementation.", AgentRole.Researcher);
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
    Assert.True(File.Exists(Path.Combine(contextDirectory, "artifact-registry.json")));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "deterministic-verification.md")));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "workflow-brokers.md")));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "context-budget.md")));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "selected-skills.md")));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "source-survey.md")));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "diff-summary.md")));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "prior-task-summaries.md")));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "prior-task-evidence.md")));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "context-package.json")));
    Assert.True(File.Exists(Path.Combine(contextDirectory, "AGENTS.md")));
    Assert.False(File.Exists(Path.Combine(contextDirectory, "BACKLOG.md")));
    Assert.False(File.Exists(Path.Combine(contextDirectory, "DOGFOOD_LOG.md")));
    var manifest = File.ReadAllText(Path.Combine(contextDirectory, "manifest.md"));
    var digest = File.ReadAllText(Path.Combine(contextDirectory, "digest.md"));
    var deterministic = File.ReadAllText(Path.Combine(contextDirectory, "deterministic-verification.md"));
    var workflowBrokers = File.ReadAllText(Path.Combine(contextDirectory, "workflow-brokers.md"));
    var contextBudget = File.ReadAllText(Path.Combine(contextDirectory, "context-budget.md"));
    var selectedSkills = File.ReadAllText(Path.Combine(contextDirectory, "selected-skills.md"));
    var sourceSurvey = File.ReadAllText(Path.Combine(contextDirectory, "source-survey.md"));
    var diffSummary = File.ReadAllText(Path.Combine(contextDirectory, "diff-summary.md"));
    var summaries = File.ReadAllText(Path.Combine(contextDirectory, "prior-task-summaries.md"));
    using var registryDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(contextDirectory, "artifact-registry.json")));
    var registryRoot = registryDocument.RootElement;
    Assert.True(registryRoot.GetProperty("verified").GetBoolean());
    var artifacts = registryRoot.GetProperty("artifacts").EnumerateArray().ToArray();
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "digest.md"));
    var deterministicArtifact = artifacts.Single(artifact => artifact.GetProperty("path").GetString() == "deterministic-verification.md");
    Assert.True(deterministicArtifact.GetProperty("exists").GetBoolean());
    Assert.True(deterministicArtifact.GetProperty("byteCount").GetInt64() > 0);
    Assert.Equal(64, deterministicArtifact.GetProperty("sha256").GetString()!.Length);
    Assert.Contains("dispatch preparation", deterministicArtifact.GetProperty("freshness").GetString()!, StringComparison.Ordinal);
    Assert.True(deterministicArtifact.GetProperty("roleVisibility").EnumerateArray().Any(item => item.GetString() == "Reviewer"));
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "AGENTS.md"));
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "selected-skills.md"));
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "workflow-brokers.md"));
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "context-budget.md"));
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "context-package.json"));
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "source-survey.md"));
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "diff-summary.md"));
    Assert.Contains("artifact-registry.json", manifest, StringComparison.Ordinal);
    Assert.Contains("digest.md", manifest, StringComparison.Ordinal);
    Assert.Contains("workflow-brokers.md", manifest, StringComparison.Ordinal);
    Assert.Contains("context-budget.md", manifest, StringComparison.Ordinal);
    Assert.Contains("selected-skills.md", manifest, StringComparison.Ordinal);
    Assert.Contains("source-survey.md", manifest, StringComparison.Ordinal);
    Assert.Contains("diff-summary.md", manifest, StringComparison.Ordinal);
    Assert.Contains("prior-task-summaries.md", manifest, StringComparison.Ordinal);
    Assert.Contains("prior-task-evidence.md", manifest, StringComparison.Ordinal);
    Assert.Contains("context-package.json", manifest, StringComparison.Ordinal);
    Assert.Contains("Missing Artifact Fallback", manifest, StringComparison.Ordinal);
    Assert.Contains("Role Artifact Priorities", manifest, StringComparison.Ordinal);
    Assert.Contains("AGENTS.md", manifest, StringComparison.Ordinal);
    Assert.Contains("## Current Task", digest, StringComparison.Ordinal);
    Assert.Contains("## Role Artifact Priorities", digest, StringComparison.Ordinal);
    Assert.Contains("workflow-brokers.md", digest, StringComparison.Ordinal);
    Assert.Contains("context-budget.md", digest, StringComparison.Ordinal);
    Assert.Contains("selected-skills.md", digest, StringComparison.Ordinal);
    Assert.Contains("artifact-registry.json: authoritative list", contextBudget, StringComparison.Ordinal);
    Assert.Contains("embed: digest.md", contextBudget, StringComparison.Ordinal);
    Assert.Contains("retrieve by handle: prior-task-evidence.md", contextBudget, StringComparison.Ordinal);
    Assert.Contains("omit from prompt prose", contextBudget, StringComparison.Ordinal);
    Assert.Contains("## Prior Completed Outcomes", digest, StringComparison.Ordinal);
    Assert.Contains("prior-task-summaries.md", digest, StringComparison.Ordinal);
    Assert.Contains("prior-task-evidence.md", digest, StringComparison.Ordinal);
    Assert.Contains(".orchestrator-handoff.md", digest, StringComparison.Ordinal);
    Assert.True(File.Exists(FindRepositoryFile("scripts", "Invoke-WorkerBuildCheck.ps1")));
    Assert.Contains("## Worker Build Check", deterministic, StringComparison.Ordinal);
    Assert.Contains(".\\scripts\\Invoke-WorkerBuildCheck.ps1 <project.csproj> [project.csproj...]", deterministic, StringComparison.Ordinal);
    Assert.Contains("does not run tests or spawn testhost", deterministic, StringComparison.Ordinal);
    Assert.Contains("Subscription workers must not run raw `dotnet test`, raw `dotnet build`, or `.\\scripts\\Invoke-IsolatedDotnet.ps1`", deterministic, StringComparison.Ordinal);
    Assert.Contains("tests: pass - build: 0 errors (Invoke-WorkerBuildCheck)", deterministic, StringComparison.Ordinal);
    Assert.Contains("## Required Verification Policy", deterministic, StringComparison.Ordinal);
    Assert.Contains("Requires tests:", deterministic, StringComparison.Ordinal);
    Assert.Contains("build-test-selection", workflowBrokers, StringComparison.Ordinal);
    Assert.Contains("source-survey", workflowBrokers, StringComparison.Ordinal);
    Assert.Contains("diff-summary", workflowBrokers, StringComparison.Ordinal);
    Assert.Contains("acceptance-evidence", workflowBrokers, StringComparison.Ordinal);
    Assert.Contains(".orchestrator/dogfood-log.db", workflowBrokers, StringComparison.Ordinal);
    Assert.Contains("dogfood-log list", workflowBrokers, StringComparison.Ordinal);
    Assert.DoesNotContain("Artifact: DOGFOOD_LOG.md", workflowBrokers, StringComparison.Ordinal);
    Assert.Contains("Broker Output Contract", workflowBrokers, StringComparison.Ordinal);
    Assert.Contains("WORKER_RESULT blockers", workflowBrokers, StringComparison.Ordinal);
    Assert.Contains("Changed files: Not reported.", summaries, StringComparison.Ordinal);
    Assert.Contains("Behavior changes: Not reported.", summaries, StringComparison.Ordinal);
    Assert.Contains("Verification: `dotnet test`", summaries, StringComparison.Ordinal);
    Assert.Contains("Model fit: Not reported.", summaries, StringComparison.Ordinal);
    Assert.Contains("dotnet-windows-build-hygiene", selectedSkills, StringComparison.Ordinal);
    Assert.Contains("Status: missing", selectedSkills, StringComparison.Ordinal);
    Assert.Contains("Source files indexed:", sourceSurvey, StringComparison.Ordinal);
    Assert.Contains("Git Status", diffSummary, StringComparison.Ordinal);
    var currentTaskText = File.ReadAllText(Path.Combine(contextDirectory, "current-task.md"));
    Assert.Contains("Run worker dispatch tests.", currentTaskText, StringComparison.Ordinal);
    Assert.Contains(".\\scripts\\Invoke-WorkerBuildCheck.ps1 <project.csproj> [project.csproj...]", currentTaskText, StringComparison.Ordinal);
    Assert.Contains("tests: pass - build: 0 errors (Invoke-WorkerBuildCheck)", currentTaskText, StringComparison.Ordinal);
    Assert.Contains("Do not run raw `dotnet test`, raw `dotnet build`, or `.\\scripts\\Invoke-IsolatedDotnet.ps1`", currentTaskText, StringComparison.Ordinal);
    Assert.Contains("skills: <selected skills used or none>", currentTaskText, StringComparison.Ordinal);
    Assert.Contains(artifactOnlyTail, File.ReadAllText(Path.Combine(contextDirectory, "prior-task-evidence.md")), StringComparison.Ordinal);
    var packageDirectory = Path.Combine(contextDirectory, "packages", currentTask.Id.Value);
    Assert.True(File.Exists(Path.Combine(packageDirectory, "manifest.md")));
    Assert.True(File.Exists(Path.Combine(packageDirectory, "artifact-registry.json")));
    Assert.True(File.Exists(Path.Combine(packageDirectory, "prior-task-evidence.md")));
    using var packageDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(contextDirectory, "context-package.json")));
    Assert.Equal(currentTask.Id.Value, packageDocument.RootElement.GetProperty("taskId").GetString());
    Assert.Contains("missing artifact", packageDocument.RootElement.GetProperty("missingArtifactFallback").GetString()!, StringComparison.OrdinalIgnoreCase);
    var prompt = File.ReadAllText(result.PromptPath);
    Assert.Contains(contextDirectory, prompt, StringComparison.Ordinal);
    Assert.Contains("artifact-registry.json", prompt, StringComparison.Ordinal);
    Assert.Contains("## Prior Task Evidence", prompt, StringComparison.Ordinal);
    Assert.Contains("Read prior-task-summaries.md first", prompt, StringComparison.Ordinal);
    Assert.True(prompt.IndexOf("prior-task-summaries.md", StringComparison.Ordinal) < prompt.IndexOf("prior-task-evidence.md", StringComparison.Ordinal));
    Assert.True(!prompt.Contains(inlineHead, StringComparison.Ordinal));
    Assert.True(!prompt.Contains(artifactOnlyTail, StringComparison.Ordinal));
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
    Assert.Contains("Read prior-task-summaries.md first", prompt, StringComparison.Ordinal);
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

}
