using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Extensions.Hosting;

public sealed class AdvanceLoopTests
{
    [Xunit.Fact(DisplayName = "CreateActivateAndHandoffGoal_starts_first_subscription_dispatch")]
    public void CreateActivateAndHandoffGoalStartsFirstSubscriptionDispatch()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var kernel = new AgentOrchestratorKernel();
    var agents = AgentCatalog.Default().Agents;
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", "Start-Sleep -Seconds 30; Write-Output {promptPath}"),
        new WorkerProfile("claude-cli", "Write-Output {promptPath}")
    ]);

    Goal? goal = null;
    TaskSpec? planner = null;
    try
    {
        goal = GoalLifecycleCommands.CreateActivateAndHandoffGoal(
            kernel,
            agents,
            profiles,
            workspace,
            "Start subscription handoff on create",
            simple: false);
        planner = goal.Tasks.First(task => task.RequiredRole == AgentRole.Planner);

        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(WorkTaskStatus.Running, planner.Status);
        Assert.True(planner.LastDispatch is not null);
        Assert.Equal("codex-cli", planner.LastDispatch!.WorkerName);
        Assert.Equal(workspace.ExecutionDirectory, planner.LastDispatch.WorkingDirectory);
        Assert.True(planner.LastProcess is not null);
        Assert.True(planner.LastProcess!.IsRunning);
        Assert.Equal(planner.LastDispatch.Command, planner.LastProcess.Command);
        Assert.True(goal.Timeline.Any(evt => evt.Kind == ProgressKind.TaskDispatchRecorded && evt.TaskId == planner.Id));
        Assert.True(goal.Timeline.Any(evt => evt.Kind == ProgressKind.TaskProcessStarted && evt.TaskId == planner.Id));
    }
    finally
    {
        if (goal is not null && planner?.LastProcess is { IsRunning: true })
        {
            new BackgroundDispatchRunner().CancelLatestProcess(kernel, goal.Id, planner.Id);
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
    var goal = kernel.CreateGoal("Advance until blocked", [task]);
    var agent = new AgentDefinition(
        new AgentId("api-developer"),
        "API developer",
        AgentRole.Developer,
        new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
    var provider = new FakeSmokeProvider();
    var providers = new InMemoryModelProviderRegistry([provider]);

    var result = await GoalManagementCommandService.AdvanceGoalUntilBlockedAsync(
        kernel,
        [agent],
        providers,
        workspace,
        goal);

    Assert.True(result.Executed);
    Assert.Equal(1, result.StepCount);
    Assert.Equal(NextActionAutomationKind.DelegatePendingTask, result.Steps[0].AutomationKind);
    Assert.Equal(NextActionKind.RunAssignedTask, result.BlockingAction!.Kind);
    Assert.Contains(result.StopReason, text => text.Contains("stopped before API-backed execution", StringComparison.Ordinal));
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
    var goal = kernel.CreateGoal("Advance should not spend API tokens", [task]);
    var agent = new AgentDefinition(
        new AgentId("api-developer"),
        "API developer",
        AgentRole.Developer,
        new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
    kernel.ActivateGoal(goal.Id, [agent]);
    var provider = new FakeSmokeProvider();

    var result = await GoalManagementCommandService.AdvanceGoalAsync(
        kernel,
        [agent],
        new InMemoryModelProviderRegistry([provider]),
        workspace,
        goal);

    Assert.False(result.Executed);
    Assert.Equal(NextActionAutomationKind.RunAssignedTask, result.AutomationKind);
    Assert.Contains(result.Message, text => text.Contains("stopped before API-backed execution", StringComparison.Ordinal));
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.True(task.LastExecution is null);
    Assert.True(provider.LastRequest is null);
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
    var goal = kernel.CreateGoal("Prefer subscription should not fall back automatically", [task]);
    var agent = new AgentDefinition(
        new AgentId("prefer-subscription-developer"),
        "Prefer Subscription developer",
        AgentRole.Developer,
        new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    var provider = new FakeSmokeProvider();

    var result = await GoalManagementCommandService.AdvanceGoalUntilBlockedAsync(
        kernel,
        [agent],
        new InMemoryModelProviderRegistry([provider]),
        workspace,
        goal);

    Assert.True(result.Executed);
    Assert.Equal(1, result.StepCount);
    Assert.Equal(NextActionAutomationKind.DelegatePendingTask, result.Steps[0].AutomationKind);
    Assert.Equal(NextActionKind.RunAssignedTask, result.BlockingAction!.Kind);
    Assert.Contains(result.StopReason, text => text.Contains("only echoes the prompt path", StringComparison.Ordinal));
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.True(task.LastExecution is null);
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
    var goal = kernel.CreateGoal("Any available may fall back", [task]);
    var agent = new AgentDefinition(
        new AgentId("any-available-developer"),
        "Any Available developer",
        AgentRole.Developer,
        new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.AnyAvailable,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));

    var provider = new FakeSmokeProvider();

    var result = await GoalManagementCommandService.AdvanceGoalUntilBlockedAsync(
        kernel,
        [agent],
        new InMemoryModelProviderRegistry([provider]),
        workspace,
        goal);

    Assert.True(result.Executed);
    Assert.Equal(1, result.StepCount);
    Assert.Equal(NextActionAutomationKind.DelegatePendingTask, result.Steps[0].AutomationKind);
    Assert.Equal(NextActionKind.RunAssignedTask, result.BlockingAction!.Kind);
    Assert.Contains(result.StopReason, text => text.Contains("stopped before API fallback", StringComparison.Ordinal));
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
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
    var goal = kernel.CreateGoal("Any available may fall back when run explicitly", [task]);
    var agent = new AgentDefinition(
        new AgentId("any-available-developer"),
        "Any Available developer",
        AgentRole.Developer,
        new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.AnyAvailable,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var provider = new FakeSmokeProvider();

    var result = await GoalManagementCommandService.AdvanceGoalAsync(
        kernel,
        [agent],
        new InMemoryModelProviderRegistry([provider]),
        workspace,
        goal);

    Assert.False(result.Executed);
    Assert.Equal(NextActionAutomationKind.RunAssignedTask, result.AutomationKind);
    Assert.Contains(result.Message, text => text.Contains("stopped before API fallback", StringComparison.Ordinal));
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
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
    var goal = kernel.CreateGoal("Any available may fall back when API run is explicit", [task]);
    var agent = new AgentDefinition(
        new AgentId("any-available-developer"),
        "Any Available developer",
        AgentRole.Developer,
        new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.AnyAvailable,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var provider = new FakeSmokeProvider();

    await GoalManagementCommandService.ApplyTaskActionAsync(
        kernel,
        [agent],
        new InMemoryModelProviderRegistry([provider]),
        workspace,
        goal,
        task,
        "api-run",
        string.Empty);

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
    var goal = kernel.CreateGoal("Any available should not duplicate active subscription work", [task]);
    var agent = new AgentDefinition(
        new AgentId("any-available-developer"),
        "Any Available developer",
        AgentRole.Developer,
        new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.AnyAvailable,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var provider = new FakeSmokeProvider();

    GoalManagementCommandService.SubscriptionDispatchTask(
        kernel,
        workspace,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default());

    var ex = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(async () => await GoalManagementCommandService.ApplyTaskActionAsync(
        kernel,
        [agent],
        new InMemoryModelProviderRegistry([provider]),
        workspace,
        goal,
        task,
        "run",
        string.Empty));

    Assert.Contains(ex.Message, text => text.Contains("stopped before API fallback", StringComparison.Ordinal));
    Assert.Contains(ex.Message, text => text.Contains("status is Running", StringComparison.Ordinal));
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
    var workspace = OrchestratorWorkspace.ForDirectory(stateRoot, executionRoot);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Dispatch into execution root",
        [new TaskSpec(TaskId.New(), "Inspect command", AgentRole.Developer)]);
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();

    await GoalManagementCommandService.ApplyTaskActionAsync(
        kernel,
        agents,
        new InMemoryModelProviderRegistry([]),
        workspace,
        goal,
        task,
        "subscription-dispatch",
        string.Empty);

    Assert.Equal(executionRoot, task.LastDispatch!.WorkingDirectory);
    Assert.Contains(task.LastDispatch.Command, text => text.Contains($"--cd '{executionRoot}'", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "AdvanceGoalWithSubscriptionsUntilBlocked_continues_after_subscription_retry_window")]
    public void AdvanceGoalWithSubscriptionsUntilBlockedContinuesAfterSubscriptionRetryWindow()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Resume after retry window", [new TaskSpec(TaskId.New(), "Run after subscription retry", AgentRole.Developer)]);
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    var now = DateTimeOffset.UtcNow;
    var retryTime = now.AddHours(1);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", workspace.ExecutionDirectory, now));
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "codex exec",
        workspace.ExecutionDirectory,
        1,
        string.Empty,
        $"ERROR: You've hit your usage limit. Visit settings to purchase more credits or try again at {retryTime:h:mm tt}.",
        now));

    var result = GoalManagementCommandService.AdvanceGoalWithSubscriptionsUntilBlocked(
        kernel,
        agents,
        WorkerProfileCatalog.Default(),
        workspace,
        goal);

    Assert.False(result.Executed);
    Assert.Equal(0, result.StepCount);
    Assert.Equal(NextActionKind.RunAssignedTask, result.BlockingAction!.Kind);
    Assert.Contains(result.StopReason, text => text.Contains("Subscription retry window is deferred", StringComparison.Ordinal));
    Assert.Equal(task.SubscriptionRetryAfter, result.ContinueAfter);
    Assert.True(DashboardContinuationService.ShouldContinueWatching(result));
}
    [Xunit.Fact(DisplayName = "DashboardContinuationService_refreshes_running_process_until_handoff_is_blocked")]
    public async Task DashboardContinuationServiceRefreshesRunningProcessUntilHandoffIsBlocked()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var repository = new FileOrchestratorStateRepository(workspace.StatePath);
    var agents = new AgentCatalog(
    [
        new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly)
    ]);
    AgentCatalogStore.Save(workspace.AgentCatalogPath, agents);
    WorkerProfileStore.Save(workspace.WorkerProfilePath, WorkerProfileCatalog.Default());

    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Watch process", [new TaskSpec(TaskId.New(), "Run short process", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, agents.Agents);
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord(
            "local",
            "Start-Sleep -Milliseconds 250; Write-Output ok",
            workspace.ExecutionDirectory,
            DateTimeOffset.UtcNow));
    new BackgroundDispatchRunner().StartLatestDispatch(kernel, goal.Id, task.Id, workspace.LogDirectory);
    await repository.SaveAsync(kernel);

    using var service = new DashboardContinuationService(TimeSpan.FromMilliseconds(75), 20);
    using var lifetime = new FakeHostLifetime();
    var services = new DashboardEndpointServices(
        new DashboardStateService(repository),
        workspace,
        new InMemoryModelProviderRegistry([]),
        new DashboardHostArgs("http://localhost:5087/", null, false, "prototype-ui"),
        lifetime,
        service);

    var started = service.StartSubscriptionWatch(services, goal.Id.Value);

    Assert.True(started.IsRunning);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (DateTimeOffset.UtcNow < deadline && service.GetStatuses().Single().IsRunning)
    {
        await Task.Delay(75);
    }

    var status = service.GetStatuses().Single();
    var restored = await repository.LoadAsync();
    var restoredTask = restored.GetTask(goal.Id, task.Id);
    Assert.False(status.IsRunning);
    Assert.True(status.IterationCount > 0);
    Assert.Equal(WorkTaskStatus.Completed, restoredTask.Status);
    Assert.True(restoredTask.LastVerification?.Succeeded is true);
}

    [Xunit.Fact(DisplayName = "DashboardContinuationService_restores_active_subscription_watch_after_restart")]
    public async Task DashboardContinuationServiceRestoresActiveSubscriptionWatchAfterRestart()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var repository = new FileOrchestratorStateRepository(workspace.StatePath);
    var agents = new AgentCatalog(
    [
        new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly)
    ]);
    AgentCatalogStore.Save(workspace.AgentCatalogPath, agents);
    WorkerProfileStore.Save(workspace.WorkerProfilePath, WorkerProfileCatalog.Default());

    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Resume persisted continuation", [new TaskSpec(TaskId.New(), "Wait for retry window", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, agents.Agents);
    var task = goal.Tasks.Single();
    var now = DateTimeOffset.UtcNow;
    var retryAfter = now.AddHours(1);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", workspace.ExecutionDirectory, now));
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "codex exec",
        workspace.ExecutionDirectory,
        1,
        string.Empty,
        $"ERROR: You've hit your usage limit. Visit settings to purchase more credits or try again at {retryAfter:h:mm tt}.",
        now));
    await repository.SaveAsync(kernel);

    var storePath = Path.Combine(workspace.RootDirectory, DashboardContinuationService.StoreFileName);
    using (var firstService = new DashboardContinuationService(TimeSpan.FromMilliseconds(250), 20))
    using (var lifetime = new FakeHostLifetime())
    {
        var services = new DashboardEndpointServices(
            new DashboardStateService(repository),
            workspace,
            new InMemoryModelProviderRegistry([]),
            new DashboardHostArgs("http://localhost:5087/", null, false, "prototype-ui"),
            lifetime,
            firstService);

        var started = firstService.StartSubscriptionWatch(services, goal.Id.Value);

        Assert.True(started.IsRunning);
        Assert.False(started.RestoredFromStore);
        Assert.True(File.Exists(storePath));
        Assert.True(File.ReadAllText(storePath).Contains(goal.Id.Value, StringComparison.Ordinal));
    }

    using var secondService = new DashboardContinuationService(TimeSpan.FromMilliseconds(250), 20);
    using var secondLifetime = new FakeHostLifetime();
    var secondServices = new DashboardEndpointServices(
        new DashboardStateService(repository),
        workspace,
        new InMemoryModelProviderRegistry([]),
        new DashboardHostArgs("http://localhost:5087/", null, false, "prototype-ui"),
        secondLifetime,
        secondService);

    var restored = secondService.RestoreSubscriptionWatches(secondServices);
    var status = restored.Single();

    Assert.True(status.IsRunning);
    Assert.True(status.RestoredFromStore);
    Assert.Equal(goal.Id.Value, status.GoalId);
    Assert.True(status.StopReason.Contains("Waiting for running background work or retry window", StringComparison.Ordinal));
    Assert.True(File.Exists(storePath));
}

    [Xunit.Fact(DisplayName = "DashboardContinuationService_prunes_restored_watch_that_is_no_longer_watchable")]
    public async Task DashboardContinuationServicePrunesRestoredWatchThatIsNoLongerWatchable()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var repository = new FileOrchestratorStateRepository(workspace.StatePath);
    var agents = AgentCatalog.Default();
    AgentCatalogStore.Save(workspace.AgentCatalogPath, agents);
    WorkerProfileStore.Save(workspace.WorkerProfilePath, WorkerProfileCatalog.Default());

    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Already done", AgentRole.Developer);
    var goal = kernel.CreateGoal("Prune stale restored watch", [task]);
    kernel.ActivateGoal(goal.Id, agents.Agents);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "done");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("manual", workspace.ExecutionDirectory, 0, "ok", string.Empty, DateTimeOffset.UtcNow));
    await repository.SaveAsync(kernel);

    var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
    File.WriteAllText(
        Path.Combine(workspace.RootDirectory, DashboardContinuationService.StoreFileName),
        $$"""
        {
          "watches": [
            {
              "goalId": "{{goal.Id.Value}}",
              "startedAt": "{{startedAt:O}}",
              "lastCheckedAt": null,
              "iterationCount": 0,
              "stopReason": "Waiting for running background work or retry window to complete.",
              "lastError": null,
              "nextCheckAt": null
            }
          ]
        }
        """);

    using var service = new DashboardContinuationService(TimeSpan.FromMilliseconds(10), 3);
    using var lifetime = new FakeHostLifetime();
    var services = new DashboardEndpointServices(
        new DashboardStateService(repository),
        workspace,
        new InMemoryModelProviderRegistry([]),
        new DashboardHostArgs("http://localhost:5087/", null, false, "prototype-ui"),
        lifetime,
        service);

    var restored = service.RestoreSubscriptionWatches(services);
    Assert.Equal(1, restored.Count);
    Assert.True(restored.Single().RestoredFromStore);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
    while (DateTimeOffset.UtcNow < deadline && service.GetStatuses().Count > 0)
    {
        await Task.Delay(25);
    }

    Assert.Equal(0, service.GetStatuses().Count);
    Assert.False(File.Exists(Path.Combine(workspace.RootDirectory, DashboardContinuationService.StoreFileName)));
}

    [Xunit.Fact(DisplayName = "DashboardEndpointServices_loads_agent_catalog_with_local_fallback")]
    public void DashboardEndpointServicesLoadsAgentCatalogWithLocalFallback()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var repository = new FileOrchestratorStateRepository(workspace.StatePath);
    using var service = new DashboardContinuationService(TimeSpan.FromMilliseconds(10), 3);
    using var lifetime = new FakeHostLifetime();
    var services = new DashboardEndpointServices(
        new DashboardStateService(repository),
        workspace,
        new InMemoryModelProviderRegistry([]),
        new DashboardHostArgs("http://localhost:5087/", null, false, "prototype-ui"),
        lifetime,
        service,
        AgentCatalog.OllamaDefault());

    var loaded = services.LoadAgentCatalog();
    var health = DashboardEndpoints.BuildHealthReport(services);

    Assert.False(File.Exists(workspace.AgentCatalogPath));
    foreach (var agent in loaded.Agents)
    {
        Assert.Equal("Ollama", agent.Model.ProviderName);
    }

    foreach (var agent in health.Agents)
    {
        Assert.Equal("Ollama", agent.ProviderName);
    }
}

private sealed class FakeHostLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _started = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _stopped = new();

    public CancellationToken ApplicationStarted => _started.Token;

    public CancellationToken ApplicationStopping => _stopping.Token;

    public CancellationToken ApplicationStopped => _stopped.Token;

    public void StopApplication()
    {
        _stopping.Cancel();
        _stopped.Cancel();
    }

    public void Dispose()
    {
        _started.Dispose();
        _stopping.Dispose();
        _stopped.Dispose();
    }
}
}
