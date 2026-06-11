using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Prototype;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

public sealed class StatePersistenceAndPerformanceTests
{
    [Xunit.Fact(DisplayName = "PrototypeWorkspaceSeeder_reuses_persistent_workspace_without_overwriting_state")]
    public void PrototypeWorkspaceSeederReusesPersistentWorkspaceWithoutOverwritingState()
{
    var root = CreateTempDirectory();
    var workspace = PrototypeWorkspaceSeeder.Create(root);
    var expectedWorkspace = Path.Combine(root, PrototypeWorkspaceSeeder.PrototypeDirectoryName, PrototypeWorkspaceSeeder.PrototypeWorkspaceName);
    var statePath = Path.Combine(workspace, ".orchestrator", "state.json");
    var workerPath = Path.Combine(workspace, ".orchestrator", "workers.json");

    Assert.Equal(expectedWorkspace, workspace);
    Assert.True(File.Exists(statePath));

    var kernel = OrchestratorStateStore.Load(statePath);
    kernel.CreateGoal("Persisted dogfood goal");
    OrchestratorStateStore.Save(statePath, kernel);
    WorkerProfileStore.Save(
        workerPath,
        new WorkerProfileCatalog(
        [
            new WorkerProfile("custom-dogfood", "Write-Output custom"),
            new WorkerProfile("codex-cli", "Write-Output {promptPath}"),
            new WorkerProfile("claude-cli", "Write-Output {promptPath}")
        ]));

    var secondWorkspace = PrototypeWorkspaceSeeder.Create(root);
    var restored = OrchestratorStateStore.Load(statePath);
    var restoredWorkers = WorkerProfileStore.Load(workerPath);

    Assert.Equal(workspace, secondWorkspace);
    Assert.True(restored.Goals.Any(goal => goal.Objective == "Persisted dogfood goal"));
    Assert.Equal("Write-Output custom", restoredWorkers.GetRequired("custom-dogfood").CommandTemplate);
    Assert.Equal("Write-Output {promptPath}", restoredWorkers.GetRequired("local-echo").CommandTemplate);
    Assert.Contains(restoredWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("codex exec", StringComparison.Ordinal));
    Assert.Contains(restoredWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("--model {subscriptionModelName}", StringComparison.Ordinal));
    Assert.Contains(restoredWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("-c model_reasoning_effort={subscriptionReasoningEffort}", StringComparison.Ordinal));
    Assert.Contains(restoredWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("--sandbox workspace-write", StringComparison.Ordinal));
    Assert.Contains(restoredWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("--cd {workingDirectory}", StringComparison.Ordinal));
    Assert.Contains(restoredWorkers.GetRequired("claude-cli").CommandTemplate, text => text.Contains("claude --model {subscriptionModelName} --permission-mode bypassPermissions -p", StringComparison.Ordinal));

    File.Delete(workerPath);
    File.Delete(Path.Combine(workspace, ".orchestrator", "agents.json"));

    var repairedWorkspace = PrototypeWorkspaceSeeder.Create(root);
    var repairedWorkers = WorkerProfileStore.Load(workerPath);

    Assert.Equal(workspace, repairedWorkspace);
    Assert.True(File.Exists(workerPath));
    Assert.True(File.Exists(Path.Combine(workspace, ".orchestrator", "agents.json")));
    Assert.True(OrchestratorStateStore.Load(statePath).Goals.Any(goal => goal.Objective == "Persisted dogfood goal"));
    Assert.Contains(repairedWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("codex exec", StringComparison.Ordinal));
    Assert.Contains(repairedWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("--model {subscriptionModelName}", StringComparison.Ordinal));
    Assert.Contains(repairedWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("-c model_reasoning_effort={subscriptionReasoningEffort}", StringComparison.Ordinal));
    Assert.Contains(repairedWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("--sandbox workspace-write", StringComparison.Ordinal));
    Assert.Contains(repairedWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("--cd {workingDirectory}", StringComparison.Ordinal));
    Assert.Contains(repairedWorkers.GetRequired("claude-cli").CommandTemplate, text => text.Contains("claude --model {subscriptionModelName} --permission-mode bypassPermissions -p", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "PrototypeWorkspaceSeeder_uses_local_agent_fallback_when_supplied")]
    public void PrototypeWorkspaceSeederUsesLocalAgentFallbackWhenSupplied()
{
    var root = CreateTempDirectory();
    var workspace = PrototypeWorkspaceSeeder.Create(root, AgentCatalog.OllamaDefault());
    var agentPath = Path.Combine(workspace, ".orchestrator", "agents.json");

    var restored = AgentCatalogStore.Load(agentPath);

    foreach (var role in Enum.GetValues<AgentRole>())
    {
        var agent = restored.GetRequired(role);
        Assert.Equal("Ollama", agent.Model.ProviderName);
        Assert.Equal("qwen2.5-coder:7b", agent.Model.ModelName);
        Assert.True(agent.Subscription is null);
        Assert.Equal("qwen3:8b", agent.ComplexModel!.ModelName);
    }
}
    [Xunit.Fact(DisplayName = "PrototypeWorkspaceSeeder_repairs_paid_defaults_to_local_fallback")]
    public void PrototypeWorkspaceSeederRepairsPaidDefaultsToLocalFallback()
{
    var root = CreateTempDirectory();
    var workspace = PrototypeWorkspaceSeeder.Create(root);
    var agentPath = Path.Combine(workspace, ".orchestrator", "agents.json");
    AgentCatalogStore.Save(agentPath, AgentCatalog.Default());

    PrototypeWorkspaceSeeder.Create(root, AgentCatalog.OllamaDefault());

    var restored = AgentCatalogStore.Load(agentPath);
    foreach (var role in Enum.GetValues<AgentRole>())
    {
        Assert.Equal("Ollama", restored.GetRequired(role).Model.ProviderName);
    }
}
    [Xunit.Fact(DisplayName = "PrototypeWorkspaceSeeder_preserves_custom_real_subscription_profiles")]
    public void PrototypeWorkspaceSeederPreservesCustomRealSubscriptionProfiles()
{
    var root = CreateTempDirectory();
    var workspace = PrototypeWorkspaceSeeder.Create(root);
    var statePath = Path.Combine(workspace, ".orchestrator", "state.json");
    var workerPath = Path.Combine(workspace, ".orchestrator", "workers.json");
    const string customCodex = "codex exec --sandbox workspace-write --cd {workingDirectory} --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} (Get-Content -Raw {promptPath})";

    var kernel = OrchestratorStateStore.Load(statePath);
    kernel.CreateGoal("Keep custom subscription profile");
    OrchestratorStateStore.Save(statePath, kernel);
    WorkerProfileStore.Save(
        workerPath,
        WorkerProfileCatalog.Default().Upsert(new WorkerProfile("codex-cli", customCodex)));

    PrototypeWorkspaceSeeder.Create(root);

    var restoredWorkers = WorkerProfileStore.Load(workerPath);
    Assert.Equal(customCodex, restoredWorkers.GetRequired("codex-cli").CommandTemplate);
    Assert.True(OrchestratorStateStore.Load(statePath).Goals.Any(goal => goal.Objective == "Keep custom subscription profile"));
}
    [Xunit.Fact(DisplayName = "PrototypeWorkspaceSeeder_upgrades_readonly_codex_subscription_profiles")]
    public void PrototypeWorkspaceSeederUpgradesReadonlyCodexSubscriptionProfiles()
{
    var root = CreateTempDirectory();
    var workspace = PrototypeWorkspaceSeeder.Create(root);
    var workerPath = Path.Combine(workspace, ".orchestrator", "workers.json");
    WorkerProfileStore.Save(
        workerPath,
        WorkerProfileCatalog.Default().Upsert(new WorkerProfile("codex-cli", "codex exec --skip-git-repo-check (Get-Content -Raw {promptPath})")));

    PrototypeWorkspaceSeeder.Create(root);

    var restoredWorkers = WorkerProfileStore.Load(workerPath);
    Assert.Contains(restoredWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("--sandbox workspace-write", StringComparison.Ordinal));
    Assert.Contains(restoredWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("--cd {workingDirectory}", StringComparison.Ordinal));
    Assert.Contains(restoredWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("--model {subscriptionModelName}", StringComparison.Ordinal));
    Assert.Contains(restoredWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("-c model_reasoning_effort={subscriptionReasoningEffort}", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "PrototypeWorkspaceSeeder_upgrades_codex_profiles_missing_model_or_reasoning_propagation")]
    public void PrototypeWorkspaceSeederUpgradesCodexProfilesMissingModelOrReasoningPropagation()
{
    var root = CreateTempDirectory();
    var workspace = PrototypeWorkspaceSeeder.Create(root);
    var workerPath = Path.Combine(workspace, ".orchestrator", "workers.json");
    WorkerProfileStore.Save(
        workerPath,
        WorkerProfileCatalog.Default().Upsert(new WorkerProfile("codex-cli", "codex exec --skip-git-repo-check --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})")));

    PrototypeWorkspaceSeeder.Create(root);

    var restoredWorkers = WorkerProfileStore.Load(workerPath);
    Assert.Contains(restoredWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("--model {subscriptionModelName}", StringComparison.Ordinal));
    Assert.Contains(restoredWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("-c model_reasoning_effort={subscriptionReasoningEffort}", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "PrototypeWorkspaceSeeder_upgrades_stale_persisted_agent_catalog")]
    public void PrototypeWorkspaceSeederUpgradesStalePersistedAgentCatalog()
{
    var root = CreateTempDirectory();
    var workspace = PrototypeWorkspaceSeeder.Create(root);
    var agentPath = Path.Combine(workspace, ".orchestrator", "agents.json");

    var matchingPlanner = new AgentDefinition(
        new AgentId("custom-openai-planner"),
        "Custom OpenAI planner",
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey, "high"),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
        Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.StaleOpenAiCodexSubscriptionModelAlias, "high"),
        ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey, "high", AgentCatalog.ComplexApiMaxOutputTokens));

    AgentCatalogStore.Save(
        agentPath,
        new AgentCatalog(
        [
            matchingPlanner,
            new(
                new AgentId("anthropic-researcher"),
                "Anthropic researcher",
                AgentRole.Researcher,
                new ModelProfile("Anthropic", "claude-sonnet", ModelCapability.Text | ModelCapability.ToolUse, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
                Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet")),
            new(
                new AgentId("openai-developer-old"),
                "OpenAI developer old",
                AgentRole.Developer,
                new ModelProfile("OpenAI", "gpt-5", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey, "low"),
                ExecutionPolicy: AgentExecutionPolicy.ApiOnly),
            new(
                new AgentId("openai-tester-old"),
                "OpenAI tester old",
                AgentRole.Tester,
                new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey, "medium"),
                ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
                Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "medium")),
            new(
                new AgentId("anthropic-reviewer"),
                "Anthropic reviewer",
                AgentRole.Reviewer,
                new ModelProfile("Anthropic", "claude-sonnet", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
                Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet"))
        ]));

    PrototypeWorkspaceSeeder.Create(root);

    var restored = AgentCatalogStore.Load(agentPath);
    AssertPrototypeAgent(restored.GetRequired(AgentRole.Planner), "openai-planner");
    AssertPrototypeAgent(restored.GetRequired(AgentRole.Researcher), "openai-researcher");
    AssertPrototypeAgent(restored.GetRequired(AgentRole.Developer), "openai-developer");
    AssertPrototypeAgent(restored.GetRequired(AgentRole.Tester), "openai-tester");
    AssertPrototypeAgent(restored.GetRequired(AgentRole.Reviewer), "openai-reviewer");
}
    [Xunit.Fact(DisplayName = "OrchestratorStateStore_roundtrips_kernel_snapshot")]
    public async Task OrchestratorStateStoreRoundtripsKernelSnapshot()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "state.json");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Persist state");
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.SetTaskVerificationPlan(goal.Id, task.Id, "Run dotnet test before accepting.");
    await new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([new FakeSmokeProvider()]))
        .RunAsync(goal.Id, task.Id);
    var promptCharacterCount = task.LastExecution!.PromptCharacterCount;

