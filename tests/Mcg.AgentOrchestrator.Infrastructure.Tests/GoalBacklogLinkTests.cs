using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection("EnvMutation")]
public sealed class GoalBacklogLinkTests
{
    // ── Intake: create with link ──────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalBacklogLink_create_with_link_carries_source_backlog_item_id")]
    public void CreateWithLinkCarriesSourceBacklogItemId()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, "# Backlog\n\n## My Feature\n\nFeature body.\n");
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CliCommandDispatcher.ExecuteCommand(
            ["backlog-intake", "My Feature", "--create-simple-goal"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

        Assert.True(currentGoal is not null);
        // Intake links the goal to the REAL backlog item id (a GUID from AddAsync), not a title slug,
        // so auto-close fires for GUID-keyed items. Assert against the actually-seeded item's id.
        var seededId = new BacklogStore(workspace.BacklogStorePath)
            .ListAsync().GetAwaiter().GetResult()
            .Single(entry => string.Equals(entry.Title, "My Feature", StringComparison.Ordinal))
            .Id;
        Assert.Equal(seededId, currentGoal!.SourceBacklogItemId);
        Assert.False(File.Exists(Path.Combine(root, "BACKLOG.md")));
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_simple_goal_backlog_item_flag_resolves_prefix_and_persists_link")]
    public async Task SimpleGoalBacklogItemFlagResolvesPrefixAndPersistsLink()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Explicit direct link");
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["simple-goal", "Implement explicit direct link", "--backlog-item", item.Id[..8]],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Assert.NotNull(currentGoal);
        Assert.Equal(item.Id, currentGoal!.SourceBacklogItemId);
        Assert.Contains($"Source backlog: {item.Id}", output);
        await CreateMigratedStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        var restored = await CreateMigratedStateRepository(workspace.SqliteStatePath).LoadAsync();
        Assert.Equal(item.Id, restored.GetGoal(currentGoal.Id).SourceBacklogItemId);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_promotion_wires_existing_promoted_prerequisite")]
    public async Task PromotionWiresExistingPromotedPrerequisite()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var prerequisiteItem = await store.AddAsync("Prerequisite");
        var dependentItem = await store.AddAsync("Dependent");
        await store.AddDependencyAsync(
            dependentItem.Id,
            new(prerequisiteItem.Id, BacklogDependencyTargetKind.Backlog));
        var kernel = new AgentOrchestratorKernel();
        var prerequisiteGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Promoted prerequisite");
        kernel.SetGoalSourceBacklogItemId(prerequisiteGoal.Id, prerequisiteItem.Id);
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["simple-goal", "Promote dependent", "--backlog-item", dependentItem.Id[..8]],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Assert.NotNull(currentGoal);
        Assert.Contains(prerequisiteGoal.Id, currentGoal!.DependsOn);
        Assert.Single((await store.GetByExactIdAsync(dependentItem.Id))!.Dependencies);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_promotion_blocks_unpromoted_prerequisite_before_goal_creation")]
    public async Task PromotionBlocksUnpromotedPrerequisiteBeforeGoalCreation()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var prerequisiteItem = await store.AddAsync("Unpromoted prerequisite");
        var dependentItem = await store.AddAsync("Dependent");
        await store.AddDependencyAsync(
            dependentItem.Id,
            new(prerequisiteItem.Id, BacklogDependencyTargetKind.Backlog));
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var error = Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["simple-goal", "Promote dependent", "--backlog-item", dependentItem.Id[..8]],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Assert.Contains("waiting on open prerequisite", error.Message, StringComparison.Ordinal);
        Assert.Empty(kernel.Goals);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_backlog_intake_backlog_item_flag_resolves_prefix_and_creates_linked_goal")]
    public async Task BacklogIntakeBacklogItemFlagResolvesPrefixAndCreatesLinkedGoal()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var decoy = await store.AddAsync("Decoy intake link");
        var item = await store.AddAsync("Explicit intake link", "Body with no shared filter text.");
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var changed = CliCommandDispatcher.ExecuteCommand(
            ["backlog-intake", decoy.Title, "--backlog-item", item.Id[..8], "--create-simple-goal"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

        Assert.True(changed);
        Assert.NotNull(currentGoal);
        Assert.Equal(item.Id, currentGoal!.SourceBacklogItemId);
        Assert.Contains("Explicit intake link", currentGoal.Objective, StringComparison.Ordinal);
        Assert.DoesNotContain("Decoy intake link", currentGoal.Objective, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_goal_from_backlog_forwards_backlog_item_value_to_intake")]
    public async Task GoalFromBacklogForwardsBacklogItemValueToIntake()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        await store.AddAsync("Wrong alias link");
        var item = await store.AddAsync("Explicit alias link");
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var changed = CliCommandDispatcher.ExecuteCommand(
            ["goal", "Wrong alias link", "--from-backlog", "--backlog-item", item.Id[..8], "--create-simple-goal"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

        Assert.True(changed);
        Assert.NotNull(currentGoal);
        Assert.Equal(item.Id, currentGoal!.SourceBacklogItemId);
        Assert.Contains("Explicit alias link", currentGoal.Objective, StringComparison.Ordinal);
        Assert.DoesNotContain("Wrong alias link", currentGoal.Objective, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_simple_goal_opening_prose_resolves_source_backlog_item")]
    public async Task SimpleGoalOpeningProseResolvesSourceBacklogItem()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Prose direct link");
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CliCommandDispatcher.ExecuteCommand(
            ["simple-goal", $"(backlog {item.Id[..8]}) Implement prose direct link"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

        Assert.NotNull(currentGoal);
        Assert.Equal(item.Id, currentGoal!.SourceBacklogItemId);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_simple_goal_without_source_backlog_item_is_unchanged")]
    public void SimpleGoalWithoutSourceBacklogItemIsUnchanged()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CliCommandDispatcher.ExecuteCommand(
            ["simple-goal", "Implement unlinked direct goal"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

        Assert.NotNull(currentGoal);
        Assert.Null(currentGoal!.SourceBacklogItemId);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_intake_against_done_item_warns_but_creates_direct_goal")]
    public async Task IntakeAgainstDoneItemWarnsButCreatesDirectGoal()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var item = await store.AddAsync("Done direct link");
        await store.CloseAsync(item.Id);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["simple-goal", "Implement done direct link", "--backlog-item", item.Id[..8]],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Assert.NotNull(currentGoal);
        Assert.Equal(item.Id, currentGoal!.SourceBacklogItemId);
        Assert.Contains("already Done", output);
        Assert.Contains("goal creation will continue", output);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_create_goal_from_sqlite_only_backlog_carries_source_backlog_item_id")]
    public void CreateGoalFromSqliteOnlyBacklogCarriesSourceBacklogItemId()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, "# Backlog\n\n## Five Role Feature\n\nFeature body.\n");
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var changed = CliCommandDispatcher.ExecuteCommand(
            ["backlog-intake", "Five Role Feature", "--create-goal"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

        var seededId = new BacklogStore(workspace.BacklogStorePath)
            .ListAsync().GetAwaiter().GetResult()
            .Single(entry => string.Equals(entry.Title, "Five Role Feature", StringComparison.Ordinal))
            .Id;
        Assert.True(changed);
        Assert.NotNull(currentGoal);
        Assert.Equal(5, currentGoal!.Tasks.Count);
        Assert.Equal(seededId, currentGoal.SourceBacklogItemId);
        Assert.False(File.Exists(Path.Combine(root, "BACKLOG.md")));
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_intake_filter_matches_body_and_exact_id")]
    public async Task IntakeFilterMatchesBodyAndExactId()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var bodyMatched = await store.AddAsync("Body Search Feature", "Contains a unique sqlite-only phrase.");
        var idMatched = await store.AddAsync("Exact Id Feature", "Body.");

        var bodyPlan = BacklogIntakePlanner.Build(workspace.BacklogStorePath, "sqlite-only phrase", 5);
        var idPlan = BacklogIntakePlanner.Build(workspace.BacklogStorePath, idMatched.Id, 5);

        Assert.Equal(bodyMatched.Id, Assert.Single(bodyPlan.Items).Id);
        Assert.Equal(idMatched.Id, Assert.Single(idPlan.Items).Id);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_intake_ignores_present_backlog_markdown")]
    public void IntakeIgnoresPresentBacklogMarkdown()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, "# Backlog\n\n## SQLite Feature\n\nFeature body.\n");
        File.WriteAllText(Path.Combine(root, "BACKLOG.md"), "# Backlog\n\n## Markdown Only Feature\n\nLegacy body.\n");
        var workspace = CreateRefinedWorkspace(root);

        var plan = BacklogIntakePlanner.Build(workspace.BacklogStorePath, "Markdown Only Feature", 5);

        Assert.Empty(plan.Items);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_intake_missing_sqlite_backlog_fails_without_backlog_markdown_requirement")]
    public void IntakeMissingSqliteBacklogFailsWithoutBacklogMarkdownRequirement()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "BACKLOG.md"), "# Backlog\n\n## Legacy Feature\n\nLegacy body.\n");
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var ex = Xunit.Assert.ThrowsAny<FileNotFoundException>(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-intake", "Legacy Feature", "--create-simple-goal"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Assert.Contains("backlog.db", ex.Message);
        Assert.DoesNotContain("BACKLOG.md", ex.Message);
        Assert.False(File.Exists(workspace.BacklogStorePath));
        Assert.Empty(kernel.Goals);
        Assert.Null(currentGoal);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_create_flag_with_multiple_matches_fails_before_creating_goal")]
    public void CreateFlagWithMultipleMatchesFailsBeforeCreatingGoal()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, "# Backlog\n\n## Ambiguous Alpha\n\nBody.\n\n## Ambiguous Beta\n\nBody.\n");
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-intake", "Ambiguous", "--create-simple-goal"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Assert.Contains("matched multiple items", ex.Message);
        Assert.Contains("Ambiguous Alpha", ex.Message);
        Assert.Contains("Ambiguous Beta", ex.Message);
        Assert.Empty(kernel.Goals);
        Assert.Null(currentGoal);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_new_source_backlog_id_creates_goal_and_persists_indexed_record")]
    public async Task NewSourceBacklogIdCreatesGoalAndPersistsIndexedRecord()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, "# Backlog\n\n## Indexed Feature\n\nFeature body.\n");
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var changed = CliCommandDispatcher.ExecuteCommand(
            ["backlog-intake", "Indexed Feature", "--create-simple-goal"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

        Assert.True(changed);
        Assert.NotNull(currentGoal);
        var seededId = new BacklogStore(workspace.BacklogStorePath)
            .ListAsync().GetAwaiter().GetResult()
            .Single(entry => string.Equals(entry.Title, "Indexed Feature", StringComparison.Ordinal))
            .Id;
        Assert.Equal(seededId, currentGoal!.SourceBacklogItemId);

        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(kernel);

        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={workspace.SqliteStatePath}");
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT source_backlog_item_id FROM goals WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", currentGoal.Id.Value);
            Assert.Equal(seededId, (string?)await cmd.ExecuteScalarAsync());
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_goals_source_backlog_item_id'";
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync() ?? 0L));
        }

        var intakeRecord = new BacklogIntakeRecordStore(workspace.SqliteStatePath).Get(seededId);
        Assert.NotNull(intakeRecord);
        Assert.Equal("GoalCreated", intakeRecord!.Status);
        Assert.Equal(currentGoal.Id.Value, intakeRecord.GoalId);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_duplicate_source_backlog_id_returns_existing_goal_without_new_goal")]
    public void DuplicateSourceBacklogIdReturnsExistingGoalWithoutNewGoal()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, "# Backlog\n\n## Retry Feature\n\nFeature body.\n");
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var firstChanged = CliCommandDispatcher.ExecuteCommand(
            ["backlog-intake", "Retry Feature", "--create-simple-goal"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
        var firstGoal = currentGoal!;

        var output = CaptureConsole(() =>
        {
            var secondChanged = CliCommandDispatcher.ExecuteCommand(
                ["backlog-intake", "Retry Feature", "--create-simple-goal"],
                kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
            Assert.False(secondChanged);
        });

        Assert.True(firstChanged);
        Assert.Single(kernel.Goals);
        Assert.Equal(firstGoal.Id, currentGoal!.Id);
        Assert.Contains($"already has goal {firstGoal.Id.Value[..8]}", output);
        Assert.Contains("no new goal created", output);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_persistent_duplicate_returns_existing_goal_without_new_goal_row")]
    public void PersistentDuplicateReturnsExistingGoalWithoutNewGoalRow()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, "# Backlog\n\n## Persistent Retry Feature\n\nFeature body.\n");
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var firstChanged = CliPersistentStateRunner.ExecuteCommand(
            ["backlog-intake", "Persistent Retry Feature", "--create-simple-goal"],
            repository, workspace, ref agents, providers, ref profiles, ref currentGoal);
        var firstGoalId = currentGoal!.Id.Value;

        var secondOutput = CaptureConsole(() =>
        {
            var secondChanged = CliPersistentStateRunner.ExecuteCommand(
                ["backlog-intake", "Persistent Retry Feature", "--create-simple-goal"],
                repository, workspace, ref agents, providers, ref profiles, ref currentGoal);
            Assert.False(secondChanged);
        });

        var restored = repository.LoadAsync().GetAwaiter().GetResult();
        Assert.True(firstChanged);
        Assert.Single(restored.Goals);
        Assert.Equal(firstGoalId, restored.Goals.Single().Id.Value);
        Assert.Equal(firstGoalId, currentGoal!.Id.Value);
        Assert.Contains($"already has goal {firstGoalId[..8]}", secondOutput);
        Assert.Contains("no new goal created", secondOutput);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_in_progress_intake_retry_reports_record_without_new_goal")]
    public void InProgressIntakeRetryReportsRecordWithoutNewGoal()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, "# Backlog\n\n## Active Retry Feature\n\nFeature body.\n");
        var workspace = CreateRefinedWorkspace(root);
        var item = new BacklogStore(workspace.BacklogStorePath)
            .ListAsync().GetAwaiter().GetResult()
            .Single(entry => string.Equals(entry.Title, "Active Retry Feature", StringComparison.Ordinal));
        new BacklogIntakeRecordStore(workspace.SqliteStatePath).Reserve(item.Id, item.Title);

        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["backlog-intake", "Active Retry Feature", "--create-simple-goal"],
                kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
            Assert.False(changed);
        });

        Assert.Empty(kernel.Goals);
        Assert.Null(currentGoal);
        Assert.Contains("is InProgress", output);
        Assert.Contains("no new goal created", output);
        Assert.Contains("--force-reclaim", output);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_intake_reservation_waits_for_transient_state_write_lock")]
    public async Task IntakeReservationWaitsForTransientStateWriteLock()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, "# Backlog\n\n## Locked Intake Feature\n\nFeature body.\n");
        var workspace = CreateRefinedWorkspace(root);
        var item = new BacklogStore(workspace.BacklogStorePath)
            .ListAsync().GetAwaiter().GetResult()
            .Single(entry => string.Equals(entry.Title, "Locked Intake Feature", StringComparison.Ordinal));
        var store = new BacklogIntakeRecordStore(workspace.SqliteStatePath);

        using var lockConnection = new SqliteConnection($"Data Source={workspace.SqliteStatePath};Mode=ReadWrite;Pooling=False;");
        lockConnection.Open();
        using var lockCommand = lockConnection.CreateCommand();
        lockCommand.CommandText = "BEGIN IMMEDIATE";
        lockCommand.ExecuteNonQuery();

        var reserveTask = Task.Run(() => store.Reserve(item.Id, item.Title));
        Assert.False(ReferenceEquals(reserveTask, await Task.WhenAny(reserveTask, Task.Delay(TimeSpan.FromMilliseconds(100)))));

        using var releaseCommand = lockConnection.CreateCommand();
        releaseCommand.CommandText = "COMMIT";
        releaseCommand.ExecuteNonQuery();

        var reservation = await reserveTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(BacklogIntakeReservationKind.Acquired, reservation.Kind);
        Assert.Equal(item.Id, reservation.Record.SourceBacklogItemId);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_in_progress_intake_reports_launcher_log_paths")]
    public void InProgressIntakeReportsLauncherLogPaths()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, "# Backlog\n\n## Logged Retry Feature\n\nFeature body.\n");
        var workspace = CreateRefinedWorkspace(root);
        var item = new BacklogStore(workspace.BacklogStorePath)
            .ListAsync().GetAwaiter().GetResult()
            .Single(entry => string.Equals(entry.Title, "Logged Retry Feature", StringComparison.Ordinal));
        var stdoutPath = Path.Combine(root, "logs", "intake.out.log");
        var stderrPath = Path.Combine(root, "logs", "intake.err.log");
        var previousStdout = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH");
        var previousStderr = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH", stdoutPath);
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH", stderrPath);
            new BacklogIntakeRecordStore(workspace.SqliteStatePath).Reserve(item.Id, item.Title);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH", previousStdout);
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH", previousStderr);
        }

        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["backlog-intake", "Logged Retry Feature", "--create-simple-goal"],
                kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
            Assert.False(changed);
        });

        Assert.Empty(kernel.Goals);
        Assert.Contains(Path.GetFullPath(stdoutPath), output);
        Assert.Contains(Path.GetFullPath(stderrPath), output);
    }

    // ── Intake: batch (multiple filters -> one goal each) ─────────────────────

    [Xunit.Fact(DisplayName = "GoalBacklogLink_batch_intake_creates_one_linked_goal_per_filter")]
    public void BatchIntakeCreatesOneLinkedGoalPerFilter()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, "# Backlog\n\n## Alpha Feature\n\nAlpha body.\n\n## Beta Feature\n\nBeta body.\n");
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CliCommandDispatcher.ExecuteCommand(
            ["backlog-intake", "Alpha Feature", "Beta Feature", "--create-simple-goal"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

        Assert.Equal(2, kernel.Goals.Count);
        var seeded = new BacklogStore(workspace.BacklogStorePath).ListAsync().GetAwaiter().GetResult();
        var alphaId = seeded.Single(entry => string.Equals(entry.Title, "Alpha Feature", StringComparison.Ordinal)).Id;
        var betaId = seeded.Single(entry => string.Equals(entry.Title, "Beta Feature", StringComparison.Ordinal)).Id;
        Assert.True(kernel.Goals.Any(goal => goal.SourceBacklogItemId == alphaId));
        Assert.True(kernel.Goals.Any(goal => goal.SourceBacklogItemId == betaId));
    }

    // ── Intake: skip already-done item ───────────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalBacklogLink_no_goal_for_already_done_backlog_item_at_intake")]
    public async Task NoGoalForAlreadyDoneItemAtIntake()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);

        // Seed the item into the BacklogStore and mark it Done.
        var store = new BacklogStore(workspace.BacklogStorePath);
        var itemId = BacklogStore.SlugId("Done Feature");
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(new BacklogItem(itemId, "Done Feature", "Feature body.", BacklogItemStatus.Open, now, now, null));
        await store.CloseAsync(itemId);

        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() =>
        {
            CliCommandDispatcher.ExecuteCommand(
                ["backlog-intake", "Done Feature", "--create-simple-goal"],
                kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
        });

        Assert.True(currentGoal is null);
        Assert.Equal(0, kernel.Goals.Count);
        Assert.True(output.Contains("already Done", StringComparison.OrdinalIgnoreCase));
    }

    // ── Land: close linked item on successful acceptance ─────────────────────

    [Xunit.Fact(DisplayName = "GoalBacklogLink_land_closes_linked_backlog_item")]
    public async Task LandClosesLinkedBacklogItem()
    {
        var dbPath = Path.Combine(CreateTempDirectory(), "backlog.db");
        var store = new BacklogStore(dbPath);
        var item = await store.AddAsync("Landing target");

        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Land this", [new TaskSpec(TaskId.New(), "Do it", AgentRole.Developer)]);
        kernel.SetGoalSourceBacklogItemId(goal.Id, item.Id);

        GoalLandingPostActions.AutoCloseSourceBacklogItem(goal, dbPath, kernel: kernel);

        var fetched = await store.GetByExactIdAsync(item.Id);
        Assert.True(fetched is not null);
        Assert.True(fetched!.Status == BacklogItemStatus.Done);
        var note = Assert.Single(fetched.Notes);
        Assert.Contains(goal.Id.Value, note.Text);
        Assert.Contains("integrateCommit=unknown", note.Text);

        GoalLandingPostActions.AutoCloseSourceBacklogItem(goal, dbPath, kernel: kernel);
        var rerun = await store.GetByExactIdAsync(item.Id);
        Assert.Single(rerun!.Notes);
    }

    // ── Land: already-closed item is a safe no-op ────────────────────────────

    [Xunit.Fact(DisplayName = "GoalBacklogLink_land_is_noop_when_backlog_item_already_closed")]
    public async Task LandIsNoopWhenItemAlreadyClosed()
    {
        var dbPath = Path.Combine(CreateTempDirectory(), "backlog.db");
        var store = new BacklogStore(dbPath);
        var item = await store.AddAsync("Already shipped");
        await store.CloseAsync(item.Id);

        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Land this", [new TaskSpec(TaskId.New(), "Do it", AgentRole.Developer)]);
        kernel.SetGoalSourceBacklogItemId(goal.Id, item.Id);

        // Must not throw; must leave item as Done.
        GoalLandingPostActions.AutoCloseSourceBacklogItem(goal, dbPath);

        var fetched = await store.GetByExactIdAsync(item.Id);
        Assert.True(fetched is not null);
        Assert.True(fetched!.Status == BacklogItemStatus.Done);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_land_without_source_backlog_item_skips_silently")]
    public void LandWithoutSourceBacklogItemSkipsSilently()
    {
        var dbPath = Path.Combine(CreateTempDirectory(), "backlog.db");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Land this", [new TaskSpec(TaskId.New(), "Do it", AgentRole.Developer)]);
        var messages = new List<string>();

        var closed = GoalLandingPostActions.AutoCloseSourceBacklogItem(goal, dbPath, messages.Add);

        Assert.False(closed);
        Assert.Empty(messages);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_land_missing_source_backlog_item_records_goal_warning_without_throwing")]
    public void LandMissingSourceBacklogItemRecordsGoalWarningWithoutThrowing()
    {
        var dbPath = Path.Combine(CreateTempDirectory(), "backlog.db");
        _ = new BacklogStore(dbPath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Land this", [new TaskSpec(TaskId.New(), "Do it", AgentRole.Developer)]);
        kernel.SetGoalSourceBacklogItemId(goal.Id, "deadbeefdeadbeefdeadbeefdeadbeef");
        var messages = new List<string>();

        var closed = GoalLandingPostActions.AutoCloseSourceBacklogItem(goal, dbPath, messages.Add, kernel);

        Assert.False(closed);
        Assert.Contains(messages, message => message.Contains("not found", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(kernel.GetTimeline(goal.Id), evt =>
            evt.Message.Contains("linked backlog item deadbeefdeadbeefdeadbeefdeadbeef was not found", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_backlog_show_lists_linked_goals_and_landing_state")]
    public async Task BacklogShowListsLinkedGoalsAndLandingState()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var item = await store.AddAsync("Visible linked goal");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Show linked goal", [new TaskSpec(TaskId.New(), "Do it", AgentRole.Developer)]);
        kernel.SetGoalSourceBacklogItemId(goal.Id, item.Id);
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-show", item.Id[..8]],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Assert.Contains("Linked goals:", output);
        Assert.Contains(goal.Id.Value[..8], output);
        Assert.Contains("landing=", output);
    }
}
