using System.Diagnostics;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("EnvMutation")]
public sealed class AdvanceLoopTests
{
    private static string CreateTempDirectory()
    {
        var path = InfrastructureTestSupport.CreateTempDirectory();
        SeedLocalSkillCatalog(path);
        _ = StateDbMigrations.EnsureUpToDate(OrchestratorWorkspace.ForDirectory(path).SqliteStatePath);
        return path;
    }

    internal const string WorkerReleaseFileName = "advance-loop-worker.release";

    internal static string WorkerReleasePath(string root) => Path.Combine(root, WorkerReleaseFileName);

    internal static string BlockingWorkerCommand(string root, string output)
    {
        var releasePath = WorkerReleasePath(root).Replace("'", "''", StringComparison.Ordinal);
        // The safety cap only handles a dead test host; normal teardown releases the worker.
        return $"for ($poll = 0; $poll -lt 1500 -and -not (Test-Path -LiteralPath '{releasePath}'); $poll++) {{ Start-Sleep -Milliseconds 200 }}; " + output;
    }

    internal static void ReleaseBlockingWorkers(string root) => File.WriteAllText(WorkerReleasePath(root), "released");

    internal static string BlockingCodexProfileCommand(string root) => BlockingWorkerCommand(root,
        "Write-Output {subscriptionModelName}; Write-Output {subscriptionReasoningEffort}; Write-Output '--sandbox {sandboxMode} --cd {workingDirectory}'");

    internal static string BlockingClaudeProfileCommand(string root) => BlockingWorkerCommand(root,
        "Write-Output {subscriptionModelName}; Write-Output '--permission-mode {permissionMode}'");

    internal static string BlockingXhighCodexProfileCommand(string root) => BlockingWorkerCommand(root,
        "Write-Output {subscriptionModelName}; Write-Output model_reasoning_effort={subscriptionReasoningEffort}; Write-Output '--sandbox {sandboxMode} --cd {workingDirectory}'");

