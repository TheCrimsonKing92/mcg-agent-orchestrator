using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

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

        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
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
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
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

        GoalLandingPostActions.AutoCloseSourceBacklogItem(goal, dbPath);

        var fetched = await store.GetByExactIdAsync(item.Id);
        Assert.True(fetched is not null);
        Assert.True(fetched!.Status == BacklogItemStatus.Done);
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
}