    OrchestratorStateStore.Save(path, kernel);
    var restored = OrchestratorStateStore.Load(path);

    Assert.Equal(goal.Id, restored.Goals.Single().Id);
    Assert.Equal("Persist state", restored.Goals.Single().Objective);
    Assert.Equal("Run dotnet test before accepting.", restored.GetTask(goal.Id, task.Id).VerificationPlan);
    Assert.Equal(TaskComplexity.Simple, restored.GetTask(goal.Id, task.Id).LastExecution!.TaskComplexity);
    Assert.Equal(promptCharacterCount, restored.GetTask(goal.Id, task.Id).LastExecution!.PromptCharacterCount);
}
    [Xunit.Fact(DisplayName = "OrchestratorStateStore_handles_concurrent_atomic_saves")]
    public async Task OrchestratorStateStoreHandlesConcurrentAtomicSaves()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, ".orchestrator", "state.json");
    var repository = new FileOrchestratorStateRepository(path);

    var writes = Enumerable.Range(0, 16)
        .Select(index => Task.Run(async () =>
        {
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal($"Concurrent save {index}");
            await repository.SaveAsync(kernel);
        }))
        .ToArray();

    await Task.WhenAll(writes);

    var restored = await repository.LoadAsync();
    Assert.Equal(1, restored.Goals.Count);
    Assert.True(restored.Goals.Single().Objective.StartsWith("Concurrent save ", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "OrchestratorStateStore_retries_transient_atomic_replace_access_denial")]
    public async Task OrchestratorStateStoreRetriesTransientAtomicReplaceAccessDenial()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, ".orchestrator", "state.json");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "{}");

    await using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    var kernel = new AgentOrchestratorKernel();
    kernel.CreateGoal("Retry transient destination lock");

    var save = OrchestratorStateStore.SaveAsync(path, kernel);
    await Task.Delay(125);
    await hold.DisposeAsync();
    await save;

    var restored = OrchestratorStateStore.Load(path);
    Assert.Equal("Retry transient destination lock", restored.Goals.Single().Objective);
    Assert.Equal(0, Directory.EnumerateFiles(Path.GetDirectoryName(path)!, ".state.json.*.tmp").Count());
}
    [Xunit.Fact(DisplayName = "Dashboard_and_state_persistence_have_reasonable_smoke_performance")]
    public async Task DashboardAndStatePersistenceHaveReasonableSmokePerformance()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, ".orchestrator", "state.json");
    var repository = new FileOrchestratorStateRepository(path);
    var agents = AgentCatalog.Default().Agents;
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Render a large local dashboard smoke scenario");
    kernel.ActivateGoal(goal.Id, agents);

    for (var index = 0; index < 100; index++)
    {
        kernel.AddTask(
            goal.Id,
            index % 2 == 0 ? AgentRole.Developer : AgentRole.Tester,
            $"Synthetic performance task {index}",
            agents,
            "Record smoke evidence.");
    }

    var elapsed = Stopwatch.StartNew();
    await repository.SaveAsync(kernel);
    var restored = await repository.LoadAsync();
    var html = DashboardRenderer.Render(restored, new DashboardRenderOptions(AutoRefreshSeconds: 5));
    elapsed.Stop();

    Assert.True(restored.Goals.Single().Tasks.Count >= 100);
    Assert.True(html.Contains("Synthetic performance task 99", StringComparison.Ordinal));
    Xunit.Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Persistence/render smoke took {elapsed.Elapsed}.");
}

    private static void AssertPrototypeAgent(AgentDefinition agent, string expectedId)
{
    Assert.Equal(expectedId, agent.Id.Value);
    Assert.Equal("OpenAI", agent.Model.ProviderName);
    Assert.Equal("gpt-5.4-mini", agent.Model.ModelName);
    Assert.Equal(AgentCatalog.RoutineReasoningEffort, agent.Model.ReasoningEffort);
    Assert.Equal(AgentCatalog.RoutineApiMaxOutputTokens, agent.Model.MaxOutputTokens);
    Assert.Equal(AgentExecutionPolicy.PreferSubscription, agent.ExecutionPolicy);
    Assert.True(agent.Subscription is not null);
    var subscription = agent.Subscription!;
    Assert.Equal("codex-cli", subscription.WorkerProfileName);
    Assert.Equal(AgentCatalog.OpenAiSubscriptionModelAlias, subscription.ModelAlias);
    Assert.Equal(AgentCatalog.RoutineSubscriptionReasoningEffort, subscription.ReasoningEffort);
    Assert.True(agent.ComplexModel is not null);
    Assert.Equal("OpenAI", agent.ComplexModel!.ProviderName);
    Assert.Equal("gpt-5.5", agent.ComplexModel.ModelName);
    Assert.Equal(AgentCatalog.ComplexReasoningEffort, agent.ComplexModel.ReasoningEffort);
    Assert.Equal(AgentCatalog.ComplexApiMaxOutputTokens, agent.ComplexModel.MaxOutputTokens);
}
}