    [Xunit.Fact(DisplayName = "CreateActivateAndHandoffGoal_starts_first_subscription_dispatch")]
    public void CreateActivateAndHandoffGoalStartsFirstSubscriptionDispatch()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    SeedSpecRefinerBinding(workspace);
    var kernel = new AgentOrchestratorKernel();
    var agents = AgentCatalog.Default().Agents;
    var providers = new InMemoryModelProviderRegistry([]);
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", BlockingCodexProfileCommand(root)),
        new WorkerProfile("claude-cli", "Write-Output {subscriptionModelName}; Write-Output {promptPath}")
    ]);

    Goal? goal = null;
        TaskSpec? researcher = null;
    try
    {
        goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel,
            agents,
            GoalObjectivePlanner.Build("Start subscription handoff on create", GoalIntakePipeline.FiveRole),
            workspace,
            providers);
        var advancement = new GoalAdvancementOperations().AdvanceGoalWithSubscriptionsUntilBlocked(
            kernel,
            agents,
            profiles,
            workspace,
            goal,
            allowLargePaidSubscriptionStart: true,
            providers: providers);
        researcher = goal.Tasks.First(task => task.RequiredRole == AgentRole.Researcher);
        var handoffDiagnostic =
            $"Expected automatic handoff to complete {nameof(NextActionAutomationKind.StartRecordedDispatch)}. " +
            $"StopReason: {advancement.StopReason}; Failure: {advancement.Failure?.Reason ?? "<none>"}";

        Assert.True(
            advancement.Steps.Any(step =>
                step.Executed &&
                step.AutomationKind == NextActionAutomationKind.StartRecordedDispatch),
            handoffDiagnostic);

        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(WorkTaskStatus.Running, researcher.Status);
        Assert.True(researcher.LastDispatch is not null);
        var dispatch = researcher.LastDispatch!;
        Assert.True(dispatch.WorkerName is "codex-cli" or "claude-cli", dispatch.WorkerName);
        if (dispatch.WorkerName.Equals("claude-cli", StringComparison.Ordinal))
        {
            Assert.Equal("claude-haiku-4-5", dispatch.ModelName);
        }

        Assert.Equal(workspace.ExecutionDirectory, dispatch.WorkingDirectory);
        Assert.True(researcher.LastProcess is not null, handoffDiagnostic);
        Assert.True(researcher.LastProcess!.IsRunning);
        Assert.Equal(dispatch.Command, researcher.LastProcess.Command);
        Assert.True(goal.Timeline.Any(evt => evt.Kind == ProgressKind.TaskDispatchRecorded && evt.TaskId == researcher.Id));
        Assert.True(goal.Timeline.Any(evt => evt.Kind == ProgressKind.TaskProcessStarted && evt.TaskId == researcher.Id));
    }
    finally
    {
        try
        {
            if (goal is not null && researcher?.LastProcess is { IsRunning: true })
            {
                new BackgroundDispatchRunner().CancelLatestProcess(kernel, goal.Id, researcher.Id);
            }
        }
        finally
        {
            ReleaseBlockingWorkers(root);
        }
    }
}

    [Xunit.Fact(DisplayName = "AdvanceGoalUntilBlocked_delegates_and_stops_before_api_execution")]
    public async Task AdvanceGoalUntilBlockedDelegatesAndStopsBeforeApiExecution()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Implement API-backed task", AgentRole.Developer, "Record explicit verification.");
    var goal = CreateRefinedGoal(kernel, "Advance until blocked", [task]);
    var agent = new AgentDefinition(
        new AgentId("api-developer"),
        "API developer",
        AgentRole.Developer,
        new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
    var provider = new FakeSmokeProvider();
    var providers = new InMemoryModelProviderRegistry([provider]);

    var result = await new GoalAdvancementOperations().AdvanceGoalUntilBlockedAsync(
        kernel,
        [agent],
        providers,
        workspace,
        goal);

    Assert.True(result.Executed);
    Assert.Equal(1, result.StepCount);
    Assert.Equal(NextActionAutomationKind.DelegatePendingTask, result.Steps[0].AutomationKind);
    Assert.Equal(NextActionKind.RunAssignedTask, result.BlockingAction!.Kind);
    Assert.Contains("stopped before API-backed execution", result.StopReason, StringComparison.Ordinal);
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.True(task.LastExecution is null);
    Assert.True(provider.LastRequest is null);
}

    [Xunit.Fact(DisplayName = "AdvanceGoalAsync_stops_before_api_only_model_execution")]
    public async Task AdvanceGoalAsyncStopsBeforeApiOnlyModelExecution()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Implement API-backed task", AgentRole.Developer, "Record explicit verification.");
    var goal = CreateRefinedGoal(kernel, "Advance should not spend API tokens", [task]);
    var agent = new AgentDefinition(
        new AgentId("api-developer"),
        "API developer",
        AgentRole.Developer,
        new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
    kernel.ActivateGoal(goal.Id, [agent]);
    var provider = new FakeSmokeProvider();

    var result = await new GoalAdvancementOperations().AdvanceGoalAsync(
        kernel,
        [agent],
        new InMemoryModelProviderRegistry([provider]),
        workspace,
        goal);

    Assert.False(result.Executed);
    Assert.Equal(NextActionAutomationKind.RunAssignedTask, result.AutomationKind);
    Assert.Contains("stopped before API-backed execution", result.Message, StringComparison.Ordinal);
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.True(task.LastExecution is null);
    Assert.True(provider.LastRequest is null);
}

    [Xunit.Fact(DisplayName = "AdvanceGoalAsync_stops_before_starting_recorded_dispatch")]
    public async Task AdvanceGoalAsyncStopsBeforeStartingRecordedDispatch()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Run prepared subscription work", AgentRole.Developer, "Record explicit verification.");
    var goal = CreateRefinedGoal(kernel, "Advance should not start worker processes", [task]);
    var agent = new AgentDefinition(
        new AgentId("subscription-developer"),
        "Subscription developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    kernel.ActivateGoal(goal.Id, [agent]);
    kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord("codex-cli", "Write-Output ok", workspace.ExecutionDirectory, DateTimeOffset.UtcNow));

    var result = await new GoalAdvancementOperations().AdvanceGoalAsync(
        kernel,
        [agent],
        new InMemoryModelProviderRegistry([]),
        workspace,
        goal);

    Assert.False(result.Executed);
    Assert.Equal(NextActionAutomationKind.StartRecordedDispatch, result.AutomationKind);
    Assert.Contains("stopped before starting recorded dispatch", result.Message, StringComparison.Ordinal);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.True(task.LastDispatch is not null);
    Assert.True(task.LastProcess is null);
}

    [Xunit.Fact(DisplayName = "Advance_action_reports_dispatch_state_captured_before_the_refresh")]
    public async Task AdvanceActionReportsDispatchStateCapturedBeforeTheRefresh()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Watch a short local process", AgentRole.Developer, "Record explicit verification.");
    var goal = CreateRefinedGoal(kernel, "Advance refreshes a finished process", [task]);
    var agent = new AgentDefinition(
        new AgentId("subscription-developer"),
        "Subscription developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    kernel.ActivateGoal(goal.Id, [agent]);
    kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord("local", "Write-Output ok", workspace.ExecutionDirectory, DateTimeOffset.UtcNow));
    new BackgroundDispatchRunner().StartLatestDispatch(kernel, goal.Id, task.Id, workspace.LogDirectory);

    var startedProcess = task.LastProcess!;
    Assert.True(startedProcess.IsRunning);

    // Event gate: the wrapper writes the exit artifact when the child exits. No wall-clock pacing.
    var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
    while (DateTimeOffset.UtcNow < deadline && !File.Exists(startedProcess.ExitCodePath))
    {
        await Task.Delay(25);
    }

    Assert.True(File.Exists(startedProcess.ExitCodePath));

    var pendingItem = kernel.BuildNextActions(goal.Id).Items.First();
    Assert.Equal(NextActionKind.RefreshRunningProcess, pendingItem.Kind);
    Assert.NotNull(DispatchRecoveryView.EvaluateState(goal, pendingItem, commandLineSnapshot: null));

    var outcome = await new GoalAdvancementOperations().AdvanceGoalAsync(
        kernel,
        [agent],
        new InMemoryModelProviderRegistry([]),
        workspace,
        goal);

    Assert.True(outcome.Executed);
    Assert.Equal(NextActionAutomationKind.RefreshRunningProcess, outcome.AutomationKind);
    Assert.True(task.LastProcess?.CompletedAt is not null);

    // The refresh is exactly what invalidates the evaluation, so a renderer reading the live goal
    // after the step reports no dispatch state at all. The operation must carry the state it saw.
    Assert.Null(DispatchRecoveryView.EvaluateState(goal, outcome.Action!, commandLineSnapshot: null));
    Assert.NotNull(outcome.ActionDispatchState);

}

    [Xunit.Fact(DisplayName = "Advance_results_trim_verbose_automation_failures")]
    public async Task AdvanceResultsTrimVerboseAutomationFailures()
{
    var profileName = "profile-start-" + new string('p', 2000) + "-profile-tail";
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Run missing subscription profile", AgentRole.Developer, "Record explicit verification.");
    var goal = CreateRefinedGoal(kernel, "Trim advance automation failure", [task]);
    var agent = new AgentDefinition(
        new AgentId("subscription-developer"),
        "Subscription developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-test", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
        Subscription: new SubscriptionLaunchProfile(profileName));
    kernel.ActivateGoal(goal.Id, [agent]);

    var single = await new GoalAdvancementOperations().AdvanceGoalAsync(
        kernel,
        [agent],
        new InMemoryModelProviderRegistry([]),
        workspace,
        goal);

    Assert.False(single.Executed);
    Assert.Contains("profile-start", single.Message, StringComparison.Ordinal);
    Assert.Contains("profile-tail", single.Message, StringComparison.Ordinal);
    Assert.True(task.LastDispatch is null);

    var loopKernel = new AgentOrchestratorKernel();
    var loopTask = new TaskSpec(TaskId.New(), "Run missing subscription profile", AgentRole.Developer, "Record explicit verification.");
    var loopGoal = CreateRefinedGoal(loopKernel, "Trim advance loop automation failure", [loopTask]);
    var loop = await new GoalAdvancementOperations().AdvanceGoalUntilBlockedAsync(
        loopKernel,
        [agent],
        new InMemoryModelProviderRegistry([]),
        workspace,
        loopGoal);

    Assert.True(loop.Executed);
    Assert.Equal(1, loop.StepCount);
    Assert.Contains("profile-start", loop.StopReason, StringComparison.Ordinal);
    Assert.Contains("profile-tail", loop.StopReason, StringComparison.Ordinal);
    Assert.True(loopTask.LastDispatch is null);

}

    [Xunit.Fact(DisplayName = "AdvanceGoalUntilBlocked_blocks_prefer_subscription_before_api_fallback")]
    public async Task AdvanceGoalUntilBlockedBlocksPreferSubscriptionBeforeApiFallback()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    WorkerProfileStore.Save(
        workspace.WorkerProfilePath,
        WorkerProfileCatalog.Default().Upsert(new WorkerProfile("codex-cli", "Write-Output {promptPath}")));
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Avoid surprise API spend", AgentRole.Developer, "Record explicit verification.");
    var goal = CreateRefinedGoal(kernel, "Prefer subscription should not fall back automatically", [task]);
    EnsureGoalWorktree(root, goal.Id);
    var agent = new AgentDefinition(
        new AgentId("prefer-subscription-developer"),
        "Prefer Subscription developer",
        AgentRole.Developer,
        new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    var provider = new FakeSmokeProvider();

    var result = await new GoalAdvancementOperations().AdvanceGoalUntilBlockedAsync(
        kernel,
        [agent],
        new InMemoryModelProviderRegistry([provider]),
        workspace,
        goal);

    Assert.True(result.Executed);
    Assert.Equal(2, result.StepCount);
    Assert.Equal(NextActionAutomationKind.DelegatePendingTask, result.Steps[0].AutomationKind);
    Assert.Equal(NextActionAutomationKind.RunAssignedTask, result.Steps[1].AutomationKind);
    Assert.Equal(NextActionKind.ExecuteRecordedDispatch, result.BlockingAction!.Kind);
    Assert.Contains("stopped before starting recorded dispatch", result.StopReason, StringComparison.Ordinal);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.True(task.LastDispatch is not null);
    Assert.True(task.LastProcess is null);
    Assert.True(task.LastExecution is null);
    Assert.True(provider.LastRequest is null);
}
    [Xunit.Fact(DisplayName = "AdvanceGoalUntilBlocked_stops_before_any_available_api_fallback")]
    public async Task AdvanceGoalUntilBlockedStopsBeforeAnyAvailableApiFallback()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    WorkerProfileStore.Save(
        workspace.WorkerProfilePath,
        WorkerProfileCatalog.Default().Upsert(new WorkerProfile("codex-cli", "Write-Output {promptPath}")));
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Allow explicit fallback", AgentRole.Developer, "Record explicit verification.");
    var goal = CreateRefinedGoal(kernel, "Any available may fall back", [task]);
    EnsureGoalWorktree(root, goal.Id);
    var agent = new AgentDefinition(
        new AgentId("any-available-developer"),
        "Any Available developer",
        AgentRole.Developer,
        new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.AnyAvailable,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));

    var provider = new FakeSmokeProvider();

    var result = await new GoalAdvancementOperations().AdvanceGoalUntilBlockedAsync(
        kernel,
        [agent],
        new InMemoryModelProviderRegistry([provider]),
        workspace,
        goal);

    Assert.True(result.Executed);
    Assert.Equal(2, result.StepCount);
    Assert.Equal(NextActionAutomationKind.DelegatePendingTask, result.Steps[0].AutomationKind);
    Assert.Equal(NextActionAutomationKind.RunAssignedTask, result.Steps[1].AutomationKind);
    Assert.Equal(NextActionKind.ExecuteRecordedDispatch, result.BlockingAction!.Kind);
    Assert.Contains("stopped before starting recorded dispatch", result.StopReason, StringComparison.Ordinal);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.True(task.LastDispatch is not null);
    Assert.True(task.LastProcess is null);
    Assert.True(task.LastExecution is null);
    Assert.True(provider.LastRequest is null);
}

    [Xunit.Fact(DisplayName = "AdvanceGoalAsync_blocks_any_available_api_fallback_without_explicit_api_run")]
    public async Task AdvanceGoalAsyncBlocksAnyAvailableApiFallbackWithoutExplicitApiRun()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    WorkerProfileStore.Save(
        workspace.WorkerProfilePath,
        WorkerProfileCatalog.Default().Upsert(new WorkerProfile("codex-cli", "Write-Output {promptPath}")));
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Allow explicit fallback", AgentRole.Developer, "Record explicit verification.");
    var goal = CreateRefinedGoal(kernel, "Any available may fall back when run explicitly", [task]);
    var agent = new AgentDefinition(
        new AgentId("any-available-developer"),
        "Any Available developer",
        AgentRole.Developer,
        new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.AnyAvailable,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    kernel.ActivateGoal(goal.Id, [agent]);
    EnsureGoalWorktree(root, goal.Id);
    var provider = new FakeSmokeProvider();

    var result = await new GoalAdvancementOperations().AdvanceGoalAsync(
        kernel,
        [agent],
        new InMemoryModelProviderRegistry([provider]),
        workspace,
        goal);

    Assert.True(result.Executed);
    Assert.Equal(NextActionAutomationKind.RunAssignedTask, result.AutomationKind);
    Assert.Contains("Run the assigned model-backed task", result.Message, StringComparison.Ordinal);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.True(task.LastDispatch is not null);
    Assert.True(task.LastProcess is null);
    Assert.True(task.LastExecution is null);
    Assert.True(provider.LastRequest is null);
}
    [Xunit.Fact(DisplayName = "TaskApiRun_allows_any_available_api_fallback_when_explicit")]
    public async Task TaskApiRunAllowsAnyAvailableApiFallbackWhenExplicit()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    WorkerProfileStore.Save(
        workspace.WorkerProfilePath,
        WorkerProfileCatalog.Default().Upsert(new WorkerProfile("codex-cli", "Write-Output {promptPath}")));
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Allow explicit fallback", AgentRole.Developer, "Record explicit verification.");
    var goal = CreateRefinedGoal(kernel, "Any available may fall back when API run is explicit", [task]);
    var agent = new AgentDefinition(
        new AgentId("any-available-developer"),
        "Any Available developer",
        AgentRole.Developer,
        new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.AnyAvailable,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var provider = new FakeSmokeProvider();

    await new GoalAdvancementOperations().ApiRunAssignedTaskAsync(
        kernel, [agent], new InMemoryModelProviderRegistry([provider]), workspace, goal, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.True(task.LastExecution is not null);
    Assert.True(provider.LastRequest is not null);
}
    [Xunit.Fact(DisplayName = "TaskRun_blocks_any_available_api_fallback_after_subscription_dispatch")]
    public async Task TaskRunBlocksAnyAvailableApiFallbackAfterSubscriptionDispatch()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    WorkerProfileStore.Save(workspace.WorkerProfilePath, WorkerProfileCatalog.Default());
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Avoid duplicate fallback", AgentRole.Developer, "Record explicit verification.");
    var goal = CreateRefinedGoal(kernel, "Any available should not duplicate active subscription work", [task]);
    var agent = new AgentDefinition(
        new AgentId("any-available-developer"),
        "Any Available developer",
        AgentRole.Developer,
        new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.AnyAvailable,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    kernel.ActivateGoal(goal.Id, [agent]);
    EnsureGoalWorktree(root, goal.Id);
    var provider = new FakeSmokeProvider();

    new GoalDispatchOperations().SubscriptionDispatchTask(
        kernel,
        workspace,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default());

    var ex = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(async () => await new GoalAdvancementOperations().RunAssignedTaskAsync(
        kernel, [agent], new InMemoryModelProviderRegistry([provider]), workspace, goal, task.Id));

    Assert.Contains("stopped before API fallback", ex.Message, StringComparison.Ordinal);
    Assert.Contains("status is Running", ex.Message, StringComparison.Ordinal);
    Assert.True(provider.LastRequest is null);
    Assert.True(task.LastDispatch is not null);
    Assert.True(task.LastExecution is null);
}

    [Xunit.Fact(DisplayName = "SubscriptionDispatch_uses_workspace_execution_directory")]
    public async Task SubscriptionDispatchUsesWorkspaceExecutionDirectory()
{
    var root = CreateTempDirectory();
    var stateRoot = Path.Combine(root, "state");
    var executionRoot = Path.Combine(root, "repo");
    Directory.CreateDirectory(executionRoot);
    SeedLocalSkillCatalog(executionRoot);
    var workspace = OrchestratorWorkspace.ForDirectory(stateRoot, executionRoot);
    var kernel = new AgentOrchestratorKernel();
    var goal = CreateRefinedGoal(kernel,
        "Dispatch into execution root",
        [new TaskSpec(TaskId.New(), "Inspect command", AgentRole.Planner)]);
    var agents = AgentCatalog.Default().Agents;
    var profiles = WorkerProfileCatalog.Default();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();

    await Task.FromResult(new GoalDispatchOperations().SubscriptionDispatchTask(
        kernel, workspace, goal, task, agents, profiles));

    Assert.Equal(executionRoot, task.LastDispatch!.WorkingDirectory);
    var profile = profiles.GetRequired(task.LastDispatch.WorkerName);
    if (profile.CommandTemplate.Contains("{workingDirectory}", StringComparison.OrdinalIgnoreCase))
    {
        Assert.Contains(executionRoot, task.LastDispatch.Command, StringComparison.Ordinal);
    }
}

    [Xunit.Fact(DisplayName = "StartSubscriptionReadyTasks_starts_only_new_subscription_dispatches")]
    public void StartSubscriptionReadyTasksStartsOnlyNewSubscriptionDispatches()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var kernel = new AgentOrchestratorKernel();
    var prepared = new TaskSpec(TaskId.New(), "Previously prepared local work", AgentRole.Planner, "Record explicit verification.");
    var subscription = new TaskSpec(TaskId.New(), "Prepare subscription work", AgentRole.Planner, "Record explicit verification.");
    var goal = CreateRefinedGoal(kernel, "Start only subscription-ready work", [prepared, subscription]);
    var agent = new AgentDefinition(
        new AgentId("subscription-planner"),
        "Subscription planner",
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    kernel.ActivateGoal(goal.Id, [agent]);
    kernel.RecordTaskDispatch(
        goal.Id,
        prepared.Id,
        new TaskDispatchRecord("manual", BlockingWorkerCommand(root, "Write-Output manual"), workspace.ExecutionDirectory, DateTimeOffset.UtcNow));
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", BlockingCodexProfileCommand(root))
    ]);

    try
    {
        var result = new GoalDispatchOperations().StartSubscriptionReadyTasks(
            kernel,
            workspace,
            goal,
            [agent],
            profiles);

        Assert.Equal(1, result.Dispatches.Count);
        Assert.Equal(subscription.Id, result.Dispatches.Single().Task.Id);
        Assert.Equal(1, result.Processes.Tasks.Count);
        Assert.Equal(subscription.Id, result.Processes.Tasks.Single().Id);
        Assert.True(subscription.LastDispatch is not null);
        Assert.True(subscription.LastProcess is { IsRunning: true });
        Assert.True(prepared.LastDispatch is not null);
        Assert.True(prepared.LastProcess is null);
    }
    finally
    {
        try
        {
            if (subscription.LastProcess is { IsRunning: true })
            {
                new BackgroundDispatchRunner().CancelLatestProcess(kernel, goal.Id, subscription.Id);
            }
        }
        finally
        {
            ReleaseBlockingWorkers(root);
        }
    }
}

    [Xunit.Fact(DisplayName = "SubscriptionDispatchReadyBatch_applies_completed_result_without_redispatch")]
    public void SubscriptionDispatchReadyBatchAppliesCompletedResultWithoutRedispatch()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var clock = DateTimeOffset.Parse("2026-07-11T01:46:19Z");
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Inspect stale process state", AgentRole.Planner, "Record explicit verification.");
    var goal = CreateRefinedGoal(kernel, "Recover stale process before batch formation", [task]);
    var agent = new AgentDefinition(
        new AgentId("subscription-planner"),
        "Subscription planner",
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "exit.txt");
    File.WriteAllText(stdout, "done");
    File.WriteAllText(stderr, string.Empty);
    DispatchExitArtifacts.Write(exit, DispatchExitArtifacts.Native(0, "worker exited", clock));
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "old dispatch", root, clock));
    kernel.RecordTaskProcessStarted(
        goal.Id,
        task.Id,
        new TaskProcessRecord(Environment.ProcessId, "old dispatch", root, stdout, stderr, exit, clock, null, null));

    var snapshot = kernel.ExportSnapshot();
    var goalSnapshot = snapshot.Goals.Single();
    var staleAssignedSnapshot = goalSnapshot.Tasks.Single() with { Status = WorkTaskStatus.Assigned };
    kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
    {
        Goals = [goalSnapshot with { Tasks = [staleAssignedSnapshot] }]
    });
    goal = kernel.GetGoal(goal.Id);
    task = goal.Tasks.Single();
    var profiles = new WorkerProfileCatalog([new WorkerProfile("codex-cli", BlockingCodexProfileCommand(root))]);

    var batch = new GoalDispatchOperations(isProcessRunning: _ => false).SubscriptionDispatchReadyBatch(
        kernel,
        workspace,
        goal,
        [agent],
        profiles);

    Assert.Empty(batch.Dispatches);
    var reconciledTask = kernel.GetTask(goal.Id, task.Id);
    Assert.True(reconciledTask.LastProcess is { IsRunning: false });
    Assert.NotNull(reconciledTask.LastDispatch);
    Assert.Equal("old dispatch", reconciledTask.LastDispatch!.Command);
    var verification = Assert.IsType<TaskVerificationRecord>(reconciledTask.LastVerification);
    Assert.Contains("done", verification.StandardOutput, StringComparison.Ordinal);
    Assert.Contains(kernel.GetGoal(goal.Id).Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskNote &&
        evt.Message.Contains("Reconciled completed dispatch before preparing another dispatch", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "SubscriptionDispatchReadyBatch_preserves_synthetic_exit_for_typed_reconciliation")]
    public void SubscriptionDispatchReadyBatchPreservesSyntheticExitForTypedReconciliation()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var clock = DateTimeOffset.Parse("2026-07-11T01:49:19Z");
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Inspect interrupted process state", AgentRole.Planner, "Record explicit verification.");
    var goal = CreateRefinedGoal(kernel, "Preserve interrupted process before batch formation", [task]);
    var agent = new AgentDefinition(
        new AgentId("subscription-planner"),
        "Subscription planner",
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "exit.txt");
    File.WriteAllText(stdout, "partial work");
    File.WriteAllText(stderr, string.Empty);
    DispatchExitArtifacts.Write(exit, DispatchExitArtifacts.Synthetic(1, "startup sweep interrupted worker", clock));
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "old dispatch", root, clock));
    kernel.RecordTaskProcessStarted(
        goal.Id,
        task.Id,
        new TaskProcessRecord(28516, "old dispatch", root, stdout, stderr, exit, clock, null, null));

    var snapshot = kernel.ExportSnapshot();
    var goalSnapshot = snapshot.Goals.Single();
    var staleAssignedSnapshot = goalSnapshot.Tasks.Single() with { Status = WorkTaskStatus.Assigned };
    kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
    {
        Goals = [goalSnapshot with { Tasks = [staleAssignedSnapshot] }]
    });
    goal = kernel.GetGoal(goal.Id);
    task = goal.Tasks.Single();
    var profiles = new WorkerProfileCatalog([new WorkerProfile("codex-cli", BlockingCodexProfileCommand(root))]);

    var batch = new GoalDispatchOperations(isProcessRunning: _ => false).SubscriptionDispatchReadyBatch(
        kernel,
        workspace,
        goal,
        [agent],
        profiles);

    Assert.Empty(batch.Dispatches);
    var preservedTask = kernel.GetTask(goal.Id, task.Id);
    Assert.Equal(WorkTaskStatus.Assigned, preservedTask.Status);
    Assert.True(preservedTask.LastProcess is { IsRunning: true });
    Assert.Equal("old dispatch", preservedTask.LastDispatch!.Command);
    Assert.DoesNotContain(kernel.GetGoal(goal.Id).Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskNote &&
        evt.Message.Contains("Auto-cleared stale LastProcess.IsRunning before dispatch", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "SubscriptionDispatchReadyBatch_does_not_clear_live_child_pid_with_exit_artifact")]
    public void SubscriptionDispatchReadyBatchDoesNotClearLiveChildPidWithExitArtifact()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var clock = DateTimeOffset.Parse("2026-07-11T01:52:19Z");
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Inspect live process state", AgentRole.Planner, "Record explicit verification.");
    var goal = CreateRefinedGoal(kernel, "Hold live process before batch formation", [task]);
    var agent = new AgentDefinition(
        new AgentId("subscription-planner"),
        "Subscription planner",
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "exit.txt");
    File.WriteAllText(stdout, "done");
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "old dispatch", root, clock));
    var processRecord = new TaskProcessRecord(28516, "old dispatch", root, stdout, stderr, exit, clock, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, processRecord);
    var childIdentity = new SpawnProcessIdentity(28517, clock.AddSeconds(1), @"C:\workers\child.exe");
    File.WriteAllText(
        BackgroundDispatchRunner.GetHeartbeatPath(processRecord),
        "{\"pid\":28516,\"childPid\":28517,\"ownedPids\":[28517],\"ownedProcessIdentities\":[{\"processId\":28517,\"startedAt\":\"2026-07-11T01:52:20Z\",\"imagePath\":\"C:\\\\workers\\\\child.exe\"}],\"state\":\"running\",\"lastObservedAt\":\"2026-07-11T01:52:19Z\",\"lastProgressAt\":\"2026-07-11T01:52:19Z\",\"stdoutBytes\":4,\"stderrBytes\":0,\"ownedCpuMs\":1}");
    var profiles = new WorkerProfileCatalog([new WorkerProfile("codex-cli", BlockingCodexProfileCommand(root))]);

    var batch = new GoalDispatchOperations(
            isProcessRunning: pid => pid == 28517,
            readProcessIdentity: pid => pid == 28517 ? childIdentity : null)
        .SubscriptionDispatchReadyBatch(
            kernel,
            workspace,
            kernel.GetGoal(goal.Id),
            [agent],
            profiles);

    Assert.Empty(batch.Dispatches);
    var heldTask = kernel.GetTask(goal.Id, task.Id);
    Assert.NotNull(heldTask.LastProcess);
    Assert.True(heldTask.LastProcess!.IsRunning);
    Assert.DoesNotContain(kernel.GetGoal(goal.Id).Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskNote &&
        evt.Message.Contains("Auto-cleared stale LastProcess.IsRunning before dispatch", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "SubscriptionDispatchReadyBatch_leaves_dead_pid_without_exit_artifact_for_reap_watchdog")]
    public void SubscriptionDispatchReadyBatchLeavesDeadPidWithoutExitArtifactForReapWatchdog()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var clock = DateTimeOffset.Parse("2026-07-11T01:58:19Z");
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Inspect missing exit artifact", AgentRole.Planner, "Record explicit verification.");
    var goal = CreateRefinedGoal(kernel, "Hold missing exit artifact before batch formation", [task]);
    var agent = new AgentDefinition(
        new AgentId("subscription-planner"),
        "Subscription planner",
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "exit.txt");
    File.WriteAllText(stdout, "done");
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "old dispatch", root, clock));
    kernel.RecordTaskProcessStarted(
        goal.Id,
        task.Id,
        new TaskProcessRecord(28516, "old dispatch", root, stdout, stderr, exit, clock, null, null));
    var profiles = new WorkerProfileCatalog([new WorkerProfile("codex-cli", BlockingCodexProfileCommand(root))]);

    var batch = new GoalDispatchOperations(isProcessRunning: _ => false).SubscriptionDispatchReadyBatch(
        kernel,
        workspace,
        kernel.GetGoal(goal.Id),
        [agent],
        profiles);

    Assert.Empty(batch.Dispatches);
    var heldTask = kernel.GetTask(goal.Id, task.Id);
    Assert.NotNull(heldTask.LastProcess);
    Assert.True(heldTask.LastProcess!.IsRunning);
    Assert.DoesNotContain(kernel.GetGoal(goal.Id).Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskNote &&
        evt.Message.Contains("Auto-cleared stale LastProcess.IsRunning before dispatch", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "StartSubscriptionReadyTasks_uses_parallel_planner_first_safe_batch")]
    public void StartSubscriptionReadyTasksUsesParallelPlannerFirstSafeBatch()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var kernel = new AgentOrchestratorKernel();
    var first = new TaskSpec(TaskId.New(), "Inspect first independent area", AgentRole.Planner, "Record explicit verification.");
    var second = new TaskSpec(TaskId.New(), "Inspect second independent area", AgentRole.Planner, "Record explicit verification.");
    var goal = CreateRefinedGoal(kernel, "Start only the first provider-safe subscription batch", [first, second]);
    var agent = new AgentDefinition(
        new AgentId("subscription-planner"),
        "Subscription planner",
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", BlockingCodexProfileCommand(root))
    ]);

    try
    {
        var result = new GoalDispatchOperations().StartSubscriptionReadyTasks(
            kernel,
            workspace,
            goal,
            [agent],
            profiles);

        Assert.Equal(1, result.Dispatches.Count);
        Assert.Equal(first.Id, result.Dispatches.Single().Task.Id);
        Assert.Equal(1, result.Processes.Tasks.Count);
        Assert.Equal(first.Id, result.Processes.Tasks.Single().Id);
        Assert.True(first.LastProcess is { IsRunning: true });
        Assert.True(second.LastDispatch is null);
        Assert.Equal(2, result.ParallelPlan.Batches.Count);
        Assert.True(result.ParallelPlan.Decisions.Any(decision =>
            decision.IntentId == first.Id.Value &&
            decision.Disposition == ParallelExecutionDisposition.Concurrent &&
            decision.BatchNumber == 1));
        Assert.True(result.ParallelPlan.Decisions.Any(decision =>
            decision.IntentId == second.Id.Value &&
            decision.Disposition == ParallelExecutionDisposition.Serialized &&
            decision.BatchNumber == 2));
    }
    finally
    {
        try
        {
            if (first.LastProcess is { IsRunning: true })
            {
                new BackgroundDispatchRunner().CancelLatestProcess(kernel, goal.Id, first.Id);
            }
        }
        finally
        {
            ReleaseBlockingWorkers(root);
        }
    }
}

    [Xunit.Fact(DisplayName = "StartSubscriptionReadyTasks_dispatches_one_ready_sdlc_stage_at_a_time")]
    public void StartSubscriptionReadyTasksDispatchesOneReadySdlcStageAtATime()
{
    using var sandboxScope = ClearWorkerSandboxEnvironment();
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var kernel = new AgentOrchestratorKernel();
    var researcher = new TaskSpec(TaskId.New(), "Research the work", AgentRole.Researcher);
    var planner = new TaskSpec(TaskId.New(), "Plan the work", AgentRole.Planner);
    var firstDeveloper = new TaskSpec(TaskId.New(), "Implement the first part", AgentRole.Developer);
    var secondDeveloper = new TaskSpec(TaskId.New(), "Implement the second part", AgentRole.Developer);
    var tester = new TaskSpec(TaskId.New(), "Test the work", AgentRole.Tester);
    var reviewer = new TaskSpec(TaskId.New(), "Review the work", AgentRole.Reviewer);
    var goal = CreateRefinedGoal(kernel, "Dispatch in SDLC stage order", [researcher, planner, firstDeveloper, secondDeveloper, tester, reviewer]);
    AgentDefinition[] agents =
    [
        CreateSubscriptionAgent(AgentRole.Planner),
        CreateSubscriptionAgent(AgentRole.Researcher),
        CreateSubscriptionAgent(AgentRole.Developer),
        CreateSubscriptionAgent(AgentRole.Tester),
        CreateSubscriptionAgent(AgentRole.Reviewer)
    ];
    kernel.ActivateGoal(goal.Id, agents);
    EnsureGoalWorktree(root, goal.Id);
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", BlockingCodexProfileCommand(root)),
        new WorkerProfile("claude-cli", BlockingClaudeProfileCommand(root))
    ]);

    try
    {
        var first = new GoalDispatchOperations().StartSubscriptionReadyTasks(
            kernel,
            workspace,
            goal,
            agents,
            profiles);

        Assert.Equal(1, first.Dispatches.Count);
        Assert.Equal(researcher.Id, first.Dispatches[0].Task.Id);
        Assert.Equal(1, first.Processes.Tasks.Count);
        Assert.Equal(researcher.Id, first.Processes.Tasks[0].Id);
        Assert.True(researcher.LastProcess is { IsRunning: true });
        Assert.True(planner.LastDispatch is null);
        Assert.True(firstDeveloper.LastDispatch is null);

        new BackgroundDispatchRunner().CancelLatestProcess(kernel, goal.Id, researcher.Id);
        var researchPath = Path.Combine(root, "research.out.log");
        File.WriteAllText(researchPath, WorkerDispatchTestSupport.ResearcherContractFixture());
        var researchResult = ResearcherOutputContract.Resolve(File.ReadAllText(researchPath));
        Assert.True(researchResult.Succeeded, researchResult.Diagnostic);
        Assert.True(
            ResearcherOutputContract.TryPersistDurableReceipt(
                researchPath,
                researchResult.Research!,
                out var researchDiagnostic),
            researchDiagnostic);
        kernel.RecordTaskVerification(
            goal.Id,
            researcher.Id,
            new TaskVerificationRecord(
                "research",
                root,
                0,
                "Researcher complete.",
                string.Empty,
                DateTimeOffset.UtcNow,
                StandardOutputPath: researchPath));

        var second = new GoalDispatchOperations().StartSubscriptionReadyTasks(
            kernel,
            workspace,
            goal,
            agents,
            profiles);

        Assert.Equal(1, second.Dispatches.Count);
        Assert.Equal(planner.Id, second.Dispatches[0].Task.Id);
        Assert.Equal(1, second.Processes.Tasks.Count);
        Assert.Equal(planner.Id, second.Processes.Tasks[0].Id);
        Assert.True(planner.LastProcess is { IsRunning: true });
        Assert.True(firstDeveloper.LastDispatch is null);

        new BackgroundDispatchRunner().CancelLatestProcess(kernel, goal.Id, planner.Id);
        var planPath = Path.Combine(root, "planner.out.log");
        var plan = WorkerDispatchTestSupport.PlannerContractPlanFixture();
        File.WriteAllText(planPath, plan);
        Assert.True(
            PlannerOutputContract.TryPersistDurableReceipt(
                planPath,
                planPath,
                plan,
                out var planDiagnostic),
            planDiagnostic);
        kernel.RecordTaskVerification(
            goal.Id,
            planner.Id,
            new TaskVerificationRecord(
                "plan",
                root,
                0,
                "Planner complete.",
                string.Empty,
                DateTimeOffset.UtcNow,
                StandardOutputPath: planPath));

        var third = new GoalDispatchOperations().StartSubscriptionReadyTasks(
            kernel,
            workspace,
            goal,
            agents,
            profiles,
            approveHighRiskOwnership: true);

        Assert.True(
            third.Dispatches.Count > 0,
            string.Join(
                Environment.NewLine,
                third.BlockedDiagnostics.Select(item =>
                {
                    var blockedTask = goal.Tasks.Single(task => task.Id.Value == item.TaskId);
                    return $"{blockedTask.RequiredRole} {item.TaskId}: {item.Reason}: {string.Join(" | ", item.Details ?? [])}";
                })));
        foreach (var dispatch in third.Dispatches)
        {
            Assert.Equal(AgentRole.Developer, dispatch.Task.RequiredRole);
        }

        Assert.True(tester.LastDispatch is null);
        Assert.True(reviewer.LastDispatch is null);
    }
    finally
    {
        try
        {
            foreach (var task in goal.Tasks.Where(task => task.LastProcess is { IsRunning: true }))
            {
                new BackgroundDispatchRunner().CancelLatestProcess(kernel, goal.Id, task.Id);
            }
        }
        finally
        {
            ReleaseBlockingWorkers(root);
        }
    }
}

    [Xunit.Fact(DisplayName = "StartSubscriptionReadyTasks_uses_xhigh_reasoning_for_high_risk_reviewer")]
    public async Task StartSubscriptionReadyTasksUsesXhighReasoningForHighRiskReviewer()
{
    using var sandboxScope = ClearWorkerSandboxEnvironment();
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
    var kernel = new AgentOrchestratorKernel();
    var planner = new TaskSpec(TaskId.New(), "Plan the work", AgentRole.Planner);
    var researcher = new TaskSpec(TaskId.New(), "Research the work", AgentRole.Researcher);
    var developer = new TaskSpec(TaskId.New(), "Implement the work", AgentRole.Developer);
    var tester = new TaskSpec(TaskId.New(), "Test the work", AgentRole.Tester);
    var reviewer = new TaskSpec(TaskId.New(), "Review implementation output and risks.", AgentRole.Reviewer);
    var goal = CreateRefinedGoal(kernel, "Dispatch high-risk Reviewer from live loop shape", [planner, researcher, developer, tester, reviewer]);
    AgentDefinition[] agents =
    [
        CreateSubscriptionAgent(AgentRole.Planner),
        CreateSubscriptionAgent(AgentRole.Researcher),
        CreateSubscriptionAgent(AgentRole.Developer),
        CreateSubscriptionAgent(AgentRole.Tester),
        CreateSubscriptionAgent(AgentRole.Reviewer)
    ];
    kernel.RecordGoalPolicyDecision(
        goal.Id,
        "Intake pipeline decision (auto): developer-reviewer; reasons: high-risk objective needs pre-acceptance review; risk labels: complex, high-risk, scope-implicit.");
    kernel.ActivateGoal(goal.Id, agents);
    foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
    {
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, $"{task.RequiredRole} complete.");
    }

    EnsureGoalWorktree(root, goal.Id);
    await repository.SaveAsync(kernel);
    var loopKernel = await repository.LoadGoalsAsync([goal.Id]);
    var loopGoal = loopKernel.GetGoal(goal.Id);
    var loopReviewer = loopGoal.Tasks.Single(task => task.Id == reviewer.Id);
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", BlockingXhighCodexProfileCommand(root)),
        new WorkerProfile("claude-cli", BlockingClaudeProfileCommand(root))
    ]);

    try
    {
        var result = new GoalDispatchOperations().StartSubscriptionReadyTasks(
            loopKernel,
            workspace,
            loopGoal,
            agents,
            profiles);

        var reloadedReviewer = loopKernel.GetGoal(goal.Id).Tasks.Single(task => task.Id == reviewer.Id);
        Assert.Single(result.Dispatches);
        Assert.Equal(reviewer.Id, result.Dispatches[0].Task.Id);
        Assert.Equal("codex-cli", reloadedReviewer.LastDispatch!.WorkerName);
        Assert.Equal("xhigh", reloadedReviewer.LastDispatch.ReasoningEffort);
        Assert.Equal("intake-risk", reloadedReviewer.LastDispatch.ReasoningEffortReason);
        Assert.Contains("model_reasoning_effort='xhigh'", reloadedReviewer.LastDispatch.Command, StringComparison.Ordinal);
        Assert.Contains("reasoning-effort: xhigh (intake-risk)", File.ReadAllText(Path.Combine(
            reloadedReviewer.LastDispatch.WorkingDirectory,
            ".orchestrator-context",
            goal.Id.Value,
            "subscription-preflight.md")), StringComparison.Ordinal);
        Assert.True(reloadedReviewer.LastProcess is { IsRunning: true });
    }
    finally
    {
        try
        {
            if (loopReviewer.LastProcess is { IsRunning: true })
            {
                new BackgroundDispatchRunner().CancelLatestProcess(loopKernel, goal.Id, reviewer.Id);
            }
        }
        finally
        {
            ReleaseBlockingWorkers(root);
        }
    }
}

    [Xunit.Fact(DisplayName = "StartSubscriptionReadyTasks_does_not_prepare_already_running_tasks")]
    public void StartSubscriptionReadyTasksDoesNotPrepareAlreadyRunningTasks()
{
    using var sandboxScope = ClearWorkerSandboxEnvironment();
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Run once", AgentRole.Developer);
    var goal = CreateRefinedGoal(kernel, "Do not double dispatch running task", [task]);
    var agents = new[] { CreateSubscriptionAgent(AgentRole.Developer) };
    kernel.ActivateGoal(goal.Id, agents);
    EnsureGoalWorktree(root, goal.Id);
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", BlockingCodexProfileCommand(root)),
        new WorkerProfile("claude-cli", BlockingClaudeProfileCommand(root))
    ]);

    try
    {
        var first = new GoalDispatchOperations().StartSubscriptionReadyTasks(
            kernel,
            workspace,
            goal,
            agents,
            profiles);

        Assert.Equal(1, first.Dispatches.Count);
        Assert.Equal(task.Id, first.Dispatches[0].Task.Id);
        Assert.True(task.LastProcess is { IsRunning: true });

        var second = new GoalDispatchOperations().StartSubscriptionReadyTasks(
            kernel,
            workspace,
            goal,
            agents,
            profiles);

        Assert.Equal(0, second.Dispatches.Count);
        Assert.Equal(0, second.Processes.Tasks.Count);
        Assert.Equal(1, goal.Timeline.Count(evt => evt.Kind == ProgressKind.TaskDispatchRecorded && evt.TaskId == task.Id));
        Assert.Equal(1, goal.Timeline.Count(evt => evt.Kind == ProgressKind.TaskProcessStarted && evt.TaskId == task.Id));
    }
    finally
    {
        try
        {
            if (task.LastProcess is { IsRunning: true })
            {
                new BackgroundDispatchRunner().CancelLatestProcess(kernel, goal.Id, task.Id);
            }
        }
        finally
        {
            ReleaseBlockingWorkers(root);
        }
    }
}

    [Xunit.Fact(DisplayName = "BuildReadyTaskParallelPlan_preserves_same_stage_parallelism")]
    public void BuildReadyTaskParallelPlanPreservesSameStageParallelism()
{
    var kernel = new AgentOrchestratorKernel();
    var firstDeveloper = new TaskSpec(TaskId.New(), "Implement the first part", AgentRole.Developer);
    var secondDeveloper = new TaskSpec(TaskId.New(), "Implement the second part", AgentRole.Developer);
    var goal = CreateRefinedGoal(kernel, "Plan same-stage parallelism", [firstDeveloper, secondDeveloper]);
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);

    var plan = DispatchReadinessRules.BuildReadyTaskParallelPlan(goal);
    Assert.Equal(1, plan.Batches.Count);
    var firstBatch = plan.Batches[0];

    Assert.Equal(2, firstBatch.IntentIds.Count);
    Assert.True(firstBatch.IntentIds.Contains(firstDeveloper.Id.Value, StringComparer.OrdinalIgnoreCase));
    Assert.True(firstBatch.IntentIds.Contains(secondDeveloper.Id.Value, StringComparer.OrdinalIgnoreCase));
}

