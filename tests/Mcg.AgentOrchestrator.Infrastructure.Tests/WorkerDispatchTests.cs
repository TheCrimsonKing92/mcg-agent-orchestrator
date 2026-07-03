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
using System.Text.Json;

public sealed class WorkerDispatchTests
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
            DateTimeOffset.UtcNow));

        Assert.Equal(newAgent.Id, task.AssignedAgentId);
        Assert.Equal("codex-cli", task.LastDispatch!.WorkerName);
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
            DateTimeOffset.UtcNow));

        Assert.Equal(agent.Id, task.AssignedAgentId);
        Assert.Equal("codex-cli", task.LastDispatch!.WorkerName);
        Assert.DoesNotContain("Warning:", stderr);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskRedelegated);
    }

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_allows_swept_terminal_goal_without_starting_worker")]
    public void WorkerProfileDispatcherPreflightAllowsSweptTerminalGoalWithoutStartingWorker()
    {
        var root = CreateTempDirectory();
        var promptRoot = Path.Combine(root, "prompts");
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Plan swept preflight.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Repair terminal preflight desync", [task]);
        var agent = SubscriptionPlannerAgent("planner", "Planner");
        kernel.ActivateGoal(goal.Id, [agent]);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

        var sweep = TerminalGoalSweep.Run(kernel, root, goal.Id);
        var repairedGoal = kernel.GetGoal(goal.Id);
        var repairedTask = repairedGoal.Tasks.Single(candidate => candidate.Id == task.Id);
        Assert.Equal(GoalStatus.Active, repairedGoal.Status);
        Assert.Equal(WorkTaskStatus.Assigned, repairedTask.Status);

        var prepared = WorkerProfileDispatcher.PrepareSubscriptionTask(
            kernel,
            repairedGoal,
            repairedTask,
            [agent],
            DispatchTestProfiles(),
            promptRoot,
            root,
            DateTimeOffset.UtcNow);

        Assert.Contains(sweep.Goals.Single().Repairs, repair => repair.Kind == "terminal-task-desync");
        Assert.NotNull(prepared.PromptPath);
        Assert.Null(repairedTask.LastProcess);
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
            DateTimeOffset.UtcNow);

        Assert.Equal(newAgent.Id, task.AssignedAgentId);
        Assert.Equal("codex-cli", task.LastDispatch!.WorkerName);
        Assert.Equal("OpenAI", task.LastDispatch.ProviderName);
    }

    [Xunit.Fact(DisplayName = "CliStartup_sets_protected_pid_before_worker_dispatch")]
    public void CliStartupSetsProtectedPidBeforeWorkerDispatch()
    {
        var original = Environment.GetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable);
        try
        {
            Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable, null);

            CliProtectedProcessEnvironment.EnsureProtectedPid();

            var protectedPid = Environment.GetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable);
            Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), protectedPid);

            Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable, "12345");
            CliProtectedProcessEnvironment.EnsureProtectedPid();

            Assert.Equal("12345", Environment.GetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable, original);
        }
    }

    [Xunit.Fact(DisplayName = "Headless_monitor_goal_preflight_does_not_start_paid_worker_dispatch")]
    public async Task HeadlessMonitorGoalPreflightDoesNotStartPaidWorkerDispatch()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Do paid subscription work.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Observe paid-capable goal without dispatch", [task]);
    var agent = new AgentDefinition(
        new AgentId("subscription-developer"),
        "Subscription developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "medium"));
    kernel.ActivateGoal(goal.Id, [agent]);
    using var output = new StringWriter();

    await GoalMonitoringSubscriptionCommand.RunAsync(
        ["monitor-goal", goal.Id.Value[..8], "--once"],
        output,
        kernel,
        workspace,
        [agent],
        WorkerProfileCatalog.Default());

    Assert.Contains("event: goal.snapshot", output.ToString());
    Assert.Null(task.LastDispatch);
    Assert.Null(task.LastProcess);
    Assert.False(Directory.Exists(Path.Combine(workspace.OrchestratorDirectory, "prompts")));
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
    Assert.Contains(preparation.Command, text => text.Contains("agent-cli --prompt", StringComparison.Ordinal));
    Assert.Contains(preparation.Command, text => text.Contains("--role Developer", StringComparison.Ordinal));
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
    Assert.Contains(prompt, text => text.Contains(latestFeedback, StringComparison.Ordinal));
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
    Assert.Contains(File.ReadAllText(second.PromptPath), text => text.Contains("latest retry feedback for second prompt", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "StartDispatches_refreshes_recorded_prompt_before_worker_start")]
    public void StartDispatchesRefreshesRecordedPromptBeforeWorkerStart()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var kernel = new AgentOrchestratorKernel(new TestClock(DateTimeOffset.Parse("2026-06-28T14:00:00Z")));
    var developer = new TaskSpec(TaskId.New(), "Implement retry prompt regeneration.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Fix stale retry dispatch prompts", [developer]);
    kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
        "Fix stale retry dispatch prompts",
        ["Recorded worker starts launch a prompt rendered from current task state."],
        VerificationClass.TestVerifiable,
        [],
        []));
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox {sandboxMode} --cd {workingDirectory}")
    ]);
    var originalDisableStart = Environment.GetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable);

    try
    {
        var prepared = GoalManagementCommandService.ProfileDispatchTask(
            kernel,
            workspace,
            goal,
            developer,
            profiles.GetRequired("codex-cli"),
            [agent]);
        var lateState = "late operator note that must appear in the prompt started by the worker";
        kernel.RecordTaskNote(goal.Id, developer.Id, lateState);
        Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, "1");

        Assert.Throws<InvalidOperationException>(() =>
            GoalManagementCommandService.StartDispatches(kernel, workspace, goal, [agent], profiles));

        var refreshedPromptPath = developer.LastDispatch!.PromptPath!;
        Assert.NotEqual(prepared.PromptPath, refreshedPromptPath);
        Assert.Contains(File.ReadAllText(refreshedPromptPath), text => text.Contains(lateState, StringComparison.Ordinal));
        Assert.DoesNotContain(refreshedPromptPath, developer.LastDispatch.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Content -Raw", developer.LastDispatch.Command, StringComparison.Ordinal);
        Xunit.Assert.Null(developer.LastProcess);
    }
    finally
    {
        Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, originalDisableStart);
    }
}

    [Xunit.Fact(DisplayName = "StartDispatches_fails_closed_when_recorded_worker_profile_is_missing")]
    public void StartDispatchesFailsClosedWhenRecordedWorkerProfileIsMissing()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var kernel = new AgentOrchestratorKernel(new TestClock(DateTimeOffset.Parse("2026-06-28T14:10:00Z")));
    var developer = new TaskSpec(TaskId.New(), "Implement retry prompt regeneration.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Fix stale retry dispatch prompts", [developer]);
    kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
        "Fix stale retry dispatch prompts",
        ["Recorded worker starts fail closed when the previous worker profile cannot be resolved."],
        VerificationClass.TestVerifiable,
        [],
        []));
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox {sandboxMode} --cd {workingDirectory}")
    ]);
    var originalDisableStart = Environment.GetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable);

    try
    {
        var prepared = GoalManagementCommandService.ProfileDispatchTask(
            kernel,
            workspace,
            goal,
            developer,
            profiles.GetRequired("codex-cli"),
            [agent]);
        var missingProfiles = new WorkerProfileCatalog([]);
        Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, "1");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GoalManagementCommandService.StartDispatches(kernel, workspace, goal, [agent], missingProfiles));

        Assert.Contains("worker profile 'codex-cli' is not available", ex.Message);
        Assert.Equal(prepared.PromptPath, developer.LastDispatch!.PromptPath);
        Xunit.Assert.Null(developer.LastProcess);
    }
    finally
    {
        Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, originalDisableStart);
    }
}

    [Xunit.Fact(DisplayName = "WorkerPromptInputBudget_keeps_within_budget_prompt_unchanged")]
    public void WorkerPromptInputBudgetKeepsWithinBudgetPromptUnchanged()
{
    var brief = CreateBudgetBrief("brief instructions only");

    var result = WorkerPromptInputBudget.Apply(brief, "Anthropic", "claude-sonnet-4-6");

    Assert.False(result.Trimmed);
    Assert.Equal(brief.Content, result.Brief.Content);
    Assert.Empty(result.DroppedSections);
}

    [Xunit.Fact(DisplayName = "WorkerPromptInputBudget_drops_evidence_before_other_context")]
    public void WorkerPromptInputBudgetDropsEvidenceBeforeOtherContext()
{
    var brief = CreateBudgetBrief(
        "brief instructions",
        "## Prior Task Evidence",
        new string('e', 240),
        "## Source Survey",
        "source-survey-kept",
        "## Worker Context Digest",
        "digest-kept");
    var budget = WorkerPromptInputBudget.CountTokens(brief.Content.Replace(new string('e', 240), string.Empty, StringComparison.Ordinal));

    var result = WorkerPromptInputBudget.Apply(brief, "Anthropic", "claude-sonnet-4-6", budget);

    Assert.True(result.DroppedSections.SequenceEqual(["evidence"]));
    Assert.False(result.Brief.Content.Contains("Prior Task Evidence", StringComparison.Ordinal));
    Assert.True(result.Brief.Content.Contains("source-survey-kept", StringComparison.Ordinal));
    Assert.True(result.Brief.Content.Contains("digest-kept", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerPromptInputBudget_drops_context_sections_in_fixed_priority_order_until_fit")]
    public void WorkerPromptInputBudgetDropsContextSectionsInFixedPriorityOrderUntilFit()
{
    var brief = CreateBudgetBrief(
        "brief instructions",
        "## Prior Task Evidence",
        new string('e', 240),
        "## Source Survey",
        new string('s', 240),
        "## Worker Context Digest",
        new string('d', 240));
    var budget = WorkerPromptInputBudget.CountTokens("brief instructions") + 2;

    var result = WorkerPromptInputBudget.Apply(brief, "Anthropic", "claude-sonnet-4-6", budget);

    Assert.True(result.DroppedSections.SequenceEqual(["evidence", "source survey", "digest"]));
    Assert.False(result.Brief.Content.Contains("Prior Task Evidence", StringComparison.Ordinal));
    Assert.False(result.Brief.Content.Contains("Source Survey", StringComparison.Ordinal));
    Assert.False(result.Brief.Content.Contains("Worker Context Digest", StringComparison.Ordinal));
    Assert.True(result.Brief.Content.Contains("brief instructions", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerPromptInputBudget_rejects_irreducible_over_budget_prompt")]
    public void WorkerPromptInputBudgetRejectsIrreducibleOverBudgetPrompt()
{
    var brief = CreateBudgetBrief(
        "brief instructions must remain intact " + new string('b', 120),
        "## Prior Task Evidence",
        new string('e', 240));

    var ex = Assert.Throws<WorkerPromptInputBudgetExceededException>(
        () => WorkerPromptInputBudget.Apply(brief, "Ollama", "qwen3:8b", inputTokenBudgetOverride: 5));

    Assert.Equal(brief.GoalId, ex.GoalId);
    Assert.Equal(brief.TaskId, ex.TaskId);
    Assert.Equal("Ollama", ex.ProviderName);
    Assert.Equal("qwen3:8b", ex.ModelName);
    Assert.True(ex.TokenCount > ex.TokenBudget);
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

    var ex = Assert.Throws<WorkerPromptInputBudgetExceededException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt));

    Assert.Equal(task.Id, ex.TaskId);
    Assert.False(Directory.Exists(promptRoot));
    Assert.Null(task.LastDispatch);
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
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
    var prompt = File.ReadAllText(dispatchResult.PromptPath);
    Assert.Equal(prompt.Length, task.LastDispatch.PromptCharacterCount);
    Assert.Equal(expectedPromptCharacters, task.LastDispatch.PromptCharacterCount);
    Assert.Contains(prompt, text => text.Contains("Model fit: OpenAI/gpt-5.3-codex - adequate|overkill|underpowered - <task shape> - <short reason>", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("WORKER_RESULT", StringComparison.Ordinal));
    var preflightPath = Path.Combine(contextDirectory, "subscription-preflight.md");
    Assert.True(File.Exists(preflightPath));
    Assert.Contains(File.ReadAllText(preflightPath), text => text.Contains("ready: profile, sandbox, worktree, and retry state passed deterministic preflight", StringComparison.Ordinal));
    Assert.Contains(File.ReadAllText(Path.Combine(contextDirectory, "digest.md")), text => text.Contains("subscription-preflight.md", StringComparison.Ordinal));
    Assert.Contains(File.ReadAllText(Path.Combine(contextDirectory, "manifest.md")), text => text.Contains("deterministic profile, sandbox, worktree", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_reports_goal_worktree_git_metadata_access_without_worker_start")]
    public void WorkerProfileDispatcherPreflightReportsGoalWorktreeGitMetadataAccessWithoutWorkerStart()
{
    var previousSandbox = Environment.GetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable);
    var root = CreateSeededDispatchRepository();
    try
    {
        Environment.SetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable, "1");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Inspect worker git metadata permissions", [new TaskSpec(TaskId.New(), "Inspect worker git metadata permissions.", AgentRole.Developer)]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(root, goal.Id);

        var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
            goal,
            task,
            [agent],
            WorkerProfileCatalog.Default(),
            worktree,
            DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            allowGitReference: true);

        var findings = string.Join("\n", preflight.Findings);
        Assert.True(preflight.Allowed, findings);
        Assert.Null(task.LastDispatch);
        Assert.Contains("git metadata index_lock=", findings);
        Assert.Contains(Path.Combine(".git", "worktrees", goal.Id.Value[..8], "index.lock"), findings.Replace('/', Path.DirectorySeparatorChar));
        Assert.Contains("current_process_can_write=True", findings);
        Assert.Contains(
            OperatingSystem.IsWindows() ? "worker_git_write=blocked-by-low-integrity" : "worker_git_write=same-as-orchestrator",
            findings);
        Assert.Contains("commit_contract=workers edit worktree files; orchestrator commits verified dirty edits on behalf", findings);
        Assert.Contains("ok: worktree clean before dispatch", findings);
    }
    finally
    {
        Environment.SetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable, previousSandbox);
    }
}

    [Xunit.Fact(DisplayName = "DispatchStateSurface_reports_active_test_child_with_command_line")]
    public void DispatchStateSurfaceReportsActiveTestChildWithCommandLine()
{
    var root = CreateTempDirectory();
    var now = DateTimeOffset.Parse("2026-07-03T06:00:00Z");
    var clock = new TestClock(now);
    var (kernel, goal, task, process) = CreateRecordedDispatch(root, clock);
    WriteHeartbeat(
        process,
        now.AddSeconds(-20),
        now.AddSeconds(-20),
        "running",
        stdoutBytes: 12,
        stderrBytes: 0,
        childPid: 222,
        ownedCpuMs: 100,
        ownedPids: [111, 222]);

    var state = CreateStateSurface(clock, livePids: [111, 222], commandLines: new Dictionary<int, string>
        {
            [111] = "pwsh -File worker-wrapper.ps1",
            [222] = "dotnet test --filter WorkerDispatchTests"
        })
        .Evaluate(goal.Id, task);

    Assert.Equal(DispatchStateKind.ActiveTestChild, state.Kind);
    Assert.Equal("hold", state.RecommendedAction);
    Assert.True(state.ProcessTree.HasLiveChild);
    Assert.Equal("dotnet test --filter WorkerDispatchTests", state.ProcessTree.ChildCommandLine);
    Assert.Equal(DispatchRecoveryAction.Hold, state.RecoveryDecision.Action);
    _ = kernel;
}

    [Xunit.Fact(DisplayName = "DispatchStateSurface_reports_exited_worker_awaiting_reconcile")]
    public void DispatchStateSurfaceReportsExitedWorkerAwaitingReconcile()
{
    var root = CreateTempDirectory();
    var now = DateTimeOffset.Parse("2026-07-03T06:05:00Z");
    var clock = new TestClock(now);
    var (_, goal, task, process) = CreateRecordedDispatch(root, clock);
    File.WriteAllText(process.ExitCodePath, "0");
    WriteHeartbeat(
        process,
        now.AddSeconds(-10),
        now.AddSeconds(-10),
        "exiting",
        stdoutBytes: 20,
        stderrBytes: 0,
        childPid: null,
        ownedPids: [111]);

    var state = CreateStateSurface(clock, livePids: [], commandLines: new Dictionary<int, string>())
        .Evaluate(goal.Id, task);

    Assert.Equal(DispatchStateKind.ExitedAwaitingReconcile, state.Kind);
    Assert.Equal("refresh-dispatch", state.RecommendedAction);
    Assert.True(state.Artifacts.ExitCodeExists);
    Assert.Equal(DispatchRecoveryAction.ReconcileFromExit, state.RecoveryDecision.Action);
}

    [Xunit.Fact(DisplayName = "DispatchStateSurface_reports_stale_cleanup_when_process_and_exit_are_absent")]
    public void DispatchStateSurfaceReportsStaleCleanupWhenProcessAndExitAreAbsent()
{
    var root = CreateTempDirectory();
    var clock = new TestClock(DateTimeOffset.Parse("2026-07-03T06:10:00Z"));
    var (_, goal, task, _) = CreateRecordedDispatch(root, clock);

    var state = CreateStateSurface(clock, livePids: [], commandLines: new Dictionary<int, string>())
        .Evaluate(goal.Id, task);

    Assert.Equal(DispatchStateKind.StaleCleanup, state.Kind);
    Assert.Equal("mark-stale", state.RecommendedAction);
    Assert.False(state.Artifacts.ExitCodeExists);
    Assert.False(state.Heartbeat.IsAvailable);
    Assert.Equal(DispatchRecoveryAction.MarkStale, state.RecoveryDecision.Action);
}

    [Xunit.Fact(DisplayName = "DispatchStateSurface_reports_wedged_live_process_after_idle_timeout")]
    public void DispatchStateSurfaceReportsWedgedLiveProcessAfterIdleTimeout()
{
    var root = CreateTempDirectory();
    var now = DateTimeOffset.Parse("2026-07-03T06:20:00Z");
    var clock = new TestClock(now);
    var (_, goal, task, process) = CreateRecordedDispatch(root, clock);
    WriteHeartbeat(
        process,
        now.AddMinutes(-40),
        now.AddMinutes(-40),
        "running",
        stdoutBytes: 0,
        stderrBytes: 0,
        childPid: null,
        ownedCpuMs: 0,
        ownedPids: [111]);

    var state = CreateStateSurface(clock, livePids: [111], commandLines: new Dictionary<int, string>
        {
            [111] = "codex exec prompt"
        })
        .Evaluate(goal.Id, task);

    Assert.Equal(DispatchStateKind.WedgedProcess, state.Kind);
    Assert.Equal("classify-blocker", state.RecommendedAction);
    Assert.Equal(DispatchRecoveryAction.ClassifyBlocker, state.RecoveryDecision.Action);
    Assert.True(state.RecoveryDecision.Reason.Contains("idle", StringComparison.OrdinalIgnoreCase), state.RecoveryDecision.Reason);
}

    [Xunit.Fact(DisplayName = "DispatchStateSurface_reports_dirty_worktree_and_commit_state")]
    public void DispatchStateSurfaceReportsDirtyWorktreeAndCommitState()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-07-03T06:30:00Z"));
    var (_, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "WORKER_RESULT:",
        string.Empty,
        clock);
    File.WriteAllText(Path.Combine(process.WorkingDirectory, "operator-state-surface.txt"), "dirty evidence");

    var state = CreateStateSurface(clock, livePids: [], commandLines: new Dictionary<int, string>())
        .Evaluate(goal.Id, task);

    Assert.True(state.Worktree.Exists);
    Assert.True(state.Worktree.IsGitWorktree);
    Assert.True(state.Worktree.IsDirty == true);
    Assert.False(string.IsNullOrWhiteSpace(state.Worktree.HeadCommit));
    Assert.NotNull(state.Worktree.CommitsAfterDispatch);
    Assert.Contains(state.Worktree.StatusEntries, entry => entry.Contains("operator-state-surface.txt", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "Dashboard_work_summary_surfaces_authoritative_dispatch_state")]
    public void DashboardWorkSummarySurfacesAuthoritativeDispatchState()
{
    var root = CreateSeededDispatchRepository();
    var now = DateTimeOffset.Parse("2026-07-03T06:35:00Z");
    var clock = new TestClock(now);
    var (_, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "worker output",
        string.Empty,
        clock);
    File.WriteAllText(Path.Combine(process.WorkingDirectory, "dispatch-state-evidence.txt"), "dirty evidence");
    WriteHeartbeat(
        process,
        now.AddSeconds(-12),
        now.AddSeconds(-12),
        "exiting",
        stdoutBytes: 13,
        stderrBytes: 0,
        childPid: 222,
        ownedPids: [process.ProcessId, 222],
        exitFileExists: true);

    var summary = DashboardResponseMapper.ToTaskWorkSummaryDto(goal, task);
    var state = Assert.IsType<DispatchAuthoritativeStateDto>(summary.DispatchState);

    Assert.Equal(DispatchStateKind.ExitedAwaitingReconcile, state.Kind);
    Assert.Equal("refresh-dispatch", state.RecommendedAction);
    Assert.Equal(DispatchRecoveryAction.ReconcileFromExit, state.RecoveryDecision.Action);
    Assert.Equal(process.ProcessId, state.ProcessTree.WrapperProcessId);
    Assert.Equal(222, state.ProcessTree.ChildProcessId);
    Assert.Contains(state.ProcessTree.Processes, node => node.ProcessId == process.ProcessId);
    Assert.Contains(state.ProcessTree.Processes, node => node.ProcessId == 222);
    Assert.True(state.Artifacts.StandardOutputExists);
    Assert.Equal(13, state.Artifacts.StandardOutputBytes);
    Assert.True(state.Artifacts.ExitCodeExists);
    Assert.True(state.Artifacts.HeartbeatExists);
    Assert.True(state.Worktree.IsDirty == true);
    Assert.False(string.IsNullOrWhiteSpace(state.Worktree.HeadCommit));
    Assert.NotNull(state.Worktree.CommitsAfterDispatch);
    Assert.Contains(state.Worktree.StatusEntries, entry => entry.Contains("dispatch-state-evidence.txt", StringComparison.Ordinal));
    Assert.True(state.StaleThresholds.RecentHeartbeatGraceSeconds > 0);
    Assert.True(state.StaleThresholds.LiveIdleTimeoutSeconds > state.StaleThresholds.RecentHeartbeatGraceSeconds);
    Assert.Contains("dirty_worktree=True", state.Summary);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_does_not_start_worker_or_mutate_owned_ephemeral_cleanup")]
    public void WorkerProfileDispatcherPreflightDoesNotStartWorkerOrMutateOwnedEphemeralCleanup()
    {
    var root = CreateSeededDispatchRepository();
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Update src/example.txt.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Preflight cleanup boundary", [task]);
    var agent = SubscriptionDeveloperAgent();
    kernel.ActivateGoal(goal.Id, [agent]);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var contextPath = Path.Combine(root, ".orchestrator-context", goal.Id.Value);
    var tempPath = Path.Combine(root, ".t", goal.Id.Value[..8] + "-preflight");
    Directory.CreateDirectory(contextPath);
    Directory.CreateDirectory(tempPath);
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        worktree,
        DateTimeOffset.Parse("2026-07-02T19:00:00Z"),
        sandboxOptions: sandbox);

    Assert.True(preflight.Allowed, string.Join("\n", preflight.Findings));
    Assert.Contains(preflight.Findings, finding => finding.Contains("ready: profile, sandbox, worktree, and retry state passed deterministic preflight", StringComparison.Ordinal));
    Assert.Null(task.LastDispatch);
    Assert.Null(task.LastProcess);
    Assert.True(Directory.Exists(contextPath));
    Assert.True(Directory.Exists(tempPath));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_blocks_claude_cli_only_auth_under_low_integrity_before_dispatch")]
    public void WorkerProfileDispatcherPreflightBlocksClaudeCliOnlyAuthUnderLowIntegrityBeforeDispatch()
{
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Verify Claude auth preflight", [new TaskSpec(TaskId.New(), "Test the implementation.", AgentRole.Tester)]);
    var agent = new AgentDefinition(
        new AgentId("tester"),
        "Tester",
        AgentRole.Tester,
        new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet-4-6", "medium"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var sandbox = new WorkerSandboxOptions(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
    var credentialPath = Path.Combine(root, ".claude", ".credentials.json");
    var authProbe = () => new ClaudeCliAuthState(
        HasAnthropicApiKey: false,
        HasCliCredentialArtifact: true,
        CredentialArtifactPath: credentialPath);

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        worktree,
        DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
        claudeAuthProbe: authProbe,
        sandboxOptions: sandbox);
    var ex = Assert.Throws<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        worktree,
        DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
        claudeAuthProbe: authProbe,
        sandboxOptions: sandbox));

    var findings = string.Join("\n", preflight.Findings);
    Assert.False(preflight.Allowed);
    Assert.Equal(ClaudeCliAuthProbe.AuthUnavailableErrorCode, preflight.ErrorCode);
    Assert.Contains(ClaudeCliAuthProbe.AuthUnavailableErrorCode, findings);
    Assert.Contains("Low-IL Claude subscription dispatch is refused before worker start", findings);
    Assert.Contains(ClaudeCliAuthProbe.AuthUnavailableErrorCode, ex.Message);
    Assert.Equal(ClaudeCliAuthProbe.AuthUnavailableErrorCode, Assert.IsType<WorkerSubscriptionPreflightException>(ex).ErrorCode);
    Assert.False(Directory.Exists(promptRoot));
    Assert.Null(task.LastDispatch);
    Assert.Null(task.LastProcess);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_skips_claude_auth_guard_for_codex_low_integrity_dispatch")]
    public void WorkerProfileDispatcherPreflightSkipsClaudeAuthGuardForCodexLowIntegrityDispatch()
{
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Verify codex auth preflight isolation", [new TaskSpec(TaskId.New(), "Test the implementation.", AgentRole.Tester)]);
    var agent = new AgentDefinition(
        new AgentId("tester"),
        "Tester",
        AgentRole.Tester,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "medium"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var sandbox = new WorkerSandboxOptions(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
    Func<ClaudeCliAuthState> authProbe = () => throw new InvalidOperationException("Claude auth probe must not run for codex workers.");

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        worktree,
        DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
        claudeAuthProbe: authProbe,
        sandboxOptions: sandbox);
    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        worktree,
        DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
        claudeAuthProbe: authProbe,
        sandboxOptions: sandbox);

    Assert.True(preflight.Allowed, string.Join("\n", preflight.Findings));
    Assert.Null(preflight.ErrorCode);
    Assert.Equal("codex-cli", task.LastDispatch!.WorkerName);
    Assert.Null(task.LastProcess);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_allows_claude_low_integrity_when_api_key_is_present")]
    public void WorkerProfileDispatcherPreflightAllowsClaudeLowIntegrityWhenApiKeyIsPresent()
{
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Verify Claude api key auth preflight", [new TaskSpec(TaskId.New(), "Test the implementation.", AgentRole.Tester)]);
    var agent = new AgentDefinition(
        new AgentId("tester"),
        "Tester",
        AgentRole.Tester,
        new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet-4-6", "medium"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var sandbox = new WorkerSandboxOptions(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
    var authProbe = () => new ClaudeCliAuthState(
        HasAnthropicApiKey: true,
        HasCliCredentialArtifact: true,
        CredentialArtifactPath: Path.Combine(root, ".claude", ".credentials.json"));

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        worktree,
        DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
        claudeAuthProbe: authProbe,
        sandboxOptions: sandbox);
    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        worktree,
        DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
        claudeAuthProbe: authProbe,
        sandboxOptions: sandbox);

    Assert.True(preflight.Allowed, string.Join("\n", preflight.Findings));
    Assert.Null(preflight.ErrorCode);
    Assert.Equal("claude-cli", task.LastDispatch!.WorkerName);
    Assert.Null(task.LastProcess);
}

    [Xunit.Fact(DisplayName = "WorkerSandboxOptions_has_no_provider_property")]
    public void WorkerSandboxOptionsHasNoProviderProperty()
{
    Assert.Null(typeof(WorkerSandboxOptions).GetProperty("Provider"));
}

    [Xunit.Fact(DisplayName = "IWorkerProvider_keeps_sandbox_policy_on_IWorkerSandbox")]
    public void IWorkerProviderKeepsSandboxPolicyOnIWorkerSandbox()
{
    Assert.True(typeof(IWorkerSandbox).IsInterface);
    Assert.True(new EnvironmentWorkerSandbox() is IWorkerSandbox);

    Assert.Null(typeof(IWorkerProvider).GetProperty("Options"));
    Assert.Null(typeof(IWorkerProvider).GetProperty("Sandbox"));
    Assert.Null(typeof(WorkerCapabilities).GetProperty("Sandbox"));
    Assert.Null(typeof(WorkerCapabilities).GetProperty("SandboxMode"));
}

    [Xunit.Fact(DisplayName = "DispatchProcessHost_seeds_claude_auth_environment_for_claude_worker_sandbox")]
    public void DispatchProcessHostSeedsClaudeAuthEnvironmentForClaudeWorkerSandbox()
{
    var previousKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    var root = CreateTempDirectory();
    try
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "test-claude-key");
        var startInfo = CreateSandboxStartInfo(root);
        var sandboxRoot = Path.Combine(root, ".mcg-sandbox");

        DispatchProcessHost.SeedProviderEnvironment(startInfo, WorkerSandboxProvider.Claude, sandboxRoot);

        Assert.Equal("test-claude-key", startInfo.Environment["ANTHROPIC_API_KEY"]);
        Assert.False(startInfo.Environment.ContainsKey("CODEX_HOME"));
        Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "codex-home")));
        Assert.True(startInfo.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var claudeConfigDir));
        Assert.True(Directory.Exists(claudeConfigDir));
        Assert.Equal("{}\n", File.ReadAllText(Path.Combine(claudeConfigDir!, "settings.json")));
    }
    finally
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", previousKey);
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

    [Xunit.Fact(DisplayName = "DispatchProcessHost_writes_claude_auth_diagnostic_when_api_key_missing")]
    public void DispatchProcessHostWritesClaudeAuthDiagnosticWhenApiKeyMissing()
{
    var previousKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    var root = CreateTempDirectory();
    try
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        var startInfo = CreateSandboxStartInfo(root);
        var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
        var stderrPath = Path.Combine(root, "dispatch.stderr.log");

        DispatchProcessHost.SeedProviderEnvironment(startInfo, WorkerSandboxProvider.Claude, sandboxRoot, stderrPath);

        Assert.False(startInfo.Environment.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.False(startInfo.Environment.ContainsKey("CODEX_HOME"));
        Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "codex-home")));
        Assert.True(startInfo.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var claudeConfigDir));
        Assert.True(Directory.Exists(claudeConfigDir));
        var stderr = File.ReadAllText(stderrPath);
        Assert.Contains("ANTHROPIC_API_KEY is not set", stderr);
        Assert.Contains("Claude", stderr);
    }
    finally
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", previousKey);
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

    [Xunit.Fact(DisplayName = "DispatchProcessHost_worker_stderr_stream_preserves_claude_auth_diagnostic")]
    public void DispatchProcessHostWorkerStderrStreamPreservesClaudeAuthDiagnostic()
{
    var previousKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    var root = CreateTempDirectory();
    try
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        var startInfo = CreateSandboxStartInfo(root);
        var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
        var stderrPath = Path.Combine(root, "dispatch.stderr.log");

        DispatchProcessHost.SeedProviderEnvironment(startInfo, WorkerSandboxProvider.Claude, sandboxRoot, stderrPath);

        using (var stderr = DispatchProcessHost.OpenWorkerStderrStream(stderrPath))
        using (var writer = new StreamWriter(stderr))
        {
            writer.WriteLine("worker stderr");
        }

        var text = File.ReadAllText(stderrPath);
        Assert.Contains("ANTHROPIC_API_KEY is not set", text);
        Assert.Contains("worker stderr", text);
        Assert.True(
            text.IndexOf("ANTHROPIC_API_KEY is not set", StringComparison.Ordinal) <
            text.IndexOf("worker stderr", StringComparison.Ordinal),
            text);
    }
    finally
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", previousKey);
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

    [Xunit.Fact(DisplayName = "DispatchProcessHost_does_not_inject_claude_environment_for_codex_worker_sandbox")]
    public void DispatchProcessHostDoesNotInjectClaudeEnvironmentForCodexWorkerSandbox()
{
    var previousKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    var root = CreateTempDirectory();
    try
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "test-claude-key");
        var startInfo = CreateSandboxStartInfo(root);
        var sandboxRoot = Path.Combine(root, ".mcg-sandbox");

        DispatchProcessHost.SeedProviderEnvironment(startInfo, WorkerSandboxProvider.Codex, sandboxRoot);

        Assert.False(startInfo.Environment.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.False(startInfo.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
        Assert.Equal(Path.Combine(sandboxRoot, "codex-home"), startInfo.Environment["CODEX_HOME"]);
        Assert.True(Directory.Exists(Path.Combine(sandboxRoot, "codex-home")));
    }
    finally
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", previousKey);
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

    [Xunit.Fact(DisplayName = "DispatchFailureClassifier_classifies_claude_401_as_provider_authentication_failure")]
    public void DispatchFailureClassifierClassifiesClaude401AsProviderAuthenticationFailure()
{
    var task = new TaskSpec(TaskId.New(), "Implement with Claude.", AgentRole.Developer);
    var verification = new TaskVerificationRecord(
        "claude --model claude-haiku-4-5 -p prompt",
        "C:\\repo",
        1,
        string.Empty,
        "ERROR: Failed to authenticate: API Error 401 Unauthorized",
        DateTimeOffset.Parse("2026-06-26T12:00:00Z"));

    var outcome = DispatchFailureClassifier.Classify(task, verification);

    Assert.Equal(DispatchOutcomeKind.ProviderAuthentication, outcome.Kind);
    Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    Assert.True(outcome.EvidenceSummary.Contains("401", StringComparison.OrdinalIgnoreCase), outcome.EvidenceSummary);
}

    [Xunit.Fact(DisplayName = "DispatchFailureClassifier_uses_provider_failure_kind_from_worker_provider_parse_outcome")]
    public void DispatchFailureClassifierUsesProviderFailureKindFromWorkerProviderParseOutcome()
{
    var provider = WorkerProviderCatalog.Default().Resolve(ProviderKind.OpenAICodexCli);
    var failureKind = provider.ParseOutcome(new WorkerProviderOutcome(
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Try again later."));

    var outcome = DispatchFailureClassifier.ClassifyProviderFailure(
        failureKind,
        exitCode: 1,
        hasZeroByteOutput: true,
        evidenceSummary: "provider supplied typed failure");

    Assert.Equal(ProviderFailureKind.RateLimit, failureKind);
    Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
    Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
}

    [Xunit.Fact(DisplayName = "DispatchFailureClassifier_identifies_subscription_dispatch_from_typed_provider_identity")]
    public void DispatchFailureClassifierIdentifiesSubscriptionDispatchFromTypedProviderIdentity()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Exercise typed provider dispatch classification",
        [new TaskSpec(TaskId.New(), "Implement the change.", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, [SubscriptionDeveloperAgent()]);
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord(
            WorkerProfileDispatcher.OpenAiSubscriptionProfileName,
            "opaque launcher command",
            "C:\\repo",
            DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
    var verification = new TaskVerificationRecord(
        "opaque launcher command",
        "C:\\repo",
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Try again later.",
        DateTimeOffset.Parse("2026-06-26T12:01:00Z"));

    var outcome = DispatchFailureClassifier.Classify(task, verification);

    Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
}

    [Xunit.Fact(DisplayName = "DispatchFailureClassifier_does_not_infer_subscription_dispatch_from_command_text")]
    public void DispatchFailureClassifierDoesNotInferSubscriptionDispatchFromCommandText()
{
    var task = new TaskSpec(TaskId.New(), "Implement the change.", AgentRole.Developer);
    var verification = new TaskVerificationRecord(
        "codex-cli simulated command text",
        "C:\\repo",
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Try again later.",
        DateTimeOffset.Parse("2026-06-26T12:01:00Z"));

    var outcome = DispatchFailureClassifier.Classify(task, verification);

    Assert.NotEqual(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
}

    [Xunit.Fact(DisplayName = "WorkerProviderCatalog_try_resolve_profile_returns_typed_identity")]
    public void WorkerProviderCatalogTryResolveProfileReturnsTypedIdentity()
{
    var catalog = WorkerProviderCatalog.Default();

    var found = catalog.TryResolveProfile(WorkerProfileDispatcher.OpenAiSubscriptionProfileName, out var provider);

    Assert.True(found);
    Assert.Equal(ProviderKind.OpenAICodexCli, provider.Identity.Kind);
    Assert.False(provider.Capabilities.CanSelfCommit);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_blocks_repo_scoped_skill_targets")]
    public void WorkerProfileDispatcherPreflightBlocksRepoScopedSkillTargets()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Create .agents/skills/example/SKILL.md", [new TaskSpec(TaskId.New(), "Author .agents/skills/example/SKILL.md", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        workingDirectory,
        DateTimeOffset.Parse("2026-06-13T12:00:00Z"),
        sandboxOptions: sandbox);
    var ex = Assert.Throws<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        DateTimeOffset.Parse("2026-06-13T12:00:00Z")));

    Assert.False(preflight.Allowed);
    Assert.Equal("blocked", preflight.CapabilityStatus);
    Assert.Contains(string.Join("\n", preflight.Findings), text => text.Contains(".agents/skills", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("Subscription preflight failed", StringComparison.Ordinal));
    Assert.False(Directory.Exists(promptRoot));
    Assert.True(task.LastDispatch is null);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_allows_repo_scoped_skill_targets_for_full_permission_profile")]
    public void WorkerProfileDispatcherPreflightAllowsRepoScopedSkillTargetsForFullPermissionProfile()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Create .agents/skills/example/SKILL.md", [new TaskSpec(TaskId.New(), "Author .agents/skills/example/SKILL.md", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet-4-6", "medium"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        workingDirectory,
        DateTimeOffset.Parse("2026-06-13T12:00:00Z"),
        sandboxOptions: sandbox);

    Assert.True(preflight.Allowed);
    Assert.Equal("repo-skill-write", preflight.CapabilityStatus);
    Assert.Contains(string.Join("\n", preflight.Findings), text => text.Contains("repo-scoped .agents/skills", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_repo_scoped_skill_full_permission_smoke_creates_and_commits_from_goal_worktree")]
    public void WorkerProfileDispatcherRepoScopedSkillFullPermissionSmokeCreatesAndCommitsFromGoalWorktree()
{
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Create .agents/skills/smoke/SKILL.md", [new TaskSpec(TaskId.New(), "Author .agents/skills/smoke/SKILL.md and commit it.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("repo-skill-smoke", "claude-sonnet-4-6", "medium"));
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile(
            "repo-skill-smoke",
            "Write-Output 'subscription model {subscriptionModelName}'; Write-Output 'permission --permission-mode bypassPermissions'; New-Item -ItemType Directory -Force '.agents/skills/smoke' | Out-Null; Set-Content -Path '.agents/skills/smoke/SKILL.md' -Value \"---`nname: smoke`ndescription: Smoke test skill.`n---`n`n# Smoke`n\"; git add .agents/skills/smoke/SKILL.md; git commit -m 'Add smoke skill'; Write-Output 'WORKER_RESULT:'; Write-Output 'files: .agents/skills/smoke/SKILL.md'; Write-Output 'commands: git add .agents/skills/smoke/SKILL.md; git commit -m Add smoke skill'; Write-Output 'tests: repo-skill smoke committed'; Write-Output 'blockers: none'; Write-Output 'model_fit: deterministic full-permission profile - adequate - repo skill write smoke'; Write-Output 'skills: skill-creator'; Write-Output 'confidence: high'; Write-Output 'END_WORKER_RESULT'")
    ]);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var dispatchedAt = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        profiles,
        worktree,
        dispatchedAt,
        sandboxOptions: sandbox);
    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        profiles,
        promptRoot,
        worktree,
        dispatchedAt,
        sandboxOptions: sandbox);
    var result = RunPowerShellCommand(worktree, task.LastDispatch!.Command);

    Assert.True(preflight.Allowed);
    Assert.Equal("repo-skill-write", preflight.CapabilityStatus);
    Assert.Equal(0, result.ExitCode);
    Assert.True(result.StandardOutput.Contains("WORKER_RESULT:", StringComparison.Ordinal));
    Assert.True(File.Exists(Path.Combine(worktree, ".agents", "skills", "smoke", "SKILL.md")));
    Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
    Assert.Equal("Add smoke skill", ReadGit(worktree, ["log", "-1", "--pretty=%s"]));
    var committedFiles = ReadGit(worktree, ["show", "--name-only", "--pretty=", "HEAD"]);
    Assert.True(committedFiles.Contains(".agents/skills/smoke/SKILL.md", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_blocks_missing_required_local_skills")]
    public void WorkerProfileDispatcherPreflightBlocksMissingRequiredLocalSkills()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(Path.Combine(workingDirectory, ".agents", "skills"));
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Implement .NET build verification.", [new TaskSpec(TaskId.New(), "Run dotnet test for the implementation.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        workingDirectory,
        DateTimeOffset.Parse("2026-06-13T12:00:00Z"));
    var ex = Assert.Throws<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        DateTimeOffset.Parse("2026-06-13T12:00:00Z")));

    Assert.False(preflight.Allowed);
    Assert.Contains(string.Join("\n", preflight.Findings), text => text.Contains("missing required local skill", StringComparison.Ordinal));
    Assert.Contains(string.Join("\n", preflight.Findings), text => text.Contains("dotnet-windows-build-hygiene", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("missing required local skill", StringComparison.Ordinal));
    Assert.False(Directory.Exists(promptRoot));
    Assert.True(task.LastDispatch is null);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_ready_batch_skips_preflight_blocked_tasks")]
    public void WorkerProfileDispatcherReadyBatchSkipsPreflightBlockedTasks()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var blockedTask = new TaskSpec(TaskId.New(), "Author .agents/skills/example/SKILL.md", AgentRole.Developer);
    var allowedTask = new TaskSpec(TaskId.New(), "Update src/example.txt", AgentRole.Developer);
    var goal = kernel.CreateGoal("Batch preflight", [blockedTask, allowedTask]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);

    var results = WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(
        kernel,
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        DateTimeOffset.Parse("2026-06-13T12:00:00Z"));

    Assert.Equal(1, results.Count);
    Assert.Equal(allowedTask.Id, results.Single().Task.Id);
    Assert.True(goal.Tasks.Single(task => task.Id == blockedTask.Id).LastDispatch is null);
    Assert.True(goal.Tasks.Single(task => task.Id == allowedTask.Id).LastDispatch is not null);
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
    var profile = new WorkerProfile("codex-cli", "codex exec");

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
    var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
    var profile = new WorkerProfile("codex-cli", "codex exec --sandbox workspace-write --cd {workingDirectory}");

    var result = WorkerProfileDispatcher.PrepareTask(kernel, goal, task, profile, promptRoot, workingDirectory, DateTimeOffset.UtcNow);

    var prompt = File.ReadAllText(result.PromptPath);
    Assert.Contains(prompt, text => text.Contains($"Working directory, use absolute paths: {workingDirectory}", StringComparison.Ordinal));
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
    Assert.Contains(prompt, text => text.Contains("Current target context:", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains($"- Branch: {branch}", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains($"- HEAD commit: {head}", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("latest developer retry feedback", StringComparison.Ordinal));

    var reviewerDispatch = WorkerProfileDispatcher.PrepareTask(
        kernel,
        goal,
        reviewer,
        profile,
        promptRoot,
        worktree,
        DateTimeOffset.Parse("2026-06-27T12:01:30Z"));

    var reviewerPrompt = File.ReadAllText(reviewerDispatch.PromptPath);
    Assert.Contains(reviewerPrompt, text => text.Contains("Current target context:", StringComparison.Ordinal));
    Assert.Contains(reviewerPrompt, text => text.Contains($"- Branch: {branch}", StringComparison.Ordinal));
    Assert.Contains(reviewerPrompt, text => text.Contains($"- HEAD commit: {head}", StringComparison.Ordinal));
    Assert.Contains(reviewerPrompt, text => text.Contains("latest developer retry feedback", StringComparison.Ordinal));

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
    Assert.Contains(redispatchPrompt, text => text.Contains($"- Branch: {branch}", StringComparison.Ordinal));
    Assert.Contains(redispatchPrompt, text => text.Contains($"- HEAD commit: {redispatchHead}", StringComparison.Ordinal));
    Assert.True(!redispatchPrompt.Contains($"- HEAD commit: {head}", StringComparison.Ordinal));
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
        sandboxOptions: sandbox);

    Assert.Equal(assignedAgent.Id, task.AssignedAgentId);
    Assert.Equal("claude-cli", task.LastDispatch!.WorkerName);
    Assert.Equal("Anthropic", task.LastDispatch.ProviderName);
    Assert.Equal("claude-haiku-4-5", task.LastDispatch.ModelName);
}
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_prepares_subscription_tasks_by_assigned_provider")]
    public void WorkerProfileDispatcherPreparesSubscriptionTasksByAssignedProvider()
{
    // Hermetic: clear the operator's MCG_WORKER_SANDBOX so this asserts the default dispatch mode
    // regardless of how the suite was launched (see ClearWorkerSandboxEnv).
    using var _sandboxEnv = ClearWorkerSandboxEnv();
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

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_ready_batch_dispatches_Tester_after_persisted_Developer_completion_without_process_start")]
    public void WorkerProfileDispatcherReadyBatchDispatchesTesterAfterPersistedDeveloperCompletionWithoutProcessStart()
{
    using var _sandboxEnv = ClearWorkerSandboxEnv();
    var root = CreateTempDirectory();
    File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Dispatch Tester after Developer completion");
    var agents = new[]
    {
        TestSubscriptionAgent("planner", "Planner", AgentRole.Planner),
        TestSubscriptionAgent("researcher", "Researcher", AgentRole.Researcher),
        TestSubscriptionAgent("developer", "Developer", AgentRole.Developer),
        TestSubscriptionAgent("tester", "Tester", AgentRole.Tester)
    };
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("test-subscription", "worker --model {subscriptionModelName} --reasoning {subscriptionReasoningEffort} --prompt {promptPath} --cd {workingDirectory}")
    ]);
    kernel.ActivateGoal(goal.Id, agents);
    foreach (var task in goal.Tasks.Where(task =>
        task.RequiredRole is AgentRole.Planner or AgentRole.Researcher or AgentRole.Developer))
    {
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, $"{task.RequiredRole} completed in persisted state.");
    }

    var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles);
    var readiness = DispatchReadinessEvaluator.EvaluateDispatchReadiness(goal, plan, DateTimeOffset.UtcNow);
    var batch = GoalManagementCommandService.SubscriptionDispatchReadyBatch(
        kernel,
        CreateRefinedWorkspace(root),
        goal,
        agents,
        profiles);

    Assert.IsType<DispatchReadinessReady>(readiness);
    Assert.Single(batch.Dispatches);
    var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
    Assert.Equal(tester.Id, batch.Dispatches.Single().Task.Id);
    Assert.Equal(WorkTaskStatus.Running, tester.Status);
    Assert.Null(tester.LastProcess);
}

