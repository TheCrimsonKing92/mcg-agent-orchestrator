using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class SqliteOrchestratorStateRepositoryTests
{
    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_existing_schema_startup_skips_DDL")]
    public void ExistingSchemaStartupSkipsDdl()
    {
        var db = TempDb();
        _ = new SqliteOrchestratorStateRepository(db);

        var statements = new List<string>();
        _ = new SqliteOrchestratorStateRepository(db, statements.Add);

        Assert.DoesNotContain(statements, IsWriteCategoryStartupStatement);
        Assert.Contains(statements, s => s.StartsWith("PRAGMA busy_timeout", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_fresh_schema_creates_expected_catalog_objects")]
    public void FreshSchemaCreatesExpectedCatalogObjects()
    {
        var db = TempDb();
        _ = new SqliteOrchestratorStateRepository(db);

        using var conn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;");
        conn.Open();

        Xunit.Assert.Equal(
            ["goals", "human_input_requests", "meta", "model_fit_history"],
            QueryStrings(conn, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name"));
        Xunit.Assert.Equal(
            ["ix_model_fit_history_model", "ix_model_fit_history_role"],
            QueryStrings(conn, "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'ix_model_fit_history_%' ORDER BY name"));
        Xunit.Assert.Equal(
            ["id:TEXT:0", "status:TEXT:1", "objective:TEXT:1", "source_backlog_item_id:TEXT:0", "updated_at:TEXT:1", "snapshot_json:TEXT:1", "version:INTEGER:1"],
            QueryStrings(conn, "SELECT name || ':' || type || ':' || [notnull] FROM pragma_table_info('goals') ORDER BY cid"));
    }

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

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_roundtrips_acceptance_retry_context")]
    public async Task AcceptanceRetryContextRoundtripsThroughSqlite()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var agent = new AgentDefinition(
            AgentId.New(), "Developer", AgentRole.Developer,
            new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        var goal = kernel.CreateGoal("Retry acceptance failure");
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        var failingCheck = "SqliteOrchestratorStateRepositoryTests.Acceptance_retry_check_failed";
        var operatorFeedback = "Operator rejection: fix the SQLite acceptance retry context before reporting complete.";
        kernel.RecordAcceptanceFailure(goal.Id, [failingCheck]);
        kernel.RetryTask(goal.Id, task.Id, operatorFeedback);

        await repo.SaveAsync(kernel);
        var restored = await repo.LoadAsync();

        var restoredGoal = restored.Goals.Single();
        Assert.NotNull(restoredGoal.LatestAcceptanceFailure);
        Assert.Contains(failingCheck, restoredGoal.LatestAcceptanceFailure.FailedChecks);
        var restoredBrief = restored.BuildTaskBrief(goal.Id, task.Id).Content;
        Assert.Contains(failingCheck, restoredBrief);
        Assert.Contains(operatorFeedback, restoredBrief);
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

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_persists_model_fit_history_rows")]
    public async Task PersistsModelFitHistoryRows()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = RecordDispatchOutcome(
            kernel,
            AgentRole.Developer,
            "Anthropic",
            "claude-sonnet-4-6",
            TaskComplexity.Complex,
            exitCode: 0,
            "Model fit: Anthropic/claude-sonnet-4-6 - adequate - implementation - scoped edit");

        await repo.SaveAsync(kernel);

        var rows = await repo.ListModelFitHistoryAsync();
        var row = rows.Single();
        Assert.Equal(goal.Id.Value, row.GoalId);
        Assert.Equal(AgentRole.Developer, row.Role);
        Assert.Equal("Anthropic", row.ProviderName);
        Assert.Equal("claude-sonnet-4-6", row.ModelName);
        Assert.Equal(TaskComplexity.Complex, row.Complexity);
        Assert.Equal("implementation", row.TaskShape);
        Assert.Equal(WorkTaskStatus.Completed, row.Outcome);
        Assert.Equal(ModelFitHistory.Adequate, row.SelfRating);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_scorecard_reflects_model_fit_history_store")]
    public async Task ScorecardReflectsModelFitHistoryStore()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        RecordDispatchOutcome(kernel, AgentRole.Developer, "OpenAI", "gpt-5.5", TaskComplexity.Complex, 1,
            "Model fit: OpenAI/gpt-5.5 - adequate - implementation - failed despite fit");
        RecordDispatchOutcome(kernel, AgentRole.Developer, "OpenAI", "gpt-5.5", TaskComplexity.Complex, 1,
            "Model fit: OpenAI/gpt-5.5 - underpowered - implementation - missed repo context");
        RecordDispatchOutcome(kernel, AgentRole.Developer, "OpenAI", "gpt-5.5", TaskComplexity.Complex, 0,
            "Model fit: OpenAI/gpt-5.5 - adequate - implementation - completed");

        await repo.SaveAsync(kernel);

        var scorecard = await repo.BuildModelOutcomeScorecardAsync();
        var record = scorecard.Single(r => r.ProviderName == "OpenAI" && r.ModelName == "gpt-5.5");
        Assert.Equal(1, record.Completed);
        Assert.Equal(2, record.Failed);
        Assert.Equal(2, record.SelfRatedAdequate);
        Assert.Equal(1, record.SelfRatedUnderpowered);
        Assert.Equal(1, record.Divergence);
        Assert.Equal(ModelOutcomeRecommendation.Avoid, record.Recommendation);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_best_fit_for_role_prefers_non_underpowered_model")]
    public async Task BestFitForRolePrefersNonUnderpoweredModel()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        RecordDispatchOutcome(kernel, AgentRole.Planner, "Ollama", "qwen3:8b", TaskComplexity.Simple, 0,
            "Model fit: Ollama/qwen3:8b - underpowered - planning - too shallow");
        RecordDispatchOutcome(kernel, AgentRole.Planner, "Ollama", "qwen3:8b", TaskComplexity.Simple, 0,
            "Model fit: Ollama/qwen3:8b - underpowered - planning - too shallow");
        RecordDispatchOutcome(kernel, AgentRole.Planner, "Anthropic", "claude-sonnet-4-6", TaskComplexity.Complex, 0,
            "Model fit: Anthropic/claude-sonnet-4-6 - adequate - planning - good decomposition");
        RecordDispatchOutcome(kernel, AgentRole.Planner, "Anthropic", "claude-sonnet-4-6", TaskComplexity.Complex, 0,
            "Model fit: Anthropic/claude-sonnet-4-6 - adequate - planning - good decomposition");

        await repo.SaveAsync(kernel);

        var best = await repo.QueryBestFitForRoleAsync(AgentRole.Planner);
        Assert.True(best is not null);
        Assert.Equal("Anthropic", best!.ProviderName);
        Assert.Equal("claude-sonnet-4-6", best.ModelName);
        Assert.Equal(ModelOutcomeRecommendation.Prefer, best.Recommendation);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_idempotent_schema_migration_adds_version_column")]
    public void IdempotentSchemaMigrationAddsVersionColumn()
    {
        // Simulate a DB created by an old binary (no version column) by creating schema manually.
        var db = TempDb();
        using var setupConn = new SqliteConnection($"Data Source={db};Mode=ReadWriteCreate;Pooling=False;");
        setupConn.Open();
        // Microsoft.Data.Sqlite executes only one statement per ExecuteNonQuery; split each DDL.
        static void Exec(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        Exec(setupConn, "PRAGMA journal_mode=WAL");
        Exec(setupConn, "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        Exec(setupConn, "CREATE TABLE goals (id TEXT PRIMARY KEY, status TEXT NOT NULL, objective TEXT NOT NULL, source_backlog_item_id TEXT NULL, updated_at TEXT NOT NULL, snapshot_json TEXT NOT NULL)");
        Exec(setupConn, "CREATE TABLE human_input_requests (id TEXT PRIMARY KEY, goal_id TEXT NOT NULL, snapshot_json TEXT NOT NULL)");
        Exec(setupConn, "CREATE TABLE model_fit_history (goal_id TEXT NOT NULL, task_id TEXT NOT NULL, role TEXT NOT NULL, provider_name TEXT NOT NULL, model_name TEXT NOT NULL, complexity TEXT NULL, task_shape TEXT NULL, outcome TEXT NOT NULL, self_rating TEXT NOT NULL, timestamp TEXT NOT NULL, PRIMARY KEY (goal_id, task_id, timestamp))");
        Exec(setupConn, "CREATE INDEX ix_goals_source_backlog_item_id ON goals(source_backlog_item_id)");
        Exec(setupConn, "INSERT INTO meta (key, value) VALUES ('schema_version', '1')");
        setupConn.Close();

        // Opening the repo on this old-schema DB should add the version column without recreating tables.
        _ = new SqliteOrchestratorStateRepository(db);

        using var checkConn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;");
        checkConn.Open();
        var columns = QueryStrings(checkConn, "SELECT name FROM pragma_table_info('goals') ORDER BY cid");
        Assert.Contains("version", columns);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_LoadGoalAsync_returns_snapshot_for_existing_goal")]
    public async Task LoadGoalAsync_ReturnsSnapshotForExistingGoal()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Load target goal");
        await repo.SaveAsync(kernel);

        var snap = await repo.LoadGoalAsync(goal.Id);

        Assert.NotNull(snap);
        Assert.Equal(goal.Id.Value, snap.Id);
        Assert.Equal("Load target goal", snap.Objective);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_LoadGoalAsync_returns_null_for_missing_goal")]
    public async Task LoadGoalAsync_ReturnsNullForMissingGoal()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var snap = await repo.LoadGoalAsync(GoalId.New());
        Assert.Null(snap);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_TransactGoalAsync_writes_only_target_goal_row")]
    public async Task TransactGoalAsync_WritesOnlyTargetGoalRow()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goalA = kernel.CreateGoal("Goal A");
        var goalB = kernel.CreateGoal("Goal B - untouched");
        await repo.SaveAsync(kernel);

        // TransactGoalAsync produces a snapshot with an updated objective for goal A only.
        await repo.TransactGoalAsync<bool>(
            goalA.Id,
            (snap, ct) =>
            {
                // Return an updated snapshot with a sentinel in the timeline (not changing objective,
                // just verifying the write path works; we reuse the loaded snapshot unchanged here).
                return Task.FromResult((true, snap, true));
            });

        // Goal A was written (version should be 2 now - incremented by SaveAsync and TransactGoalAsync).
        // Goal B must remain at version 1 (only touched by the initial SaveAsync).
        using var conn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;");
        conn.Open();
        var versions = QueryStrings(conn,
            "SELECT id || ':' || version FROM goals ORDER BY objective");
        Assert.Equal(2, versions.Count);
        var vA = versions.Single(v => v.StartsWith(goalA.Id.Value));
        var vB = versions.Single(v => v.StartsWith(goalB.Id.Value));
        var versionA = int.Parse(vA.Split(':')[1]);
        var versionB = int.Parse(vB.Split(':')[1]);
        Assert.True(versionA > versionB, $"Goal A (v{versionA}) should have a higher version than goal B (v{versionB}) after TransactGoalAsync");
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_TransactGoalAsync_stale_version_retries_and_succeeds")]
    public async Task TransactGoalAsync_StaleVersionRetriesAndSucceeds()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("CAS retry goal");
        await repo.SaveAsync(kernel);

        var delegateCalls = 0;

        // Simulate a concurrent write that increments the version between the first load and CAS.
        // We do this by interleaving an extra SaveAsync on the first delegate call.
        await repo.TransactGoalAsync<bool>(
            goal.Id,
            async (snap, ct) =>
            {
                delegateCalls++;
                if (delegateCalls == 1)
                {
                    // Concurrent writer increments the version before our CAS write.
                    await repo.SaveAsync(kernel);
                }
                return (true, snap, true);
            });

        // Delegate was called at least twice: once with stale version, once after retry.
        Assert.True(delegateCalls >= 2, $"Expected retry on version mismatch, got {delegateCalls} delegate calls");
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_TransactGoalAsync_version_incremented_atomically")]
    public async Task TransactGoalAsync_VersionIncrementedAtomically()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Atomic version goal");
        await repo.SaveAsync(kernel);

        await repo.TransactGoalAsync<bool>(
            goal.Id,
            (snap, ct) => Task.FromResult((true, snap, true)));

        using var conn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM goals WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", goal.Id.Value);
        var version = Convert.ToInt32(cmd.ExecuteScalar());
        // Initial SaveAsync gives version=1, TransactGoalAsync should give version=2.
        Assert.Equal(2, version);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_TransactGoalAsync_disjoint_goals_do_not_block_each_other")]
    public async Task TransactGoalAsync_DisjointGoalsDoNotBlockEachOther()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goalA = kernel.CreateGoal("Concurrent goal A");
        var goalB = kernel.CreateGoal("Concurrent goal B");
        await repo.SaveAsync(kernel);

        var snapA = await repo.LoadGoalAsync(goalA.Id);
        var snapB = await repo.LoadGoalAsync(goalB.Id);

        // Simulate goal A holding a long mutate outside the write tx, while goal B does a short CAS.
        // Both use TransactGoalAsync which only holds BEGIN IMMEDIATE for the brief CAS write.
        var taskA = Task.Run(async () =>
        {
            await repo.TransactGoalAsync<bool>(
                goalA.Id,
                async (snap, ct) =>
                {
                    await Task.Delay(500, ct); // long work outside write tx
                    return (true, snap, true);
                });
        });

        // Goal B should complete its short CAS write DURING goal A's long mutate delay.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await repo.TransactGoalAsync<bool>(
            goalB.Id,
            (snap, ct) => Task.FromResult((true, snap, true)));
        sw.Stop();

        // Goal B's write should complete well before goal A's 500 ms delay ends.
        // Allow generous margin (250 ms) for test environment variance.
        Assert.True(sw.ElapsedMilliseconds < 250,
            $"Goal B's TransactGoalAsync took {sw.ElapsedMilliseconds}ms — should complete independently of goal A's long mutate");

        await taskA; // ensure A also completes
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_lists_goal_metadata")]
    public async Task SqliteRepositoryListsGoalMetadata()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
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

    private static bool IsWriteCategoryStartupStatement(string sql)
    {
        var trimmed = sql.TrimStart();
        return trimmed.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("INSERT ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("ALTER ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("DROP ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("PRAGMA journal_mode", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> QueryStrings(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var results = new List<string>();
        while (reader.Read())
            results.Add(reader.GetString(0));
        return results;
    }

    private static Goal RecordDispatchOutcome(
        AgentOrchestratorKernel kernel,
        AgentRole role,
        string provider,
        string model,
        TaskComplexity complexity,
        int exitCode,
        string modelFitNote)
    {
        var goal = kernel.CreateGoal($"Model fit {Guid.NewGuid():n}", [new TaskSpec(TaskId.New(), "task", role)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        var at = DateTimeOffset.UtcNow.AddTicks(kernel.Goals.Count);
        var dispatch = new TaskDispatchRecord(
            "worker-cli",
            $"worker {goal.Id.Value}",
            "C:\\work",
            at,
            provider,
            model,
            TaskComplexity: complexity);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            dispatch.Command,
            dispatch.WorkingDirectory,
            exitCode,
            modelFitNote,
            exitCode == 0 ? string.Empty : "failed",
            at.AddSeconds(1)));
        return goal;
    }
}