private static AgentDefinition CreateSubscriptionAgent(AgentRole role)
{
    return new AgentDefinition(
        new AgentId("subscription-" + role.ToString().ToLowerInvariant()),
        "Subscription " + role,
        role,
        new ModelProfile("OpenAI", "gpt-test", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
}

    [Xunit.Fact(DisplayName = "AdvanceGoalWithSubscriptionsUntilBlocked_continues_after_subscription_retry_window")]
    public void AdvanceGoalWithSubscriptionsUntilBlockedContinuesAfterSubscriptionRetryWindow()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var kernel = new AgentOrchestratorKernel();
    var goal = CreateRefinedGoal(kernel, "Resume after retry window", [new TaskSpec(TaskId.New(), "Run after subscription retry", AgentRole.Developer)]);
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    var now = DateTimeOffset.UtcNow;
    var retryTime = now.AddHours(1);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", workspace.ExecutionDirectory, now, WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "codex exec",
        workspace.ExecutionDirectory,
        1,
        string.Empty,
        $"ERROR: You've hit your usage limit. Visit settings to purchase more credits or try again at {retryTime:h:mm tt}.",
        now));

    var result = new GoalAdvancementOperations().AdvanceGoalWithSubscriptionsUntilBlocked(
        kernel,
        agents,
        WorkerProfileCatalog.Default(),
        workspace,
        goal);

    Assert.False(result.Executed);
    Assert.Equal(0, result.StepCount);
    Assert.Equal(NextActionKind.RunAssignedTask, result.BlockingAction!.Kind);
    Assert.Contains("Subscription retry window is deferred", result.StopReason, StringComparison.Ordinal);
    Assert.Equal(task.SubscriptionRetryAfter, result.ContinueAfter);
}
    [Xunit.Fact(DisplayName = "AdvanceGoalWithSubscriptionsUntilBlocked_continues_prompt_below_new_large_threshold")]
    public void AdvanceGoalWithSubscriptionsUntilBlockedContinuesPromptBelowNewLargeThreshold()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var kernel = new AgentOrchestratorKernel();
    var objective = "Design and implement a production multi-tenant distributed architecture " + new string('o', 5000);
    var task = new TaskSpec(
        TaskId.New(),
        "Build an end-to-end distributed integration with horizontal scaling across API CLI dashboard provider subscription worker persistence state tests docs " + new string('t', 5000),
        AgentRole.Developer,
        "Verify the full integration with build, tests, dashboard smoke, and focused regression evidence. " + new string('v', 5000));
    var goal = CreateRefinedGoal(kernel, objective, [task]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-codex"));
    kernel.ActivateGoal(goal.Id, [agent]);
    EnsureGoalWorktree(root, goal.Id);
    var promptCharacters = kernel.BuildTaskBrief(goal.Id, task.Id).Content.Length;

    var blocked = new GoalAdvancementOperations().AdvanceGoalWithSubscriptionsUntilBlocked(
        kernel,
        [agent],
        WorkerProfileCatalog.Default(),
        workspace,
        goal);

    Assert.True(promptCharacters > 6000);
    Assert.True(promptCharacters <= PaidPromptThresholds.AnomalyPromptThreshold(TaskComplexity.Complex, usesComplexModel: true));
    Assert.True(blocked.Executed);
    Assert.True(blocked.StepCount > 0);
    Assert.False(blocked.StopReason.Contains("--confirm-large-paid-subscription-start", StringComparison.Ordinal));
    Assert.True(task.LastDispatch is not null);
}
private static Goal CreateRefinedGoal(AgentOrchestratorKernel kernel, string objective, IReadOnlyList<TaskSpec> tasks)
{
    var goal = kernel.CreateGoal(objective, tasks);
    kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
        "Advance loop fixture goal is already refined.",
        ["Advance loop fixture goal is already refined."],
        VerificationClass.TestVerifiable,
        [],
        []));
    return goal;
}