private static AgentDefinition TestSubscriptionAgent(string id, string name, AgentRole role) =>
    new(
        new AgentId(id),
        name,
        role,
        new ModelProfile("Test", "test-model", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("test-subscription", "test-model", "low"));

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_ready_batch_records_provider_from_claude_launcher")]
    public void WorkerProfileDispatcherReadyBatchRecordsProviderFromClaudeLauncher()
{
    using var _sandboxEnv = ClearWorkerSandboxEnv();
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Dispatch Claude write worker",
        [new TaskSpec(TaskId.New(), "Update src/example.txt.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-haiku-4-5"));
    kernel.ActivateGoal(goal.Id, [agent]);

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        goal.Tasks.Single(),
        [agent],
        WorkerProfileCatalog.Default(),
        workingDirectory,
        dispatchedAt);
    Assert.True(preflight.Allowed, string.Join("\n", preflight.Findings));

    var results = WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(
        kernel,
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt);

    Assert.Single(results);
    var developer = goal.Tasks.Single();
    Assert.Equal("claude-cli", developer.LastDispatch!.WorkerName);
    Assert.Contains(developer.LastDispatch.Command, text => text.Contains("claude --model 'claude-haiku-4-5' --permission-mode 'bypassPermissions'", StringComparison.Ordinal));
    Assert.DoesNotContain(" -p", developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain("Get-Content -Raw", developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Equal("Anthropic", developer.LastDispatch.ProviderName);
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
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("codex exec --skip-git-repo-check --oss --local-provider ollama", StringComparison.Ordinal));
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

    Assert.Contains(developer.LastDispatch!.Command, text => text.Contains("--model 'gpt-5.5'", StringComparison.Ordinal));
    Assert.Contains(developer.LastDispatch.Command, text => text.Contains("model_reasoning_effort='high'", StringComparison.Ordinal));
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
    Assert.Equal("gpt-5.3-codex-spark", task.LastDispatch.ModelName);
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("--model 'gpt-5.3-codex-spark'", StringComparison.Ordinal));
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
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("codex exec", StringComparison.Ordinal));
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("--model 'gpt-5.3-codex-spark'", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "SubscriptionDispatch_override_profile_replaces_agent_default")]
    public void SubscriptionDispatchOverrideProfileReplacesAgentDefault()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
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
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
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
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("model_reasoning_effort='low'", StringComparison.Ordinal));
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
    Assert.Equal("gpt-5.5", task.LastDispatch.ModelName);
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("--model 'gpt-5.5'", StringComparison.Ordinal));
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
    Assert.Contains(task.LastDispatch.Command, text => text.Contains("claude --model 'claude-sonnet' --permission-mode 'plan'", StringComparison.Ordinal));
    Assert.DoesNotContain(" -p", task.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain("Get-Content -Raw", task.LastDispatch.Command, StringComparison.Ordinal);
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

    Assert.Contains(ex.Message, text => text.Contains("Subscription preflight failed", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("only echoes prompt path", StringComparison.Ordinal));
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

    Assert.Contains(ex.Message, text => text.Contains("Subscription preflight failed", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("not patch-capable", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("missing", StringComparison.Ordinal));
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

    [Xunit.Fact(DisplayName = "SubscriptionPlan_includes_selected_worker_route_for_ready_paid_task")]
    public void SubscriptionPlanIncludesSelectedWorkerRouteForReadyPaidTask()
{
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Update a dashboard label.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Route ready paid task", [task]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);

    var plan = SubscriptionPlanBuilder.Build(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        _ => 1200);

    var item = plan.Items.Single();
    Assert.True(item.CanPrepare);
    Xunit.Assert.NotNull(item.Route);
    Assert.Equal(WorkerRouteDisposition.Selected, item.Route!.Disposition);
    Xunit.Assert.Contains("ready for subscription dispatch", item.Route.Recommendation, StringComparison.Ordinal);
    Xunit.Assert.Contains(item.Route.Reasons, text => text.Contains("provider=OpenAI", StringComparison.Ordinal));
    Xunit.Assert.Contains(item.Route.Reasons, text => text.Contains("profile=codex-cli", StringComparison.Ordinal));
    Xunit.Assert.Contains(item.Route.Reasons, text => text.Contains("estimated cost-guard prompt chars=1200", StringComparison.Ordinal));
    Xunit.Assert.Contains(item.Route.Reasons, text => text.Contains("patch-capable profile", StringComparison.Ordinal));
    Xunit.Assert.Contains(item.Route.Alternatives, text => text.Contains("local Ollama/qwen", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_blocks_developer_route_without_patch_capable_profile")]
    public void SubscriptionPlanBlocksDeveloperRouteWithoutPatchCapableProfile()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Route blocked developer profile");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var profiles = WorkerProfileCatalog.Default().Upsert(new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} {promptPath}"));

    var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles);

    var developer = plan.Items.First(item => item.Role == AgentRole.Developer);
    Assert.False(developer.CanPrepare);
    Xunit.Assert.NotNull(developer.Route);
    Assert.Equal(WorkerRouteDisposition.Blocked, developer.Route!.Disposition);
    Xunit.Assert.Contains(developer.Route.Reasons, text => text.Contains("provider=OpenAI", StringComparison.Ordinal));
    Xunit.Assert.Contains(developer.Route.Reasons, text => text.Contains("patch capability missing", StringComparison.Ordinal));
    Xunit.Assert.Contains("not patch-capable", developer.Route.Recommendation, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_routes_equivalent_tasks_differently_for_patch_capability")]
    public void SubscriptionPlanRoutesEquivalentTasksDifferentlyForPatchCapability()
{
    var kernel = new AgentOrchestratorKernel();
    var developerTask = new TaskSpec(TaskId.New(), "Inspect the same source file.", AgentRole.Developer);
    var reviewerTask = new TaskSpec(TaskId.New(), "Inspect the same source file.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Route by required capability", [developerTask, reviewerTask]);
    IReadOnlyList<AgentDefinition> agents =
    [
        new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("custom-agent", "gpt-5.5", "low")),
        new AgentDefinition(
            new AgentId("reviewer"),
            "Reviewer",
            AgentRole.Reviewer,
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("custom-agent", "gpt-5.5", "low"))
    ];
    var profiles = WorkerProfileCatalog.Default().Upsert(new WorkerProfile("custom-agent", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} {promptPath}"));
    kernel.ActivateGoal(goal.Id, agents);

    var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles);

    var developer = plan.Items.Single(item => item.Role == AgentRole.Developer);
    var reviewer = plan.Items.Single(item => item.Role == AgentRole.Reviewer);
    Assert.False(developer.CanPrepare);
    Assert.True(reviewer.CanPrepare);
    Assert.Equal(WorkerRouteDisposition.Blocked, developer.Route!.Disposition);
    Assert.Equal(WorkerRouteDisposition.Selected, reviewer.Route!.Disposition);
    Xunit.Assert.Contains(developer.Route.Reasons, text => text.Contains("patch capability missing", StringComparison.Ordinal));
    Xunit.Assert.DoesNotContain(reviewer.Route.Reasons, text => text.Contains("patch capability missing", StringComparison.Ordinal));
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
    Assert.Equal(developer.SubscriptionRetryAfter, plan.CapacitySchedule.NextRetryAfter);
    Assert.Equal(1, plan.CapacitySchedule.DeferredCount);
    Assert.False(item.CanPrepare);
    Assert.Equal(developer.SubscriptionRetryAfter, item.RetryAfter);
    var capacityAction = plan.CapacitySchedule.Actions.Single(action => action.TaskId == developer.Id.Value);
    Assert.Equal(ProviderCapacityDisposition.Deferred, capacityAction.Disposition);
    Assert.True(capacityAction.Alternatives.Any(alternative => alternative.Contains("different provider", StringComparison.OrdinalIgnoreCase)));
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
    Assert.True(risk.IsAnomalous);
    Assert.False(risk.TaskCountExceedsThreshold);
    Assert.False(risk.UsesComplexPaidModel);
    Assert.Equal("large paid subscription start", SubscriptionPromptCostGuard.BuildInlineLabel(risk));
    Assert.Contains(ex.Message, text => text.Contains("--confirm-large-paid-subscription-start", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("thresholds 18000 chars or 3 task(s)", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("Paid subscription start requires explicit confirmation", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("Inspect the generated prompt before paid subscription start", StringComparison.Ordinal));
    Assert.True(task.LastDispatch is null);
}
    [Xunit.Fact(DisplayName = "SubscriptionPromptCostGuard_paid_batch_fanout_with_small_prompts_is_advisory_not_blocking")]
    public void SubscriptionPromptCostGuardPaidBatchFanoutWithSmallPromptsIsAdvisoryNotBlocking()
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
    Assert.True(risk is not null);
    Assert.Equal(5, risk!.TaskCount);
    Assert.Equal(2500, risk.PromptCharacterCount);
    Assert.False(risk.PromptExceedsBatchThreshold);
    Assert.False(risk.HasOversizedPrompt);
    Assert.True(risk.TaskCountExceedsThreshold);
    Assert.False(risk.UsesComplexPaidModel);
    Assert.False(risk.IsAnomalous);
    Assert.Equal("paid subscription fanout", SubscriptionPromptCostGuard.BuildInlineLabel(risk));
    Assert.True(risk.Details.Any(detail => detail.Contains("Paid task count 5 exceeds 3", StringComparison.Ordinal)));
    // Fan-out with small prompts is advisory, not an anomaly, so the start is not blocked.
    SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(risk, confirmed: false);
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

    // Substantial prior evidence engages the allowance. The estimate is capped near the allowance
    // and is line-ending-sensitive (CRLF on Windows vs LF on Linux), so assert it is clearly
    // substantial rather than pinned to the exact boundary; the behavioral check is risk == null.
    Assert.True(AgentOrchestratorKernel.EstimatePriorTaskEvidenceCharacterCount(goal, nextTask.Id) > PaidPromptThresholds.PriorTaskEvidenceAllowance / 2);
    Xunit.Assert.Null(risk);
}
    [Xunit.Fact(DisplayName = "SubscriptionPromptCostGuard_proportionate_batch_total_is_advisory_not_blocking")]
    public void SubscriptionPromptCostGuardProportionateBatchTotalIsAdvisoryNotBlocking()
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
    // 24000 across 3 Complex tasks (each within the complexity band) exceeds the soft batch ceiling
    // but is below the anomaly batch ceiling (2x), so it is advisory only and does not block.
    Assert.True(risk is not null);
    Assert.Equal(24000, risk!.PromptCharacterCount);
    Assert.True(risk.PromptExceedsBatchThreshold);
    Assert.False(risk.HasOversizedPrompt);
    Assert.False(risk.TaskCountExceedsThreshold);
    Assert.True(risk.UsesComplexPaidModel);
    Assert.False(risk.IsAnomalous);
    Assert.Equal("large paid subscription start", SubscriptionPromptCostGuard.BuildInlineLabel(risk));
    SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(risk, confirmed: false);
}
    [Xunit.Fact(DisplayName = "SubscriptionPromptCostGuard_large_but_proportionate_prompt_is_advisory_anomalous_prompt_blocks")]
    public void SubscriptionPromptCostGuardLargeButProportionatePromptIsAdvisoryAnomalousPromptBlocks()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Plan a paid subscription task",
        [new TaskSpec(TaskId.New(), "Do substantial paid subscription work.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-codex"));
    kernel.ActivateGoal(goal.Id, [agent]);

    // 11000 chars: over the soft prompt ceiling but under the anomaly ceiling for either complexity
    // classification -> advisory only, does NOT block (the behavior change: no rote confirm flag).
    var proportionate = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
        goal, [agent], WorkerProfileCatalog.Default(), _ => 11000);
    Assert.True(proportionate is not null);
    Assert.True(proportionate!.HasOversizedPrompt);
    Assert.False(proportionate.IsAnomalous);
    SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(proportionate, confirmed: false);

    // 20000 chars: disproportionate to complexity -> anomaly -> requires explicit confirmation.
    var anomalous = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
        goal, [agent], WorkerProfileCatalog.Default(), _ => 20000);
    Assert.True(anomalous!.IsAnomalous);
    Assert.Throws<InvalidOperationException>(() => SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(anomalous, confirmed: false));
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
    Assert.Contains(ex.Message, text => text.Contains("Subscription preflight failed", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("subscription retry deferred until", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_blocks_same_provider_tasks_during_provider_cooldown")]
    public void WorkerProfileDispatcherBlocksSameProviderTasksDuringProviderCooldown()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var failureAt = DateTimeOffset.UtcNow;
    var retryTime = failureAt.AddHours(2);
    var kernel = new AgentOrchestratorKernel();
    var limitedTask = new TaskSpec(TaskId.New(), "Hit a subscription usage limit", AgentRole.Developer);
    var sameProviderTask = new TaskSpec(TaskId.New(), "Continue work on the same provider", AgentRole.Developer);
    var goal = kernel.CreateGoal("Block same provider while cooling down", [limitedTask, sameProviderTask]);
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    kernel.RecordTaskDispatch(
        goal.Id,
        limitedTask.Id,
        new TaskDispatchRecord(
            "codex-cli",
            "codex exec",
            workingDirectory,
            failureAt,
            "OpenAI",
            "gpt-5.5"));
    kernel.RecordDispatchExecutionResult(goal.Id, limitedTask.Id, new TaskVerificationRecord(
        "codex exec",
        workingDirectory,
        1,
        string.Empty,
        $"ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at {retryTime:h:mm tt}.",
        failureAt));

    var plan = SubscriptionPlanBuilder.Build(goal, agents, WorkerProfileCatalog.Default());
    var sameProviderItem = plan.Items.Single(item => item.TaskId == sameProviderTask.Id.Value);
    var providerBudget = plan.ProviderBudgets.Single(item => item.ProviderName == "OpenAI");
    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        sameProviderTask,
        agents,
        WorkerProfileCatalog.Default(),
        workingDirectory,
        failureAt.AddMinutes(10));
    var results = WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(
        kernel,
        goal,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        failureAt.AddMinutes(10));

    Assert.False(sameProviderItem.CanPrepare);
    Assert.Equal(limitedTask.SubscriptionRetryAfter, sameProviderItem.RetryAfter);
    Assert.Equal(limitedTask.SubscriptionRetryAfter, plan.CapacitySchedule.NextRetryAfter);
    Assert.True(plan.CapacitySchedule.Actions.Any(action =>
        action.TaskId == sameProviderTask.Id.Value &&
        action.Disposition == ProviderCapacityDisposition.Deferred &&
        action.Alternatives.Any(alternative => alternative.Contains("different provider", StringComparison.OrdinalIgnoreCase))));
    Assert.True(providerBudget.IsCoolingDown);
    Assert.Equal(limitedTask.SubscriptionRetryAfter, providerBudget.RetryAfter);
    Assert.Equal(TaskDisplayNumber.Resolve(goal, limitedTask.Id), providerBudget.SourceTaskNumber);
    Assert.Equal(1, providerBudget.RecoverableLimitFailureCount);
    Assert.Contains(sameProviderItem.Detail, text => text.Contains("Provider OpenAI is cooling down", StringComparison.Ordinal));
    Assert.Contains(sameProviderItem.Detail, text => text.Contains(TaskDisplayNumber.Resolve(goal, limitedTask.Id).ToString(), StringComparison.Ordinal));
    Assert.False(preflight.Allowed);
    Assert.True(preflight.Findings.Any(finding => finding.Contains("provider OpenAI is cooling down", StringComparison.Ordinal)));
    Assert.False(results.Any(result => result.Task.Id == sameProviderTask.Id));
    Assert.True(sameProviderTask.LastDispatch is null);
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_keeps_unrelated_provider_out_of_cooldown")]
    public void SubscriptionPlanKeepsUnrelatedProviderOutOfCooldown()
{
    var workingDirectory = CreateTempDirectory();
    var failureAt = DateTimeOffset.UtcNow;
    var retryTime = failureAt.AddHours(2);
    var kernel = new AgentOrchestratorKernel();
    var openAiTask = new TaskSpec(TaskId.New(), "Hit OpenAI usage limit", AgentRole.Developer);
    var anthropicTask = new TaskSpec(TaskId.New(), "Continue on Anthropic", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Keep unrelated provider usable", [openAiTask, anthropicTask]);
    IReadOnlyList<AgentDefinition> agents =
    [
        new AgentDefinition(
            new AgentId("openai-developer"),
            "OpenAI developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5")),
        new AgentDefinition(
            new AgentId("anthropic-reviewer"),
            "Anthropic reviewer",
            AgentRole.Reviewer,
            new ModelProfile("Anthropic", "claude-haiku-4-5", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-haiku-4-5"))
    ];
    kernel.ActivateGoal(goal.Id, agents);
    kernel.RecordTaskDispatch(
        goal.Id,
        openAiTask.Id,
        new TaskDispatchRecord(
            "codex-cli",
            "codex exec",
            workingDirectory,
            failureAt,
            "OpenAI",
            "gpt-5.5"));
    kernel.RecordDispatchExecutionResult(goal.Id, openAiTask.Id, new TaskVerificationRecord(
        "codex exec",
        workingDirectory,
        1,
        string.Empty,
        $"ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at {retryTime:h:mm tt}.",
        failureAt));

    var plan = SubscriptionPlanBuilder.Build(goal, agents, WorkerProfileCatalog.Default());
    var openAiBudget = plan.ProviderBudgets.Single(item => item.ProviderName == "OpenAI");
    var anthropicBudget = plan.ProviderBudgets.Single(item => item.ProviderName == "Anthropic");
    var anthropicItem = plan.Items.Single(item => item.TaskId == anthropicTask.Id.Value);

    Assert.True(openAiBudget.IsCoolingDown);
    Assert.False(anthropicBudget.IsCoolingDown);
    Xunit.Assert.Null(anthropicBudget.RetryAfter);
    Assert.Equal(0, anthropicBudget.DeferredCount);
    Xunit.Assert.Null(anthropicItem.RetryAfter);
    Assert.False(anthropicItem.Detail.Contains("cooling down", StringComparison.OrdinalIgnoreCase));
}

    [Xunit.Fact(DisplayName = "Dashboard_task_summary_exposes_worker_result_skill_usage")]
    public void DashboardTaskSummaryExposesWorkerResultSkillUsage()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Implement with selected skills", AgentRole.Developer);
    var goal = kernel.CreateGoal("Expose skill evidence", [task]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "worker refresh",
        root,
        0,
        "Done." + Environment.NewLine + WorkerResultBlock(
            "src/Feature.cs",
            "dotnet test --filter Feature",
            "Passed: 1",
            "abc123",
            skills: "dotnet-windows-build-hygiene, orchestrator-dogfood"),
        string.Empty,
        DateTimeOffset.UtcNow));

    var summary = DashboardResponseMapper.ToTaskSummaryDto(goal, task);

    Assert.True(summary.HasWorkerResultSkillEvidence);
    Assert.True(summary.WorkerResultSkills!.Any(skill => skill == "dotnet-windows-build-hygiene"));
    Assert.True(summary.WorkerResultSkills!.Any(skill => skill == "orchestrator-dogfood"));
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
    Assert.Contains(ex.Message, text => text.Contains("Subscription preflight failed", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("repeated recoverable subscription limits require operator review", StringComparison.Ordinal));

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

    Assert.Contains(ex.Message, text => text.Contains("Subscription preflight failed", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("goal workspace", StringComparison.Ordinal));
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

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_blocks_dirty_file_role_worktree")]
    public void WorkerProfileDispatcherPreflightBlocksDirtyFileRoleWorktree()
{
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-12T10:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Implement the feature with a clean workspace");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    File.WriteAllText(Path.Combine(worktree, "dirty.txt"), "uncommitted");

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        agents,
        WorkerProfileCatalog.Default(),
        worktree,
        dispatchedAt);
    var ex = Assert.Throws<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        worktree,
        dispatchedAt));

    Assert.False(preflight.Allowed);
    Assert.True(preflight.Findings.Any(finding => finding.Contains("worktree has 1 uncommitted change", StringComparison.Ordinal)));
    Assert.True(preflight.Findings.Any(finding => finding.Contains("build environment: goal lease not yet created", StringComparison.Ordinal)));
    Assert.True(preflight.Findings.Any(finding => finding.Contains(Path.Combine("slots", "slot-"), StringComparison.OrdinalIgnoreCase)));
    Assert.Contains(ex.Message, text => text.Contains("worktree has 1 uncommitted change", StringComparison.Ordinal));
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.True(task.LastDispatch is null);
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

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_cancel_kills_tracked_tree_and_allows_redispatch_without_paid_start")]
    public void BackgroundDispatchRunnerCancelKillsTrackedTreeAndAllowsRedispatchWithoutPaidStart()
{
    var root = CreateTempDirectory();
    var now = DateTimeOffset.Parse("2026-06-21T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Stop worker tree and redispatch");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var dispatch = new TaskDispatchRecord("codex-cli", "codex exec prompt", root, now);
    kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
    kernel.RecordTaskProcessStarted(
        goal.Id,
        task.Id,
        new TaskProcessRecord(
            111,
            dispatch.Command,
            dispatch.WorkingDirectory,
            "out.log",
            "err.log",
            "exit.txt",
            now,
            null,
            null,
            OwnedProcessIds: [111, 222]));
    var running = new HashSet<int> { 111, 222 };
    var killed = new List<int>();
    var runner = new BackgroundDispatchRunner(
        new TestClock(now.AddMinutes(1)),
        isStillRunning: pid => running.Contains(pid),
        tryKillOwnedProcess: pid =>
        {
            killed.Add(pid);
            running.Remove(pid);
            return true;
        });

    runner.CancelLatestProcess(kernel, goal.Id, task.Id);
    kernel.RequeueInterruptedDispatch(goal.Id, task.Id, "Redispatch after stopped worker tree.");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt 2", root, now.AddMinutes(2)));

    Assert.True(killed.SequenceEqual([111, 222]));
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.Equal("codex exec prompt 2", task.LastDispatch!.Command);
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

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_reconcile_completes_dead_exiting_worker_from_exit_file")]
    public void BackgroundDispatchRunnerReconcileCompletesDeadExitingWorkerFromExitFile()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-25T06:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Complete dead exiting worker from exit file");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, "WORKER_RESULT:");
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, now.AddMinutes(-5)));
    var process = new TaskProcessRecord(
        999999,
        "codex exec prompt",
        root,
        stdout,
        stderr,
        exit,
        now.AddMinutes(-5),
        null,
        null,
        OwnedProcessIds: [111, 222]);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    WriteHeartbeat(
        process,
        now.AddMinutes(-1),
        now.AddMinutes(-1),
        "exiting",
        14,
        0,
        childPid: null,
        ownedPids: [111, 222],
        exitFileExists: true);

    var outcome = new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
        .ReconcileLatestProcess(kernel, goal.Id, task.Id);

    Assert.NotNull(outcome.Verification);
    Assert.Equal(0, outcome.ProcessRecord.ExitCode);
    Assert.Equal(clock.UtcNow, outcome.ProcessRecord.CompletedAt);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_sweep_completes_role_boundary_exited_worker_from_exit_file")]
    public void BackgroundDispatchRunnerSweepCompletesRoleBoundaryExitedWorkerFromExitFile()
    {
        var root = CreateTempDirectory();
        var stdout = Path.Combine(root, "out.log");
        var stderr = Path.Combine(root, "err.log");
        var exit = Path.Combine(root, "worker.exit.txt");
        var now = DateTimeOffset.Parse("2026-06-25T06:30:00Z");
        var clock = new TestClock(now);
        File.WriteAllText(stdout, "WORKER_RESULT:");
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");

        var developerId = TaskId.New().Value;
        var testerId = TaskId.New().Value;
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    "goal-role-boundary",
                    "Complete role boundary worker",
                    GoalStatus.Active,
                    [
                        new TaskSnapshot(
                            developerId,
                            "Developer task exited at role boundary",
                            AgentRole.Developer,
                            WorkTaskStatus.Assigned,
                            "agent-dev",
                            null,
                            null,
                            [],
                            new TaskDispatchSnapshot("codex-cli", "codex exec prompt", root, now.AddMinutes(-5)),
                            new TaskProcessSnapshot(
                                999999,
                                "codex exec prompt",
                                root,
                                stdout,
                                stderr,
                                exit,
                                now.AddMinutes(-5),
                                now.AddMinutes(-1),
                                0,
                                OwnedProcessIds: [111, 222])),
                        new TaskSnapshot(
                            testerId,
                            "Tester task should be next",
                            AgentRole.Tester,
                            WorkTaskStatus.Assigned,
                            "agent-test",
                            null,
                            null,
                            [],
                            null,
                            null)
                    ],
                    [])
            ],
            []));
        var goal = kernel.Goals.Single();
        var developer = goal.Tasks.First(task => task.Id.Value == developerId);
        var tester = goal.Tasks.First(task => task.Id.Value == testerId);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(WorkspaceExists: true)));

        var swept = new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
            .SweepExitedProcesses(kernel);
        var nextActions = kernel.BuildNextActions(goal.Id).Items;

        Assert.Equal(1, swept);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(0, developer.LastVerification!.ExitCode);
        Assert.Equal(clock.UtcNow, developer.LastProcess!.CompletedAt);
        Assert.DoesNotContain(nextActions, action => action.TaskId == developer.Id);
        Assert.Contains(nextActions, action => action.TaskId == tester.Id && action.Kind == NextActionKind.RunAssignedTask);
    }

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_reconcile_ignores_stale_child_pid_when_exit_file_exists")]
    public void BackgroundDispatchRunnerReconcileIgnoresStaleChildPidWhenExitFileExists()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-25T06:05:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Hold live child despite exit file");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, "WORKER_RESULT:");
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, now.AddMinutes(-5)));
    var process = new TaskProcessRecord(999999, "codex exec prompt", root, stdout, stderr, exit, now.AddMinutes(-5), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    WriteHeartbeat(
        process,
        now.AddMinutes(-1),
        now.AddMinutes(-1),
        "running",
        14,
        0,
        childPid: 333,
        exitFileExists: true);

    var outcome = new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
        .ReconcileLatestProcess(kernel, goal.Id, task.Id);

    Assert.NotNull(outcome.Verification);
    Assert.Equal(0, outcome.ProcessRecord.ExitCode);
    Assert.Equal(clock.UtcNow, outcome.ProcessRecord.CompletedAt);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_reconcile_holds_exit_file_when_owned_pid_still_running")]
    public void BackgroundDispatchRunnerReconcileHoldsExitFileWhenOwnedPidStillRunning()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-25T06:10:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Hold live owned process despite exit file");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, "WORKER_RESULT:");
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, now.AddMinutes(-5)));
    var process = new TaskProcessRecord(
        999998,
        "codex exec prompt",
        root,
        stdout,
        stderr,
        exit,
        now.AddMinutes(-5),
        null,
        null,
        OwnedProcessIds: [444, 555]);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    WriteHeartbeat(
        process,
        now.AddMinutes(-1),
        now.AddMinutes(-1),
        "exiting",
        14,
        0,
        childPid: null,
        ownedPids: [444, 555],
        exitFileExists: true);

    var outcome = new BackgroundDispatchRunner(clock, isStillRunning: pid => pid == 444)
        .ReconcileLatestProcess(kernel, goal.Id, task.Id);

    Assert.Null(outcome.Verification);
    Assert.Null(outcome.ProcessRecord.ExitCode);
    Assert.Null(outcome.ProcessRecord.CompletedAt);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
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

    [Xunit.Theory(DisplayName = "BackgroundDispatchRunner_refresh_uses_provider_identity_for_codex_exit_file_behavior")]
    [Xunit.InlineData("codex-cli", "subscription worker prompt", true)]
    [Xunit.InlineData("claude-cli", "claude prompt", false)]
    [Xunit.InlineData("custom-agent", "codex exec prompt", false)]
    public void BackgroundDispatchRunnerRefreshUsesProviderIdentityForCodexExitFileBehavior(
        string workerProfileName,
        string command,
        bool expectedExitFile)
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "exit.txt");
    var now = DateTimeOffset.Parse("2026-06-11T16:10:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Detect profile-specific wrapper behavior");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, "Implemented the change.");
    File.WriteAllText(stderr, "Tokens used: input=123 output=45");
    File.SetLastWriteTimeUtc(stdout, now.AddMinutes(-3).UtcDateTime);
    File.SetLastWriteTimeUtc(stderr, now.AddMinutes(-3).UtcDateTime);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(workerProfileName, command, root, now.AddMinutes(-5)));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, command, root, stdout, stderr, exit, now.AddMinutes(-5), null, null));

    var refreshed = new BackgroundDispatchRunner(clock, TimeSpan.FromMinutes(2), _ => true)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(expectedExitFile, File.Exists(exit));
    if (expectedExitFile)
    {
        Assert.Equal(1, refreshed.ExitCode);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Contains(task.LastVerification!.StandardError, text => text.Contains("wrapper appears hung after codex final output", StringComparison.Ordinal));
    }
    else
    {
        Assert.Null(refreshed.ExitCode);
        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.Null(task.LastVerification);
    }
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
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", root, now.AddMinutes(-40)));
    var process = new TaskProcessRecord(999999, "claude prompt", root, stdout, stderr, exit, now.AddMinutes(-40), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    WriteHeartbeat(process, now.AddMinutes(-31), now.AddMinutes(-31), "running", 0, 0);

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

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_startup_hang_fast_path_fires_when_cpu_idle_and_no_output")]
    public void BackgroundDispatchRunnerStartupHangFastPathFiresWhenCpuIdleAndNoOutput()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Startup hang with idle cpu and no output");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Planner);
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", root, now.AddMinutes(-40)));
    var process = new TaskProcessRecord(999999, "claude prompt", root, stdout, stderr, exit, now.AddMinutes(-40), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    WriteHeartbeat(process, now.AddMinutes(-31), now.AddMinutes(-31), "running", 0, 0, ownedCpuMs: 0L, childPid: null);

    var completed = new BackgroundDispatchRunner(
            clock,
            isStillRunning: _ => true,
            startupHangTimeout: TimeSpan.FromMinutes(4))
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(1, completed.ExitCode);
    Assert.Equal(clock.UtcNow, completed.CompletedAt);
    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.True(File.Exists(exit));
    Assert.Contains(task.LastVerification!.StandardError, text => text.Contains("never launched", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("ownedCpuMs=0", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_startup_hang_suppressed_when_child_alive_and_idle")]
    public void BackgroundDispatchRunnerStartupHangSuppressedWhenChildAliveAndIdle()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("API-bound worker: child alive but idle on the provider");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", root, now.AddMinutes(-10)));
    var process = new TaskProcessRecord(999999, "claude prompt", root, stdout, stderr, exit, now.AddMinutes(-10), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    // childPid set (tool launched) but ownedCpuMs idle + no output: an API-bound claude/codex -p worker
    // waiting on the provider. The tool WAS invoked, so the startup-hang fast-path must NOT fire.
    WriteHeartbeat(process, now.AddMinutes(-1), now.AddMinutes(-1), "running", 0, 0, ownedCpuMs: 0L, childPid: 4242);

    var refreshed = new BackgroundDispatchRunner(
            clock,
            isStillRunning: _ => true,
            startupHangTimeout: TimeSpan.FromMinutes(4),
            progressStallTimeout: TimeSpan.FromMinutes(30))
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(process, refreshed);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.False(File.Exists(exit));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_startup_hang_suppressed_when_cpu_above_epsilon")]
    public void BackgroundDispatchRunnerStartupHangSuppressedWhenCpuAboveEpsilon()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("No startup hang when cpu active");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", root, now.AddMinutes(-10)));
    var process = new TaskProcessRecord(999999, "claude prompt", root, stdout, stderr, exit, now.AddMinutes(-10), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    // ownedCpuMs=2000 > CpuIdleEpsilonMs=1000: worker is actively burning CPU, suppress fast-path
    WriteHeartbeat(process, now.AddMinutes(-1), now.AddMinutes(-1), "running", 0, 0, ownedCpuMs: 2000L);

    var refreshed = new BackgroundDispatchRunner(
            clock,
            isStillRunning: _ => true,
            startupHangTimeout: TimeSpan.FromMinutes(4),
            progressStallTimeout: TimeSpan.FromMinutes(30))
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(process, refreshed);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.False(File.Exists(exit));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_startup_hang_suppressed_when_ownedCpuMs_absent")]
    public void BackgroundDispatchRunnerStartupHangSuppressedWhenOwnedCpuMsAbsent()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("No startup hang for old-format heartbeat");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", root, now.AddMinutes(-10)));
    var process = new TaskProcessRecord(999999, "claude prompt", root, stdout, stderr, exit, now.AddMinutes(-10), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    // ownedCpuMs absent (old-format heartbeat, pre-cf59ce9e): suppress fast-path entirely
    WriteHeartbeat(process, now.AddMinutes(-1), now.AddMinutes(-1), "running", 0, 0, ownedCpuMs: null);

    var refreshed = new BackgroundDispatchRunner(
            clock,
            isStillRunning: _ => true,
            startupHangTimeout: TimeSpan.FromMinutes(4),
            progressStallTimeout: TimeSpan.FromMinutes(30))
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(process, refreshed);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.False(File.Exists(exit));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_startup_hang_suppressed_when_output_bytes_nonzero")]
    public void BackgroundDispatchRunnerStartupHangSuppressedWhenOutputBytesNonzero()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("No startup hang when output present");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, "some output");
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", root, now.AddMinutes(-10)));
    var process = new TaskProcessRecord(999999, "claude prompt", root, stdout, stderr, exit, now.AddMinutes(-10), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    // stdoutBytes=11 nonzero: process communicated something, suppress fast-path
    WriteHeartbeat(process, now.AddMinutes(-1), now.AddMinutes(-1), "running", 11, 0, ownedCpuMs: 0L);

    var refreshed = new BackgroundDispatchRunner(
            clock,
            isStillRunning: _ => true,
            startupHangTimeout: TimeSpan.FromMinutes(4),
            progressStallTimeout: TimeSpan.FromMinutes(30))
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(process, refreshed);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.False(File.Exists(exit));
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
    var head = ReadGit(worktree, ["rev-parse", "--short", "HEAD"]);

    var logs = Path.Combine(root, "logs");
    Directory.CreateDirectory(logs);
    var stdout = Path.Combine(logs, "dev.out.log");
    var stderr = Path.Combine(logs, "dev.err.log");
    var exit = Path.Combine(logs, "dev.exit.txt");
    File.WriteAllText(stdout, "Implemented the change." + Environment.NewLine + WorkerResultBlock("feature.txt", "implemented feature", "Passed: 1", head));
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

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_hung_claude_cli_wrapper_completes_when_worktree_evidence_passes")]
    public void BackgroundDispatchRunnerHungClaudeCliWrapperCompletesWhenWorktreeEvidencePasses()
{
    var root = CreateSeededDispatchRepository();
    var now = DateTimeOffset.Parse("2026-06-12T10:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var taskSpec = new TaskSpec(TaskId.New(), "Developer task.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Hung claude-cli wrapper with evidence", [taskSpec]);
    var agent = new AgentDefinition(
        new AgentId("claude-developer"),
        "Claude Developer",
        AgentRole.Developer,
        new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey));
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
    File.WriteAllText(stderr, string.Empty);

    var task = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", worktree, now.AddMinutes(-40)));
    var process = new TaskProcessRecord(999999, "claude prompt", worktree, stdout, stderr, exit, now.AddMinutes(-40), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    // Heartbeat: childPid=null signals the worker has already exited; stalled progress
    // beyond postOutputIdleTimeout of 2 minutes triggers the hung-wrapper detector.
    WriteHeartbeat(process, now.AddMinutes(-31), now.AddMinutes(-31), "running", 0, 0, childPid: null);

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
        "dotnet test --filter OrchestratorHealthInspector\r\nPassed! - Failed: 0, Passed: 16, Skipped: 0, Total: 16.\r\nFull suite Core 172/172 + Infrastructure 326/326.\r\n" +
            WorkerResultBlock("none", "dotnet test --filter OrchestratorHealthInspector", "Passed: 16", "none"),
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

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_tester_clean_dispatch_with_worker_result_passes")]
    public void BackgroundDispatchRunnerTesterCleanDispatchWithWorkerResultPasses()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "structured verification of behavioral contract\nconfidence: high\nblockers: none\nEND_WORKER_RESULT",
        string.Empty,
        clock,
        taskDescription: "Verify behavior with automated and manual checks",
        verificationPlan: "Run the focused tests and confirm the acceptance criteria.");

    // A verify-only Tester on a clean worktree that reported via WORKER_RESULT (no "N passed" line, and
    // a non-zero exit that is unreliable under the sandbox) must NOT be false-failed for lacking
    // file-change evidence — it did exactly its job, and the acceptance suite re-runs the real tests.
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.False(task.LastVerification!.StandardError.Contains("did not produce required relevant file-change evidence", StringComparison.Ordinal));
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

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_low_integrity_dirty_worktree_with_verification_evidence_only_stays_failed")]
    public void BackgroundDispatchRunnerLowIntegrityDirtyWorktreeWithVerificationEvidenceOnlyStaysFailed()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the feature and ran the focused tests.\r\nPassed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3.",
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "implemented but not committed"),
        sandboxLowIntegrity: true);

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains(
        task.LastVerification.StandardError,
        text => text.Contains("left the worktree dirty", StringComparison.Ordinal));
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Contains(ReadGit(worktree, ["status", "--short"]), text => text.Contains("feature.txt", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_self_committing_provider_dirty_verified_without_low_integrity_evidence_stays_failed")]
    public void BackgroundDispatchRunnerSelfCommittingProviderDirtyVerifiedWithoutLowIntegrityEvidenceStaysFailed()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the feature and ran the focused tests.\r\nPassed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3.",
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "implemented but not committed"),
        workerName: "claude-cli",
        command: "claude prompt");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("left the worktree dirty", StringComparison.Ordinal));
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Contains(ReadGit(worktree, ["status", "--short"]), text => text.Contains("feature.txt", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_codex_provider_can_self_commit_false_without_low_integrity_stays_failed")]
    public void BackgroundDispatchRunnerCodexProviderCanSelfCommitFalseWithoutLowIntegrityStaysFailed()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the feature and ran the focused tests.\r\nPassed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3.",
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "implemented but not committed"));

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains(
        task.LastVerification.StandardError,
        text => text.Contains("left the worktree dirty", StringComparison.Ordinal));
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Contains(ReadGit(worktree, ["status", "--short"]), text => text.Contains("feature.txt", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_typed_provider_can_self_commit_false_without_low_integrity_stays_failed")]
    public void BackgroundDispatchRunnerTypedProviderCanSelfCommitFalseWithoutLowIntegrityStaysFailed()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var workerProfile = "typed-openai-worker";
    var providers = new WorkerProviderCatalog([
        new StaticWorkerProvider(
            new WorkerProviderIdentity(ProviderKind.OpenAICodexCli, UsesCodexExitFileBehavior: true),
            workerProfile,
            "OpenAI",
            new WorkerCapabilities(
                CanSelfCommit: false,
                CanSelfVerify: true,
                SupportsInteractiveSession: true,
                SupportsPlanMode: true))
    ]);
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the feature and ran the focused tests.\r\nPassed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3.",
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "implemented but not committed"),
        workerName: workerProfile,
        command: "opaque worker prompt",
        workerProviderKind: ProviderKind.OpenAICodexCli);

    new BackgroundDispatchRunner(clock, workerProviders: providers).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains(
        task.LastVerification.StandardError,
        text => text.Contains("left the worktree dirty", StringComparison.Ordinal));
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Contains(ReadGit(worktree, ["status", "--short"]), text => text.Contains("feature.txt", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_does_not_activate_commit_path_from_provider_display_name")]
    public void BackgroundDispatchRunnerDoesNotActivateCommitPathFromProviderDisplayName()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the feature and ran the focused tests.\r\nPassed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3.",
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "implemented but not committed"),
        workerName: "opaque-worker",
        command: "opaque worker prompt",
        providerName: "OpenAI");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains(
        task.LastVerification.StandardError,
        text => text.Contains("left the worktree dirty", StringComparison.Ordinal));
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Contains(ReadGit(worktree, ["status", "--short"]), text => text.Contains("feature.txt", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_nonzero_exit_dirty_verified_without_typed_sandbox_evidence_stays_failed")]
    public void BackgroundDispatchRunnerFileRoleNonZeroExitDirtyVerifiedWithoutTypedSandboxEvidenceStaysFailed()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the change and ran the focused tests.\r\nPassed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2.",
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "edited but commit failed under low integrity"),
        sandboxLowIntegrity: true);

    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Contains(ReadGit(worktree, ["status", "--short"]), text => text.Contains("feature.txt", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_git_metadata_permission_failure_is_nonfatal_with_dirty_worker_result")]
    public void BackgroundDispatchRunnerGitMetadataPermissionFailureIsNonfatalWithDirtyWorkerResult()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Edited the requested files, but git commit was blocked." + Environment.NewLine +
            WorkerResultBlock("feature.txt", "git add -A; git commit -m Feature", "not-run"),
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "edited before git metadata failure"),
        sandboxLowIntegrity: true);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var indexLockPath = Path.GetFullPath(Path.Combine(
        root,
        ".git",
        "worktrees",
        goal.Id.Value[..8],
        "index.lock"));
    File.WriteAllText(
        process.StandardErrorPath,
        $"fatal: Unable to create '{indexLockPath}': Permission denied");
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.Contains(
        task.LastVerification.StandardError,
        text => text.Contains("Classified worker git metadata write failure as non-fatal", StringComparison.Ordinal));
    Assert.Contains(
        task.LastVerification.StandardError,
        text => text.Contains("index_lock=", StringComparison.Ordinal));
    Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
    Assert.Contains(ReadGit(worktree, ["show", "--name-only", "--pretty=", "HEAD"]), text => text.Contains("feature.txt", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_low_integrity_dotnet_1312_dirty_worker_result_is_committed_by_orchestrator")]
    public void BackgroundDispatchRunnerLowIntegrityDotnet1312DirtyWorkerResultIsCommittedByOrchestrator()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the requested change." + Environment.NewLine +
            WorkerResultBlock("feature.txt", "dotnet test --filter LowIntegrity", "not-run"),
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "edited before dotnet 1312 failure"),
        sandboxLowIntegrity: true);
    File.WriteAllText(
        process.StandardErrorPath,
        "dotnet.cmd: CreateProcessAsUserW 1312: A specified logon session does not exist. It may already have been terminated.");
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.Contains(
        task.LastVerification.StandardError,
        text => text.Contains("Orchestrator committed the worker's verified worktree edits", StringComparison.Ordinal));
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
    Assert.Contains(ReadGit(worktree, ["show", "--name-only", "--pretty=", "HEAD"]), text => text.Contains("feature.txt", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_low_integrity_git_1312_commits_work_without_sandbox_marker")]
    public void BackgroundDispatchRunnerLowIntegrityGit1312CommitsWorkWithoutSandboxMarker()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the requested change." + Environment.NewLine +
            WorkerResultBlock("feature.txt", "git status --short", "not-run"),
        string.Empty,
        clock,
        worktree =>
        {
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "edited before git 1312 failure");
            File.WriteAllText(Path.Combine(worktree, WorkerSandboxPreparer.MarkerFileName), "{}");
        },
        sandboxLowIntegrity: true);
    File.WriteAllText(
        process.StandardErrorPath,
        "git.exe: CreateProcessAsUserW failed 1312: A specified logon session does not exist.");
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.Contains(
        task.LastVerification.StandardError,
        text => text.Contains("Orchestrator committed the worker's verified worktree edits", StringComparison.Ordinal));
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Contains(ReadGit(worktree, ["show", "--name-only", "--pretty=", "HEAD"]), text => text.Contains("feature.txt", StringComparison.Ordinal));
    Assert.DoesNotContain(WorkerSandboxPreparer.MarkerFileName, ReadGit(worktree, ["show", "--name-only", "--pretty=", "HEAD"]), StringComparison.Ordinal);
    Assert.Equal(string.Empty, ReadGit(worktree, ["ls-files", "--", WorkerSandboxPreparer.MarkerFileName]));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_nonzero_exit_dirty_unverified_stays_failed")]
    public void BackgroundDispatchRunnerFileRoleNonZeroExitDirtyUnverifiedStaysFailed()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Started editing but the provider connection dropped before anything was verified.",
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "half-done, unverified"));

    // Dirty but UNVERIFIED on a non-zero exit: the orchestrator must NOT blindly commit unproven work.
    // It stays failed so it surfaces for retry/escalation.
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.False(task.LastVerification.StandardError.Contains("Orchestrator committed", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_verification_only_tester_nonzero_exit_with_evidence_passes")]
    public void BackgroundDispatchRunnerVerificationOnlyTesterNonZeroExitWithEvidencePasses()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "Reviewed the implementation and ran the focused suite.\r\nPassed! - Failed: 0, Passed: 4, Skipped: 0, Total: 4.",
        string.Empty,
        clock,
        taskDescription: "Verify behavior with automated and manual checks",
        verificationPlan: "Run the focused tests and confirm the acceptance criteria.");

    // A verification-only Tester (no file changes requested) that verified successfully on a clean
    // worktree but exited non-zero due to Low-IL shutdown friction. The deliverable is the verification,
    // the worktree is clean, and the acceptance gate re-verifies — so accept rather than fail on the
    // unreliable exit code.
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.Contains(
        task.LastVerification.StandardError,
        text => text.Contains("Accepted on verification evidence despite a non-zero worker exit", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_clean_worktree_nonzero_exit_without_evidence_stays_failed")]
    public void BackgroundDispatchRunnerCleanWorktreeNonZeroExitWithoutEvidenceStaysFailed()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "Provider connection dropped before any checks ran.",
        string.Empty,
        clock,
        taskDescription: "Verify behavior with automated and manual checks",
        verificationPlan: "Run the focused tests and confirm the acceptance criteria.");

    // Clean worktree + non-zero exit but NO verification evidence: a genuine failure (the worker never
    // verified anything), not exit-code noise. Must stay failed.
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_empty_stdout_nonzero_exit_records_empty_output_retry")]
    public void BackgroundDispatchRunnerEmptyStdoutNonzeroExitRecordsEmptyOutputRetry()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        string.Empty,
        string.Empty,
        clock,
        taskDescription: "Verify behavior with automated and manual checks",
        verificationPlan: "Run the focused tests and confirm the acceptance criteria.");
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Equal(1, task.EmptyOutputRetryCount);
    Assert.True(DispatchFailureClassifier.IsTransientEmptyOutputDispatchFlake(task.LastVerification));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_exit_zero_empty_streamed_output_with_worker_result_file_completes")]
    public void BackgroundDispatchRunnerExitZeroEmptyStreamedOutputWithWorkerResultFileCompletes()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        string.Empty,
        string.Empty,
        clock,
        worktree => File.WriteAllText(
            Path.Combine(worktree, "WORKER_RESULT.md"),
            WorkerResultBlock("none", "dotnet test --filter BufferedWorker", "not-run")),
        taskDescription: "Verify behavior with automated and manual checks",
        verificationPlan: "Run the focused tests and confirm the acceptance criteria.");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.True(task.LastVerification.WorkerResultPresent);
    Assert.Equal(0, task.EmptyOutputRetryCount);
    Assert.False(DispatchFailureClassifier.IsTransientEmptyOutputDispatchFlake(task.LastVerification));
    Assert.Equal("0", File.ReadAllText(process.ExitCodePath));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_exit_zero_empty_streamed_output_with_committed_change_completes")]
    public void BackgroundDispatchRunnerExitZeroEmptyStreamedOutputWithCommittedChangeCompletes()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        string.Empty,
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
    Assert.True(task.LastVerification.HasCommittedChanges);
    Assert.Equal(0, task.EmptyOutputRetryCount);
    Assert.False(DispatchFailureClassifier.IsTransientEmptyOutputDispatchFlake(task.LastVerification));
    Assert.Equal("0", File.ReadAllText(process.ExitCodePath));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_nonzero_exit_with_artifacts_does_not_complete")]
    public void BackgroundDispatchRunnerNonZeroExitWithArtifactsDoesNotComplete()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        string.Empty,
        string.Empty,
        clock,
        worktree => File.WriteAllText(
            Path.Combine(worktree, "WORKER_RESULT.md"),
            WorkerResultBlock("none", "dotnet test --filter BufferedWorker", "not-run")),
        taskDescription: "Verify behavior with automated and manual checks",
        verificationPlan: "Run the focused tests and confirm the acceptance criteria.");
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.True(task.LastVerification.WorkerResultPresent);
    Assert.NotEqual(
        DispatchOutcomeKind.VerifiedSuccess,
        DispatchFailureClassifier.Classify(task, task.LastVerification).Kind);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_with_committed_change_passes")]
    public void BackgroundDispatchRunnerFileRoleWithCommittedChangePasses()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Committed implementation." + Environment.NewLine + WorkerResultBlock("feature.txt", "implemented feature", "Passed: 1"),
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

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_missing_policy_test_evidence_passes_advisory")]
    public void BackgroundDispatchRunnerFileRoleMissingPolicyTestEvidencePassesAdvisory()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Committed implementation." + Environment.NewLine + WorkerResultBlock("src/Feature.cs", "implemented feature", "not run"),
        string.Empty,
        clock,
        worktree =>
        {
            Directory.CreateDirectory(Path.Combine(worktree, "src"));
            File.WriteAllText(Path.Combine(worktree, "src", "Feature.cs"), "public sealed class Feature {}");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Feature"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
        });

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    // WORKER_RESULT is advisory: a relevant commit on a clean worktree is sufficient at
    // dispatch time. Test evidence is enforced by the acceptance run, not the self-report.
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_without_worker_result_contract_passes_advisory")]
    public void BackgroundDispatchRunnerFileRoleWithoutWorkerResultContractPassesAdvisory()
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

    // A missing WORKER_RESULT block no longer fails the dispatch: git ground truth (the
    // relevant committed change on a clean worktree) carries the substance. The block is advisory.
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_with_partial_worker_result_contract_passes_advisory")]
    public void BackgroundDispatchRunnerFileRoleWithPartialWorkerResultContractPassesAdvisory()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var output = """
        Committed implementation.
        WORKER_RESULT:
        files: feature.txt
        commands: git add -A; git commit -m Feature
        tests: Passed: 1
        commit: abc123
        blockers: none
        model_fit: deterministic fixture - adequate - contract validation
        confidence: high
        END_WORKER_RESULT
        """;
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        output,
        string.Empty,
        clock,
        worktree =>
        {
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Feature"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
        });

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    // A partial/odd-shaped WORKER_RESULT (here missing the skills field) no longer fails the
    // dispatch — field shape is advisory; the relevant committed change is the substance.
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_with_fake_commit_sha_passes_advisory")]
    public void BackgroundDispatchRunnerFileRoleWithFakeCommitShaPassesAdvisory()
{
    // The worker reports a fake commit SHA "abc123", but it actually committed a relevant
    // change on a clean worktree. The self-reported commit is advisory and no longer checked;
    // git ground truth (a relevant commit after dispatch) is the substance, so this passes.
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var output = """
        Committed implementation.
        WORKER_RESULT:
        files: feature.txt
        commands: git add -A; git commit -m Feature
        tests: Passed: 1
        commit: abc123
        blockers: none
        model_fit: deterministic fixture - adequate - contract validation
        skills: dotnet-windows-build-hygiene
        confidence: high
        """;
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        output,
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

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_with_worker_result_file_mismatch_passes_advisory")]
    public void BackgroundDispatchRunnerFileRoleWithWorkerResultFileMismatchPassesAdvisory()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Committed implementation." + Environment.NewLine + WorkerResultBlock("other.txt", "implemented feature", "Passed: 1"),
        string.Empty,
        clock,
        worktree =>
        {
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Feature"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
        });

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    // The worker's files field (other.txt) disagrees with what git shows changed (feature.txt).
    // The self-reported file list is advisory and no longer cross-checked; the relevant
    // committed change is the substance, so this passes.
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_with_commit_and_residual_dirty_worktree_commits_residual")]
    public void BackgroundDispatchRunnerFileRoleWithCommitAndResidualDirtyWorktreeCommitsResidual()
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

    Assert.True(
        task.Status == WorkTaskStatus.Completed,
        task.LastVerification?.StandardError ?? "missing verification");
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("Orchestrator committed the worker's verified worktree edits", StringComparison.Ordinal));
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
    Assert.Equal("Developer task.: Committed implementation.", ReadGit(worktree, ["log", "-1", "--pretty=%s"]));
    Assert.Equal("2", ReadGit(worktree, ["rev-list", "--count", "HEAD~2..HEAD"]));
    Assert.Equal("seed.txt", ReadGit(worktree, ["show", "--name-only", "--pretty=", "HEAD"]));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_residual_commit_failure_surfaces_git_error_and_dirty_summary")]
    public void BackgroundDispatchRunnerFileRoleResidualCommitFailureSurfacesGitErrorAndDirtySummary()
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
    var hookPath = Path.Combine(root, ".git", "hooks", "pre-commit");
    File.WriteAllText(
        hookPath,
        "#!/bin/sh\n" +
        "echo blocked residual commit >&2\n" +
        "exit 42\n");
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(
            hookPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains(
        task.LastVerification.StandardError,
        text => text.Contains("Orchestrator commit-on-behalf git command failed", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("operation=commit", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("blocked residual commit", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("left the worktree dirty", StringComparison.Ordinal));
    Assert.Contains(task.LastVerification.StandardError, text => text.Contains("status_short=M seed.txt", StringComparison.Ordinal));
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Contains(ReadGit(worktree, ["status", "--short"]), text => text.Contains("seed.txt", StringComparison.Ordinal));
    Assert.Equal("1", ReadGit(worktree, ["rev-list", "--count", "HEAD~1..HEAD"]));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_nonzero_exit_with_commit_and_residual_dirty_worktree_fails")]
    public void BackgroundDispatchRunnerFileRoleNonZeroExitWithCommitAndResidualDirtyWorktreeFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
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
        },
        workerName: "claude-cli",
        command: "claude prompt");
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Contains(ReadGit(worktree, ["status", "--short"]), text => text.Contains("seed.txt", StringComparison.Ordinal));
    Assert.Equal("1", ReadGit(worktree, ["rev-list", "--count", "HEAD~1..HEAD"]));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_with_no_change_rationale_and_clean_worktree_passes")]
    public void BackgroundDispatchRunnerFileRoleWithNoChangeRationaleAndCleanWorktreePasses()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "NO_CHANGE: Existing focused test already covers this behavior." + Environment.NewLine +
            WorkerResultBlock("none", "inspected existing tests", "existing focused test covers behavior", "none"),
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
    // Hermetic: this asserts the DEFAULT (no-OS-sandbox) dispatch mode, which reads
    // WorkerSandboxOptions.FromEnvironment(). Clear the operator's MCG_WORKER_SANDBOX so the test is
    // deterministic even when the suite is run under `conduct`/acceptance with the var set.
    using var _sandboxEnv = ClearWorkerSandboxEnv();
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
    var authProbe = () => new ClaudeCliAuthState(
        HasAnthropicApiKey: true,
        HasCliCredentialArtifact: false,
        CredentialArtifactPath: null);

    WorkerProfileDispatcher.PrepareSubscriptionTask(kernel, developerGoal, developerTask, [developerAgent], WorkerProfileCatalog.Default(), promptRoot, workingDirectory, dispatchedAt, claudeAuthProbe: authProbe);
    WorkerProfileDispatcher.PrepareSubscriptionTask(kernel, reviewerGoal, reviewerTask, [reviewerAgent], WorkerProfileCatalog.Default(), promptRoot, workingDirectory, dispatchedAt, claudeAuthProbe: authProbe);

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
    File.WriteAllText(Path.Combine(workingDirectory, "DOGFOOD_LOG.md"), "Compatibility pointer only.");
    File.WriteAllText(Path.Combine(workingDirectory, "TestRepo.sln"), ""); // mark as dotnet for toolchain detection
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
    Assert.Contains(deterministicArtifact.GetProperty("freshness").GetString()!, text => text.Contains("dispatch preparation", StringComparison.Ordinal));
    Assert.True(deterministicArtifact.GetProperty("roleVisibility").EnumerateArray().Any(item => item.GetString() == "Reviewer"));
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "AGENTS.md"));
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "selected-skills.md"));
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "workflow-brokers.md"));
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "context-budget.md"));
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "context-package.json"));
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "source-survey.md"));
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "diff-summary.md"));
    Assert.Contains(manifest, text => text.Contains("artifact-registry.json", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("digest.md", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("workflow-brokers.md", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("context-budget.md", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("selected-skills.md", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("source-survey.md", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("diff-summary.md", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("prior-task-summaries.md", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("prior-task-evidence.md", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("context-package.json", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("Missing Artifact Fallback", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("Role Artifact Priorities", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("AGENTS.md", StringComparison.Ordinal));
    Assert.Contains(digest, text => text.Contains("## Current Task", StringComparison.Ordinal));
    Assert.Contains(digest, text => text.Contains("## Role Artifact Priorities", StringComparison.Ordinal));
    Assert.Contains(digest, text => text.Contains("workflow-brokers.md", StringComparison.Ordinal));
    Assert.Contains(digest, text => text.Contains("context-budget.md", StringComparison.Ordinal));
    Assert.Contains(digest, text => text.Contains("selected-skills.md", StringComparison.Ordinal));
    Assert.Contains(contextBudget, text => text.Contains("artifact-registry.json: authoritative list", StringComparison.Ordinal));
    Assert.Contains(contextBudget, text => text.Contains("embed: digest.md", StringComparison.Ordinal));
    Assert.Contains(contextBudget, text => text.Contains("retrieve by handle: prior-task-evidence.md", StringComparison.Ordinal));
    Assert.Contains(contextBudget, text => text.Contains("omit from prompt prose", StringComparison.Ordinal));
    Assert.Contains(digest, text => text.Contains("## Prior Completed Outcomes", StringComparison.Ordinal));
    Assert.Contains(digest, text => text.Contains("prior-task-summaries.md", StringComparison.Ordinal));
    Assert.Contains(digest, text => text.Contains("prior-task-evidence.md", StringComparison.Ordinal));
    Assert.Contains(digest, text => text.Contains(".orchestrator-handoff.md", StringComparison.Ordinal));
    Assert.Contains(deterministic, text => text.Contains("## Isolated .NET Verification", StringComparison.Ordinal));
    Assert.Contains(deterministic, text => text.Contains(".\\scripts\\Invoke-IsolatedDotnet.ps1", StringComparison.Ordinal));
    Assert.Contains(deterministic, text => text.Contains("-GoalPrefix", StringComparison.Ordinal));
    Assert.Contains(deterministic, text => text.Contains("instead of raw `dotnet test`", StringComparison.Ordinal));
    Assert.Contains(deterministic, text => text.Contains("## Required Verification Policy", StringComparison.Ordinal));
    Assert.Contains(deterministic, text => text.Contains("Requires tests:", StringComparison.Ordinal));
    Assert.Contains(workflowBrokers, text => text.Contains("build-test-selection", StringComparison.Ordinal));
    Assert.Contains(workflowBrokers, text => text.Contains("source-survey", StringComparison.Ordinal));
    Assert.Contains(workflowBrokers, text => text.Contains("diff-summary", StringComparison.Ordinal));
    Assert.Contains(workflowBrokers, text => text.Contains("acceptance-evidence", StringComparison.Ordinal));
    Assert.Contains(workflowBrokers, text => text.Contains(".orchestrator/dogfood-log.db", StringComparison.Ordinal));
    Assert.Contains(workflowBrokers, text => text.Contains("dogfood-log list", StringComparison.Ordinal));
    Assert.DoesNotContain("Artifact: DOGFOOD_LOG.md", workflowBrokers, StringComparison.Ordinal);
    Assert.Contains(workflowBrokers, text => text.Contains("Broker Output Contract", StringComparison.Ordinal));
    Assert.Contains(workflowBrokers, text => text.Contains("WORKER_RESULT blockers", StringComparison.Ordinal));
    Assert.Contains(summaries, text => text.Contains("Changed files: Not reported.", StringComparison.Ordinal));
    Assert.Contains(summaries, text => text.Contains("Behavior changes: Not reported.", StringComparison.Ordinal));
    Assert.Contains(summaries, text => text.Contains("Verification: `dotnet test`", StringComparison.Ordinal));
    Assert.Contains(summaries, text => text.Contains("Model fit: Not reported.", StringComparison.Ordinal));
    Assert.Contains(selectedSkills, text => text.Contains("dotnet-windows-build-hygiene", StringComparison.Ordinal));
    Assert.Contains(selectedSkills, text => text.Contains("Status: missing", StringComparison.Ordinal));
    Assert.Contains(sourceSurvey, text => text.Contains("Source files indexed:", StringComparison.Ordinal));
    Assert.Contains(diffSummary, text => text.Contains("Git Status", StringComparison.Ordinal));
    Assert.Contains(File.ReadAllText(Path.Combine(contextDirectory, "current-task.md")), text => text.Contains("Run worker dispatch tests.", StringComparison.Ordinal));
    Assert.Contains(File.ReadAllText(Path.Combine(contextDirectory, "current-task.md")), text => text.Contains("skills: <selected skills used or none>", StringComparison.Ordinal));
    Assert.Contains(File.ReadAllText(Path.Combine(contextDirectory, "prior-task-evidence.md")), text => text.Contains(artifactOnlyTail, StringComparison.Ordinal));
    var packageDirectory = Path.Combine(contextDirectory, "packages", currentTask.Id.Value);
    Assert.True(File.Exists(Path.Combine(packageDirectory, "manifest.md")));
    Assert.True(File.Exists(Path.Combine(packageDirectory, "artifact-registry.json")));
    Assert.True(File.Exists(Path.Combine(packageDirectory, "prior-task-evidence.md")));
    using var packageDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(contextDirectory, "context-package.json")));
    Assert.Equal(currentTask.Id.Value, packageDocument.RootElement.GetProperty("taskId").GetString());
    Assert.Contains(packageDocument.RootElement.GetProperty("missingArtifactFallback").GetString()!, text => text.Contains("missing artifact", StringComparison.OrdinalIgnoreCase));
    var prompt = File.ReadAllText(result.PromptPath);
    Assert.Contains(prompt, text => text.Contains(contextDirectory, StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("artifact-registry.json", StringComparison.Ordinal));
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
    [Xunit.InlineData(AgentRole.Developer, "artifact-registry.json: verify current context artifacts", "workflow-brokers.md: use deterministic broker actions")]
    [Xunit.InlineData(AgentRole.Tester, "artifact-registry.json: verify artifact freshness", "workflow-brokers.md: use deterministic broker actions")]
    [Xunit.InlineData(AgentRole.Reviewer, "artifact-registry.json: verify hashes", "workflow-brokers.md: check deterministic broker failures")]
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

    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_selects_relevant_skills_and_registers_skill_artifact")]
    public void WorkerContextArtifactsSelectsRelevantSkillsAndRegistersSkillArtifact()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    WriteSkill(workingDirectory, "dotnet-windows-build-hygiene");
    WriteSkill(workingDirectory, "orchestrator-dogfood");
    WriteSkill(workingDirectory, "aspnet-core");
    WriteSkill(workingDirectory, "playwright");
    WriteSkill(workingDirectory, "skill-authoring");
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(
        TaskId.New(),
        "Implement an ASP.NET Core dashboard UI improvement for orchestrator subscription dispatch with Playwright browser automation coverage and update .agents/skills/skill-authoring/SKILL.md.",
        AgentRole.Developer,
        "Run dotnet test for the focused worker dispatch tests, inspect selected-skills.md, and run a Playwright dashboard UI smoke check.");
    var goal = kernel.CreateGoal("Improve orchestrator dogfood backlog automation", [task]);

    var contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory);

    var selectedSkills = File.ReadAllText(Path.Combine(contextDirectory, "selected-skills.md"));
    using var registryDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(contextDirectory, "artifact-registry.json")));
    var artifacts = registryDocument.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
    var skillArtifact = artifacts.Single(artifact => artifact.GetProperty("path").GetString() == "selected-skills.md");
    Assert.True(skillArtifact.GetProperty("exists").GetBoolean());
    Assert.Contains(selectedSkills, text => text.Contains("dotnet-windows-build-hygiene", StringComparison.Ordinal));
    Assert.Contains(selectedSkills, text => text.Contains("orchestrator-dogfood", StringComparison.Ordinal));
    Assert.Contains(selectedSkills, text => text.Contains("aspnet-core", StringComparison.Ordinal));
    Assert.Contains(selectedSkills, text => text.Contains("playwright", StringComparison.Ordinal));
    Assert.Contains(selectedSkills, text => text.Contains("skill-authoring", StringComparison.Ordinal));
    Assert.Contains(selectedSkills, text => text.Contains("Status: available", StringComparison.Ordinal));
    Assert.Contains(selectedSkills, text => text.Contains("WORKER_RESULT skills field", StringComparison.Ordinal));
    Assert.Contains(skillArtifact.GetProperty("summary").GetString()!, text => text.Contains("skill selection", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_selects_different_skill_manifests_for_tasks_in_same_goal")]
    public void WorkerContextArtifactsSelectsDifferentSkillManifestsForTasksInSameGoal()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    WriteSkill(workingDirectory, "dotnet-windows-build-hygiene");
    WriteSkill(workingDirectory, "orchestrator-dogfood");
    WriteSkill(workingDirectory, "orchestrator-worker-verification");
    WriteSkill(workingDirectory, "aspnet-core");
    WriteSkill(workingDirectory, "playwright");
    var kernel = new AgentOrchestratorKernel();
    var implementation = new TaskSpec(
        TaskId.New(),
        "Implement an ASP.NET Core dashboard UI workflow with Playwright coverage.",
        AgentRole.Developer,
        "Run dotnet test and a dashboard UI smoke.");
    var review = new TaskSpec(
        TaskId.New(),
        "Review worker result contract evidence.",
        AgentRole.Reviewer,
        "Inspect verification records and dispatch logs.");
    var goal = kernel.CreateGoal("Improve dashboard worker routing", [implementation, review]);

    var contextDirectory = WorkerContextArtifacts.Write(goal, implementation, workingDirectory);
    var implementationSkills = File.ReadAllText(Path.Combine(contextDirectory, "selected-skills.md"));
    WorkerContextArtifacts.Write(goal, review, workingDirectory);
    var reviewSkills = File.ReadAllText(Path.Combine(contextDirectory, "selected-skills.md"));
    var implementationPackageSkills = File.ReadAllText(Path.Combine(contextDirectory, "packages", implementation.Id.Value, "selected-skills.md"));
    var reviewPackageSkills = File.ReadAllText(Path.Combine(contextDirectory, "packages", review.Id.Value, "selected-skills.md"));

    Assert.Contains(implementationSkills, text => text.Contains("dotnet-windows-build-hygiene", StringComparison.Ordinal));
    Assert.Contains(implementationSkills, text => text.Contains("aspnet-core", StringComparison.Ordinal));
    Assert.Contains(implementationSkills, text => text.Contains("playwright", StringComparison.Ordinal));
    Assert.Contains(reviewSkills, text => text.Contains("orchestrator-worker-verification", StringComparison.Ordinal));
    Assert.False(reviewSkills.Contains("aspnet-core", StringComparison.Ordinal));
    Assert.False(reviewSkills.Contains("playwright", StringComparison.Ordinal));
    Assert.False(string.Equals(implementationSkills, reviewSkills, StringComparison.Ordinal));
    Assert.Equal(implementationSkills, implementationPackageSkills);
    Assert.Equal(reviewSkills, reviewPackageSkills);
}

    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_writes_source_survey_and_diff_summary_artifacts")]
    public void WorkerContextArtifactsWritesSourceSurveyAndDiffSummaryArtifacts()
{
    var workingDirectory = CreateSeededDispatchRepository();
    Directory.CreateDirectory(Path.Combine(workingDirectory, "src", "Feature"));
    Directory.CreateDirectory(Path.Combine(workingDirectory, "tests", "Feature.Tests"));
    Directory.CreateDirectory(Path.Combine(workingDirectory, "src", "Feature", "bin"));
    File.WriteAllText(Path.Combine(workingDirectory, "src", "Feature", "FeatureService.cs"), "public sealed class FeatureService {}");
    File.WriteAllText(Path.Combine(workingDirectory, "tests", "Feature.Tests", "FeatureServiceTests.cs"), "public sealed class FeatureServiceTests {}");
    File.WriteAllText(Path.Combine(workingDirectory, "src", "Feature", "bin", "Generated.cs"), "generated");
    RunGit(workingDirectory, ["add", "-A"], DateTimeOffset.Parse("2026-01-01T00:01:00Z"));
    RunGit(workingDirectory, ["commit", "-m", "Add feature source"], DateTimeOffset.Parse("2026-01-01T00:01:00Z"));
    File.WriteAllText(Path.Combine(workingDirectory, "src", "Feature", "FeatureService.cs"), "public sealed class FeatureService { public int Version => 2; }");
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(
        TaskId.New(),
        "Update FeatureService behavior and tests.",
        AgentRole.Developer,
        "Run focused FeatureService tests.");
    var goal = kernel.CreateGoal("Improve FeatureService source survey context", [task]);

    var contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory);

    var sourceSurvey = File.ReadAllText(Path.Combine(contextDirectory, "source-survey.md"));
    var diffSummary = File.ReadAllText(Path.Combine(contextDirectory, "diff-summary.md"));
    using var registryDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(contextDirectory, "artifact-registry.json")));
    var artifacts = registryDocument.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
    Assert.Contains(sourceSurvey, text => text.Contains("src/Feature/FeatureService.cs", StringComparison.Ordinal));
    Assert.Contains(sourceSurvey, text => text.Contains("tests/Feature.Tests/FeatureServiceTests.cs", StringComparison.Ordinal));
    Assert.Contains(sourceSurvey, text => text.Contains("## Likely Tests", StringComparison.Ordinal));
    Assert.Contains(sourceSurvey, text => text.Contains("## Public API Symbols", StringComparison.Ordinal));
    Assert.Contains(sourceSurvey, text => text.Contains("public class FeatureService", StringComparison.Ordinal));
    Assert.Contains(sourceSurvey, text => text.Contains("## Call-Site Hints", StringComparison.Ordinal));
    Assert.Contains(sourceSurvey, text => text.Contains("FeatureService.cs: featureservice", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(sourceSurvey, text => text.Contains("## Ownership Hints", StringComparison.Ordinal));
    Assert.Contains(sourceSurvey, text => text.Contains("src/Feature: production source", StringComparison.Ordinal));
    Assert.Contains(sourceSurvey, text => text.Contains("tests/Feature.Tests: test source", StringComparison.Ordinal));
    Assert.Contains(sourceSurvey, text => text.Contains("Regeneration: generated at dispatch preparation", StringComparison.Ordinal));
    Assert.False(sourceSurvey.Contains("bin/Generated.cs", StringComparison.Ordinal));
    Assert.Contains(diffSummary, text => text.Contains("src/Feature/FeatureService.cs", StringComparison.Ordinal));
    Assert.Contains(diffSummary, text => text.Contains("Regeneration: generated at dispatch preparation", StringComparison.Ordinal));
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "source-survey.md"));
    Assert.True(artifacts.Any(artifact => artifact.GetProperty("path").GetString() == "diff-summary.md"));
}

    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_writes_deterministic_verification_checklist_for_reviewer")]
    public void WorkerContextArtifactsWritesDeterministicVerificationChecklistForReviewer()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(Path.Combine(workingDirectory, "config"));
    File.WriteAllText(Path.Combine(workingDirectory, "config", "acceptance-manifest.json"), "{}");
    var kernel = new AgentOrchestratorKernel();
    var developer = new TaskSpec(TaskId.New(), "Implement deterministic review checklist.", AgentRole.Developer);
    var tester = new TaskSpec(TaskId.New(), "Run verification.", AgentRole.Tester);
    var reviewer = new TaskSpec(TaskId.New(), "Review deterministic evidence.", AgentRole.Reviewer, "Review deterministic-verification.md first.");
    var goal = kernel.CreateGoal("Review with deterministic evidence", [developer, tester, reviewer]);
    kernel.ReportTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
        "codex exec prompt",
        workingDirectory,
        0,
        "Implemented." + Environment.NewLine + WorkerResultBlock("src/Feature.cs, bin/generated.dll", "dotnet test", "Passed: 1", "abc123"),
        string.Empty,
        DateTimeOffset.UtcNow,
        "Model fit: OpenAI/gpt-5.5 - adequate - implementation fixture."));
    kernel.ReportTaskProgress(goal.Id, tester.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, tester.Id, new TaskVerificationRecord(
        "dotnet test",
        workingDirectory,
        0,
        "Passed: 1",
        string.Empty,
        DateTimeOffset.UtcNow));

    var contextDirectory = WorkerContextArtifacts.Write(goal, reviewer, workingDirectory);

    var checklist = File.ReadAllText(Path.Combine(contextDirectory, "deterministic-verification.md"));
    var manifest = File.ReadAllText(Path.Combine(contextDirectory, "manifest.md"));
    Assert.Contains(checklist, text => text.Contains("Acceptance manifest: present: config/acceptance-manifest.json", StringComparison.Ordinal));
    Assert.Contains(checklist, text => text.Contains("prior Developer verification passed", StringComparison.Ordinal));
    Assert.Contains(checklist, text => text.Contains("reported WORKER_RESULT contract", StringComparison.Ordinal));
    Assert.Contains(checklist, text => text.Contains("reported generated path changes: bin/generated.dll", StringComparison.Ordinal));
    Assert.Contains(checklist, text => text.Contains("prior Tester task", StringComparison.Ordinal));
    Assert.Contains(checklist, text => text.Contains("no model-fit evidence", StringComparison.Ordinal));
    Assert.Contains(checklist, text => text.Contains("## Test Impact Plan", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("deterministic-verification.md", StringComparison.Ordinal));
    Assert.Contains(manifest, text => text.Contains("check deterministic failures", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerContextArtifacts_writes_unmet_acceptance_criterion_retry_feedback")]
    public void WorkerContextArtifactsWritesUnmetAcceptanceCriterionRetryFeedback()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(
        TaskId.New(),
        "Fix the acceptance criterion miss.",
        AgentRole.Developer,
        "Run focused acceptance retry tests.");
    var goal = kernel.CreateGoal("Retry with deterministic criterion feedback", [task]);
    kernel.RecordCriterionRetryFeedback(
        goal.Id,
        task.Id,
        ["grep-present docs/usage.md contains Ready: docs/usage.md is missing Ready"]);

    var contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory);

    var currentTask = File.ReadAllText(Path.Combine(contextDirectory, "current-task.md"));
    Assert.Contains(currentTask, text => text.Contains("## Unmet acceptance criteria from the prior attempt - fix these:", StringComparison.Ordinal));
    Assert.Contains(currentTask, text => text.Contains("docs/usage.md is missing Ready", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_puts_latest_acceptance_failure_before_context_digest_on_retry")]
    public void BuildTaskBriefPutsLatestAcceptanceFailureBeforeContextDigestOnRetry()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    var contextDirectory = Path.Combine(root, "context");
    Directory.CreateDirectory(workingDirectory);
    Directory.CreateDirectory(contextDirectory);
    var clock = new MutableClock(DateTimeOffset.Parse("2026-06-26T12:00:00Z"));
    var kernel = new AgentOrchestratorKernel(clock);
    var task = new TaskSpec(TaskId.New(), "Fix acceptance-failed schema assertions.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Retry after acceptance failure", [task]);
    kernel.ActivateGoal(goal.Id, [new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey))]);

    var firstAttempt = kernel.BuildTaskBrief(
        goal.Id,
        task.Id,
        workingDirectory: workingDirectory,
        contextDirectory: contextDirectory).Content;
    Assert.DoesNotContain("ACCEPTANCE FAILURE", firstAttempt, StringComparison.Ordinal);

    kernel.RecordAcceptanceFailure(goal.Id, [
        "OldSchemaTests.OldFailure"
    ]);
    clock.Advance();
    kernel.RetryTask(goal.Id, task.Id, "old operator feedback");
    clock.Advance();
    kernel.RecordAcceptanceFailure(goal.Id, [
        "SqliteOrchestratorStateRepositoryTests.Loads_existing_goal_schema",
        "SqliteOrchestratorStateRepositoryTests.Saves_goal_schema_columns"
    ]);
    clock.Advance();
    var operatorFeedback = "Operator rejection: acceptance failed on sqlite schema assertions; fix the schema mapping before reporting complete.";
    kernel.RetryTask(goal.Id, task.Id, operatorFeedback);

    var retryPrompt = kernel.BuildTaskBrief(
        goal.Id,
        task.Id,
        workingDirectory: workingDirectory,
        contextDirectory: contextDirectory).Content;

    var failureStart = retryPrompt.IndexOf("<!-- ACCEPTANCE_FAILURE_START -->", StringComparison.Ordinal);
    var instructions = retryPrompt.IndexOf("## Instructions", StringComparison.Ordinal);
    var contextPointer = retryPrompt.IndexOf("Context files:", StringComparison.Ordinal);
    Assert.True(failureStart >= 0, retryPrompt);
    Assert.True(failureStart < contextPointer, retryPrompt);
    Assert.True(failureStart < instructions, retryPrompt);
    Assert.True(retryPrompt.Contains("<!-- ACCEPTANCE_FAILURE_END -->", StringComparison.Ordinal), retryPrompt);
    Assert.True(retryPrompt.Contains(operatorFeedback, StringComparison.Ordinal), retryPrompt);
    Assert.True(retryPrompt.Contains("SqliteOrchestratorStateRepositoryTests.Loads_existing_goal_schema", StringComparison.Ordinal), retryPrompt);
    Assert.True(retryPrompt.Contains("SqliteOrchestratorStateRepositoryTests.Saves_goal_schema_columns", StringComparison.Ordinal), retryPrompt);
    var failureEnd = retryPrompt.IndexOf("<!-- ACCEPTANCE_FAILURE_END -->", StringComparison.Ordinal);
    var failureBlock = retryPrompt[failureStart..failureEnd];
    Assert.DoesNotContain("OldSchemaTests.OldFailure", failureBlock, StringComparison.Ordinal);
    Assert.DoesNotContain("old operator feedback", failureBlock, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_puts_latest_developer_retry_blocker_before_prior_branch_and_digest_context")]
    public void BuildTaskBriefPutsLatestDeveloperRetryBlockerBeforePriorBranchAndDigestContext()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    var contextDirectory = Path.Combine(root, "context");
    Directory.CreateDirectory(workingDirectory);
    Directory.CreateDirectory(contextDirectory);
    var clock = new MutableClock(DateTimeOffset.Parse("2026-06-28T12:00:00Z"));
    var kernel = new AgentOrchestratorKernel(clock);
    var planner = new TaskSpec(TaskId.New(), "Plan retry prompt construction.", AgentRole.Planner);
    var developer = new TaskSpec(TaskId.New(), "Implement retry prompt construction.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Make Developer retry feedback first class", [planner, developer]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    kernel.RecordTaskVerification(goal.Id, planner.Id, new TaskVerificationRecord(
        "codex exec planner",
        workingDirectory,
        0,
        "prior summary text: existing branch looked clean",
        string.Empty,
        clock.UtcNow));
    kernel.ReportTaskProgress(goal.Id, planner.Id, WorkTaskStatus.Completed, "Planner completed.");
    kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec old-focused-tests.md",
        workingDirectory,
        clock.UtcNow));
    kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
        "dotnet test --filter OldFocusedTests",
        workingDirectory,
        1,
        "old focused tests failed",
        string.Empty,
        clock.UtcNow));
    kernel.ReportTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Failed, "Developer attempt failed.");
    clock.Advance();
    kernel.RetryTask(goal.Id, developer.Id, "Old retry reason for prior history.");
    clock.Advance();
    var exactBlocker = "Reviewer blocker: src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs method PrepareSubscriptionTask still buries RetryTask feedback after context digest.";
    kernel.RetryTask(goal.Id, developer.Id, exactBlocker);

    var prompt = kernel.BuildTaskBrief(
        goal.Id,
        developer.Id,
        workingDirectory: workingDirectory,
        contextDirectory: contextDirectory,
        targetBranchName: "goal/41c6e2a2",
        targetHeadCommit: "abcdef123456").Content;

    var retryStart = prompt.IndexOf("<!-- LATEST_DEVELOPER_RETRY_BLOCKER_START -->", StringComparison.Ordinal);
    var retryEnd = prompt.IndexOf("<!-- LATEST_DEVELOPER_RETRY_BLOCKER_END -->", StringComparison.Ordinal);
    Assert.True(retryStart >= 0, prompt);
    Assert.True(retryEnd > retryStart, prompt);
    Assert.True(retryStart < prompt.IndexOf("Goal:", StringComparison.Ordinal), prompt);
    Assert.True(retryStart < prompt.IndexOf("Context files:", StringComparison.Ordinal), prompt);
    Assert.True(retryStart < prompt.IndexOf("Current target context:", StringComparison.Ordinal), prompt);
    Assert.True(retryStart < prompt.IndexOf("## Prior Task Evidence", StringComparison.Ordinal), prompt);
    Assert.True(retryStart < prompt.IndexOf("## Recent Timeline", StringComparison.Ordinal), prompt);
    Assert.Contains(prompt, text => text.Contains(exactBlocker, StringComparison.Ordinal));

    var retryBlock = prompt[retryStart..retryEnd];
    Assert.Contains(retryBlock, text => text.Contains(exactBlocker, StringComparison.Ordinal));
    Assert.Contains(retryBlock, text => text.Contains("src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs", StringComparison.Ordinal));
    Assert.Contains(retryBlock, text => text.Contains("PrepareSubscriptionTask", StringComparison.Ordinal));
    Assert.Contains(retryBlock, text => text.Contains("- Branch: goal/41c6e2a2", StringComparison.Ordinal));
    Assert.Contains(retryBlock, text => text.Contains("- HEAD commit: abcdef123456", StringComparison.Ordinal));
    Assert.DoesNotContain("Old retry reason for prior history.", retryBlock, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_omits_developer_retry_blocker_on_first_attempt")]
    public void BuildTaskBriefOmitsDeveloperRetryBlockerOnFirstAttempt()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "repo");
    var contextDirectory = Path.Combine(root, "context");
    Directory.CreateDirectory(workingDirectory);
    Directory.CreateDirectory(contextDirectory);
    var kernel = new AgentOrchestratorKernel();
    var developer = new TaskSpec(TaskId.New(), "Implement first attempt prompt construction.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Keep first attempt prompt stable", [developer]);

    var prompt = kernel.BuildTaskBrief(
        goal.Id,
        developer.Id,
        workingDirectory: workingDirectory,
        contextDirectory: contextDirectory).Content;

    Assert.DoesNotContain("LATEST DEVELOPER RETRY BLOCKER", prompt, StringComparison.Ordinal);
    Assert.DoesNotContain("LATEST_DEVELOPER_RETRY_BLOCKER_START", prompt, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerPromptInputBudget_keeps_developer_retry_blocker_when_dropping_lower_context")]
    public void WorkerPromptInputBudgetKeepsDeveloperRetryBlockerWhenDroppingLowerContext()
{
    var exactBlocker = "Reviewer blocker: src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs method PrepareSubscriptionTask still ignores latest retry feedback.";
    var content = string.Join(Environment.NewLine, [
        "# Agent Task Brief",
        string.Empty,
        "<!-- LATEST_DEVELOPER_RETRY_BLOCKER_START -->",
        "## LATEST DEVELOPER RETRY BLOCKER - FIX FIRST",
        "Retry feedback (verbatim):",
        exactBlocker,
        "<!-- LATEST_DEVELOPER_RETRY_BLOCKER_END -->",
        string.Empty,
        "## Instructions",
        "Complete this SDLC task.",
        string.Empty,
        "## Prior Task Evidence",
        new string('p', 400),
        string.Empty,
        "## Last Verification",
        new string('v', 400),
        string.Empty,
        "## Context Digest",
        new string('d', 400)
    ]);
    var brief = new TaskBrief(
        new GoalId("goal123456789"),
        new TaskId("task123456789"),
        AgentRole.Developer,
        "Developer: retry prompt budget",
        content);
    var budget = WorkerPromptInputBudget.CountTokens(content.Replace(new string('p', 400), string.Empty, StringComparison.Ordinal)
        .Replace(new string('v', 400), string.Empty, StringComparison.Ordinal)
        .Replace(new string('d', 400), string.Empty, StringComparison.Ordinal));

    var result = WorkerPromptInputBudget.Apply(brief, "Ollama", "qwen3:8b", budget);

    Assert.True(result.Trimmed);
    Assert.Contains(result.DroppedSections, section => section == "evidence");
    Assert.Contains(result.DroppedSections, section => section == "digest");
    Assert.Contains(result.Brief.Content, text => text.Contains("LATEST DEVELOPER RETRY BLOCKER", StringComparison.Ordinal));
    Assert.Contains(result.Brief.Content, text => text.Contains(exactBlocker, StringComparison.Ordinal));
    Assert.Contains(result.Brief.Content, text => text.Contains("PrepareSubscriptionTask", StringComparison.Ordinal));
    Assert.DoesNotContain(new string('p', 400), result.Brief.Content, StringComparison.Ordinal);
    Assert.DoesNotContain(new string('d', 400), result.Brief.Content, StringComparison.Ordinal);
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

    [Xunit.Fact(DisplayName = "DispatchDiagnostic_exit0_with_output_yields_success_record_with_all_fields")]
    public void DispatchDiagnosticExit0WithOutputYieldsSuccessRecordWithAllFields()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "abc12345-def67890-20260623.out.log");
    var stderr = Path.Combine(root, "abc12345-def67890-20260623.err.log");
    var exit = Path.Combine(root, "abc12345-def67890-20260623.exit.txt");
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-23T10:00:00Z"));
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Test success diagnostic record", [new TaskSpec(TaskId.New(), "Plan the work", AgentRole.Planner)]);
    var agent = new AgentDefinition(
        new AgentId("test-planner"),
        "Test Planner",
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    File.WriteAllText(stdout, "Worker completed the task successfully.");
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "echo done", root, clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "echo done", root, stdout, stderr, exit, clock.UtcNow, null, null));
    var spy = new CaptureDiagnosticWriter();

    new BackgroundDispatchRunner(clock, isStillRunning: _ => false, diagnosticWriter: spy)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(1, spy.Records.Count);
    var record = spy.Records.Single();
    Assert.Equal(goal.Id.Value, record.GoalId);
    Assert.Equal(task.Id.Value, record.TaskId);
    Assert.Equal("abc12345-def67890-20260623", record.Prefix);
    Assert.Equal(0, record.ExitCode);
    Assert.Equal(stdout, record.OutputPath);
    Assert.True(record.FileExists);
    Assert.True(record.FileLen > 0);
    Assert.True(record.ReadLen > 0);
    Assert.Equal("success", record.Classification);
    Assert.True(record.Reason.Contains("exit 0", StringComparison.OrdinalIgnoreCase));
    Assert.NotEmpty(record.Timestamp);
}

    [Xunit.Fact(DisplayName = "DispatchDiagnostic_exit1_empty_output_yields_genuine_failure_record")]
    public void DispatchDiagnosticExit1EmptyOutputYieldsGenuineFailureRecord()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "abc12345-def67890-20260623.out.log");
    var stderr = Path.Combine(root, "abc12345-def67890-20260623.err.log");
    var exit = Path.Combine(root, "abc12345-def67890-20260623.exit.txt");
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-23T10:00:00Z"));
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Test genuine-failure diagnostic record", [new TaskSpec(TaskId.New(), "Plan the work", AgentRole.Planner)]);
    var agent = new AgentDefinition(
        new AgentId("test-planner"),
        "Test Planner",
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "1");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "echo done", root, clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "echo done", root, stdout, stderr, exit, clock.UtcNow, null, null));
    var spy = new CaptureDiagnosticWriter();

    new BackgroundDispatchRunner(clock, isStillRunning: _ => false, diagnosticWriter: spy)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(1, spy.Records.Count);
    var record = spy.Records.Single();
    Assert.Equal(1, record.ExitCode);
    Assert.Equal(0L, record.FileLen);
    Assert.Equal(0L, record.ReadLen);
    Assert.Equal("genuine-failure", record.Classification);
}

    [Xunit.Fact(DisplayName = "DispatchDiagnostic_exit1_rate_limit_in_stderr_yields_rate_limited_record")]
    public void DispatchDiagnosticExit1RateLimitInStderrYieldsRateLimitedRecord()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "abc12345-def67890-20260623.out.log");
    var stderr = Path.Combine(root, "abc12345-def67890-20260623.err.log");
    var exit = Path.Combine(root, "abc12345-def67890-20260623.exit.txt");
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-23T10:00:00Z"));
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Test rate-limited diagnostic record", [new TaskSpec(TaskId.New(), "Plan the work", AgentRole.Planner)]);
    var agent = new AgentDefinition(
        new AgentId("test-planner"),
        "Test Planner",
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, "ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex to purchase more credits or try again at 5:00 PM.");
    File.WriteAllText(exit, "1");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "echo done", root, clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "echo done", root, stdout, stderr, exit, clock.UtcNow, null, null));
    var spy = new CaptureDiagnosticWriter();

    new BackgroundDispatchRunner(clock, isStillRunning: _ => false, diagnosticWriter: spy)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(1, spy.Records.Count);
    var record = spy.Records.Single();
    Assert.Equal(1, record.ExitCode);
    Assert.Equal("rate-limited", record.Classification);
    Assert.True(record.Reason.Contains("usage limit", StringComparison.OrdinalIgnoreCase));
}

    [Xunit.Fact(DisplayName = "DispatchDiagnostic_exception_in_writer_does_not_propagate_to_caller")]
    public void DispatchDiagnosticExceptionInWriterDoesNotPropagateToCaller()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "abc12345-def67890-20260623.out.log");
    var stderr = Path.Combine(root, "abc12345-def67890-20260623.err.log");
    var exit = Path.Combine(root, "abc12345-def67890-20260623.exit.txt");
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-23T10:00:00Z"));
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Test diagnostic exception swallowing", [new TaskSpec(TaskId.New(), "Plan the work", AgentRole.Planner)]);
    var agent = new AgentDefinition(
        new AgentId("test-planner"),
        "Test Planner",
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    File.WriteAllText(stdout, "Worker completed.");
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "echo done", root, clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "echo done", root, stdout, stderr, exit, clock.UtcNow, null, null));

    var completed = new BackgroundDispatchRunner(clock, isStillRunning: _ => false, diagnosticWriter: new ThrowingDiagnosticWriter())
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(0, completed.ExitCode);
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
}

    [Xunit.Fact(DisplayName = "DispatchDiagnostic_FileDiagnosticWriter_produces_parseable_jsonl_with_all_required_fields")]
    public void DispatchDiagnosticFileDiagnosticWriterProducesParseableJsonlWithAllRequiredFields()
{
    var root = CreateTempDirectory();
    var logDir = Path.Combine(root, "logs");
    Directory.CreateDirectory(logDir);
    var stdout = Path.Combine(logDir, "abc12345-def67890-20260623.out.log");
    var stderr = Path.Combine(logDir, "abc12345-def67890-20260623.err.log");
    var exit = Path.Combine(logDir, "abc12345-def67890-20260623.exit.txt");
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-23T10:00:00Z"));
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Test FileDiagnosticWriter JSONL output", [new TaskSpec(TaskId.New(), "Plan the work", AgentRole.Planner)]);
    var agent = new AgentDefinition(
        new AgentId("test-planner"),
        "Test Planner",
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    File.WriteAllText(stdout, "Worker produced output.");
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "echo done", root, clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "echo done", root, stdout, stderr, exit, clock.UtcNow, null, null));

    new BackgroundDispatchRunner(clock, isStillRunning: _ => false, diagnosticWriter: new FileDiagnosticWriter())
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    var logPath = Path.Combine(logDir, "dispatch-diagnostics.jsonl");
    Assert.True(File.Exists(logPath));
    var line = File.ReadAllLines(logPath).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
    Assert.NotNull(line);
    var doc = JsonDocument.Parse(line!);
    var root2 = doc.RootElement;
    Assert.True(root2.TryGetProperty("goalId", out _));
    Assert.True(root2.TryGetProperty("taskId", out _));
    Assert.True(root2.TryGetProperty("prefix", out _));
    Assert.True(root2.TryGetProperty("exitCode", out _));
    Assert.True(root2.TryGetProperty("outputPath", out _));
    Assert.True(root2.TryGetProperty("fileExists", out _));
    Assert.True(root2.TryGetProperty("fileLen", out _));
    Assert.True(root2.TryGetProperty("readLen", out _));
    Assert.True(root2.TryGetProperty("stderrLen", out _));
    Assert.True(root2.TryGetProperty("classification", out var cls));
    Assert.Equal("success", cls.GetString());
    Assert.True(root2.TryGetProperty("reason", out _));
    Assert.True(root2.TryGetProperty("timestamp", out _));
}

    [Xunit.Fact(DisplayName = "ShowOrchestratorLogArtifacts_defaults_to_latest_dispatch_run_and_all_restores_history")]
    public void ShowOrchestratorLogArtifactsDefaultsToLatestDispatchRunAndAllRestoresHistory()
{
    var root = CreateTempDirectory();
    var scripts = Path.Combine(root, "scripts");
    var logs = Path.Combine(root, ".orchestrator", "logs");
    Directory.CreateDirectory(scripts);
    Directory.CreateDirectory(logs);
    File.Copy(
        FindRepositoryFile("scripts", "Show-OrchestratorLogArtifacts.ps1"),
        Path.Combine(scripts, "Show-OrchestratorLogArtifacts.ps1"));

    const string goalPrefix = "946820de";
    const string taskPrefix = "1f0ce3bb";
    var olderPrefix = $"{goalPrefix}-{taskPrefix}-20260629120000";
    var latestPrefix = $"{goalPrefix}-{taskPrefix}-20260629130000";

    WriteDispatchArtifact(logs, olderPrefix, ".out.log", "older stdout line 1\nolder stdout tail", DateTime.Parse("2026-06-29T12:00:01"));
    WriteDispatchArtifact(logs, olderPrefix, ".err.log", "older stderr tail", DateTime.Parse("2026-06-29T12:00:02"));
    WriteDispatchArtifact(logs, olderPrefix, ".exit.txt", "1", DateTime.Parse("2026-06-29T12:00:03"));
    WriteDispatchArtifact(logs, latestPrefix, ".out.log", "latest stdout line 1\nlatest stdout tail", DateTime.Parse("2026-06-29T13:00:01"));
    WriteDispatchArtifact(logs, latestPrefix, ".err.log", "latest stderr tail", DateTime.Parse("2026-06-29T13:00:02"));
    WriteDispatchArtifact(logs, latestPrefix, ".exit.txt", "0", DateTime.Parse("2026-06-29T13:00:03"));

    var defaultResult = RunPowerShellCommand(
        root,
        "& '.\\scripts\\Show-OrchestratorLogArtifacts.ps1' -GoalPrefix '946820de' -TaskPrefix '1f0ce3bb' -TailLines 1");

    Assert.Equal(0, defaultResult.ExitCode);
    Assert.Contains(latestPrefix, defaultResult.StandardOutput);
    Assert.Contains("latest stdout tail", defaultResult.StandardOutput);
    Assert.DoesNotContain(olderPrefix, defaultResult.StandardOutput);
    Assert.DoesNotContain("older stdout tail", defaultResult.StandardOutput);

    var allResult = RunPowerShellCommand(
        root,
        "& '.\\scripts\\Show-OrchestratorLogArtifacts.ps1' -GoalPrefix '946820de' -TaskPrefix '1f0ce3bb' -TailLines 1 -All");

    Assert.Equal(0, allResult.ExitCode);
    Assert.Contains(olderPrefix, allResult.StandardOutput);
    Assert.Contains(latestPrefix, allResult.StandardOutput);
    Assert.Contains("older stdout tail", allResult.StandardOutput);
    Assert.Contains("latest stdout tail", allResult.StandardOutput);
}

    private static TaskBrief CreateBudgetBrief(params string[] lines)
{
    return new TaskBrief(
        new GoalId("goal123456789"),
        new TaskId("task123456789"),
        AgentRole.Developer,
        "Developer: budget",
        string.Join(Environment.NewLine, lines));
}

    private static ProcessStartInfo CreateSandboxStartInfo(string workingDirectory)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = WorkerShell.Executable,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false
    };
    startInfo.Environment.Remove("ANTHROPIC_API_KEY");
    startInfo.Environment.Remove("CLAUDE_CONFIG_DIR");
    return startInfo;
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

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, TaskProcessRecord Process) CreateRecordedDispatch(
        string root,
        IClock clock,
        AgentRole role = AgentRole.Developer)
{
    Directory.CreateDirectory(root);
    var logs = Path.Combine(root, "logs");
    Directory.CreateDirectory(logs);
    var stdout = Path.Combine(logs, $"{role}.out.log");
    var stderr = Path.Combine(logs, $"{role}.err.log");
    var exit = Path.Combine(logs, $"{role}.exit.txt");
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, string.Empty);

    var kernel = new AgentOrchestratorKernel();
    var taskSpec = new TaskSpec(TaskId.New(), $"{role} task.", role);
    var goal = kernel.CreateGoal("Dispatch state surface goal", [taskSpec]);
    var agent = new AgentDefinition(
        new AgentId(role.ToString().ToLowerInvariant()),
        role.ToString(),
        role,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single(candidate => candidate.RequiredRole == role);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, clock.UtcNow));
    var process = new TaskProcessRecord(111, "codex exec prompt", root, stdout, stderr, exit, clock.UtcNow, null, null, OwnedProcessIds: [111]);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    return (kernel, goal, task, process);
}

    private static DispatchStateSurface CreateStateSurface(
        IClock clock,
        IReadOnlyCollection<int> livePids,
        IReadOnlyDictionary<int, string> commandLines) =>
        new(
            clock,
            isProcessAlive: livePids.Contains,
            readCommandLines: pids => pids
                .Distinct()
                .Where(commandLines.ContainsKey)
                .ToDictionary(pid => pid, pid => commandLines[pid]));

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, TaskProcessRecord Process) CreateCompletedGoalWorktreeDispatch(
        string root,
        AgentRole role,
        string standardOutput,
        string standardError,
        IClock clock,
        Action<string>? mutateWorktree = null,
        string? taskDescription = null,
        string? verificationPlan = null,
        bool sandboxLowIntegrity = false,
        string workerName = "codex-cli",
        string command = "codex exec prompt",
        ProviderKind workerProviderKind = ProviderKind.Unknown,
        string? providerName = null)
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
    var head = ReadGit(worktree, ["rev-parse", "--short", "HEAD"]);
    standardOutput = standardOutput.Replace("{commit}", head, StringComparison.Ordinal);
    standardError = standardError.Replace("{commit}", head, StringComparison.Ordinal);

    var logs = Path.Combine(root, "logs");
    Directory.CreateDirectory(logs);
    var stdout = Path.Combine(logs, $"{role}.out.log");
    var stderr = Path.Combine(logs, $"{role}.err.log");
    var exit = Path.Combine(logs, $"{role}.exit.txt");
    File.WriteAllText(stdout, standardOutput);
    File.WriteAllText(stderr, standardError);
    File.WriteAllText(exit, "0");

    var task = goal.Tasks.Single(candidate => candidate.RequiredRole == role);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        workerName,
        command,
        worktree,
        clock.UtcNow,
        providerName,
        SandboxLowIntegrity: sandboxLowIntegrity,
        WorkerProviderKind: workerProviderKind));
    var process = new TaskProcessRecord(999999, command, worktree, stdout, stderr, exit, clock.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    return (kernel, goal, task, process);
}

    private static void WriteHeartbeat(
    TaskProcessRecord process,
    DateTimeOffset lastObservedAt,
    DateTimeOffset lastProgressAt,
    string state,
    long stdoutBytes,
    long stderrBytes,
    int? childPid = 888888,
    long? ownedCpuMs = null,
    IReadOnlyList<int>? ownedPids = null,
    bool exitFileExists = false)
{
    var childPidJson = childPid.HasValue ? childPid.Value.ToString() : "null";
    var ownedCpuMsJson = ownedCpuMs.HasValue ? $",\"ownedCpuMs\":{ownedCpuMs.Value}" : string.Empty;
    var ownedPidsJson = ownedPids is { Count: > 0 }
        ? string.Join(",", ownedPids)
        : string.Empty;
    var exitFileExistsJson = exitFileExists ? "true" : "false";
    File.WriteAllText(
        BackgroundDispatchRunner.GetHeartbeatPath(process),
        "{" +
        "\"pid\":999999," +
        $"\"childPid\":{childPidJson}," +
        $"\"ownedPids\":[{ownedPidsJson}]," +
        $"\"startedAt\":\"{process.StartedAt:O}\"," +
        $"\"lastObservedAt\":\"{lastObservedAt:O}\"," +
        $"\"lastProgressAt\":\"{lastProgressAt:O}\"," +
        $"\"state\":\"{state}\"," +
        $"\"stdoutBytes\":{stdoutBytes}," +
        $"\"stderrBytes\":{stderrBytes}," +
        $"\"exitFileExists\":{exitFileExistsJson}" +
        ownedCpuMsJson +
        "}");
}

    // Clears the operator's MCG_WORKER_SANDBOX for the duration of a test so dispatch-mode assertions
    // are hermetic — WorkerProfileDispatcher reads WorkerSandboxOptions.FromEnvironment(), so a test
    // asserting the default (workspace-write) sandbox mode would otherwise fail when the suite is run
    // under `conduct`/acceptance with MCG_WORKER_SANDBOX=1 set. Restores the prior value on dispose.
    private static WorkerSandboxEnvRestore ClearWorkerSandboxEnv()
    {
        var previous = Environment.GetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable);
        Environment.SetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable, null);
        return new WorkerSandboxEnvRestore(previous);
    }

    private sealed class WorkerSandboxEnvRestore(string? previous) : IDisposable
    {
        public void Dispose() =>
            Environment.SetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable, previous);
    }

    private static AgentDefinition SubscriptionPlannerAgent(string id, string name) => new(
        new AgentId(id),
        name,
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));

    private static AgentDefinition SubscriptionDeveloperAgent() => new(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));

    private static WorkerProfileCatalog DispatchTestProfiles() => new(
    [
        new WorkerProfile(
            "codex-cli",
            "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} {promptPath}")
    ]);

    private static AgentOrchestratorKernel WithGoalStatus(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        GoalStatus status)
    {
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals
                .Select(goal => goal.Id == goalId.Value ? goal with { Status = status } : goal)
                .ToArray()
        });
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

