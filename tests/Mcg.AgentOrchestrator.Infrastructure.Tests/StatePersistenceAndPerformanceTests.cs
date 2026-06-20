using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
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
        var statePath = Path.Combine(workspace, ".orchestrator", "state.db");
        var workerPath = Path.Combine(workspace, ".orchestrator", "workers.json");

        Assert.Equal(expectedWorkspace, workspace);
        Assert.True(File.Exists(statePath));

        var kernel = LoadState(statePath);
        kernel.CreateGoal("Persisted dogfood goal");
        SaveState(statePath, kernel);
        WorkerProfileStore.Save(
            workerPath,
            new WorkerProfileCatalog(
            [
                new WorkerProfile("custom-dogfood", "Write-Output custom"),
            new WorkerProfile("codex-cli", "Write-Output {promptPath}"),
            new WorkerProfile("claude-cli", "Write-Output {promptPath}")
            ]));

        var secondWorkspace = PrototypeWorkspaceSeeder.Create(root);
        var restored = LoadState(statePath);
        var restoredWorkers = WorkerProfileStore.Load(workerPath);

        Assert.Equal(workspace, secondWorkspace);
        Assert.True(restored.Goals.Any(goal => goal.Objective == "Persisted dogfood goal"));
        Assert.Equal("Write-Output custom", restoredWorkers.GetRequired("custom-dogfood").CommandTemplate);
        Assert.Equal("Write-Output {promptPath}", restoredWorkers.GetRequired("local-echo").CommandTemplate);
        Assert.Contains(restoredWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("codex exec", StringComparison.Ordinal));
        Assert.Contains(restoredWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("--model {subscriptionModelName}", StringComparison.Ordinal));
        Assert.Contains(restoredWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("-c model_reasoning_effort={subscriptionReasoningEffort}", StringComparison.Ordinal));
        Assert.Contains(restoredWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("--sandbox {sandboxMode}", StringComparison.Ordinal));
        Assert.Contains(restoredWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("--cd {workingDirectory}", StringComparison.Ordinal));
        Assert.Contains(restoredWorkers.GetRequired("claude-cli").CommandTemplate, text => text.Contains("claude --model {subscriptionModelName} --permission-mode {permissionMode} -p", StringComparison.Ordinal));

        File.Delete(workerPath);
        File.Delete(Path.Combine(workspace, ".orchestrator", "agents.json"));

        var repairedWorkspace = PrototypeWorkspaceSeeder.Create(root);
        var repairedWorkers = WorkerProfileStore.Load(workerPath);

        Assert.Equal(workspace, repairedWorkspace);
        Assert.True(File.Exists(workerPath));
        Assert.True(File.Exists(Path.Combine(workspace, ".orchestrator", "agents.json")));
        Assert.True(LoadState(statePath).Goals.Any(goal => goal.Objective == "Persisted dogfood goal"));
        Assert.Contains(repairedWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("codex exec", StringComparison.Ordinal));
        Assert.Contains(repairedWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("--model {subscriptionModelName}", StringComparison.Ordinal));
        Assert.Contains(repairedWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("-c model_reasoning_effort={subscriptionReasoningEffort}", StringComparison.Ordinal));
        Assert.Contains(repairedWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("--sandbox {sandboxMode}", StringComparison.Ordinal));
        Assert.Contains(repairedWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("--cd {workingDirectory}", StringComparison.Ordinal));
        Assert.Contains(repairedWorkers.GetRequired("claude-cli").CommandTemplate, text => text.Contains("claude --model {subscriptionModelName} --permission-mode {permissionMode} -p", StringComparison.Ordinal));
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
        var statePath = Path.Combine(workspace, ".orchestrator", "state.db");
        var workerPath = Path.Combine(workspace, ".orchestrator", "workers.json");
        const string customCodex = "codex exec --sandbox {sandboxMode} --cd {workingDirectory} --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} (Get-Content -Raw {promptPath})";

        var kernel = LoadState(statePath);
        kernel.CreateGoal("Keep custom subscription profile");
        SaveState(statePath, kernel);
        WorkerProfileStore.Save(
            workerPath,
            WorkerProfileCatalog.Default().Upsert(new WorkerProfile("codex-cli", customCodex)));

        PrototypeWorkspaceSeeder.Create(root);

        var restoredWorkers = WorkerProfileStore.Load(workerPath);
        Assert.Equal(customCodex, restoredWorkers.GetRequired("codex-cli").CommandTemplate);
        Assert.True(LoadState(statePath).Goals.Any(goal => goal.Objective == "Keep custom subscription profile"));
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
        Assert.Contains(restoredWorkers.GetRequired("codex-cli").CommandTemplate, text => text.Contains("--sandbox {sandboxMode}", StringComparison.Ordinal));
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
    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_roundtrips_kernel_snapshot")]
    public async Task SqliteOrchestratorStateRepositoryRoundtripsKernelSnapshot()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "state.db");
        var repository = new SqliteOrchestratorStateRepository(path);
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

        await repository.SaveAsync(kernel);
        var restored = await repository.LoadAsync();

        Assert.Equal(goal.Id, restored.Goals.Single().Id);
        Assert.Equal("Persist state", restored.Goals.Single().Objective);
        Assert.Equal("Run dotnet test before accepting.", restored.GetTask(goal.Id, task.Id).VerificationPlan);
        Assert.Equal(TaskComplexity.Simple, restored.GetTask(goal.Id, task.Id).LastExecution!.TaskComplexity);
        Assert.Equal(promptCharacterCount, restored.GetTask(goal.Id, task.Id).LastExecution!.PromptCharacterCount);
    }

    [Xunit.Fact(DisplayName = "OrchestratorWorkspace_scopes_named_tenants_to_isolated_state")]
    public void OrchestratorWorkspaceScopesNamedTenantsToIsolatedState()
    {
        var root = CreateTempDirectory();
        var defaultWorkspace = OrchestratorWorkspace.ForDirectory(root);
        var tenantWorkspace = OrchestratorWorkspace.ForDirectory(root, tenantName: "tenant_a");

        Assert.Equal("default", defaultWorkspace.TenantName);
        Assert.False(defaultWorkspace.IsTenantScoped);
        Assert.True(defaultWorkspace.SqliteStatePath.EndsWith(Path.Combine(".orchestrator", "state.db"), StringComparison.Ordinal));
        Assert.Equal("tenant_a", tenantWorkspace.TenantName);
        Assert.True(tenantWorkspace.IsTenantScoped);
        Assert.True(tenantWorkspace.SqliteStatePath.Contains(Path.Combine(".orchestrator", "tenants", "tenant_a", "state.db"), StringComparison.Ordinal));
        Assert.True(tenantWorkspace.ContinuationStorePath.Contains(Path.Combine(".orchestrator", "tenants", "tenant_a", DashboardContinuationService.StoreFileName), StringComparison.Ordinal));
        Assert.False(defaultWorkspace.SqliteStatePath.Equals(tenantWorkspace.SqliteStatePath, StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "OrchestratorTenantSelection_accepts_cli_override_and_rejects_path_segments")]
    public void OrchestratorTenantSelectionAcceptsCliOverrideAndRejectsPathSegments()
    {
        var selection = OrchestratorTenantSelection.FromArgs(
            ["--tenant", "customer-1", "simple-goal", "Do work"],
            "from_env");

        Assert.Equal("customer-1", selection.TenantName);
        Assert.True(selection.CommandArgs.SequenceEqual(["simple-goal", "Do work"]));
        Assert.Throws<ArgumentException>(() => OrchestratorTenantSelection.FromArgs(["--tenant=../bad", "goals"], null));
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_handles_concurrent_saves")]
    public async Task SqliteOrchestratorStateRepositoryHandlesConcurrentSaves()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, ".orchestrator", "state.db");
        var repository = new SqliteOrchestratorStateRepository(path);

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
        Assert.Equal(16, restored.Goals.Count);
        for (var index = 0; index < 16; index++)
        {
            Assert.True(restored.Goals.Any(goal => goal.Objective == $"Concurrent save {index}"));
        }
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_transaction_preserves_concurrent_mutations")]
    public async Task SqliteOrchestratorStateRepositoryTransactionPreservesConcurrentMutations()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, ".orchestrator", "state.db");
        var repository = new SqliteOrchestratorStateRepository(path);
        await repository.SaveAsync(new AgentOrchestratorKernel());

        var writes = Enumerable.Range(0, 12)
            .Select(index => Task.Run(() => repository.TransactAsync(
                (kernel, _) =>
                {
                    kernel.CreateGoal($"Serialized mutation {index}");
                    return Task.FromResult((true, true));
                })))
            .ToArray();

        await Task.WhenAll(writes);

        var restored = await repository.LoadAsync();
        Assert.Equal(12, restored.Goals.Count);
        var objectives = restored.Goals.Select(goal => goal.Objective).ToList();
        for (var index = 0; index < 12; index++)
        {
            Assert.True(objectives.Contains($"Serialized mutation {index}"));
        }
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_mixed_concurrent_writers_do_not_throw_locked")]
    public async Task SqliteOrchestratorStateRepositoryMixedConcurrentWritersDoNotThrowLocked()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, ".orchestrator", "state.db");
        var repository = new SqliteOrchestratorStateRepository(path);
        await repository.SaveAsync(new AgentOrchestratorKernel());

        // Mirrors the conduct --loop crash: per-tick SaveAsync checkpoints racing goal-create
        // TransactAsync mutations. The fix is that contention serializes/retries instead of throwing
        // an unhandled SQLITE_BUSY "database is locked" that would kill the loop.
        var writers = new List<Task>();
        for (var i = 0; i < 6; i++)
        {
            writers.Add(Task.Run(async () => await repository.SaveAsync(await repository.LoadAsync())));
            var index = i;
            writers.Add(Task.Run(() => repository.TransactAsync(
                (kernel, _) =>
                {
                    kernel.CreateGoal($"Concurrent create {index}");
                    return Task.FromResult((true, true));
                })));
        }

        // Throws if any writer surfaced an unhandled lock error; completion is the core assertion.
        await Task.WhenAll(writers);

        // The repository is still functional and consistent after the contention storm.
        await repository.TransactAsync((kernel, _) =>
        {
            kernel.CreateGoal("post-contention write");
            return Task.FromResult((true, true));
        });
        var restored = await repository.LoadAsync();
        var objectives = restored.Goals.Select(goal => goal.Objective).ToList();
        Assert.True(objectives.Contains("post-contention write"));
    }

    [Xunit.Fact(DisplayName = "Dashboard_and_state_persistence_have_reasonable_smoke_performance")]
    public async Task DashboardAndStatePersistenceHaveReasonableSmokePerformance()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, ".orchestrator", "state.db");
        var repository = new SqliteOrchestratorStateRepository(path);
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

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_bounds_verification_output_when_path_set_and_text_is_large")]
    public async Task SqliteOrchestratorStateRepositoryBoundsVerificationOutputWhenPathSetAndTextIsLarge()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, ".orchestrator", "state.db");
        var repository = new SqliteOrchestratorStateRepository(path);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Bound large stdout");
        var agent = new AgentDefinition(
            AgentId.New(), "Developer", AgentRole.Developer,
            new ModelProfile("Fake", "fake", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        var logPath = Path.Combine(root, ".orchestrator", "logs", "stdout.log").Replace('\\', '/');
        var largeStdout = new string('A', 220_000);
        var largeStderr = new string('E', 220_000);

        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "dotnet test", root, 0,
            largeStdout, largeStderr,
            DateTimeOffset.UtcNow,
            StandardOutputPath: logPath,
            StandardErrorPath: logPath + ".err"));

        await repository.SaveAsync(kernel);

        // Load must succeed; path reference and exit code preserved
        var restored = await repository.LoadAsync();
        var restoredTask = restored.GetTask(goal.Id, task.Id);
        Xunit.Assert.NotNull(restoredTask.LastVerification);
        Assert.Equal(logPath, restoredTask.LastVerification!.StandardOutputPath);
        Assert.Equal(0, restoredTask.LastVerification.ExitCode);
        // Bounded preview must begin with the head of the original text
        Assert.Contains(
            restoredTask.LastVerification.StandardOutput,
            text => text.StartsWith(new string('A', 4096), StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_preserves_full_inline_output_when_no_path_is_set")]
    public async Task SqliteOrchestratorStateRepositoryPreservesFullInlineOutputWhenNoPathIsSet()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, ".orchestrator", "state.db");
        var repository = new SqliteOrchestratorStateRepository(path);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("No path keeps full text");
        var agent = new AgentDefinition(
            AgentId.New(), "Developer", AgentRole.Developer,
            new ModelProfile("Fake", "fake", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);

        // Large output but NO path — backward-compatible old-state form
        var largeOutput = new string('B', 220_000);

        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "dotnet test", root, 0,
            largeOutput, string.Empty,
            DateTimeOffset.UtcNow));   // no StandardOutputPath

        await repository.SaveAsync(kernel);

        // Full text must be round-tripped when no path reference exists
        var restored = await repository.LoadAsync();
        var restoredTask = restored.GetTask(goal.Id, task.Id);
        Xunit.Assert.NotNull(restoredTask.LastVerification);
        Assert.Equal(largeOutput, restoredTask.LastVerification!.StandardOutput);
        Xunit.Assert.Null(restoredTask.LastVerification.StandardOutputPath);
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

    private static AgentOrchestratorKernel LoadState(string path)
    {
        return new SqliteOrchestratorStateRepository(path).LoadAsync().GetAwaiter().GetResult();
    }

    private static void SaveState(string path, AgentOrchestratorKernel kernel)
    {
        new SqliteOrchestratorStateRepository(path).SaveAsync(kernel).GetAwaiter().GetResult();
    }
}