private static string EnsureGoalWorktree(string root, GoalId goalId)
{
    if (!Directory.Exists(Path.Combine(root, ".git")))
    {
        AssertGit(root, "init", "-b", "main");
        AssertGit(root, "config", "user.email", "tests@example.com");
        AssertGit(root, "config", "user.name", "Advance Loop Tests");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        AssertGit(root, "add", "-A");
        AssertGit(root, "commit", "-m", "Seed");
    }

    return GoalWorktrees.Ensure(root, goalId);
}

private static void AssertGit(string workingDirectory, params string[] args)
{
    var result = GitCli.Run(workingDirectory, args);
    Assert.True(result.Succeeded, $"git {string.Join(' ', args)} failed: {result.Error}");
}

private static EnvironmentScope ClearWorkerSandboxEnvironment()
{
    return new EnvironmentScope(
        (WorkerSandboxOptions.EnabledVariable, null),
        (WorkerSandboxOptions.AccountVariable, null),
        (WorkerSandboxOptions.CredentialTargetVariable, null));
}

private sealed class EnvironmentScope : IDisposable
{
    private readonly (string Name, string? Previous)[] _previous;

    public EnvironmentScope(params (string Name, string? Value)[] values)
    {
        _previous = values
            .Select(value => (value.Name, Environment.GetEnvironmentVariable(value.Name)))
            .ToArray();
        foreach (var (name, value) in values)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, previous) in _previous)
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}

private static void SeedSpecRefinerBinding(OrchestratorWorkspace workspace)
{
    ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
        new ModelFunctionBinding(
            ModelFunctionPurposes.SpecRefiner,
            ModelLane.CheapApi,
            new ModelProfile("missing-provider", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            Name: ModelFunctionPurposes.SpecRefiner)
    ]));
}

}