private static string ReadGit(string workingDirectory, string[] arguments)
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

    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start git.");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit(60000);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
    }

    return output.Trim();
}

private static (int ExitCode, string StandardOutput, string StandardError) RunPowerShellCommand(string workingDirectory, string command)
{
    // Resolve the PowerShell host the same way production dispatch does (pwsh-preferred, with the
    // Windows-only -ExecutionPolicy), so this smoke runs natively on Linux instead of resolving
    // powershell.exe via WSL interop against a Linux working directory.
    var startInfo = new ProcessStartInfo
    {
        FileName = WorkerShell.Executable,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = workingDirectory
    };
    foreach (var argument in WorkerShell.BaseArguments())
    {
        startInfo.ArgumentList.Add(argument);
    }

    startInfo.ArgumentList.Add(command);

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start PowerShell.");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit(60000);

    return (process.ExitCode, output, error);
}

private static void WriteDispatchArtifact(string logsRoot, string prefix, string suffix, string content, DateTime lastWriteTime)
{
    var path = Path.Combine(logsRoot, prefix + suffix);
    File.WriteAllText(path, content);
    File.SetLastWriteTime(path, lastWriteTime);
}

private static string FindRepositoryFile(params string[] relativeSegments)
{
    var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
    var candidate = Path.Combine(new[] { repositoryRoot }.Concat(relativeSegments).ToArray());
    if (File.Exists(candidate))
    {
        return candidate;
    }

    throw new FileNotFoundException($"Could not find repository file '{Path.Combine(relativeSegments)}'.");
}

