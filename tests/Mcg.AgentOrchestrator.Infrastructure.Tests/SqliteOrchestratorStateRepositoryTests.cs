using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class SqliteOrchestratorStateRepositoryTests
{
    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_roundtrips_snapshot_through_SQLite")]
    public async Task SnapshotRoundtripThroughSqlite()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var agent = new AgentDefinition(
            AgentId.New(), "Developer", AgentRole.Developer,
            new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        var goal = kernel.CreateGoal("Roundtrip goal");
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        kernel.SetTaskVerificationPlan(goal.Id, task.Id, "Run dotnet test");
        await new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([new FakeSmokeProvider()]))
            .RunAsync(goal.Id, task.Id);

        await repo.SaveAsync(kernel);
        var restored = await repo.LoadAsync();

        Assert.Equal(goal.Id, restored.Goals.Single().Id);
        Assert.Equal("Roundtrip goal", restored.Goals.Single().Objective);
        Assert.Equal(GoalStatus.Active, restored.Goals.Single().Status);
        Assert.Equal("Run dotnet test", restored.GetTask(goal.Id, task.Id).VerificationPlan);
        Assert.Equal(TaskComplexity.Simple, restored.GetTask(goal.Id, task.Id).LastExecution!.TaskComplexity);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_per_goal_upsert_preserves_unmodified_goal")]
    public async Task PerGoalUpsertPreservesUnmodifiedGoal()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goalA = kernel.CreateGoal("Goal A");
        var goalB = kernel.CreateGoal("Goal B - should be unchanged");
        await repo.SaveAsync(kernel);

        await repo.TransactAsync((k, _) =>
        {
            var a = k.Goals.Single(g => g.Objective == "Goal A");
            k.ActivateGoal(a.Id, AgentCatalog.Default().Agents);
            return Task.FromResult((true, true));
        });

        var restored = await repo.LoadAsync();
        Assert.Equal(2, restored.Goals.Count);
        var restoredA = restored.Goals.Single(g => g.Id == goalA.Id);
        var restoredB = restored.Goals.Single(g => g.Id == goalB.Id);
        Assert.Equal(GoalStatus.Active, restoredA.Status);
        Assert.Equal(GoalStatus.Draft, restoredB.Status);
        Assert.Equal("Goal B - should be unchanged", restoredB.Objective);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_updated_at_not_bumped_for_unchanged_goal")]
    public async Task UpdatedAtNotBumpedForUnchangedGoal()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal("Stable goal");
        kernel.CreateGoal("Changing goal");
        await repo.SaveAsync(kernel);

        var before = await repo.ListGoalMetadataAsync();
        var stableUpdatedAt1 = DateTimeOffset.Parse(before.Single(m => m.Objective == "Stable goal").UpdatedAt);

        await Task.Delay(10);

        await repo.TransactAsync((k, _) =>
        {
            var changing = k.Goals.Single(g => g.Objective == "Changing goal");
            k.ActivateGoal(changing.Id, AgentCatalog.Default().Agents);
            return Task.FromResult((true, true));
        });

        var after = await repo.ListGoalMetadataAsync();
        var stableUpdatedAt2 = DateTimeOffset.Parse(after.Single(m => m.Objective == "Stable goal").UpdatedAt);
        var changingUpdatedAt2 = DateTimeOffset.Parse(after.Single(m => m.Objective == "Changing goal").UpdatedAt);

        Assert.Equal(stableUpdatedAt1, stableUpdatedAt2);
        Assert.True(changingUpdatedAt2 >= stableUpdatedAt1);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_stale_kernel_save_does_not_delete_goal_not_in_snapshot")]
    public async Task StaleKernelSaveDoesNotDeleteGoalNotInSnapshot()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);

        // Establish two goals in the store.
        var kernelFull = new AgentOrchestratorKernel();
        var goalA = kernelFull.CreateGoal("Goal A");
        kernelFull.CreateGoal("Goal B");
        await repo.SaveAsync(kernelFull);

        // Save a fresh kernel that only knows about Goal A — simulates a stale or partially-hydrated
        // kernel (e.g. loaded before Goal B was created). Must NOT delete Goal B.
        var kernelStale = new AgentOrchestratorKernel();
        kernelStale.CreateGoal(goalA.Id, "Goal A");
        await repo.SaveAsync(kernelStale);

        var restored = await repo.LoadAsync();
        Assert.Equal(2, restored.Goals.Count);
        Assert.True(restored.Goals.Any(g => g.Objective == "Goal A"));
        Assert.True(restored.Goals.Any(g => g.Objective == "Goal B"));

        var listing = await repo.ListGoalMetadataAsync();
        Assert.Equal(2, listing.Count);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_transaction_rollback_on_failing_mutation")]
    public async Task TransactionRollbackOnFailingMutation()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal("Pre-failure goal");
        await repo.SaveAsync(kernel);

        await Xunit.Assert.ThrowsAsync<InvalidOperationException>(() =>
            repo.TransactAsync<bool>((k, _) =>
            {
                k.CreateGoal("Transient goal that should not persist");
                throw new InvalidOperationException("Simulated mutation failure");
            }));

        var restored = await repo.LoadAsync();
        Assert.Equal(1, restored.Goals.Count);
        Assert.Equal("Pre-failure goal", restored.Goals.Single().Objective);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_json_to_sqlite_migration_preserves_goals_tasks_status")]
    public async Task JsonToSqliteMigrationPreservesGoalsTasksStatus()
    {
        var root = CreateTempDirectory();
        var jsonPath = Path.Combine(root, "state.json");
        var dbPath = Path.Combine(root, "state.db");

        var kernel = new AgentOrchestratorKernel();
        var agent = new AgentDefinition(
            AgentId.New(), "Developer", AgentRole.Developer,
            new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        var goal = kernel.CreateGoal("Migrated goal");
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        await new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([new FakeSmokeProvider()]))
            .RunAsync(goal.Id, task.Id);
        OrchestratorStateStore.Save(jsonPath, kernel);

        var migrated = await SqliteStateJsonMigrator.MigrateIfNeededAsync(jsonPath, dbPath);
        Assert.True(migrated);
        Assert.True(File.Exists(dbPath));
        Assert.True(File.Exists(jsonPath + ".pre-sqlite-migration.bak"));

        var repo = new SqliteOrchestratorStateRepository(dbPath);
        var restored = await repo.LoadAsync();

        Assert.Equal(goal.Id, restored.Goals.Single().Id);
        Assert.Equal("Migrated goal", restored.Goals.Single().Objective);
        Assert.Equal(GoalStatus.Active, restored.Goals.Single().Status);
        Assert.Equal(TaskComplexity.Simple, restored.GetTask(goal.Id, task.Id).LastExecution!.TaskComplexity);
    }

    [Xunit.Fact(DisplayName = "SqliteStateJsonMigrator_skips_migration_when_db_already_exists")]
    public async Task MigratorSkipsWhenDbExists()
    {
        var root = CreateTempDirectory();
        var jsonPath = Path.Combine(root, "state.json");
        var dbPath = Path.Combine(root, "state.db");

        File.WriteAllText(jsonPath, "{}");
        File.WriteAllText(dbPath, "existing");

        var migrated = await SqliteStateJsonMigrator.MigrateIfNeededAsync(jsonPath, dbPath);
        Assert.False(migrated);
        Assert.Equal("existing", File.ReadAllText(dbPath));
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_concurrent_transactions_serialize_mutations")]
    public async Task ConcurrentTransactionsSerializeMutations()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        await repo.SaveAsync(new AgentOrchestratorKernel());

        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            repo.TransactAsync((kernel, _) =>
            {
                kernel.CreateGoal($"Concurrent goal {i}");
                return Task.FromResult((true, true));
            }))).ToArray();

        await Task.WhenAll(tasks);

        var restored = await repo.LoadAsync();
        Assert.Equal(8, restored.Goals.Count);
        for (var i = 0; i < 8; i++)
            Assert.True(restored.Goals.Any(g => g.Objective == $"Concurrent goal {i}"));
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_list_metadata_returns_indexed_fields_without_snapshot")]
    public async Task ListMetadataReturnsIndexedFields()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal("Listed goal A");
        kernel.CreateGoal("Listed goal B");
        await repo.SaveAsync(kernel);

        var listing = await repo.ListGoalMetadataAsync();

        Assert.Equal(2, listing.Count);
        Assert.True(listing.All(m => !string.IsNullOrEmpty(m.Id)));
        Assert.True(listing.All(m => !string.IsNullOrEmpty(m.Status)));
        Assert.True(listing.All(m => !string.IsNullOrEmpty(m.Objective)));
        Assert.True(listing.All(m => !string.IsNullOrEmpty(m.UpdatedAt)));
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_human_input_requests_roundtrip")]
    public async Task HumanInputRequestsRoundtrip()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var agent = new AgentDefinition(
            AgentId.New(), "Developer", AgentRole.Developer,
            new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        var goal = kernel.CreateGoal("Goal with human input");
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        kernel.RequestHumanInput(goal.Id, task.Id, "What should I do?");

        await repo.SaveAsync(kernel);
        var restored = await repo.LoadAsync();

        Assert.Equal(1, restored.HumanInputRequests.Count);
        Assert.Equal("What should I do?", restored.HumanInputRequests.Single().Question);
        Assert.Equal(goal.Id, restored.HumanInputRequests.Single().GoalId);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_human_input_upsert_updates_existing_row")]
    public async Task HumanInputUpsertUpdatesExistingRow()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var agent = new AgentDefinition(
            AgentId.New(), "Developer", AgentRole.Developer,
            new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        var goal = kernel.CreateGoal("Goal needing input");
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which option?");
        await repo.SaveAsync(kernel);

        // Answer it and re-save: the incremental upsert must UPDATE the existing row, not duplicate it.
        kernel.SubmitHumanInput(request.Id, "Option A");
        await repo.SaveAsync(kernel);

        var restored = await repo.LoadAsync();
        Assert.Equal(1, restored.HumanInputRequests.Count);
        var restoredRequest = restored.HumanInputRequests.Single();
        Assert.True(restoredRequest.IsCompleted);
        Assert.Equal("Option A", restoredRequest.Answer);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_stale_kernel_save_does_not_delete_human_input_request_not_in_snapshot")]
    public async Task StaleKernelSaveDoesNotDeleteHumanInputRequestNotInSnapshot()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var agent = new AgentDefinition(
            AgentId.New(), "Developer", AgentRole.Developer,
            new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);

        // Establish a goal with a pending human input request in the store.
        var kernelWith = new AgentOrchestratorKernel();
        var goal = kernelWith.CreateGoal("Goal needing input");
        kernelWith.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        kernelWith.RequestHumanInput(goal.Id, task.Id, "Which option?");
        await repo.SaveAsync(kernelWith);

        // Save a stale kernel that does not contain the human input request.
        // Must NOT delete the existing request row.
        var kernelStale = new AgentOrchestratorKernel();
        kernelStale.CreateGoal(goal.Id, "Goal needing input");
        await repo.SaveAsync(kernelStale);

        var restored = await repo.LoadAsync();
        Assert.Equal(1, restored.HumanInputRequests.Count);
        Assert.Equal("Which option?", restored.HumanInputRequests.Single().Question);
    }

    [Xunit.Fact(DisplayName = "FileOrchestratorStateRepository_lists_goal_metadata")]
    public async Task FileRepositoryListsGoalMetadata()
    {
        var dir = CreateTempDirectory();
        var path = Path.Combine(dir, "state.json");
        var repo = new FileOrchestratorStateRepository(path);
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal("First goal");
        kernel.CreateGoal("Second goal");
        await repo.SaveAsync(kernel);

        var listing = await repo.ListGoalMetadataAsync();

        Assert.Equal(2, listing.Count);
        Assert.True(listing.Any(m => m.Objective == "First goal"));
        Assert.True(listing.Any(m => m.Objective == "Second goal"));
        Assert.True(listing.All(m => m.Status == GoalStatus.Draft.ToString()));
    }

    private static string TempDb()
    {
        var dir = CreateTempDirectory();
        return Path.Combine(dir, "state.db");
    }
}
