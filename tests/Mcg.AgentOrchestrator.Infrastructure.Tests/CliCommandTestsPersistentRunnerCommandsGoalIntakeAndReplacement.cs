using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed partial class CliCommandTestsPersistentRunnerCommandsGoalIntakeAndReplacement : CliCommandTestBase
{
    [Xunit.Fact]
    public async Task BacklogIntakePreviewDoesNotRequireGoalCreationFinalizer()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync(
            "Preview this backlog slice without creating a goal");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => Xunit.Assert.False(
            CliPersistentStateRunner.ExecuteCommand(
                ["backlog-intake", item.Id],
                repository,
                workspace,
                ref agents,
                new InMemoryModelProviderRegistry([]),
                ref profiles,
                ref currentGoal)));

        Xunit.Assert.Contains("Preview this backlog slice", output, StringComparison.Ordinal);
        Xunit.Assert.Empty((await repository.LoadAsync()).Goals);
        Xunit.Assert.Empty(await repository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind));
    }

    [Xunit.Fact]
    public async Task BacklogIntakeKeylessCreationMarksRecordGoalCreated()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync(
            "Create a keyless backlog goal");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        Xunit.Assert.True(CliPersistentStateRunner.ExecuteCommand(
            ["backlog-intake", item.Id, "--create-simple-goal", "--backlog-coverage", "slice"],
            repository,
            workspace,
            ref agents,
            new InMemoryModelProviderRegistry([]),
            ref profiles,
            ref currentGoal));

        var goal = Xunit.Assert.Single((await repository.LoadAsync()).Goals);
        var record = new BacklogIntakeRecordStore(workspace.SqliteStatePath).Get(item.Id);
        Xunit.Assert.NotNull(record);
        Xunit.Assert.Equal("GoalCreated", record.Status);
        Xunit.Assert.Equal(goal.Id.Value, record.GoalId);
    }

    [Xunit.Fact]
    public async Task BacklogIntakeKeylessBatchCreatesEveryGoalWithoutReusingFinalizer()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var first = await store.AddAsync("Create first keyless batch goal");
        var second = await store.AddAsync("Create second keyless batch goal");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        Xunit.Assert.True(CliPersistentStateRunner.ExecuteCommand(
            [
                "backlog-intake", first.Id, second.Id,
                "--create-simple-goal", "--backlog-coverage", "slice"
            ],
            repository,
            workspace,
            ref agents,
            new InMemoryModelProviderRegistry([]),
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Equal(2, (await repository.LoadAsync()).Goals.Count);
        Xunit.Assert.All(
            new[] { first, second },
            item =>
            {
                var record = new BacklogIntakeRecordStore(workspace.SqliteStatePath).Get(item.Id);
                Xunit.Assert.NotNull(record);
                Xunit.Assert.Equal("GoalCreated", record.Status);
                Xunit.Assert.NotNull(record.GoalId);
            });
    }

    [Xunit.Fact]
    public async Task BacklogIntakeKeylessNoOpDoesNotRequireGoalCreationFinalizer()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var done = await store.AddAsync("Already completed backlog slice");
        await store.CloseAsync(done.Id);
        var busy = await store.AddAsync("Already reserved backlog slice");
        _ = new BacklogIntakeRecordStore(workspace.SqliteStatePath).Reserve(busy.Id, busy.Title);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        Xunit.Assert.False(CliPersistentStateRunner.ExecuteCommand(
            ["backlog-intake", done.Id, "--create-simple-goal", "--backlog-coverage", "slice"],
            repository,
            workspace,
            ref agents,
            new InMemoryModelProviderRegistry([]),
            ref profiles,
            ref currentGoal));
        Xunit.Assert.False(CliPersistentStateRunner.ExecuteCommand(
            ["backlog-intake", busy.Id, "--create-simple-goal", "--backlog-coverage", "slice"],
            repository,
            workspace,
            ref agents,
            new InMemoryModelProviderRegistry([]),
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Empty((await repository.LoadAsync()).Goals);
    }

    [Xunit.Fact]
    public async Task GoalIntake_same_key_replays_one_goal_and_task_graph()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var args = new[] { "simple-goal", "Implement one keyed task", "--request-key", "sequential-key" };

        var firstOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, providers, ref profiles, ref currentGoal));
        var first = Xunit.Assert.Single((await repository.LoadAsync()).Goals);
        var originalTaskIds = first.Tasks.Select(task => task.Id.Value).ToArray();

        var replayOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, providers, ref profiles, ref currentGoal));
        var restored = await repository.LoadAsync();

        var onlyGoal = Xunit.Assert.Single(restored.Goals);
        Xunit.Assert.Equal(first.Id, onlyGoal.Id);
        Xunit.Assert.Equal(originalTaskIds, onlyGoal.Tasks.Select(task => task.Id.Value));
        Xunit.Assert.Contains("\"state\":\"still-committing\"", firstOutput);
        Xunit.Assert.Contains("\"state\":\"created\"", firstOutput);
        Xunit.Assert.Contains("\"state\":\"created\"", replayOutput);
    }

    [Xunit.Fact]
    public async Task GoalIntake_conflicting_payload_reuse_is_rejected()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["simple-goal", "Original keyed task", "--request-key", "conflict-key"],
            repository, workspace, ref agents, providers, ref profiles, ref currentGoal));

        var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
            CliPersistentStateRunner.ExecuteCommand(
                ["simple-goal", "Changed keyed task", "--request-key", "conflict-key"],
                repository, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Xunit.Assert.Contains("GOAL_INTAKE_PAYLOAD_CONFLICT", error.Message);
        Xunit.Assert.Single((await repository.LoadAsync()).Goals);
    }

    [Xunit.Fact]
    public async Task GoalIntake_concurrent_same_key_creates_one_goal()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim(false);

        Task Start() => Task.Run(() =>
        {
            IReadOnlyList<AgentDefinition> localAgents = AgentCatalog.Default().Agents;
            var localProfiles = WorkerProfileCatalog.Default();
            Goal? localGoal = null;
            ready.Signal();
            release.Wait();
            _ = CliPersistentStateRunner.ExecuteCommand(
                ["simple-goal", "Concurrent keyed task", "--request-key", "concurrent-goal-key"],
                CreateMigratedStateRepository(workspace.SqliteStatePath),
                workspace,
                ref localAgents,
                new InMemoryModelProviderRegistry([]),
                ref localProfiles,
                ref localGoal);
        });

        var first = Start();
        var second = Start();
        Xunit.Assert.True(ready.Wait(TimeSpan.FromSeconds(15)), "Both goal attempts did not reach the event gate.");
        release.Set();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));

        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        Xunit.Assert.Single((await repository.LoadAsync()).Goals);
        Xunit.Assert.Equal(
            GoalIntakeRequestStates.Created,
            new GoalIntakeRequestStore(workspace.SqliteStatePath).Get("concurrent-goal-key")!.State);
    }

    [Xunit.Fact]
    public async Task GoalIntake_status_polls_without_retrying_creation()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var store = new GoalIntakeRequestStore(workspace.SqliteStatePath);
        _ = store.Reserve("poll-key", "poll-fingerprint");
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["goal-intake-status", "poll-key"],
            repository, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Xunit.Assert.Contains("\"requestKey\":\"poll-key\"", output);
        Xunit.Assert.Contains("\"state\":\"still-committing\"", output);
        Xunit.Assert.Empty((await repository.LoadAsync()).Goals);
    }

    [Xunit.Fact]
    public async Task GoalIntake_unkeyed_duplicate_objectives_remain_distinct()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        for (var index = 0; index < 2; index++)
        {
            _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["simple-goal", "Same unkeyed objective"],
                repository, workspace, ref agents, providers, ref profiles, ref currentGoal));
        }

        var stored = await repository.LoadAsync();
        Xunit.Assert.Equal(2, stored.Goals.Count);
        var refinementMessages = await repository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind);
        Xunit.Assert.Equal(
            stored.Goals.Select(goal => GoalRefinementWorkCoordinator.MessageId(goal.Id)).Order(),
            refinementMessages.Select(message => message.Id).Order());
    }

    [Xunit.Fact]
    public async Task GoalPlan_slice_batch_enqueues_refinement_for_every_dormant_goal()
    {
        const string plannerOutput = """
            ```json
            [{"id":"g1","objective":"Implement feature A.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureA/A.cs","dependsOn":[]},{"id":"g2","objective":"Implement feature B.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureB/B.cs","dependsOn":[]},{"id":"g3","objective":"Implement feature C.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureC/C.cs","dependsOn":[]}]
            ```
            """;
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var plannerAgent = new AgentDefinition(
            AgentId.New(),
            "Test-Planner",
            AgentRole.Planner,
            new ModelProfile("Fake", "fake-plan-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        var developerAgent = new AgentDefinition(
            AgentId.New(),
            "Test-Developer",
            AgentRole.Developer,
            new ModelProfile("Fake", "fake-dev-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        IReadOnlyList<AgentDefinition> agents = [plannerAgent, developerAgent];
        var providers = new InMemoryModelProviderRegistry([
            new FakeSmokeProvider(plannerOutput, providerName: "Fake")
        ]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["plan", "Implement three disjoint feature slices", "--slice-batch", "--confirm-plan"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var stored = await repository.LoadAsync();
        Xunit.Assert.Equal(4, stored.Goals.Count);
        Xunit.Assert.All(stored.Goals, goal =>
            Xunit.Assert.True(GoalRefinementWorkCoordinator.HasPendingWork(goal)));
        var refinementMessages = await repository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind);
        Xunit.Assert.Equal(
            stored.Goals.Select(goal => GoalRefinementWorkCoordinator.MessageId(goal.Id)).Order(),
            refinementMessages.Select(message => message.Id).Order());
    }

    [Xunit.Fact]
    public async Task GoalIntake_keyed_backlog_replay_keeps_atomic_goal_binding()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Keyed backlog source");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var args = new[]
        {
            "backlog-intake", item.Id, "--create-simple-goal", "--backlog-coverage", "slice",
            "--request-key", "backlog-key"
        };

        _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, providers, ref profiles, ref currentGoal));
        var replay = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, providers, ref profiles, ref currentGoal));

        var goal = Xunit.Assert.Single((await repository.LoadAsync()).Goals);
        var request = new GoalIntakeRequestStore(workspace.SqliteStatePath).Get("backlog-key")!;
        var backlogRecord = new BacklogIntakeRecordStore(workspace.SqliteStatePath).Get(item.Id)!;
        Xunit.Assert.Equal(goal.Id.Value, request.GoalId);
        Xunit.Assert.Equal(goal.Id.Value, backlogRecord.GoalId);
        Xunit.Assert.Contains("\"state\":\"created\"", replay);
    }

    [Xunit.Fact]
    public async Task GoalIntake_keyed_backlog_existing_goal_records_terminal_failure()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Already owned backlog source");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var unkeyedArgs = new[]
        {
            "backlog-intake", item.Id, "--create-simple-goal", "--backlog-coverage", "slice"
        };
        _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            unkeyedArgs, repository, workspace, ref agents, providers, ref profiles, ref currentGoal));
        var existingGoal = Xunit.Assert.Single((await repository.LoadAsync()).Goals);

        var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
            CliPersistentStateRunner.ExecuteCommand(
                [.. unkeyedArgs, "--request-key", "already-owned-backlog-key"],
                repository, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Xunit.Assert.Contains("GOAL_INTAKE_BACKLOG_ALREADY_HAS_GOAL", error.Message);
        Xunit.Assert.Contains(existingGoal.Id.Value, error.Message);
        var request = new GoalIntakeRequestStore(workspace.SqliteStatePath).Get("already-owned-backlog-key")!;
        Xunit.Assert.Equal(GoalIntakeRequestStates.Failed, request.State);
        Xunit.Assert.Equal("GOAL_INTAKE_BACKLOG_ALREADY_HAS_GOAL", request.FailureCode);
    }

    [Xunit.Fact]
    public async Task GoalIntake_keyed_backlog_in_progress_records_terminal_failure()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Busy backlog source");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        _ = new BacklogIntakeRecordStore(workspace.SqliteStatePath).Reserve(item.Id, "Busy backlog source");
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
            CliPersistentStateRunner.ExecuteCommand(
                [
                    "backlog-intake", item.Id, "--create-simple-goal", "--backlog-coverage", "slice",
                    "--request-key", "in-progress-backlog-key"
                ],
                repository, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Xunit.Assert.Contains("GOAL_INTAKE_BACKLOG_RESERVATION_NOT_ACQUIRED", error.Message);
        var request = new GoalIntakeRequestStore(workspace.SqliteStatePath).Get("in-progress-backlog-key")!;
        Xunit.Assert.Equal(GoalIntakeRequestStates.Failed, request.State);
        Xunit.Assert.Equal("GOAL_INTAKE_BACKLOG_RESERVATION_NOT_ACQUIRED", request.FailureCode);
        Xunit.Assert.Empty((await repository.LoadAsync()).Goals);
    }

    [Xunit.Fact]
    public void GoalIntake_failed_status_is_pollable_without_failed_exit()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var store = new GoalIntakeRequestStore(workspace.SqliteStatePath);
        _ = store.Reserve("failed-key", "failed-fingerprint");
        _ = store.MarkFailed("failed-key", "failed-fingerprint", "TEST_FAILURE", "controlled failure");
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => Xunit.Assert.False(CliPersistentStateRunner.ExecuteCommand(
            ["goal-intake-status", "failed-key"], repository, workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal)));

        Xunit.Assert.Contains("\"state\":\"failed\"", output);
        Xunit.Assert.Contains("\"failureCode\":\"TEST_FAILURE\"", output);
    }

    [Xunit.Fact]
    public async Task GoalIntake_dispatch_replay_stops_before_second_preflight()
    {
        using var sandbox = ClearWorkerSandboxEnv();
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: fake");
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
        IReadOnlyList<AgentDefinition> agents = [agent];
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var args = new[]
        {
            "simple-goal", "Dispatch exactly once", "--dispatch", "--confirm-dispatch-start",
            "--request-key", "dispatch-key"
        };
        var previous = Environment.GetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable);
        try
        {
            Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, "1");
            var first = Xunit.Assert.Throws<InvalidOperationException>(() =>
                CliPersistentStateRunner.ExecuteCommand(
                    args, repository, workspace, ref agents,
                    new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]),
                    ref profiles, ref currentGoal));
            Xunit.Assert.StartsWith("SPEC_REFINEMENT_PENDING", first.Message, StringComparison.Ordinal);

            var replay = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                args, repository, workspace, ref agents,
                new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]),
                ref profiles, ref currentGoal));
            Xunit.Assert.Contains("\"state\":\"created\"", replay);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, previous);
        }

        var goal = Xunit.Assert.Single((await repository.LoadAsync()).Goals);
        Xunit.Assert.Null(goal.Tasks.Single().LastDispatch);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_creation_returns_before_refinement_and_allows_concurrent_writer")]
    public async Task PersistentRunnerGoalCreationReturnsBeforeRefinementAndAllowsConcurrentWriter()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("blocking-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));

        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var concurrentRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var refiner = new BlockingGoalRefinerProvider();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([refiner]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        GoalId? launchedGoalId = null;
        GoalRefinementWorkCoordinator.LaunchOverride = (_, goalId) =>
        {
            launchedGoalId = goalId;
            return new GoalRefinementWorkLaunchResult(true, 42, "test-launch");
        };

        try
        {
            var createTask = Task.Run(() => CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["goal", "Implement deterministic unlocked goal refinement"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal)));
            await createTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            GoalRefinementWorkCoordinator.LaunchOverride = null;
        }

        Xunit.Assert.False(refiner.Entered.IsSet);
        var concurrentWriteElapsed = Stopwatch.StartNew();
        await concurrentRepository.TransactAsync(
            (kernel, _) =>
            {
                kernel.CreateGoal("Concurrent conductor-style state writer");
                return Task.FromResult((true, true));
            }).WaitAsync(TimeSpan.FromSeconds(15));
        concurrentWriteElapsed.Stop();
        Console.WriteLine(
            $"GOAL_CREATE_LOCK_MEASUREMENT phase=pending-refinement concurrentWriterElapsedMs={concurrentWriteElapsed.ElapsedMilliseconds}");

        var restored = await repository.LoadAsync();
        Xunit.Assert.Equal(2, restored.Goals.Count);
        Xunit.Assert.Contains(restored.Goals, goal => goal.Objective == "Concurrent conductor-style state writer");
        var created = Xunit.Assert.Single(restored.Goals, goal => goal.Objective == "Implement deterministic unlocked goal refinement");
        Xunit.Assert.Equal(created.Id, launchedGoalId);
        Xunit.Assert.Null(created.RefinedSpec);
        Xunit.Assert.Contains(created.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.StartsWith("spec_refinement outcome=pending", StringComparison.Ordinal));
        var pendingWork = Xunit.Assert.Single(
            await repository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind));
        Xunit.Assert.Equal(created.Id.Value, GoalRefinementWorkCoordinator.Deserialize(pendingWork).GoalId);
        Xunit.Assert.Equal(GoalStatus.Active, created.Status);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_create_rejects_competing_backlog_link_atomically")]
    public async Task PersistentRunnerGoalCreateRejectsCompetingBacklogLinkAtomically()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Single-consumer backlog source");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var providers = new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        GoalCreationSideEffectDelivery.BeforeStateCommit = _ =>
        {
            var competingKernel = repository.LoadAsync().GetAwaiter().GetResult();
            var competing = competingKernel.CreateGoal("Competing intake winner");
            competingKernel.SetGoalSourceBacklogItemLink(competing.Id, item.Id, SourceBacklogCoverage.Full);
            repository.SaveAsync(competingKernel).GetAwaiter().GetResult();
        };
        InvalidOperationException error;
        try
        {
            error = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
                CliPersistentStateRunner.ExecuteCommand(
                    ["goal", "Create one linked goal", "--backlog-item", item.Id, "--backlog-coverage", "full"],
                    repository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal)));
        }
        finally
        {
            GoalCreationSideEffectDelivery.BeforeStateCommit = null;
        }

        Xunit.Assert.Contains(
            "GOAL_CREATE_PRECONDITION_CHANGED reason=source-backlog-active-owner-full-coverage",
            error.Message);
        Xunit.Assert.Contains("coverage=full", error.Message);
        Xunit.Assert.Null(currentGoal);
        var restored = await repository.LoadAsync();
        var winner = Xunit.Assert.Single(restored.Goals);
        Xunit.Assert.Equal("Competing intake winner", winner.Objective);
        Xunit.Assert.Equal(item.Id, winner.SourceBacklogItemId);
        Xunit.Assert.Empty(await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory).ListAsync());
        Xunit.Assert.Empty(Directory.Exists(workspace.GoalLifecycleEventsDirectory)
            ? Directory.GetFiles(workspace.GoalLifecycleEventsDirectory, "*.jsonl")
            : []);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_create_flushes_deferred_side_effects_after_commit")]
    public async Task PersistentRunnerGoalCreateFlushesDeferredSideEffectsAfterCommit()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var repository = new InMemoryTransactionalStateRepository(new AgentOrchestratorKernel());
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["goal", "Create a goal whose ambiguous API contract needs an operator decision"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restored = await repository.LoadAsync();
        var created = Xunit.Assert.Single(restored.Goals);
        Xunit.Assert.Null(created.RefinedSpec);
        Xunit.Assert.Contains(created.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.StartsWith("spec_refinement outcome=pending", StringComparison.Ordinal));
        Xunit.Assert.Empty(await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            .ListAsync(created.Id.Value));
        Xunit.Assert.True(File.Exists(Path.Combine(
            workspace.GoalLifecycleEventsDirectory,
            $"{created.Id.Value}.jsonl")));
        Xunit.Assert.Contains(created.Id.Value[..8], output);
        var planIndex = output.IndexOf("Goal objective plan:", StringComparison.Ordinal);
        var advisoryIndex = output.IndexOf("Scope collision advisory:", StringComparison.Ordinal);
        var goalIndex = output.IndexOf($"Goal {created.Id.Value}", StringComparison.Ordinal);
        Xunit.Assert.True(planIndex >= 0 && advisoryIndex > planIndex && goalIndex > advisoryIndex, output);
        Xunit.Assert.Contains("\"historicalTimeEstimate\"", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("\"verdict\"", output, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task GoalCreateForcedFiveRolePersistsExactTaskOrder()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        const string objective = "Update src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs with one focused assertion.";

        var automaticPlan = GoalObjectivePlanner.Build(objective);
        Xunit.Assert.Equal(GoalIntakePipeline.FiveRole, automaticPlan.PipelineDecision.Pipeline);
        Xunit.Assert.False(automaticPlan.PipelineDecision.IsOverride);

        _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["goal", objective, "--pipeline", "five-role"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restored = await CreateMigratedStateRepository(workspace.SqliteStatePath).LoadAsync();
        var persistedGoal = Xunit.Assert.Single(restored.Goals);
        Xunit.Assert.Equal(
            [AgentRole.Researcher, AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
            persistedGoal.Tasks.Select(task => task.RequiredRole));
    }

    [Xunit.Fact]
    public async Task GoalReplaceCancelledZeroWorkCreatesFiveRoleSuccessor()
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, ".gitignore"),
            ".agents/" + Environment.NewLine + ".orchestrator/" + Environment.NewLine);
        RunGit(root, "add", ".gitignore");
        RunGit(root, "commit", "-m", "Ignore test-local orchestrator state");
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var backlogStore = new BacklogStore(workspace.BacklogStorePath);
        var item = await backlogStore.AddAsync("Correct a malformed zero-work goal");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initial = new AgentOrchestratorKernel();
        var predecessor = initial.CreateGoal("Malformed Developer plus Reviewer goal");
        initial.AddTask(predecessor.Id, AgentRole.Developer, "Implement the malformed intake.");
        initial.AddTask(predecessor.Id, AgentRole.Reviewer, "Review the malformed intake.");
        initial.SetGoalSourceBacklogItemLink(predecessor.Id, item.Id, SourceBacklogCoverage.Full);
        initial.CancelGoal(predecessor.Id, "Malformed task graph; no work started.");
        await repository.SaveAsync(initial);
        var briefPath = Path.Combine(root, "replacement-brief.md");
        var reasonPath = Path.Combine(root, "replacement-reason.md");
        await File.WriteAllTextAsync(briefPath, "Update src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs with one focused assertion.");
        await File.WriteAllTextAsync(reasonPath, "Correct the malformed zero-work intake.");
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var refiner = new ClarifyingGoalRefinerProvider();
        var providers = new InMemoryModelProviderRegistry([refiner]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var requestId = Guid.NewGuid();
        string[] replacementCommand =
        [
            "goal-replace", predecessor.Id.Value, "--brief-file", briefPath,
            "--reason-file", reasonPath, "--request-id", requestId.ToString(),
            "--disposition", "zero-work-correction", "--confirm-goal-replace",
            "--pipeline", "five-role"
        ];

        var error = Xunit.Record.Exception(() => CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            replacementCommand,
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal)));

        Xunit.Assert.Null(error);
        Xunit.Assert.Equal(0, refiner.InvocationCount);
        var restored = await repository.LoadAsync();
        Xunit.Assert.Equal(2, restored.Goals.Count);
        var successor = Xunit.Assert.Single(restored.Goals, goal => goal.Id != predecessor.Id);
        Xunit.Assert.Equal(item.Id, successor.SourceBacklogItemId);
        Xunit.Assert.Equal(
            [AgentRole.Researcher, AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
            successor.Tasks.Select(task => task.RequiredRole));
        Xunit.Assert.All(successor.Tasks, task => Xunit.Assert.Null(task.LastDispatch));
        Xunit.Assert.False(Directory.Exists(GoalWorktrees.WorktreePath(workspace.ExecutionDirectory, successor.Id)));

        await backlogStore.CloseAsync(item.Id);
        IReadOnlyList<AgentDefinition> replayAgents = Array.Empty<AgentDefinition>();
        var replayProfiles = new WorkerProfileCatalog([]);
        Goal? replayCurrentGoal = null;
        var replayOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            replacementCommand,
            repository,
            workspace,
            ref replayAgents,
            new InMemoryModelProviderRegistry([]),
            ref replayProfiles,
            ref replayCurrentGoal));
        Xunit.Assert.Contains("GOAL_REPLACE_REPLAYED", replayOutput, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Goal {successor.Id.Value[..8]}", replayOutput, StringComparison.Ordinal);
        Xunit.Assert.Equal(successor.Id, replayCurrentGoal!.Id);
        Xunit.Assert.Equal(0, refiner.InvocationCount);
        Xunit.Assert.Equal(2, (await repository.LoadAsync()).Goals.Count);
        await backlogStore.ReopenAsync(item.Id, "Continue the replacement landing fixture.");
        var audit = new SourceBacklogClaimStore(workspace.SqliteStatePath).FindAudit(requestId);
        Xunit.Assert.NotNull(audit);
        Xunit.Assert.Equal(GoalReplacementOutcome.Succeeded, audit!.Outcome);
        Xunit.Assert.Equal(predecessor.Id.Value, audit.PredecessorGoalId);
        Xunit.Assert.Equal(successor.Id.Value, audit.SuccessorGoalId);
        Xunit.Assert.Equal("Correct the malformed zero-work intake.", audit.Reason);
        Xunit.Assert.Equal(
            string.Join(',', successor.Tasks.Select(task => $"{task.RequiredRole}={task.AssignedAgentId}")),
            audit.AssignedAgents);

        var changedPipeline = replacementCommand.ToArray();
        changedPipeline[Array.IndexOf(changedPipeline, "five-role")] = "auto";
        Xunit.Assert.IsType<GoalReplacementIdempotencyConflictException>(Xunit.Assert.ThrowsAny<Exception>(() =>
            CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                changedPipeline,
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal))));

        var changedAgentOverride = replacementCommand
            .Concat(["--developer", "different-developer-agent"])
            .ToArray();
        Xunit.Assert.IsType<GoalReplacementIdempotencyConflictException>(Xunit.Assert.ThrowsAny<Exception>(() =>
            CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                changedAgentOverride,
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal))));
        Xunit.Assert.Equal(0, refiner.InvocationCount);

        await File.WriteAllTextAsync(reasonPath, "A different immutable operator reason.");
        var idempotencyConflict = Xunit.Assert.Throws<GoalReplacementIdempotencyConflictException>(() =>
            CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                replacementCommand,
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal)));
        Xunit.Assert.Contains(requestId.ToString("D"), idempotencyConflict.Message, StringComparison.Ordinal);
        await File.WriteAllTextAsync(reasonPath, "Correct the malformed zero-work intake.");
        var projection = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["backlog-show", item.Id],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        Xunit.Assert.Contains($"Owner:   {successor.Id.Value}", projection, StringComparison.Ordinal);
        Xunit.Assert.Contains($"- {predecessor.Id.Value[..8]} status=Cancelled authority=historical", projection, StringComparison.Ordinal);
        Xunit.Assert.Contains($"- {successor.Id.Value[..8]} status=Active authority=authoritative", projection, StringComparison.Ordinal);
        Xunit.Assert.Contains(
            $"predecessor={predecessor.Id.Value} successor={successor.Id.Value} request={requestId:D}",
            projection,
            StringComparison.Ordinal);
        var landedKernel = await repository.LoadAsync();
        var historicalPredecessor = landedKernel.Goals.Single(goal => goal.Id == predecessor.Id);
        var authoritativeSuccessor = landedKernel.Goals.Single(goal => goal.Id == successor.Id);
        Xunit.Assert.False(GoalLandingPostActions.AutoCloseSourceBacklogItem(
            historicalPredecessor,
            workspace.BacklogStorePath,
            kernel: landedKernel,
            stateDbPath: workspace.SqliteStatePath));
        Xunit.Assert.Equal(
            BacklogItemStatus.Open,
            (await new BacklogStore(workspace.BacklogStorePath).GetByExactIdAsync(item.Id))!.Status);
        foreach (var task in authoritativeSuccessor.Tasks)
        {
            landedKernel.RecordTaskVerification(
                authoritativeSuccessor.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed replacement landing fixture.", root, DateTimeOffset.UtcNow));
        }
        Xunit.Assert.Equal(GoalStatus.Verified, authoritativeSuccessor.Status);
        await repository.SaveAsync(landedKernel);
        var worktree = CommitGoalWork(
            root,
            authoritativeSuccessor.Id,
            "replacement-landing.txt",
            "replacement landed through acceptance");
        Xunit.Assert.Equal(worktree, GoalWorktrees.TryResolve(root, authoritativeSuccessor.Id));
        var busyRoot = new DotnetBuildStorageRoot(Path.Combine(workspace.OrchestratorDirectory, "replacement-acceptance-busy-pool"));
        var busyHolders = AcquireAllStableSlotExecutionLocks(busyRoot);
        string acceptanceOutput;
        try
        {
            Xunit.Assert.All(Enumerable.Range(0, DotnetBuildEnvironmentManager.BuildConcurrencySlotCount),
                index => Xunit.Assert.False(DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(index, busyRoot)));
            var verifier = new ProbeAcceptanceVerifier(() => { });
            var acceptanceContext = new CliExecutionContext(
                landedKernel,
                workspace,
                providers,
                agents,
                profiles,
                authoritativeSuccessor)
            {
                AcceptanceVerifier = verifier,
                CleanupContext = new WorktreeCleanupContext(
                    GoalWorktreeCleanupOptions.Default,
                    workspace.OrchestratorDirectory,
                    busyRoot),
                StableSlotSelector = (_, _) => AcceptanceStableSlotTestSupport.CreateFakeStableSlotLease(busyRoot.RootPath)
            };
            acceptanceOutput = CaptureConsole(() => CliCommandHandlers.Execute(
                ["acceptance", authoritativeSuccessor.Id.Value, "--no-record"],
                acceptanceContext));
            Xunit.Assert.Equal(1, verifier.RunCount);
        }
        finally
        {
            foreach (var holder in busyHolders)
                holder.Dispose();
        }

        Xunit.Assert.Contains("Fast-forwarded", acceptanceOutput, StringComparison.Ordinal);
        Xunit.Assert.True(File.Exists(Path.Combine(root, "replacement-landing.txt")));
        Xunit.Assert.Equal(
            BacklogItemStatus.Done,
            (await new BacklogStore(workspace.BacklogStorePath).GetByExactIdAsync(item.Id))!.Status);
    }

    [Xunit.Fact]
    public async Task GoalReplaceClosedBacklogPreflightRecordsDurableTypedReceipt()
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        var backlogStore = new BacklogStore(workspace.BacklogStorePath);
        var item = await backlogStore.AddAsync("Closed replacement source");
        await backlogStore.CloseAsync(item.Id);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initial = new AgentOrchestratorKernel();
        var predecessor = initial.CreateGoal("Cancelled predecessor for preflight audit");
        initial.SetGoalSourceBacklogItemLink(predecessor.Id, item.Id, SourceBacklogCoverage.Full);
        initial.CancelGoal(predecessor.Id, "No work started.");
        await repository.SaveAsync(initial);
        var briefPath = Path.Combine(root, "closed-preflight-brief.md");
        var reasonPath = Path.Combine(root, "closed-preflight-reason.md");
        await File.WriteAllTextAsync(briefPath, "Prepare a corrected successor.");
        await File.WriteAllTextAsync(reasonPath, "Reject closed source before planning.");
        var requestId = Guid.NewGuid();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var exception = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CaptureConsole(() =>
            CliPersistentStateRunner.ExecuteCommand(
                [
                    "goal-replace", predecessor.Id.Value, "--brief-file", briefPath,
                    "--reason-file", reasonPath, "--request-id", requestId.ToString(),
                    "--disposition", "zero-work-correction", "--confirm-goal-replace",
                    "--pipeline", "five-role"
                ],
                repository,
                workspace,
                ref agents,
                new InMemoryModelProviderRegistry([]),
                ref profiles,
                ref currentGoal)));

        Xunit.Assert.Contains("GOAL_REPLACE_VALIDATION_REJECTED", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("reason=source-backlog-not-open", exception.Message, StringComparison.Ordinal);
        var audit = new SourceBacklogClaimStore(workspace.SqliteStatePath).FindAudit(requestId);
        Xunit.Assert.NotNull(audit);
        Xunit.Assert.Equal(GoalReplacementOutcome.ValidationRejected, audit!.Outcome);
        Xunit.Assert.Equal("source-backlog-not-open", audit.FailureCode);
        Xunit.Assert.Equal("Reject closed source before planning.", audit.Reason);
        await File.WriteAllTextAsync(reasonPath, "Changed preflight reason.");
        var idempotencyConflict = Xunit.Assert.Throws<GoalReplacementIdempotencyConflictException>(() => CaptureConsole(() =>
            CliPersistentStateRunner.ExecuteCommand(
                [
                    "goal-replace", predecessor.Id.Value, "--brief-file", briefPath,
                    "--reason-file", reasonPath, "--request-id", requestId.ToString(),
                    "--disposition", "zero-work-correction", "--confirm-goal-replace",
                    "--pipeline", "five-role"
                ],
                repository,
                workspace,
                ref agents,
                new InMemoryModelProviderRegistry([]),
                ref profiles,
                ref currentGoal)));
        Xunit.Assert.Contains(requestId.ToString("D"), idempotencyConflict.Message, StringComparison.Ordinal);
        Xunit.Assert.Equal(predecessor.Id, Xunit.Assert.Single((await repository.LoadAsync()).Goals).Id);
    }

    [Xunit.Fact]
    public async Task GoalReplaceEligibilityEvidenceChangeRejectsTransfer()
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("callback-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Evidence token replacement source");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initial = new AgentOrchestratorKernel();
        var predecessor = initial.CreateGoal("Cancelled predecessor before evidence change");
        initial.SetGoalSourceBacklogItemLink(predecessor.Id, item.Id, SourceBacklogCoverage.Full);
        initial.CancelGoal(predecessor.Id, "No work started.");
        await repository.SaveAsync(initial);
        var briefPath = Path.Combine(root, "evidence-change-brief.md");
        var reasonPath = Path.Combine(root, "evidence-change-reason.md");
        await File.WriteAllTextAsync(briefPath, "Prepare a corrected successor after evidence capture.");
        await File.WriteAllTextAsync(reasonPath, "Reject stale eligibility evidence.");
        var requestId = Guid.NewGuid();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        GoalCreationSideEffectDelivery.BeforeStateCommit = _ =>
            RunGit(root, "branch", GoalWorktrees.BranchName(predecessor.Id));
        InvalidOperationException exception;
        try
        {
            exception = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CaptureConsole(() =>
                CliPersistentStateRunner.ExecuteCommand(
                    [
                        "goal-replace", predecessor.Id.Value, "--brief-file", briefPath,
                        "--reason-file", reasonPath, "--request-id", requestId.ToString(),
                        "--disposition", "zero-work-correction", "--confirm-goal-replace",
                        "--pipeline", "five-role"
                    ],
                    repository,
                    workspace,
                    ref agents,
                    new InMemoryModelProviderRegistry([]),
                    ref profiles,
                    ref currentGoal)));
        }
        finally
        {
            GoalCreationSideEffectDelivery.BeforeStateCommit = null;
        }

        Xunit.Assert.Contains("GOAL_REPLACE_INELIGIBLE_DISPOSITION", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("reason=ineligible-disposition", exception.Message, StringComparison.Ordinal);
        var restored = await repository.LoadAsync();
        Xunit.Assert.Equal(predecessor.Id, Xunit.Assert.Single(restored.Goals).Id);
        var claimStore = new SourceBacklogClaimStore(workspace.SqliteStatePath);
        Xunit.Assert.Equal(predecessor.Id.Value, claimStore.ResolveClaim(restored, item.Id)!.OwnerGoalId);
        Xunit.Assert.Empty(claimStore.ListLineage(item.Id));
        Xunit.Assert.Equal(GoalReplacementOutcome.IneligibleDisposition, claimStore.FindAudit(requestId)!.Outcome);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task GoalReplaceTransferAuthorityRequiresUnchangedFacts(bool changeEvidence)
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("transfer-authority-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Transfer authority replacement source");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initial = new AgentOrchestratorKernel();
        var predecessor = initial.CreateGoal("Cancelled predecessor before transfer authority check");
        initial.SetGoalSourceBacklogItemLink(predecessor.Id, item.Id, SourceBacklogCoverage.Full);
        initial.CancelGoal(predecessor.Id, "No work started.");
        await repository.SaveAsync(initial);
        var briefPath = Path.Combine(root, "transfer-authority-brief.md");
        var reasonPath = Path.Combine(root, "transfer-authority-reason.md");
        await File.WriteAllTextAsync(briefPath, "Prepare a corrected successor at the claim-transfer boundary.");
        await File.WriteAllTextAsync(reasonPath, "Bind current eligibility evidence to the transfer.");
        var requestId = Guid.NewGuid();
        var beforeOutbox = await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind);
        using var finalValidationReached = new ManualResetEventSlim(initialState: false);
        using var releaseFinalValidation = new ManualResetEventSlim(initialState: false);
        GoalReplacementEvidence.AfterFinalTransferValidation = _ =>
        {
            finalValidationReached.Set();
            if (!releaseFinalValidation.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Final transfer validation was not released by the test.");
        };

        Exception? ExecuteReplacement()
        {
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            try
            {
                CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                    [
                        "goal-replace", predecessor.Id.Value, "--brief-file", briefPath,
                        "--reason-file", reasonPath, "--request-id", requestId.ToString(),
                        "--disposition", "zero-work-correction", "--confirm-goal-replace",
                        "--pipeline", "five-role"
                    ],
                    repository,
                    workspace,
                    ref agents,
                    new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]),
                    ref profiles,
                    ref currentGoal));
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        Exception? exception;
        try
        {
            var replacement = Task.Run(ExecuteReplacement);
            Xunit.Assert.True(
                finalValidationReached.Wait(TimeSpan.FromSeconds(15)),
                "Replacement did not reach the final transfer authority boundary.");
            if (changeEvidence)
                RunGit(root, "branch", GoalWorktrees.BranchName(predecessor.Id));
            releaseFinalValidation.Set();
            exception = await replacement.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            releaseFinalValidation.Set();
            GoalReplacementEvidence.AfterFinalTransferValidation = null;
        }

        var restored = await repository.LoadAsync();
        var claimStore = new SourceBacklogClaimStore(workspace.SqliteStatePath);
        var claim = claimStore.ResolveClaim(restored, item.Id)!;
        if (changeEvidence)
        {
            Xunit.Assert.Contains("reason=eligibility-evidence-changed", Xunit.Assert.IsType<InvalidOperationException>(exception).Message, StringComparison.Ordinal);
            Xunit.Assert.Equal(predecessor.Id, Xunit.Assert.Single(restored.Goals).Id);
            Xunit.Assert.Equal(predecessor.Id.Value, claim.OwnerGoalId);
            Xunit.Assert.Empty(claimStore.ListLineage(item.Id));
            Xunit.Assert.Equal(beforeOutbox, await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));
            Xunit.Assert.Equal(GoalReplacementOutcome.ValidationRejected, claimStore.FindAudit(requestId)!.Outcome);
        }
        else
        {
            Xunit.Assert.Null(exception);
            Xunit.Assert.Equal(2, restored.Goals.Count);
            Xunit.Assert.NotEqual(predecessor.Id.Value, claim.OwnerGoalId);
            Xunit.Assert.Single(claimStore.ListLineage(item.Id));
            Xunit.Assert.Equal(GoalReplacementOutcome.Succeeded, claimStore.FindAudit(requestId)!.Outcome);
        }
    }

    [Xunit.Fact]
    public async Task GoalReplaceProtectedReplayPreservesTypedOutcome()
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var backlogStore = new BacklogStore(workspace.BacklogStorePath);
        var item = await backlogStore.AddAsync("Protected replacement source");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initial = new AgentOrchestratorKernel();
        var predecessor = initial.CreateGoal("Active protected predecessor");
        initial.SetGoalSourceBacklogItemLink(predecessor.Id, item.Id, SourceBacklogCoverage.Full);
        await repository.SaveAsync(initial);
        var briefPath = Path.Combine(root, "protected-replay-brief.md");
        var reasonPath = Path.Combine(root, "protected-replay-reason.md");
        await File.WriteAllTextAsync(briefPath, "Prepare a successor that must remain blocked.");
        await File.WriteAllTextAsync(reasonPath, "Confirm active owners stay protected.");
        var requestId = Guid.NewGuid();
        var command = new[]
        {
            "goal-replace", predecessor.Id.Value, "--brief-file", briefPath,
            "--reason-file", reasonPath, "--request-id", requestId.ToString(),
            "--disposition", "zero-work-correction", "--confirm-goal-replace",
            "--pipeline", "five-role"
        };
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var refiner = new ClarifyingGoalRefinerProvider();
        var providers = new InMemoryModelProviderRegistry([refiner]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        InvalidOperationException Replace() => Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CaptureConsole(() =>
            CliPersistentStateRunner.ExecuteCommand(
                command,
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal)));

        var first = Replace();
        Xunit.Assert.Equal(0, refiner.InvocationCount);
        await backlogStore.CloseAsync(item.Id);
        var replay = Replace();

        Xunit.Assert.Contains("GOAL_REPLACE_PROTECTED_OWNER", first.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("GOAL_REPLACE_PROTECTED_OWNER", replay.Message, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("PERSISTENCE_FAILED", replay.Message, StringComparison.Ordinal);
        Xunit.Assert.Equal(0, refiner.InvocationCount);
        await File.WriteAllTextAsync(reasonPath, "Changed protected-owner retry reason.");
        Xunit.Assert.IsType<GoalReplacementIdempotencyConflictException>(Replace());
        Xunit.Assert.Equal(0, refiner.InvocationCount);
        var audit = new SourceBacklogClaimStore(workspace.SqliteStatePath).FindAudit(requestId);
        Xunit.Assert.Equal(GoalReplacementOutcome.ProtectedOwner, audit!.Outcome);
        Xunit.Assert.NotEmpty(audit.ObjectiveHash);
        Xunit.Assert.Equal(
            "Researcher,Planner,Developer,Tester,Reviewer",
            audit.OrderedRoles);
        Xunit.Assert.Equal(predecessor.Id, Xunit.Assert.Single((await repository.LoadAsync()).Goals).Id);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task GoalReplaceTransactionFailureRollsBackSuccessorClaimAndLineage(
        bool claimObservationUnavailable)
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Rollback replacement source");
        var setupRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initial = new AgentOrchestratorKernel();
        var predecessor = initial.CreateGoal("Cancelled predecessor for rollback");
        initial.SetGoalSourceBacklogItemLink(predecessor.Id, item.Id, SourceBacklogCoverage.Full);
        initial.CancelGoal(predecessor.Id, "No work started.");
        await setupRepository.SaveAsync(initial);
        var repository = CreateMigratedStateRepository(
            workspace.SqliteStatePath,
            beforeOutboxCommit: () => throw new IOException("Injected replacement commit failure."));
        var briefPath = Path.Combine(root, "rollback-brief.md");
        var reasonPath = Path.Combine(root, "rollback-reason.md");
        await File.WriteAllTextAsync(briefPath, "Update src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs with one focused assertion.");
        await File.WriteAllTextAsync(reasonPath, "Prove atomic replacement rollback.");
        var requestId = Guid.NewGuid();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        if (claimObservationUnavailable)
        {
            CliPersistentStateRunner.BeforeGoalReplacementFailureClaimObservation = () =>
                throw new IOException("Injected claim observation failure.");
        }
        InvalidOperationException exception;
        try
        {
            exception = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
                CliPersistentStateRunner.ExecuteCommand(
                    [
                        "goal-replace", predecessor.Id.Value, "--brief-file", briefPath,
                        "--reason-file", reasonPath, "--request-id", requestId.ToString(),
                        "--disposition", "zero-work-correction", "--confirm-goal-replace",
                        "--pipeline", "five-role"
                    ],
                    repository,
                    workspace,
                    ref agents,
                    new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]),
                    ref profiles,
                    ref currentGoal)));
        }
        finally
        {
            CliPersistentStateRunner.BeforeGoalReplacementFailureClaimObservation = null;
        }

        Xunit.Assert.Contains("GOAL_REPLACE_PERSISTENCE_FAILED", exception.Message, StringComparison.Ordinal);
        var restored = await setupRepository.LoadAsync();
        Xunit.Assert.Equal(predecessor.Id, Xunit.Assert.Single(restored.Goals).Id);
        var claimStore = new SourceBacklogClaimStore(workspace.SqliteStatePath);
        var claim = claimStore.ResolveClaim(restored, item.Id);
        Xunit.Assert.Equal(predecessor.Id.Value, claim!.OwnerGoalId);
        Xunit.Assert.Empty(claimStore.ListLineage(item.Id));
        var audit = claimStore.FindAudit(requestId)!;
        Xunit.Assert.Equal(GoalReplacementOutcome.PersistenceFailed, audit.Outcome);
        if (claimObservationUnavailable)
        {
            Xunit.Assert.Null(audit.ObservedOwnerGoalId);
            Xunit.Assert.Null(audit.ObservedClaimVersion);
            Xunit.Assert.Equal("transaction-failed-claim-observation-unavailable", audit.FailureCode);
            Xunit.Assert.Contains("owner=unknown claimVersion=unknown", exception.Message, StringComparison.Ordinal);
        }
        else
        {
            Xunit.Assert.Equal(predecessor.Id.Value, audit.ObservedOwnerGoalId);
            Xunit.Assert.Equal(claim.Version, audit.ObservedClaimVersion);
            Xunit.Assert.Equal("transaction-failed", audit.FailureCode);
            Xunit.Assert.Contains($"owner={predecessor.Id.Value}", exception.Message, StringComparison.Ordinal);
            Xunit.Assert.Contains($"claimVersion={claim.Version}", exception.Message, StringComparison.Ordinal);
        }
    }

    [Xunit.Fact]
    public async Task GoalReplaceInvalidInputsRecordTypedAttempts()
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Invalid replacement input source");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initial = new AgentOrchestratorKernel();
        var predecessor = initial.CreateGoal("Cancelled predecessor for invalid input receipts");
        initial.SetGoalSourceBacklogItemLink(predecessor.Id, item.Id, SourceBacklogCoverage.Full);
        initial.CancelGoal(predecessor.Id, "No work started.");
        await repository.SaveAsync(initial);
        var briefPath = Path.Combine(root, "valid-brief.md");
        var reasonPath = Path.Combine(root, "valid-reason.md");
        await File.WriteAllTextAsync(briefPath, "Prepare a corrected successor.");
        await File.WriteAllTextAsync(reasonPath, "Correct the invalid predecessor.");
        var missingBriefPath = Path.Combine(root, "missing-brief.md");
        var missingReasonPath = Path.Combine(root, "missing-reason.md");

        var cases = new[]
        {
            (RequestId: Guid.NewGuid(), Disposition: "not-a-disposition", Brief: (string?)briefPath, Reason: (string?)reasonPath, FailureCode: "invalid-disposition"),
            (RequestId: Guid.NewGuid(), Disposition: "zero-work-correction", Brief: missingBriefPath, Reason: (string?)reasonPath, FailureCode: "brief-file-missing"),
            (RequestId: Guid.NewGuid(), Disposition: "zero-work-correction", Brief: (string?)briefPath, Reason: missingReasonPath, FailureCode: "reason-file-missing"),
            (RequestId: Guid.NewGuid(), Disposition: "zero-work-correction", Brief: null, Reason: (string?)reasonPath, FailureCode: "brief-file-missing"),
            (RequestId: Guid.NewGuid(), Disposition: "zero-work-correction", Brief: (string?)briefPath, Reason: null, FailureCode: "reason-file-missing")
        };

        foreach (var testCase in cases)
        {
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            List<string> command =
            [
                "goal-replace", predecessor.Id.Value,
                "--request-id", testCase.RequestId.ToString(),
                "--disposition", testCase.Disposition,
                "--confirm-goal-replace"
            ];
            if (testCase.Brief is not null)
                command.AddRange(["--brief-file", testCase.Brief]);
            if (testCase.Reason is not null)
                command.AddRange(["--reason-file", testCase.Reason]);
            Exception Execute() => Xunit.Assert.ThrowsAny<Exception>(() => CaptureConsole(() =>
                CliPersistentStateRunner.ExecuteCommand(
                    command,
                    repository,
                    workspace,
                    ref agents,
                    new InMemoryModelProviderRegistry([]),
                    ref profiles,
                    ref currentGoal)));

            _ = Execute();

            var audit = new SourceBacklogClaimStore(workspace.SqliteStatePath).FindAudit(testCase.RequestId);
            Xunit.Assert.NotNull(audit);
            Xunit.Assert.Equal(GoalReplacementOutcome.ValidationRejected, audit.Outcome);
            Xunit.Assert.Equal(testCase.FailureCode, audit.FailureCode);
            Xunit.Assert.Equal(predecessor.Id.Value, audit.ObservedOwnerGoalId);
            Xunit.Assert.Equal(1, audit.ObservedClaimVersion);
            var replay = Execute();
            Xunit.Assert.False(replay is GoalReplacementIdempotencyConflictException);
            Xunit.Assert.Contains("GOAL_REPLACE_VALIDATION_REJECTED", replay.Message, StringComparison.Ordinal);
        }
    }

    [Xunit.Fact]
    public async Task GoalReplaceSourceUnlinkedPredecessorRecordsTypedAttempt()
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initial = new AgentOrchestratorKernel();
        var predecessor = initial.CreateGoal("Source-unlinked predecessor");
        initial.CancelGoal(predecessor.Id, "No source association was recorded.");
        await repository.SaveAsync(initial);
        var briefPath = Path.Combine(root, "unlinked-brief.md");
        var reasonPath = Path.Combine(root, "unlinked-reason.md");
        await File.WriteAllTextAsync(briefPath, "Prepare a corrected successor.");
        await File.WriteAllTextAsync(reasonPath, "Record the rejected replacement attempt.");
        var requestId = Guid.NewGuid();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        Exception Execute() => Xunit.Assert.ThrowsAny<Exception>(() => CaptureConsole(() =>
            CliPersistentStateRunner.ExecuteCommand(
                [
                    "goal-replace", predecessor.Id.Value, "--brief-file", briefPath,
                    "--reason-file", reasonPath, "--request-id", requestId.ToString(),
                    "--disposition", "zero-work-correction", "--confirm-goal-replace",
                    "--pipeline", "five-role"
                ],
                repository,
                workspace,
                ref agents,
                new InMemoryModelProviderRegistry([]),
                ref profiles,
                ref currentGoal)));

        var first = Execute();
        var audit = new SourceBacklogClaimStore(workspace.SqliteStatePath).FindAudit(requestId);

        Xunit.Assert.Contains("reason=predecessor-has-no-source-backlog", first.Message, StringComparison.Ordinal);
        Xunit.Assert.NotNull(audit);
        Xunit.Assert.Equal(GoalReplacementOutcome.ValidationRejected, audit.Outcome);
        Xunit.Assert.Equal("predecessor-has-no-source-backlog", audit.FailureCode);
        Xunit.Assert.Equal($"unlinked:{predecessor.Id.Value}", audit.BacklogItemId);
        Xunit.Assert.Null(audit.ObservedOwnerGoalId);
        Xunit.Assert.Null(audit.ObservedClaimVersion);
        var replay = Execute();
        Xunit.Assert.Contains("GOAL_REPLACE_VALIDATION_REJECTED", replay.Message, StringComparison.Ordinal);
        Xunit.Assert.False(replay is GoalReplacementIdempotencyConflictException);
    }

    [Xunit.Fact]
    public async Task GoalReplaceCreationPreconditionChangeRecordsValidationRejected()
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("precondition-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var item = await backlog.AddAsync("Replacement dependency source");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initial = new AgentOrchestratorKernel();
        var dependency = initial.CreateGoal("Replacement dependency");
        var predecessor = initial.CreateGoal("Cancelled predecessor with dependency");
        initial.SetGoalDependency(predecessor.Id, dependency.Id);
        initial.SetGoalSourceBacklogItemLink(predecessor.Id, item.Id, SourceBacklogCoverage.Full);
        initial.CancelGoal(predecessor.Id, "No work started.");
        _ = await backlog.AddDependencyAsync(
            item.Id,
            new BacklogDependencyTarget(dependency.Id.Value, BacklogDependencyTargetKind.Goal),
            goalExists: id => id == dependency.Id.Value);
        await repository.SaveAsync(initial);
        var briefPath = Path.Combine(root, "precondition-brief.md");
        var reasonPath = Path.Combine(root, "precondition-reason.md");
        await File.WriteAllTextAsync(briefPath, "Prepare a corrected successor.");
        await File.WriteAllTextAsync(reasonPath, "Reject stale deterministic inputs.");
        var requestId = Guid.NewGuid();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        GoalCreationSideEffectDelivery.BeforeStateCommit = _ =>
            backlog.RemoveDependencyAsync(item.Id, dependency.Id.Value).GetAwaiter().GetResult();

        Exception exception;
        try
        {
            exception = Xunit.Assert.ThrowsAny<Exception>(() => CaptureConsole(() =>
                CliPersistentStateRunner.ExecuteCommand(
                    [
                        "goal-replace", predecessor.Id.Value, "--brief-file", briefPath,
                        "--reason-file", reasonPath, "--request-id", requestId.ToString(),
                        "--disposition", "zero-work-correction", "--confirm-goal-replace",
                        "--pipeline", "five-role"
                    ],
                    repository,
                    workspace,
                    ref agents,
                    new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]),
                    ref profiles,
                    ref currentGoal)));
        }
        finally
        {
            GoalCreationSideEffectDelivery.BeforeStateCommit = null;
        }

        Xunit.Assert.Contains("GOAL_REPLACE_VALIDATION_REJECTED", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("reason=source-backlog-dependencies-changed", exception.Message, StringComparison.Ordinal);
        var audit = new SourceBacklogClaimStore(workspace.SqliteStatePath).FindAudit(requestId);
        Xunit.Assert.NotNull(audit);
        Xunit.Assert.Equal(GoalReplacementOutcome.ValidationRejected, audit.Outcome);
        Xunit.Assert.Equal("source-backlog-dependencies-changed", audit.FailureCode);
        Xunit.Assert.Equal(2, (await repository.LoadAsync()).Goals.Count);
    }

    [Xunit.Fact]
    public void GoalReplaceUnavailableClaimObservationDoesNotInventExpectedFacts()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        _ = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var repository = new InMemoryTransactionalStateRepository(new AgentOrchestratorKernel())
        {
            ThrowOnLoadAsync = true
        };

        var observed = CliPersistentStateRunner.ResolveObservedClaimAfterReplacementFailure(
            new SourceBacklogClaimStore(workspace.SqliteStatePath),
            repository,
            "unobservable-backlog-item");

        Xunit.Assert.Null(observed);
    }

    [Xunit.Fact]
    public async Task GoalReplace_ChangedClaimBehindEvidenceLease_ReturnsConflict()
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("changed-owner-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Changed owner behind evidence lease");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initial = new AgentOrchestratorKernel();
        var predecessor = initial.CreateGoal("Cancelled predecessor before claim change");
        initial.SetGoalSourceBacklogItemLink(predecessor.Id, item.Id, SourceBacklogCoverage.Full);
        initial.CancelGoal(predecessor.Id, "No work started.");
        await repository.SaveAsync(initial);
        var firstBrief = Path.Combine(root, "changed-owner-first.md");
        var secondBrief = Path.Combine(root, "changed-owner-second.md");
        var reasonPath = Path.Combine(root, "changed-owner-reason.md");
        await File.WriteAllTextAsync(firstBrief, "Prepare the first corrected successor.");
        await File.WriteAllTextAsync(secondBrief, "Prepare the successor that wins ownership.");
        await File.WriteAllTextAsync(reasonPath, "Replace the zero-work predecessor.");
        var firstRequest = Guid.NewGuid();
        var secondRequest = Guid.NewGuid();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        IDisposable? evidenceLease = null;

        GoalCreationSideEffectDelivery.BeforeStateCommit = _ =>
        {
            GoalCreationSideEffectDelivery.BeforeStateCommit = null;
            IReadOnlyList<AgentDefinition> competingAgents = AgentCatalog.Default().Agents;
            var competingProfiles = WorkerProfileCatalog.Default();
            Goal? competingGoal = null;
            CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                [
                    "goal-replace", predecessor.Id.Value, "--brief-file", secondBrief,
                    "--reason-file", reasonPath, "--request-id", secondRequest.ToString(),
                    "--disposition", "zero-work-correction", "--confirm-goal-replace",
                    "--pipeline", "five-role"
                ],
                CreateMigratedStateRepository(workspace.SqliteStatePath),
                workspace,
                ref competingAgents,
                new InMemoryModelProviderRegistry([new DeterministicGoalRefinerProvider()]),
                ref competingProfiles,
                ref competingGoal));
            evidenceLease = new ReconcileSweepRemediationStore(workspace.SqliteStatePath)
                .TryAcquireAcceptanceLease(
                    predecessor.Id.Value,
                    $"goal-evidence:test:{Guid.NewGuid():N}",
                    TimeSpan.FromMinutes(30));
            Xunit.Assert.NotNull(evidenceLease);
        };

        SourceBacklogClaimConflictException conflict;
        try
        {
            conflict = Xunit.Assert.Throws<SourceBacklogClaimConflictException>(() => CaptureConsole(() =>
                CliPersistentStateRunner.ExecuteCommand(
                    [
                        "goal-replace", predecessor.Id.Value, "--brief-file", firstBrief,
                        "--reason-file", reasonPath, "--request-id", firstRequest.ToString(),
                        "--disposition", "zero-work-correction", "--confirm-goal-replace",
                        "--pipeline", "five-role"
                    ],
                    repository,
                    workspace,
                    ref agents,
                    new InMemoryModelProviderRegistry([new DeterministicGoalRefinerProvider()]),
                    ref profiles,
                    ref currentGoal)));
        }
        finally
        {
            GoalCreationSideEffectDelivery.BeforeStateCommit = null;
            evidenceLease?.Dispose();
        }

        var restored = await repository.LoadAsync();
        var claimStore = new SourceBacklogClaimStore(workspace.SqliteStatePath);
        var claim = claimStore.ResolveClaim(restored, item.Id)!;
        Xunit.Assert.Equal(claim.OwnerGoalId, conflict.ObservedOwnerGoalId);
        Xunit.Assert.Equal(claim.Version, conflict.ObservedVersion);
        Xunit.Assert.NotEqual(predecessor.Id.Value, claim.OwnerGoalId);
        var audit = claimStore.FindAudit(firstRequest)!;
        Xunit.Assert.Equal(GoalReplacementOutcome.CurrentOwnerConflict, audit.Outcome);
        Xunit.Assert.Equal(claim.OwnerGoalId, audit.ObservedOwnerGoalId);
        Xunit.Assert.Equal(claim.Version, audit.ObservedClaimVersion);
        Xunit.Assert.Equal(GoalReplacementOutcome.Succeeded, claimStore.FindAudit(secondRequest)!.Outcome);
    }

    [Xunit.Fact(DisplayName = "goal-replace reports the observed claim after a competing lease timeout")]
    public void GoalReplace_CompetingLeaseTimeout_ReportsObservedClaim()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        _ = CreateMigratedStateRepository(workspace.SqliteStatePath);
        const string predecessorGoalId = "timeout-predecessor";
        using var competingLease = new ReconcileSweepRemediationStore(workspace.SqliteStatePath)
            .TryAcquireAcceptanceLease(
                predecessorGoalId,
                $"goal-replace:test:{Guid.NewGuid():N}",
                TimeSpan.FromMinutes(30));
        Xunit.Assert.NotNull(competingLease);
        var expectedClaim = new SourceBacklogClaimSnapshot(
            "timeout-backlog",
            predecessorGoalId,
            SourceBacklogCoverage.Full,
            Version: 7,
            DateTimeOffset.Parse("2026-08-12T00:00:00Z"));
        var timeProvider = new ManualConductorTimeProviderForTests(
            DateTimeOffset.Parse("2026-08-12T00:00:00Z"));
        var waitCount = 0;

        var acquisition = CliPersistentStateRunner.AcquireGoalReplacementTransferLease(
            workspace.SqliteStatePath,
            predecessorGoalId,
            () => expectedClaim,
            timeProvider,
            _ =>
            {
                waitCount++;
                timeProvider.AdvanceForTests(TimeSpan.FromSeconds(16));
            });

        Xunit.Assert.Equal(
            CliPersistentStateRunner.GoalReplacementTransferLeaseAcquisitionKind.CompetingReplacementTimedOut,
            acquisition.Kind);
        Xunit.Assert.Same(expectedClaim, acquisition.ObservedClaim);
        Xunit.Assert.Equal(1, waitCount);
    }

    [Xunit.Fact(DisplayName = "goal-replace leaves an unchanged-claim lease timeout retryable")]
    public void GoalReplace_CompetingLeaseTimeout_WithUnchangedClaim_IsRetryable()
    {
        var requestId = Guid.NewGuid();
        const string predecessorGoalId = "timeout-predecessor";
        var expectedClaim = new SourceBacklogClaimSnapshot(
            "timeout-backlog",
            predecessorGoalId,
            SourceBacklogCoverage.Full,
            Version: 7,
            DateTimeOffset.Parse("2026-08-12T00:00:00Z"));
        var command = new GoalReplacementCommand(
            new GoalId(predecessorGoalId),
            requestId,
            GoalReplacementDisposition.ZeroWorkCorrection,
            "Retry after the competing replacement releases its lease.",
            "Prepare a corrected successor.",
            expectedClaim.OwnerGoalId,
            expectedClaim.Version,
            "five-role",
            string.Empty);
        var acquisition = new CliPersistentStateRunner.GoalReplacementTransferLeaseAcquisition(
            CliPersistentStateRunner.GoalReplacementTransferLeaseAcquisitionKind.CompetingReplacementTimedOut,
            ObservedClaim: expectedClaim);
        var recordedOutcomes = new List<GoalReplacementOutcome>();

        var exception = Xunit.Assert.Throws<GoalReplacementRetryableTimeoutException>(() =>
            CliPersistentStateRunner.ThrowGoalReplacementTransferLeaseTimeout(
                command,
                acquisition,
                (outcome, _, _, _) => recordedOutcomes.Add(outcome)));

        Xunit.Assert.Contains("GOAL_REPLACE_RETRYABLE_TIMEOUT", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains($"requestId={requestId:D}", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains($"owner={expectedClaim.OwnerGoalId}", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains($"claimVersion={expectedClaim.Version}", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Empty(recordedOutcomes);
    }

    [Xunit.Fact]
    public async Task ConcurrentGoalReplacementsCommitExactlyOneSuccessor()
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("deterministic-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Concurrent replacement source");
        var setupRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initial = new AgentOrchestratorKernel();
        var predecessor = initial.CreateGoal("Cancelled predecessor for concurrent replacement");
        initial.SetGoalSourceBacklogItemLink(predecessor.Id, item.Id, SourceBacklogCoverage.Full);
        initial.CancelGoal(predecessor.Id, "No work started.");
        await setupRepository.SaveAsync(initial);
        var firstBrief = Path.Combine(root, "first-replacement.md");
        var secondBrief = Path.Combine(root, "second-replacement.md");
        var reasonPath = Path.Combine(root, "replacement-reason.md");
        await File.WriteAllTextAsync(firstBrief, "Update src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs with first focused assertion.");
        await File.WriteAllTextAsync(secondBrief, "Update src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs with second focused assertion.");
        await File.WriteAllTextAsync(reasonPath, "Competing corrected replacement.");
        var firstRequest = Guid.NewGuid();
        var secondRequest = Guid.NewGuid();
        using var bothPrepared = new CountdownEvent(2);
        using var releaseCommit = new ManualResetEventSlim(initialState: false);
        using var winnerCommitted = new ManualResetEventSlim(initialState: false);
        var timeProvider = new ManualConductorTimeProviderForTests(
            DateTimeOffset.Parse("2026-09-18T00:00:00Z"));
        using var transferLeaseClock = GoalReplacementTransferLeaseClock.Push(
            timeProvider,
            _ =>
            {
                Xunit.Assert.True(
                    winnerCommitted.Wait(TimeSpan.FromMinutes(1)),
                    "A replacement winner did not commit before the loser lease deadline advanced.");
                timeProvider.AdvanceForTests(TimeSpan.FromSeconds(16));
            });
        GoalCreationSideEffectDelivery.BeforeStateCommit = _ =>
        {
            bothPrepared.Signal();
            Xunit.Assert.True(
                releaseCommit.Wait(TimeSpan.FromMinutes(1)),
                "Concurrent replacement commit gate was not released.");
        };

        Exception? Replace(ITransactionalOrchestratorStateRepository repository, string brief, Guid requestId)
        {
            IReadOnlyList<AgentDefinition> localAgents = AgentCatalog.Default().Agents;
            var localProfiles = WorkerProfileCatalog.Default();
            Goal? localGoal = null;
            try
            {
                CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                    [
                        "goal-replace", predecessor.Id.Value, "--brief-file", brief,
                        "--reason-file", reasonPath, "--request-id", requestId.ToString(),
                        "--disposition", "zero-work-correction", "--confirm-goal-replace",
                        "--pipeline", "five-role"
                    ],
                    repository,
                    workspace,
                    ref localAgents,
                    new InMemoryModelProviderRegistry([new DeterministicGoalRefinerProvider()]),
                    ref localProfiles,
                    ref localGoal));
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        Exception? ReplaceAndSignalWinner(
            ITransactionalOrchestratorStateRepository repository,
            string brief,
            Guid requestId)
        {
            var outcome = Replace(repository, brief, requestId);
            if (outcome is null)
            {
                winnerCommitted.Set();
            }

            return outcome;
        }

        Exception?[] outcomes;
        try
        {
            var first = Task.Run(() => ReplaceAndSignalWinner(CreateMigratedStateRepository(workspace.SqliteStatePath), firstBrief, firstRequest));
            var second = Task.Run(() => ReplaceAndSignalWinner(CreateMigratedStateRepository(workspace.SqliteStatePath), secondBrief, secondRequest));
            if (!bothPrepared.Wait(TimeSpan.FromMinutes(1)))
            {
                releaseCommit.Set();
                Exception?[] preparationOutcomes;
                try
                {
                    preparationOutcomes = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromMinutes(1));
                }
                catch (TimeoutException)
                {
                    Xunit.Assert.Fail(
                        $"Both replacements did not complete read-only preparation; " +
                        $"remaining={bothPrepared.CurrentCount} first={first.Status} second={second.Status}.");
                    throw;
                }

                var diagnostics = string.Join(
                    " | ",
                    preparationOutcomes.Select((outcome, index) =>
                        outcome is null
                            ? $"lane{index + 1}=completed"
                            : $"lane{index + 1}={outcome.GetType().Name}: {outcome.Message}"));
                Xunit.Assert.Fail(
                    $"Both replacements did not complete read-only preparation; " +
                    $"remaining={bothPrepared.CurrentCount}; {diagnostics}.");
            }
            releaseCommit.Set();
            outcomes = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromMinutes(1));
        }
        finally
        {
            releaseCommit.Set();
            GoalCreationSideEffectDelivery.BeforeStateCommit = null;
        }
        Xunit.Assert.Single(outcomes, outcome => outcome is null);
        Xunit.Assert.Contains(
            "GOAL_REPLACE_CURRENT_OWNER_CONFLICT",
            Xunit.Assert.Single(outcomes, outcome => outcome is not null)!.Message,
            StringComparison.Ordinal);
        var restored = await setupRepository.LoadAsync();
        Xunit.Assert.Equal(2, restored.Goals.Count);
        var claimStore = new SourceBacklogClaimStore(workspace.SqliteStatePath);
        Xunit.Assert.Single(claimStore.ListLineage(item.Id));
        var audits = new[] { claimStore.FindAudit(firstRequest), claimStore.FindAudit(secondRequest) };
        Xunit.Assert.Single(audits, audit => audit?.Outcome == GoalReplacementOutcome.Succeeded);
        Xunit.Assert.Single(audits, audit => audit?.Outcome == GoalReplacementOutcome.CurrentOwnerConflict);
    }

    [Xunit.Fact]
    public async Task GoalReplace_CompetingReplacementTimeout_IsRetryableWithoutAudit_ThenConflictsAfterWinnerCommits()
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("deterministic-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Retryable concurrent replacement source");
        var setupRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initial = new AgentOrchestratorKernel();
        var predecessor = initial.CreateGoal("Cancelled predecessor for retryable replacement");
        initial.SetGoalSourceBacklogItemLink(predecessor.Id, item.Id, SourceBacklogCoverage.Full);
        initial.CancelGoal(predecessor.Id, "No work started.");
        await setupRepository.SaveAsync(initial);
        var loserBrief = Path.Combine(root, "loser-replacement.md");
        var winnerBrief = Path.Combine(root, "winner-replacement.md");
        var reasonPath = Path.Combine(root, "replacement-reason.md");
        await File.WriteAllTextAsync(loserBrief, "Update src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs with the retrying assertion.");
        await File.WriteAllTextAsync(winnerBrief, "Update src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs with the winning assertion.");
        await File.WriteAllTextAsync(reasonPath, "Competing corrected replacement.");
        var loserRequest = Guid.NewGuid();
        var winnerRequest = Guid.NewGuid();

        void Replace(string brief, Guid requestId)
        {
            IReadOnlyList<AgentDefinition> localAgents = AgentCatalog.Default().Agents;
            var localProfiles = WorkerProfileCatalog.Default();
            Goal? localGoal = null;
            CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                [
                    "goal-replace", predecessor.Id.Value, "--brief-file", brief,
                    "--reason-file", reasonPath, "--request-id", requestId.ToString(),
                    "--disposition", "zero-work-correction", "--confirm-goal-replace",
                    "--pipeline", "five-role"
                ],
                CreateMigratedStateRepository(workspace.SqliteStatePath),
                workspace,
                ref localAgents,
                new InMemoryModelProviderRegistry([new DeterministicGoalRefinerProvider()]),
                ref localProfiles,
                ref localGoal));
        }

        IDisposable? competingLease = new ReconcileSweepRemediationStore(workspace.SqliteStatePath)
            .TryAcquireAcceptanceLease(
                predecessor.Id.Value,
                $"goal-replace:test:{Guid.NewGuid():N}",
                TimeSpan.FromMinutes(30));
        Xunit.Assert.NotNull(competingLease);
        var timeProvider = new ManualConductorTimeProviderForTests(
            DateTimeOffset.Parse("2026-09-18T00:00:00Z"));
        var waitCount = 0;
        GoalReplacementRetryableTimeoutException timeout;
        try
        {
            using var transferLeaseClock = GoalReplacementTransferLeaseClock.Push(
                timeProvider,
                _ =>
                {
                    waitCount++;
                    timeProvider.AdvanceForTests(TimeSpan.FromSeconds(16));
                });
            timeout = Xunit.Assert.Throws<GoalReplacementRetryableTimeoutException>(
                () => Replace(loserBrief, loserRequest));
        }
        finally
        {
            competingLease.Dispose();
        }

        Xunit.Assert.Contains("GOAL_REPLACE_RETRYABLE_TIMEOUT", timeout.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains($"requestId={loserRequest:D}", timeout.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("reason=competing-replacement-timeout", timeout.Message, StringComparison.Ordinal);
        Xunit.Assert.Equal(1, waitCount);
        var claimStore = new SourceBacklogClaimStore(workspace.SqliteStatePath);
        Xunit.Assert.Null(claimStore.FindAudit(loserRequest));

        Replace(winnerBrief, winnerRequest);
        Xunit.Assert.Equal(GoalReplacementOutcome.Succeeded, claimStore.FindAudit(winnerRequest)!.Outcome);
        var conflict = Xunit.Assert.Throws<SourceBacklogClaimConflictException>(
            () => Replace(loserBrief, loserRequest));
        Xunit.Assert.Contains("GOAL_REPLACE_CURRENT_OWNER_CONFLICT", conflict.Message, StringComparison.Ordinal);
        Xunit.Assert.Equal(GoalReplacementOutcome.CurrentOwnerConflict, claimStore.FindAudit(loserRequest)!.Outcome);
        var restored = await setupRepository.LoadAsync();
        Xunit.Assert.Equal(2, restored.Goals.Count);
        Xunit.Assert.Single(claimStore.ListLineage(item.Id));
    }

    [Xunit.Fact]
    public async Task GoalReplace_LeaseReleasedBeforeOwnerObservation_RetriesAcquisition()
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("lease-release-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Lease release replacement source");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initial = new AgentOrchestratorKernel();
        var predecessor = initial.CreateGoal("Cancelled predecessor behind released lease");
        initial.SetGoalSourceBacklogItemLink(predecessor.Id, item.Id, SourceBacklogCoverage.Full);
        initial.CancelGoal(predecessor.Id, "No work started.");
        await repository.SaveAsync(initial);
        var briefPath = Path.Combine(root, "lease-release-brief.md");
        var reasonPath = Path.Combine(root, "lease-release-reason.md");
        await File.WriteAllTextAsync(briefPath, "Prepare the successor after a competing replacement releases its lease.");
        await File.WriteAllTextAsync(reasonPath, "Retry the claim transfer after observing lease release.");
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        IDisposable? competingLease = new ReconcileSweepRemediationStore(workspace.SqliteStatePath)
            .TryAcquireAcceptanceLease(
                predecessor.Id.Value,
                $"goal-replace:{Environment.ProcessId}:competing",
                TimeSpan.FromMinutes(30));
        Xunit.Assert.NotNull(competingLease);
        using var failedAcquisitionObserved = new ManualResetEventSlim(initialState: false);
        CliPersistentStateRunner.AfterGoalReplacementLeaseAcquisitionFailed = () =>
        {
            failedAcquisitionObserved.Set();
            Interlocked.Exchange(ref competingLease, null)?.Dispose();
        };

        try
        {
            var exception = Xunit.Record.Exception(() => CaptureConsole(() =>
                CliPersistentStateRunner.ExecuteCommand(
                    [
                        "goal-replace", predecessor.Id.Value, "--brief-file", briefPath,
                        "--reason-file", reasonPath, "--request-id", Guid.NewGuid().ToString(),
                        "--disposition", "zero-work-correction", "--confirm-goal-replace",
                        "--pipeline", "five-role"
                    ],
                    repository,
                    workspace,
                    ref agents,
                    new InMemoryModelProviderRegistry([new DeterministicGoalRefinerProvider()]),
                    ref profiles,
                    ref currentGoal)));

            Xunit.Assert.Null(exception);
            Xunit.Assert.True(failedAcquisitionObserved.IsSet);
            var restored = await repository.LoadAsync();
            var successor = Xunit.Assert.Single(restored.Goals, goal => goal.Id != predecessor.Id);
            Xunit.Assert.Equal(
                successor.Id.Value,
                new SourceBacklogClaimStore(workspace.SqliteStatePath).ResolveClaim(restored, item.Id)!.OwnerGoalId);
        }
        finally
        {
            CliPersistentStateRunner.AfterGoalReplacementLeaseAcquisitionFailed = null;
            competingLease?.Dispose();
        }
    }

    [Xunit.Fact]
    public async Task GoalReplace_BacklogClosesDuringCommit_RecordsValidationRejected()
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("callback-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var backlogStore = new BacklogStore(workspace.BacklogStorePath);
        var item = await backlogStore.AddAsync("Replacement validation source");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initial = new AgentOrchestratorKernel();
        var predecessor = initial.CreateGoal("Cancelled predecessor for validation");
        initial.SetGoalSourceBacklogItemLink(predecessor.Id, item.Id, SourceBacklogCoverage.Full);
        initial.CancelGoal(predecessor.Id, "No work started.");
        await repository.SaveAsync(initial);
        var briefPath = Path.Combine(root, "validation-brief.md");
        var reasonPath = Path.Combine(root, "validation-reason.md");
        await File.WriteAllTextAsync(briefPath, "Update one focused CLI assertion.");
        await File.WriteAllTextAsync(reasonPath, "Reject a source that closes after preparation.");
        var requestId = Guid.NewGuid();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        GoalCreationSideEffectDelivery.BeforeStateCommit = _ =>
            backlogStore.CloseAsync(item.Id).GetAwaiter().GetResult();
        InvalidOperationException exception;
        try
        {
            exception = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CaptureConsole(() =>
                CliPersistentStateRunner.ExecuteCommand(
                    [
                        "goal-replace", predecessor.Id.Value, "--brief-file", briefPath,
                        "--reason-file", reasonPath, "--request-id", requestId.ToString(),
                        "--disposition", "zero-work-correction", "--confirm-goal-replace",
                        "--pipeline", "five-role"
                    ],
                    repository,
                    workspace,
                    ref agents,
                    new InMemoryModelProviderRegistry([]),
                    ref profiles,
                    ref currentGoal)));
        }
        finally
        {
            GoalCreationSideEffectDelivery.BeforeStateCommit = null;
        }

        Xunit.Assert.Contains("GOAL_REPLACE_VALIDATION_REJECTED", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("reason=source-backlog-not-open", exception.Message, StringComparison.Ordinal);
        var restored = await repository.LoadAsync();
        Xunit.Assert.Equal(predecessor.Id, Xunit.Assert.Single(restored.Goals).Id);
        var audit = new SourceBacklogClaimStore(workspace.SqliteStatePath).FindAudit(requestId);
        Xunit.Assert.Equal(GoalReplacementOutcome.ValidationRejected, audit!.Outcome);
        Xunit.Assert.Equal("source-backlog-not-open", audit.FailureCode);
    }

    [Xunit.Fact]
    public async Task GoalReplace_LegacyOwnerAmbiguous_RecordsTypedOutcome()
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("callback-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Ambiguous replacement source");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initial = new AgentOrchestratorKernel();
        var predecessor = initial.CreateGoal("Cancelled predecessor for ambiguity");
        initial.SetGoalSourceBacklogItemLink(predecessor.Id, item.Id, SourceBacklogCoverage.Full);
        initial.CancelGoal(predecessor.Id, "No work started.");
        await repository.SaveAsync(initial);
        var briefPath = Path.Combine(root, "ambiguity-brief.md");
        var reasonPath = Path.Combine(root, "ambiguity-reason.md");
        await File.WriteAllTextAsync(briefPath, "Update one focused CLI assertion.");
        await File.WriteAllTextAsync(reasonPath, "Reject ambiguous legacy ownership.");
        var requestId = Guid.NewGuid();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        GoalCreationSideEffectDelivery.BeforeStateCommit = _ =>
        {
            var competingKernel = repository.LoadAsync().GetAwaiter().GetResult();
            var competing = competingKernel.CreateGoal("Competing historical association");
            competingKernel.SetGoalSourceBacklogItemLink(competing.Id, item.Id, SourceBacklogCoverage.Full);
            competingKernel.CancelGoal(competing.Id, "Legacy duplicate association.");
            repository.SaveAsync(competingKernel).GetAwaiter().GetResult();
        };
        InvalidOperationException exception;
        try
        {
            exception = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
                CliPersistentStateRunner.ExecuteCommand(
                    [
                        "goal-replace", predecessor.Id.Value, "--brief-file", briefPath,
                        "--reason-file", reasonPath, "--request-id", requestId.ToString(),
                        "--disposition", "zero-work-correction", "--confirm-goal-replace",
                        "--pipeline", "five-role"
                    ],
                    repository,
                    workspace,
                    ref agents,
                    new InMemoryModelProviderRegistry([]),
                    ref profiles,
                    ref currentGoal)));
        }
        finally
        {
            GoalCreationSideEffectDelivery.BeforeStateCommit = null;
        }

        Xunit.Assert.Contains("GOAL_REPLACE_LEGACY_OWNER_AMBIGUOUS", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("reason=legacy-owner-ambiguous", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Equal(2, (await repository.LoadAsync()).Goals.Count);
        var audit = new SourceBacklogClaimStore(workspace.SqliteStatePath).FindAudit(requestId);
        Xunit.Assert.Equal(GoalReplacementOutcome.LegacyOwnerAmbiguous, audit!.Outcome);
        Xunit.Assert.Equal("legacy-owner-ambiguous", audit.FailureCode);
    }

    [Xunit.Fact]
    public async Task GoalCreateUnsatisfiedPipelineLeavesStateAndOutboxUnchanged()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var initialKernel = new AgentOrchestratorKernel();
        var existingGoal = initialKernel.CreateGoal(
            "Existing durable goal",
            [new TaskSpec(TaskId.New(), "Keep existing work", AgentRole.Developer)]);
        await repository.SaveAsync(initialKernel);
        var existingMessage = GoalCreationSideEffectDelivery.CreateMessage(existingGoal.Id, [], []);
        await repository.TransactWithOutboxAsync(
            (_, _) => Task.FromResult((
                ShouldSave: false,
                Result: true,
                OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[existingMessage])));
        var beforeKernel = await CreateMigratedStateRepository(workspace.SqliteStatePath).LoadAsync();
        var beforeSnapshot = JsonSerializer.Serialize(beforeKernel.ExportGoalSnapshot(existingGoal.Id));
        var beforeOutbox = await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind);
        var beforeRefinementOutbox = await repository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents
            .Where(agent => agent.Role != AgentRole.Tester)
            .ToArray();
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var exception = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
            CliPersistentStateRunner.ExecuteCommand(
                ["goal", "Update src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs", "--pipeline", "five-role"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal)));

        var reloadedRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var afterKernel = await reloadedRepository.LoadAsync();
        var afterGoal = Xunit.Assert.Single(afterKernel.Goals);
        Xunit.Assert.Equal(beforeSnapshot, JsonSerializer.Serialize(afterKernel.ExportGoalSnapshot(afterGoal.Id)));
        Xunit.Assert.Equal(beforeOutbox, await reloadedRepository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));
        Xunit.Assert.Equal(
            beforeRefinementOutbox,
            await reloadedRepository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind));
        Xunit.Assert.Equal(existingGoal.Id, currentGoal!.Id);
        Xunit.Assert.Contains("missing available agent role(s): Tester", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("No goal was created", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_create_delivery_failure_is_durable_and_retry_only")]
    public async Task PersistentRunnerGoalCreateDeliveryFailureIsDurableAndRetryOnly()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["goal", "Create a goal with durably retryable clarification delivery"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var created = Xunit.Assert.Single((await repository.LoadAsync()).Goals);
        var deliveryMessage = GoalCreationSideEffectDelivery.CreateMessage(
            created.Id,
            [new GoalCreationCollaborationEffect(
                $"{created.Id.Value}:collaboration:1",
                CollaborationItemType.Clarification,
                created.Id.Value,
                "Confirm the delivery retry contract",
                "This test fixture verifies idempotent post-commit delivery.",
                $"test:{created.Id.Value}:delivery")],
            [
                new GoalCreationLifecycleEffect(
                    $"{created.Id.Value}:lifecycle:1",
                    GoalCreationLifecycleEffectKind.GoalCreated,
                    created.Id.Value,
                    GoalCreationSideEffectDelivery.SerializePayload(
                        new GoalCreationLifecycleEffectDelivery.TextPayload(created.Objective))),
                new GoalCreationLifecycleEffect(
                    $"{created.Id.Value}:lifecycle:2",
                    GoalCreationLifecycleEffectKind.ClarificationNeeded,
                    created.Id.Value,
                    GoalCreationSideEffectDelivery.SerializePayload(
                        new GoalCreationLifecycleEffectDelivery.TextPayload("spec")))
            ]);
        await repository.TransactWithOutboxAsync(
            (kernel, _) => Task.FromResult((
                ShouldSave: true,
                Result: true,
                OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[deliveryMessage])));

        var lifecycleAttempts = 0;
        GoalCreationSideEffectDelivery.BeforeEffectDelivery = deliveryId =>
        {
            if (deliveryId.Contains(":lifecycle:", StringComparison.Ordinal) &&
                Interlocked.Increment(ref lifecycleAttempts) == 2)
            {
                throw new IOException("Injected lifecycle delivery failure.");
            }
        };

        InvalidOperationException error;
        try
        {
            error = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
                CliPersistentStateRunner.ExecuteCommand(
                    ["goal-delivery-retry", created.Id.Value],
                    repository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal)));
        }
        finally
        {
            GoalCreationSideEffectDelivery.BeforeEffectDelivery = null;
        }

        var committedKernel = await repository.LoadAsync();
        Xunit.Assert.Single(committedKernel.Goals);
        Xunit.Assert.Contains("GOAL_CREATE_DELIVERY_INCOMPLETE", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains($"goal-delivery-retry {created.Id.Value}", error.Message, StringComparison.Ordinal);
        Xunit.Assert.True(lifecycleAttempts >= 2);
        Xunit.Assert.Single(await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));
        Xunit.Assert.Single(await repository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind));

        CreateVersion7StateOutboxFixture(workspace.SqliteStatePath);
        Xunit.Assert.False(StateDbMigrations.IsUpToDate(workspace.SqliteStatePath));
        var deliveryError = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
            CliPersistentStateRunner.ExecuteCommand(
                ["goal-delivery-retry", created.Id.Value],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal)));
        var missingColumn = Xunit.Assert.IsType<Microsoft.Data.Sqlite.SqliteException>(deliveryError.InnerException);
        Xunit.Assert.Contains("no such column: quarantined_at", missingColumn.Message, StringComparison.Ordinal);
        ProgramStartupLifecycle.EnsureStateDbInitialized(["conduct", "--loop"], workspace);
        Xunit.Assert.True(StateDbMigrations.IsUpToDate(workspace.SqliteStatePath));

        var collaborationBeforeRetry = await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            .ListAsync(created.Id.Value);
        Xunit.Assert.NotEmpty(collaborationBeforeRetry);
        var eventPath = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{created.Id.Value}.jsonl");
        var eventCountBeforeRetry = File.ReadAllLines(eventPath).Length;
        Xunit.Assert.True(eventCountBeforeRetry >= 1);

        var retryOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["goal-delivery-retry", created.Id.Value],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("GOAL_CREATE_DELIVERY_COMPLETE", retryOutput, StringComparison.Ordinal);
        Xunit.Assert.Empty(await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));
        Xunit.Assert.Single((await repository.LoadAsync()).Goals);
        var collaborationAfterRetry = await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            .ListAsync(created.Id.Value);
        Xunit.Assert.Equal(
            collaborationAfterRetry.Count,
            collaborationAfterRetry.Select(item => item.CorrelationKey).Distinct(StringComparer.Ordinal).Count());
        Xunit.Assert.Equal(collaborationBeforeRetry.Count, collaborationAfterRetry.Count);

        var deliveredEvents = File.ReadAllLines(eventPath)
            .Select(line => JsonNode.Parse(line)!.AsObject())
            .ToArray();
        Xunit.Assert.True(deliveredEvents.Length > eventCountBeforeRetry);
        var deliveryIds = deliveredEvents
            .Select(evt => evt["deliveryId"]?.GetValue<string>())
            .Where(id => id is not null)
            .ToArray();
        Xunit.Assert.Equal(deliveryIds.Length, deliveryIds.Distinct(StringComparer.Ordinal).Count());

        var eventCountAfterRetry = deliveredEvents.Length;
        var repeatedRetryOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["goal-delivery-retry", created.Id.Value[..8]],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        Xunit.Assert.Contains("disposition=alreadydelivered", repeatedRetryOutput, StringComparison.Ordinal);
        Xunit.Assert.Equal(eventCountAfterRetry, File.ReadAllLines(eventPath).Length);
        Xunit.Assert.Single((await repository.LoadAsync()).Goals);
    }

    [Xunit.Theory(DisplayName = "CliPersistentStateRunner_goal_delivery_retry_releases_state_writer_during_external_io")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task PersistentRunnerGoalCreateDeliveryReleasesStateWriterDuringExternalIo(bool retry)
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var concurrentRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["goal", "Create a goal whose delivery runs outside the state transaction"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var initialCreated = Xunit.Assert.Single((await repository.LoadAsync()).Goals);
        var deliveryMessage = GoalCreationSideEffectDelivery.CreateMessage(
            initialCreated.Id,
            [],
            [new GoalCreationLifecycleEffect(
                $"{initialCreated.Id.Value}:lifecycle:lock-check",
                GoalCreationLifecycleEffectKind.GoalCreated,
                initialCreated.Id.Value,
                GoalCreationSideEffectDelivery.SerializePayload(
                    new GoalCreationLifecycleEffectDelivery.TextPayload(initialCreated.Objective)))]);
        await repository.TransactWithOutboxAsync(
            (kernel, _) => Task.FromResult((
                ShouldSave: true,
                Result: true,
                OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[deliveryMessage])));

        if (retry)
        {
            GoalCreationSideEffectDelivery.BeforeEffectDelivery = _ =>
                throw new IOException("Injected initial delivery failure.");
            try
            {
                var error = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
                    CliPersistentStateRunner.ExecuteCommand(
                        ["goal-delivery-retry", initialCreated.Id.Value],
                        repository,
                        workspace,
                        ref agents,
                        providers,
                        ref profiles,
                        ref currentGoal)));
                Xunit.Assert.Contains("GOAL_CREATE_DELIVERY_INCOMPLETE", error.Message, StringComparison.Ordinal);
            }
            finally
            {
                GoalCreationSideEffectDelivery.BeforeEffectDelivery = null;
            }

            Xunit.Assert.Single(await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));
        }

        using var deliveryEntered = new ManualResetEventSlim();
        using var releaseDelivery = new ManualResetEventSlim();
        var deliveryCalls = 0;
        GoalCreationSideEffectDelivery.BeforeEffectDelivery = _ =>
        {
            if (Interlocked.Increment(ref deliveryCalls) != 1)
                return;

            deliveryEntered.Set();
            Xunit.Assert.True(
                releaseDelivery.Wait(TimeSpan.FromSeconds(15)),
                "Timed out waiting to release the external goal-creation delivery.");
        };

        var deliveryTask = Task.Run(() => CaptureConsole(() =>
        {
            _ = CliPersistentStateRunner.ExecuteCommand(
                ["goal-delivery-retry", initialCreated.Id.Value],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        }));

        try
        {
            Xunit.Assert.True(
                deliveryEntered.Wait(TimeSpan.FromSeconds(15)),
                "Goal-creation delivery did not reach the blocking external effect.");
            await concurrentRepository.TransactAsync(
                (kernel, _) =>
                {
                    kernel.CreateGoal("Concurrent writer during goal-creation delivery");
                    return Task.FromResult((true, true));
                }).WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            releaseDelivery.Set();
            GoalCreationSideEffectDelivery.BeforeEffectDelivery = null;
        }

        await deliveryTask.WaitAsync(TimeSpan.FromSeconds(15));
        var restored = await repository.LoadAsync();
        Xunit.Assert.Equal(2, restored.Goals.Count);
        Xunit.Assert.Contains(restored.Goals, goal => goal.Objective == "Concurrent writer during goal-creation delivery");
        Xunit.Assert.Empty(await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));

        var createdGoal = Xunit.Assert.Single(restored.Goals, goal => goal.Id == initialCreated.Id);
        var collaborationItems = await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            .ListAsync(createdGoal.Id.Value);
        Xunit.Assert.Equal(
            collaborationItems.Count,
            collaborationItems.Select(item => item.CorrelationKey).Distinct(StringComparer.Ordinal).Count());
        var eventPath = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{createdGoal.Id.Value}.jsonl");
        var deliveryIds = File.ReadLines(eventPath)
            .Select(line => JsonNode.Parse(line)!["deliveryId"]?.GetValue<string>())
            .Where(id => id is not null)
            .ToArray();
        Xunit.Assert.Equal(deliveryIds.Length, deliveryIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_create_analysis_failure_leaves_no_state_or_delivery_trace")]
    public async Task PersistentRunnerGoalCreateAnalysisFailureLeavesNoStateOrDeliveryTrace()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        GoalCreationSideEffectDelivery.BeforeStateCommit = _ =>
            throw new InvalidOperationException("Injected pre-commit analysis failure.");
        InvalidOperationException error;
        try
        {
            error = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
                CliPersistentStateRunner.ExecuteCommand(
                    ["goal", "Fail after unlocked analysis and before state commit"],
                    repository,
                    workspace,
                    ref agents,
                    new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]),
                    ref profiles,
                    ref currentGoal)));
        }
        finally
        {
            GoalCreationSideEffectDelivery.BeforeStateCommit = null;
        }

        Xunit.Assert.Contains("Injected pre-commit analysis failure", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Empty((await repository.LoadAsync()).Goals);
        Xunit.Assert.Empty(await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));
        Xunit.Assert.Empty(await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory).ListAsync());
        Xunit.Assert.Empty(Directory.Exists(workspace.GoalLifecycleEventsDirectory)
            ? Directory.GetFiles(workspace.GoalLifecycleEventsDirectory, "*.jsonl")
            : []);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_create_rejects_backlog_dependency_change_at_commit")]
    public async Task PersistentRunnerGoalCreateRejectsBacklogDependencyChangeAtCommit()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var kernel = new AgentOrchestratorKernel();
        var dependencyGoal = kernel.CreateGoal("Existing prerequisite goal");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(kernel);
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var source = await backlog.AddAsync("Dependent goal source");
        _ = await backlog.AddDependencyAsync(
            source.Id,
            new BacklogDependencyTarget(dependencyGoal.Id.Value, BacklogDependencyTargetKind.Goal),
            goalExists: id => id == dependencyGoal.Id.Value);
        var providers = new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        GoalCreationSideEffectDelivery.BeforeStateCommit = _ =>
            backlog.RemoveDependencyAsync(source.Id, dependencyGoal.Id.Value).GetAwaiter().GetResult();
        InvalidOperationException error;
        try
        {
            error = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
                CliPersistentStateRunner.ExecuteCommand(
                    ["goal", "Create dependent goal", "--backlog-item", source.Id, "--backlog-coverage", "slice"],
                    repository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal)));
        }
        finally
        {
            GoalCreationSideEffectDelivery.BeforeStateCommit = null;
        }

        Xunit.Assert.Contains("GOAL_CREATE_PRECONDITION_CHANGED reason=source-backlog-dependencies-changed", error.Message, StringComparison.Ordinal);
        var restored = await repository.LoadAsync();
        Xunit.Assert.Equal(dependencyGoal.Id, Xunit.Assert.Single(restored.Goals).Id);
        Xunit.Assert.Empty(await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_create_rejects_missing_dependency_target_at_commit")]
    public async Task PersistentRunnerGoalCreateRejectsMissingDependencyTargetAtCommit()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var kernel = new AgentOrchestratorKernel();
        var dependencyGoal = kernel.CreateGoal("Prerequisite removed during analysis");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(kernel);
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var source = await backlog.AddAsync("Source whose dependency target disappears");
        _ = await backlog.AddDependencyAsync(
            source.Id,
            new BacklogDependencyTarget(dependencyGoal.Id.Value, BacklogDependencyTargetKind.Goal),
            goalExists: id => id == dependencyGoal.Id.Value);
        var providers = new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        GoalCreationSideEffectDelivery.BeforeStateCommit = _ =>
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={workspace.BacklogStorePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE backlog_dependencies SET prerequisite_id = $missing WHERE dependent_id = $source";
            command.Parameters.AddWithValue("$missing", GoalId.New().Value);
            command.Parameters.AddWithValue("$source", source.Id);
            Xunit.Assert.Equal(1, command.ExecuteNonQuery());
        };
        InvalidOperationException error;
        try
        {
            error = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
                CliPersistentStateRunner.ExecuteCommand(
                    ["goal", "Reject missing dependency target", "--backlog-item", source.Id, "--backlog-coverage", "slice"],
                    repository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal)));
        }
        finally
        {
            GoalCreationSideEffectDelivery.BeforeStateCommit = null;
        }

        Xunit.Assert.Contains("GOAL_CREATE_PRECONDITION_CHANGED reason=dependency-target-missing", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Equal(dependencyGoal.Id, Xunit.Assert.Single((await repository.LoadAsync()).Goals).Id);
        Xunit.Assert.Empty(await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_create_keeps_overlap_detected_advisory_nonblocking")]
    public async Task PersistentRunnerGoalCreateKeepsOverlapDetectedAdvisoryNonblocking()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var kernel = new AgentOrchestratorKernel();
        _ = kernel.CreateGoal($"Existing work\n\n{BacklogIntakePlanner.TargetScopeHeadingLine}\n- src/Feature/File.cs");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["goal", $"Change shared feature\n\n{BacklogIntakePlanner.TargetScopeHeadingLine}\n- src/Feature/File.cs"],
            repository,
            workspace,
            ref agents,
            new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]),
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("overlap-detected", output, StringComparison.Ordinal);
        var restored = await repository.LoadAsync();
        Xunit.Assert.Equal(2, restored.Goals.Count);
        Xunit.Assert.NotNull(currentGoal);
        var restoredCreatedGoal = restored.GetGoal(currentGoal.Id);
        Xunit.Assert.NotEmpty(restoredCreatedGoal.Tasks);
        Xunit.Assert.All(restoredCreatedGoal.Tasks, task =>
        {
            Xunit.Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Xunit.Assert.Null(task.LastDispatch);
            Xunit.Assert.Null(task.LastProcess);
        });
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_create_preserves_precommit_stdout_bytes")]
    public void PersistentRunnerGoalCreatePreservesPrecommitStdoutBytes()
    {
        const string objective = "Create deterministic goal output for stdout compatibility";

        (string Output, Goal Goal) Run(bool persistent)
        {
            var root = CreateTempDirectory();
            var workspace = CreateRefinedWorkspace(root);
            ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
                new ModelFunctionBinding(
                    ModelFunctionPurposes.SpecRefiner,
                    ModelLane.CheapApi,
                    new ModelProfile("deterministic-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                    Name: ModelFunctionPurposes.SpecRefiner)
            ]));
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            var providers = new InMemoryModelProviderRegistry([new DeterministicGoalRefinerProvider()]);
            Goal? currentGoal = null;
            var output = persistent
                ? CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                    ["goal", objective],
                    new InMemoryTransactionalStateRepository(new AgentOrchestratorKernel()),
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal))
                : CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                    ["goal", objective],
                    new AgentOrchestratorKernel(),
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal));
            return (output, Xunit.Assert.IsType<Goal>(currentGoal));
        }

        static string PrecommitOutput((string Output, Goal Goal) result)
        {
            var goalOutput = $"Goal {result.Goal.Id.Value}";
            var goalIndex = result.Output.IndexOf(goalOutput, StringComparison.Ordinal);
            Xunit.Assert.True(goalIndex > 0, result.Output);
            return result.Output[..goalIndex];
        }

        var ambientTransactionBaseline = Run(persistent: false);
        var outsideTransactionCandidate = Run(persistent: true);
        Xunit.Assert.Equal(
            PrecommitOutput(ambientTransactionBaseline),
            PrecommitOutput(outsideTransactionCandidate));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_create_rejects_missing_backlog_source_without_side_effects")]
    public async Task PersistentRunnerGoalCreateRejectsMissingBacklogSourceWithoutSideEffects()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Backlog source removed during refinement");
        var repository = new InMemoryTransactionalStateRepository(new AgentOrchestratorKernel());
        repository.BeforeNextTransaction = _ =>
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={workspace.BacklogStorePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM backlog WHERE id = $id";
            command.Parameters.AddWithValue("$id", item.Id);
            Xunit.Assert.Equal(1, command.ExecuteNonQuery());
        };
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var error = Xunit.Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
            CliPersistentStateRunner.ExecuteCommand(
                ["goal", "Reject a stale missing backlog source", "--backlog-item", item.Id, "--backlog-coverage", "slice"],
                repository,
                workspace,
                ref agents,
                new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]),
                ref profiles,
                ref currentGoal)));

        Xunit.Assert.Contains("GOAL_CREATE_PRECONDITION_CHANGED reason=source-backlog-missing", error.Message);
        Xunit.Assert.Empty((await repository.LoadAsync()).Goals);
        Xunit.Assert.Empty(await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory).ListAsync());
        Xunit.Assert.Empty(Directory.Exists(workspace.GoalLifecycleEventsDirectory)
            ? Directory.GetFiles(workspace.GoalLifecycleEventsDirectory, "*.jsonl")
            : []);
    }
    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

}