private static string WorkerResultBlock(
    string files,
    string commands,
    string tests,
    string commit = "{commit}",
    string blockers = "none",
    string modelFit = "OpenAI/gpt-5.5 - adequate - test worker fixture.",
    string skills = "dotnet-windows-build-hygiene",
    string confidence = "high")
{
    return $"""
        WORKER_RESULT:
        files: {files}
        commands: {commands}
        tests: {tests}
        commit: {commit}
        blockers: {blockers}
        model_fit: {modelFit}
        skills: {skills}
        confidence: {confidence}
        END_WORKER_RESULT
        """;
}

private static void WriteSkill(string workingDirectory, string skillName)
{
    var directory = Path.Combine(workingDirectory, ".agents", "skills", skillName);
    Directory.CreateDirectory(directory);
    File.WriteAllText(
        Path.Combine(directory, "SKILL.md"),
        $"""
        ---
        name: {skillName}
        description: Test skill fixture.
        ---

        # {skillName}
        """);
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

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        private DateTimeOffset _utcNow = utcNow;

        public DateTimeOffset UtcNow => _utcNow;

        public void Advance() => _utcNow = _utcNow.AddSeconds(1);
    }

    private sealed class CaptureDiagnosticWriter : IDispatchDiagnosticWriter
    {
        public List<DispatchDiagnosticRecord> Records { get; } = [];
        public void WriteRecord(DispatchDiagnosticRecord record) => Records.Add(record);
    }

    private sealed class ThrowingDiagnosticWriter : IDispatchDiagnosticWriter
    {
        public void WriteRecord(DispatchDiagnosticRecord record) =>
            throw new InvalidOperationException("Diagnostic writer failure (test-injected).");
    }
}

