using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

[Xunit.Collection(TestCollections.EnvMutation)]
public sealed class SqliteOrchestratorStateRepositoryTests
{
    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_existing_schema_startup_skips_DDL")]
    public void ExistingSchemaStartupSkipsDdl()
    {
        var db = TempDb();

        var statements = new List<string>();
        _ = new SqliteOrchestratorStateRepository(db, statements.Add);

        Assert.Empty(statements);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_constructor_does_not_create_or_migrate_database")]
    public void ConstructorDoesNotCreateOrMigrateDatabase()
    {
        var db = TempDb(migrate: false);

        _ = new SqliteOrchestratorStateRepository(db);

        Assert.False(File.Exists(db));
    }

    [Xunit.Fact]
    public async Task ReadOnlyLoadOfAbsentDatabaseFailsWithoutCreatingIt()
    {
        var db = TempDb(migrate: false);
        var repository = SqliteOrchestratorStateRepository.OpenReadOnly(db);

        await Assert.ThrowsAsync<SqliteException>(() =>
            repository.LoadGoalsAsync([GoalId.New()]));

        Assert.False(File.Exists(db));
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_read_only_open_loads_one_goal_without_writing")]
    public async Task ReadOnlyOpenLoadsOneGoalWithoutWriting()
    {
        var db = TempDb();
        var writable = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = RecordDispatchOutcome(
            kernel,
            AgentRole.Developer,
            "OpenAI",
            AgentCatalog.OpenAiSubscriptionModelAlias,
            TaskComplexity.Complex,
            exitCode: 0,
            "Model fit: OpenAI/gpt-5.5 - adequate - implementation - scoped edit"); // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
        await writable.SaveAsync(kernel);

        using (var stale = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;"))
        {
            stale.Open();
            ExecuteSql(stale, "UPDATE model_fit_history SET outcome_rule = NULL, outcome_class = 'unknown-era'");
        }

        using var observer = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;");
        observer.Open();
        var beforeDataVersion = Convert.ToInt64(Scalar(observer, "PRAGMA data_version"));

        var readOnly = SqliteOrchestratorStateRepository.OpenReadOnly(db);
        var loaded = await readOnly.LoadGoalsAsync([goal.Id]);

        Assert.Equal(goal.Id, Assert.Single(loaded.Goals).Id);
        Assert.Equal(beforeDataVersion, Convert.ToInt64(Scalar(observer, "PRAGMA data_version")));
        Assert.Equal("unknown-era", Scalar(observer, "SELECT outcome_class FROM model_fit_history"));
        await Assert.ThrowsAsync<SqliteException>(() => readOnly.SaveAsync(loaded));
    }

    [Xunit.Fact(DisplayName = "StateDbConnectionFactory_applies_standard_pragmas_to_every_profile")]
    public void StateDbConnectionFactoryAppliesStandardPragmasToEveryProfile()
    {
        var db = TempDb();
        _ = new SqliteOrchestratorStateRepository(db);

        using var readWrite = StateDbConnectionFactory.Open(db, StateDbConnectionProfile.ReadWrite);
        Assert.Equal("wal", Scalar(readWrite, "PRAGMA journal_mode")?.ToString(), ignoreCase: true);
        Assert.Equal(StateDbConnectionFactory.DefaultBusyTimeoutMilliseconds, Convert.ToInt32(Scalar(readWrite, "PRAGMA busy_timeout")));
        Assert.Equal(0, Convert.ToInt32(Scalar(readWrite, "PRAGMA query_only")));

        using var readOnly = StateDbConnectionFactory.Open(db, StateDbConnectionProfile.QueryOnlyRead);
        Assert.Equal("wal", Scalar(readOnly, "PRAGMA journal_mode")?.ToString(), ignoreCase: true);
        Assert.Equal(StateDbConnectionFactory.DefaultBusyTimeoutMilliseconds, Convert.ToInt32(Scalar(readOnly, "PRAGMA busy_timeout")));
        Assert.Equal(1, Convert.ToInt32(Scalar(readOnly, "PRAGMA query_only")));

        using var fastRead = StateDbConnectionFactory.Open(db, StateDbConnectionProfile.FastFailRead);
        Assert.Equal(StateDbConnectionFactory.FastFailBusyTimeoutMilliseconds, Convert.ToInt32(Scalar(fastRead, "PRAGMA busy_timeout")));
        Assert.Equal(1, Convert.ToInt32(Scalar(fastRead, "PRAGMA query_only")));
    }

    [Xunit.Fact(DisplayName = "StateDbConnectionFactory_existing_delete_database_is_promoted_to_WAL")]
    public void ExistingDeleteDatabaseIsPromotedToWal()
    {
        var db = TempDb();
        using (var connection = new SqliteConnection($"Data Source={db};Mode=ReadWriteCreate;Pooling=False;"))
        {
            connection.Open();
            ExecuteSql(connection, "PRAGMA journal_mode=DELETE");
        }

        var journalMode = StateDbMigrations.EnsureUpToDate(db);

        Assert.Equal("wal", journalMode, ignoreCase: true);
        Assert.Equal("wal", SqliteOrchestratorStateRepository.VerifyJournalMode(db), ignoreCase: true);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_ambient_write_tag_names_originating_verb")]
    public async Task AmbientWriteTagNamesOriginatingVerb()
    {
        var db = TempDb();
        var diagnosticsPath = DiagnosticsPath(db);
        var repository = new SqliteOrchestratorStateRepository(
            db,
            statementObserver: null,
            new SqliteWriteTelemetryOptions
            {
                DiagnosticsPath = diagnosticsPath,
                WarningHoldThreshold = TimeSpan.Zero,
                CriticalHoldThreshold = TimeSpan.FromMinutes(1),
                MirrorToConductEventStream = false
            });

        using (SqliteOrchestratorStateRepository.UseWriteOperationTag("cli:goal"))
        {
            await repository.SaveAsync("create", new AgentOrchestratorKernel());
        }

        var receipt = JsonNode.Parse(File.ReadAllLines(diagnosticsPath).Single())!.AsObject();
        Assert.Equal("cli:goal/create", receipt["operation"]?.GetValue<string>());
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_contended_write_uses_one_elapsed_budget")]
    public async Task ContendedWriteUsesOneElapsedBudget()
    {
        var db = TempDb();
        _ = new SqliteOrchestratorStateRepository(db);
        using var holder = StateDbConnectionFactory.Open(db, StateDbConnectionProfile.ReadWrite);
        ExecuteSql(holder, "BEGIN IMMEDIATE");
        var repository = new SqliteOrchestratorStateRepository(
            db,
            statementObserver: null,
            new SqliteWriteTelemetryOptions
            {
                BusyTimeoutMilliseconds = 200,
                BusyRetryBudget = TimeSpan.FromMilliseconds(250),
                MaxBusyRetries = int.MaxValue,
                MirrorToConductEventStream = false
            });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<SqliteException>(() =>
            repository.SaveAsync(new AgentOrchestratorKernel()));
        stopwatch.Stop();
        ExecuteSql(holder, "ROLLBACK");

        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(2));
    }

    [Xunit.Fact]
    public async Task TransientSqliteCheckpointControlledWriterRecoversWithExactRetryReceipts()
    {
        var db = TempDb();
        var diagnosticsPath = DiagnosticsPath(db);
        long elapsedMilliseconds = 0;
        SqliteConnection? holder = null;
        var holderReleased = false;
        var repository = new SqliteOrchestratorStateRepository(
            db,
            statementObserver: null,
            new SqliteWriteTelemetryOptions
            {
                DiagnosticsPath = diagnosticsPath,
                BusyTimeoutMilliseconds = 1,
                BusyRetryBudget = TimeSpan.FromMilliseconds(250),
                MaxBusyRetries = 3,
                MirrorToConductEventStream = false,
                MonotonicMilliseconds = () => elapsedMilliseconds,
                RetryDelay = (_, delay, _) =>
                {
                    elapsedMilliseconds += Math.Max(1, (long)Math.Ceiling(delay.TotalMilliseconds));
                    ExecuteSql(holder!, "ROLLBACK");
                    holderReleased = true;
                    return Task.CompletedTask;
                }
            });
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "controlled writer recovery");
        await repository.SaveAsync(kernel);
        var snapshot = kernel.ExportSnapshot().Goals.Single();
        holder = StateDbConnectionFactory.Open(db, StateDbConnectionProfile.ReadWrite);
        ExecuteSql(holder, "BEGIN IMMEDIATE");

        IReadOnlyList<GoalSnapshotCheckpointResult> results;
        using (SqliteOrchestratorStateRepository.UseWriteOperationTag("loop:tick"))
        {
            results = await repository.CheckpointGoalSnapshotsAsync([
                new GoalSnapshotSaveRequest(snapshot, snapshot)
            ]);
        }
        holder.Dispose();

        var result = Assert.Single(results);
        Assert.True(holderReleased);
        Assert.True(result.IsDurable);
        var receipts = File.ReadAllLines(diagnosticsPath)
            .Select(line => JsonNode.Parse(line)!.AsObject())
            .Where(receipt => receipt["eventType"]?.GetValue<string>() == "sqlite-state-write-lock")
            .ToArray();
        Assert.Equal(2, receipts.Length);
        Assert.Equal("retrying", receipts[0]["disposition"]?.GetValue<string>());
        Assert.Equal("recovered", receipts[1]["disposition"]?.GetValue<string>());
        Assert.All(receipts, receipt =>
        {
            Assert.Equal("state", receipt["store"]?.GetValue<string>());
            Assert.Equal(Path.GetFullPath(db), receipt["stateDbPath"]?.GetValue<string>());
            Assert.Contains("loop:tick/TransactGoalStateAsync", receipt["operation"]?.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal(5, receipt["sqliteErrorCode"]?.GetValue<int>());
            Assert.Equal(3, receipt["maxAttempts"]?.GetValue<int>());
            Assert.NotNull(receipt["attemptCount"]);
            Assert.NotNull(receipt["acquisitionWaitMs"]);
        });
        Assert.Equal(1, receipts[0]["attemptCount"]?.GetValue<int>());
        Assert.Equal(2, receipts[1]["attemptCount"]?.GetValue<int>());
    }

    [Xunit.Fact]
    public async Task TransientSqliteCheckpointRetryExhaustionReturnsTypedHold()
    {
        var db = TempDb();
        long elapsedMilliseconds = 0;
        var repository = new SqliteOrchestratorStateRepository(
            db,
            statementObserver: null,
            new SqliteWriteTelemetryOptions
            {
                BusyTimeoutMilliseconds = 1,
                BusyRetryBudget = TimeSpan.FromMilliseconds(200),
                MaxBusyRetries = 3,
                MirrorToConductEventStream = false,
                MonotonicMilliseconds = () => elapsedMilliseconds,
                RetryDelay = (_, _, _) =>
                {
                    elapsedMilliseconds = 200;
                    return Task.CompletedTask;
                }
            });
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "controlled writer exhaustion");
        await repository.SaveAsync(kernel);
        var snapshot = kernel.ExportSnapshot().Goals.Single();
        using var holder = StateDbConnectionFactory.Open(db, StateDbConnectionProfile.ReadWrite);
        ExecuteSql(holder, "BEGIN IMMEDIATE");

        IReadOnlyList<GoalSnapshotCheckpointResult> results;
        using (SqliteOrchestratorStateRepository.UseWriteOperationTag("loop:tick"))
        {
            results = await repository.CheckpointGoalSnapshotsAsync([
                new GoalSnapshotSaveRequest(snapshot, snapshot)
            ]);
        }
        ExecuteSql(holder, "ROLLBACK");

        var held = Assert.Single(results);
        Assert.Equal(GoalSnapshotCheckpointDisposition.Held, held.Disposition);
        Assert.Equal(goal.Id.Value, held.GoalId);
        Assert.Equal("state", held.Store);
        Assert.Equal(Path.GetFullPath(db), held.DatabasePath);
        Assert.Contains("loop:tick/TransactGoalStateAsync", held.Operation, StringComparison.Ordinal);
        Assert.Equal(5, held.SqliteErrorCode);
        Assert.Equal(2, held.AttemptCount);
        Assert.Equal(200, held.ElapsedMilliseconds);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_fresh_schema_creates_expected_catalog_objects")]
    public void FreshSchemaCreatesExpectedCatalogObjects()
    {
        var db = TempDb();
        _ = new SqliteOrchestratorStateRepository(db);

        using var conn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;");
        conn.Open();

        Xunit.Assert.Equal(
            [
                "backlog_intake_records",
                "engineering_practices",
                "goal_intake_requests",
                "goal_replacement_audit",
                "goal_replacement_lineage",
                "goals",
                "human_input_requests",
                "meta",
                "model_fit_history",
                "schema_migrations",
                "source_backlog_claims",
                "spawn_registry",
                "sqlite_sequence",
                "state_outbox",
                "worktree_cleanup_backoff",
                "worktree_cleanup_journal"
            ],
            QueryStrings(conn, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name"));
        Xunit.Assert.Equal(
            ["ix_engineering_practices_enabled_priority", "ix_goals_status", "ix_model_fit_history_model", "ix_model_fit_history_outcome_class", "ix_model_fit_history_role", "ix_state_outbox_kind"],
            QueryStrings(conn, "SELECT name FROM sqlite_master WHERE type = 'index' AND (name = 'ix_goals_status' OR name LIKE 'ix_model_fit_history_%' OR name = 'ix_engineering_practices_enabled_priority' OR name = 'ix_state_outbox_kind') ORDER BY name"));
        Xunit.Assert.Equal(
            ["id:TEXT:0", "status:TEXT:1", "objective:TEXT:1", "source_backlog_item_id:TEXT:0", "updated_at:TEXT:1", "snapshot_json:TEXT:1", "version:INTEGER:1"],
            QueryStrings(conn, "SELECT name || ':' || type || ':' || [notnull] FROM pragma_table_info('goals') ORDER BY cid"));
        Xunit.Assert.Equal(
            ["id", "kind", "payload_json", "created_at", "quarantined_at", "quarantine_reason", "processing_token", "processing_started_at"],
            QueryStrings(conn, "SELECT name FROM pragma_table_info('state_outbox') ORDER BY cid"));
        Xunit.Assert.Equal(
            Enumerable.Range(1, 12).Select(number => number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            QueryStrings(conn, "SELECT CAST(migration_number AS TEXT) FROM schema_migrations ORDER BY migration_number"));
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

    [Xunit.Fact(DisplayName = "GoalSliceBatchParent_SnapshotRoundTrip")]
    public async Task GoalSliceBatchParentSnapshotRoundTrip()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var kernel = new AgentOrchestratorKernel();
        var parent = kernel.CreateGoal("Slice-batch parent");
        var child = kernel.CreateGoal(
            "Slice-batch child",
            [new TaskSpec(TaskId.New(), "Implement child", AgentRole.Developer)],
            parent.Id);

        await repo.SaveAsync(kernel);
        var restored = await repo.LoadAsync();

        Assert.Equal(parent.Id, restored.GetGoal(child.Id).SliceBatchParentId);
        Assert.Null(restored.GetGoal(parent.Id).SliceBatchParentId);
    }

    [Xunit.Fact]
    public async Task SnapshotAggregateViolationRoundTripsThroughSqlite()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var kernel = new AgentOrchestratorKernel();
        var reviewerAgent = new AgentDefinition(
            AgentId.New(),
            "Reviewer",
            AgentRole.Reviewer,
            new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        var reviewerTask = new TaskSpec(TaskId.New(), "Review aggregate identity receipt", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Persist aggregate identity receipt", [reviewerTask]);
        kernel.ActivateGoal(goal.Id, [reviewerAgent]);
        var firstLocation = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var secondLocation = new ReviewFindingLocation("src/B.cs", "B.Run", "guard");
        var mismatches = new[]
        {
            new ReviewFindingIdentityMismatch(
                ReviewFindingConvergence.IdentityMovedViolationCode,
                "First moved.",
                "F-01",
                "F-01",
                firstLocation,
                new ReviewFindingLocation("src/Moved.cs", "Moved.Run", "guard")),
            new ReviewFindingIdentityMismatch(
                ReviewFindingConvergence.RecycledAnchorIdentityViolationCode,
                "Second recycled.",
                "F-02",
                "F-NEW",
                secondLocation,
                secondLocation)
        };
        kernel.RecordTaskVerification(goal.Id, reviewerTask.Id, new TaskVerificationRecord(
            "review",
            @"C:\repo",
            1,
            "invalid round",
            string.Empty,
            DateTimeOffset.Parse("2026-08-05T12:00:00Z"),
            ReviewFindingContractViolation: new ReviewFindingContractViolation(
                mismatches[0].Code,
                mismatches[0].Message,
                mismatches[0].PriorStableId,
                mismatches[0].SubmittedStableId,
                mismatches[0].PriorLocation,
                mismatches[0].SubmittedLocation,
                mismatches)));

        await repo.SaveAsync(kernel);
        var restored = await repo.LoadAsync();

        var restoredViolation = restored
            .GetTask(goal.Id, reviewerTask.Id)
            .LastVerification!
            .ReviewFindingContractViolation;
        var restoredMismatches = Assert.IsAssignableFrom<IReadOnlyList<ReviewFindingIdentityMismatch>>(
            restoredViolation!.IdentityMismatches);
        Assert.Equal(2, restoredMismatches.Count);
        Assert.Equal(["F-01", "F-02"], restoredMismatches.Select(mismatch => mismatch.PriorStableId));
        Assert.Equal("F-NEW", restoredMismatches[1].SubmittedStableId);
        Assert.Equal(secondLocation, restoredMismatches[1].SubmittedLocation);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_fast_write_produces_no_write_telemetry_receipt")]
    public async Task FastWriteProducesNoWriteTelemetryReceipt()
    {
        var db = TempDb();
        var diagnosticsPath = DiagnosticsPath(db);
        var repo = new SqliteOrchestratorStateRepository(
            db,
            statementObserver: null,
            new SqliteWriteTelemetryOptions
            {
                DiagnosticsPath = diagnosticsPath,
                MirrorToConductEventStream = false
            });

        await repo.SaveAsync(new AgentOrchestratorKernel());

        Assert.False(File.Exists(diagnosticsPath), File.Exists(diagnosticsPath) ? File.ReadAllText(diagnosticsPath) : diagnosticsPath);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_slow_write_and_blocked_writer_emit_jsonl_receipts")]
    public async Task SlowWriteAndBlockedWriterEmitJsonlReceipts()
    {
        var db = TempDb();
        var diagnosticsPath = DiagnosticsPath(db);
        var telemetryOptions = new SqliteWriteTelemetryOptions
        {
            DiagnosticsPath = diagnosticsPath,
            BusyTimeoutMilliseconds = 100,
            BusyRetryBudget = TimeSpan.FromMilliseconds(250),
            MaxBusyRetries = 1,
            BeginImmediateCommandTimeoutSeconds = 1,
            MirrorToConductEventStream = false
        };
        ITransactionalOrchestratorStateRepository holderRepo =
            new SqliteOrchestratorStateRepository(db, statementObserver: null, telemetryOptions);
        ITransactionalOrchestratorStateRepository blockedRepo =
            new SqliteOrchestratorStateRepository(db, statementObserver: null, telemetryOptions);
        await holderRepo.SaveAsync(new AgentOrchestratorKernel());

        var holderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holderTask = holderRepo.TransactAsync(
            "slow-holder-test",
            async (kernel, _) =>
            {
                holderStarted.SetResult();
                await Task.Delay(TimeSpan.FromSeconds(3));
                return (ShouldSave: false, Result: kernel.Goals.Count);
            });

        await holderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await holderTask;

        using (var holder = StateDbConnectionFactory.Open(db, StateDbConnectionProfile.ReadWrite))
        {
            ExecuteSql(holder, "BEGIN IMMEDIATE");
            await Assert.ThrowsAsync<SqliteException>(() =>
                blockedRepo.TransactAsync(
                    "blocked-writer-test",
                    (kernel, _) => Task.FromResult((ShouldSave: true, Result: kernel.Goals.Count))));
            ExecuteSql(holder, "ROLLBACK");
        }

        var receipts = File.ReadAllLines(diagnosticsPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonNode.Parse(line)!.AsObject())
            .ToList();

        Assert.All(receipts, receipt => Assert.NotNull(receipt["eventType"]?.GetValue<string>()));

        var criticalHold = Assert.Single(receipts, receipt =>
            receipt["eventType"]?.GetValue<string>() == "sqlite-state-write-hold");
        Assert.Equal("critical", criticalHold["severity"]?.GetValue<string>());
        Assert.Equal("slow-holder-test", criticalHold["operation"]?.GetValue<string>());
        Assert.Equal("commit", criticalHold["disposition"]?.GetValue<string>());
        Assert.True(criticalHold["holdMs"]?.GetValue<double>() >= 2_000, criticalHold.ToJsonString());
        Assert.NotEmpty(criticalHold["stackSummary"]!.AsArray());

        var busyFailure = Assert.Single(receipts, receipt =>
            receipt["eventType"]?.GetValue<string>() == "sqlite-state-write-busy-failure");
        Assert.Equal("critical", busyFailure["severity"]?.GetValue<string>());
        Assert.Equal("blocked-writer-test", busyFailure["operation"]?.GetValue<string>());
        Assert.Equal("busy-failure", busyFailure["disposition"]?.GetValue<string>());
        Assert.True(busyFailure["acquisitionWaitMs"]?.GetValue<double>() >= 100, busyFailure.ToJsonString());
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

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_list_metadata_surfaces_active_failed_condition")]
    public async Task ListMetadataSurfacesActiveFailedCondition()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Find failed work from the aggregate view",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, goal.Tasks.Single().Id, WorkTaskStatus.Failed, "failed");
        await repo.SaveAsync(kernel);

        var summary = Assert.Single(await repo.ListGoalMetadataAsync());
        var conductSummary = Assert.Single(await repo.ListConductLoopGoalMetadataAsync());

        Assert.Equal(GoalStatus.Active.ToString(), summary.Status);
        Assert.Equal(GoalLifecycle.ActiveWithFailedTaskCondition, summary.Condition);
        Assert.Equal(GoalLifecycle.ActiveWithFailedTaskCondition, conductSummary.Condition);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_load_goals_filters_before_snapshot_deserialization")]
    public async Task LoadGoalsFiltersBeforeSnapshotDeserialization()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var completed = kernel.CreateGoal("Completed audit goal", [new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)]);
        var active = kernel.CreateGoal("Active conductor goal");
        var failed = kernel.CreateGoal("Failed goal still needs operator/conductor attention", [new TaskSpec(TaskId.New(), "Failed", AgentRole.Developer)]);
        kernel.ActivateGoal(completed.Id, AgentCatalog.Default().Agents);
        kernel.ActivateGoal(active.Id, AgentCatalog.Default().Agents);
        kernel.ActivateGoal(failed.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskVerification(completed.Id, completed.Tasks.Single().Id,
            new TaskVerificationRecord("manual", "C:\\tmp", 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        kernel.ReportTaskProgress(failed.Id, failed.Tasks.Single().Id, WorkTaskStatus.Failed, "failed");
        await repo.SaveAsync(kernel);

        using (var conn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE goals SET snapshot_json = '{not valid json' WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", completed.Id.Value);
            cmd.ExecuteNonQuery();
        }

        var nonTerminal = await repo.LoadGoalsAsync([active.Id, failed.Id]);

        Assert.DoesNotContain(nonTerminal.Goals, goal => goal.Id == completed.Id);
        Assert.Contains(nonTerminal.Goals, goal => goal.Id == active.Id);
        Assert.Contains(nonTerminal.Goals, goal => goal.Id == failed.Id);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_LoadGoalsAsync_loads_requested_goal_human_input_only")]
    public async Task LoadGoalsAsyncLoadsRequestedGoalHumanInputOnly()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal("Target waiting goal", [new TaskSpec(TaskId.New(), "Target task", AgentRole.Developer)]);
        var other = kernel.CreateGoal("Other waiting goal", [new TaskSpec(TaskId.New(), "Other task", AgentRole.Developer)]);
        var agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(target.Id, agents);
        kernel.ActivateGoal(other.Id, agents);
        var targetRequest = kernel.RequestHumanInput(target.Id, target.Tasks.Single().Id, "Target input?");
        var otherRequest = kernel.RequestHumanInput(other.Id, other.Tasks.Single().Id, "Other input?");
        await repo.SaveAsync(kernel);

        var loaded = await repo.LoadGoalsAsync([target.Id]);

        Assert.Single(loaded.Goals);
        Assert.Equal(target.Id, loaded.Goals.Single().Id);
        var request = Assert.Single(loaded.HumanInputRequests);
        Assert.Equal(targetRequest.Id, request.Id);
        Assert.DoesNotContain(loaded.HumanInputRequests, item => item.Id == otherRequest.Id);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_lists_completed_human_input_goal_ids_without_goal_load")]
    public async Task ListGoalIdsWithCompletedHumanInputAsyncReturnsCompletedGoalIdsOnly()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal("Target answered goal", [new TaskSpec(TaskId.New(), "Target task", AgentRole.Developer)]);
        var other = kernel.CreateGoal("Other waiting goal", [new TaskSpec(TaskId.New(), "Other task", AgentRole.Developer)]);
        var agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(target.Id, agents);
        kernel.ActivateGoal(other.Id, agents);
        var targetRequest = kernel.RequestHumanInput(target.Id, target.Tasks.Single().Id, "Target input?");
        kernel.RequestHumanInput(other.Id, other.Tasks.Single().Id, "Other input?");
        kernel.SubmitHumanInput(targetRequest.Id, "Use option A.");
        await repo.SaveAsync(kernel);

        var completed = await repo.ListGoalIdsWithCompletedHumanInputAsync([target.Id, other.Id]);

        var id = Assert.Single(completed);
        Assert.Equal(target.Id, id);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_ignores_synthetic_parked_human_input_completions")]
    public async Task ListGoalIdsWithCompletedHumanInputAsyncIgnoresSyntheticParkedCompletions()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal("Synthetic parked completion", [new TaskSpec(TaskId.New(), "Target task", AgentRole.Developer)]);
        kernel.ActivateGoal(target.Id, AgentCatalog.Default().Agents);
        kernel.RequestHumanInput(target.Id, target.Tasks.Single().Id, "Need operator decision.");
        kernel.ParkGoal(target.Id, "waiting for operator answer");
        await repo.SaveAsync(kernel);

        var completed = await repo.ListGoalIdsWithCompletedHumanInputAsync([target.Id]);

        Assert.Empty(completed);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_LoadAsync_quarantines_malformed_goal_row_and_loads_remaining_state")]
    public async Task LoadAsyncQuarantinesMalformedGoalRowAndLoadsRemainingState()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var good = kernel.CreateGoal("Good goal survives");
        var bad = kernel.CreateGoal("Bad goal is quarantined");
        await repo.SaveAsync(kernel);

        using (var conn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;"))
        {
            conn.Open();
            using var read = conn.CreateCommand();
            read.CommandText = "SELECT snapshot_json FROM goals WHERE id = $id";
            read.Parameters.AddWithValue("$id", bad.Id.Value);
            var json = (string)read.ExecuteScalar()!;
            var snapshot = JsonNode.Parse(json)!;
            snapshot["Status"] = "Blocked";

            using var update = conn.CreateCommand();
            update.CommandText = "UPDATE goals SET status = 'Blocked', snapshot_json = $json WHERE id = $id";
            update.Parameters.AddWithValue("$json", snapshot.ToJsonString());
            update.Parameters.AddWithValue("$id", bad.Id.Value);
            update.ExecuteNonQuery();
        }

        AgentOrchestratorKernel restored = null!;
        var errorText = await AsyncLocalConsoleRouter.CaptureErrorAsync(async () =>
        {
            restored = await repo.LoadAsync();
        });

        Assert.Contains(restored.Goals, goal => goal.Id == good.Id);
        Assert.DoesNotContain(restored.Goals, goal => goal.Id == bad.Id);
        var quarantined = await repo.ListQuarantinedGoalRowsAsync();
        var row = Assert.Single(quarantined);
        Assert.Equal(bad.Id.Value, row.Id);
        Assert.Contains("JsonException", row.Error);
        Assert.Contains($"QUARANTINED goal {bad.Id.Value[..8]}: JsonException", errorText);

        var diagnostics = RunSqliteTool(
            "diagnostics",
            "--db",
            db);
        Assert.Equal(0, diagnostics.ExitCode);
        Assert.Contains($"QUARANTINED {bad.Id.Value[..8]}", diagnostics.Output);
    }

    [Xunit.Fact(DisplayName = "OrchestratorSqliteTools_status_help_matches_Core_GoalStatus")]
    public void OrchestratorSqliteToolsStatusHelpMatchesCoreGoalStatus()
    {
        var result = RunSqliteTool(
            "set-goal-status",
            "--help");

        Assert.Equal(0, result.ExitCode);
        foreach (var status in Enum.GetNames<GoalStatus>())
            Assert.Contains(status, result.Output);
        Assert.DoesNotContain("Blocked", result.Output);
        Assert.DoesNotContain("Proposed", result.Output);

        var rejected = RunSqliteTool(
            "set-goal-status",
            "--status",
            "Blocked",
            "abc12345");
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains("Unsupported goal status 'Blocked'", rejected.Output);
    }

    [Xunit.Fact(DisplayName = "Program_startup_error_formatter_prints_exception_type_first")]
    public void ProgramStartupErrorFormatterPrintsExceptionTypeFirst()
    {
        var formatted = ProgramStartupErrorFormatter.Format(new InvalidOperationException("outer wrapper"));

        Assert.StartsWith("InvalidOperationException: outer wrapper", formatted, StringComparison.Ordinal);
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
        var creation = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "What should I do?",
            HumanWaitKind.ProviderAuth,
            suggestedDefaultAnswer: "unused",
            resumeCommand: "provider auth resume",
            blockerFingerprint: "stable-worker-result");
        var request = creation.Request;
        kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "  WHAT should I do? ",
            HumanWaitKind.ProviderAuth,
            blockerFingerprint: "stable-worker-result");

        await repo.SaveAsync(kernel);
        var restored = await repo.LoadAsync();

        Assert.Equal(1, restored.HumanInputRequests.Count);
        var restoredRequest = restored.HumanInputRequests.Single();
        Assert.Equal(request.Id, restoredRequest.Id);
        Assert.Equal("What should I do?", restoredRequest.Question);
        Assert.Equal(goal.Id, restoredRequest.GoalId);
        Assert.Equal(HumanWaitKind.ProviderAuth, restoredRequest.Kind);
        Assert.True(restoredRequest.IsExternallyBlocked);
        Assert.False(restoredRequest.IsAutoDefaultable);
        Assert.False(restoredRequest.IsDismissible);
        Assert.Equal("provider auth resume", restoredRequest.ResumeCommand);
        Assert.Equal(HumanInputRequest.BuildQuestionFingerprint("What should I do?"), restoredRequest.QuestionFingerprint);
        Assert.Equal("stable-worker-result", restoredRequest.BlockerFingerprint);
        Assert.Equal(1, restoredRequest.SuppressionCount);
    }

    [Xunit.Fact]
    public async Task TickMerge_supersede_reset_beats_stale_answered_suppression_count()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Preserve a newer suppression reset", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        const string fingerprint = "unchanged-blocker";
        var request = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Expand scope?",
            blockerFingerprint: fingerprint).Request;
        kernel.SubmitHumanInput(request.Id, "Authorized.");
        kernel.RequestHumanInputDeduplicated(goal.Id, task.Id, "Expand scope?", blockerFingerprint: fingerprint);
        kernel.RequestHumanInputDeduplicated(goal.Id, task.Id, "Expand scope?", blockerFingerprint: fingerprint);
        await repo.SaveAsync(kernel);

        var staleKernel = await repo.LoadAsync();
        var staleBaseline = staleKernel.ExportSnapshot();
        var operatorKernel = await repo.LoadAsync();
        operatorKernel.SupersedeHumanInput(goal.Id, request.Id, "Authorized with correction.", HumanInputAnswerOrigin.Operator);
        await repo.SaveAsync(operatorKernel);

        staleKernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Expand scope?",
            blockerFingerprint: fingerprint);
        var staleCurrent = staleKernel.ExportSnapshot();
        await repo.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(
                staleBaseline.Goals.Single(),
                staleCurrent.Goals.Single(),
                staleCurrent.HumanInputRequests)]);

        var restored = await repo.LoadAsync();
        var restoredRequest = restored.GetHumanInputRequest(request.Id);
        Assert.Equal("Authorized with correction.", restoredRequest.Answer);
        Assert.Equal(0, restoredRequest.SuppressionCount);
        Assert.Equal(2, restoredRequest.SuppressionAnswerRevision);
    }

    [Xunit.Fact]
    public async Task TickMerge_persists_answered_suppression_count_and_threshold_failure()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Persist answered duplicate suppression", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var request = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Expand scope?",
            blockerFingerprint: "unchanged-blocker").Request;
        kernel.SubmitHumanInput(request.Id, "Authorized.");
        await repo.SaveAsync(kernel);

        for (var expectedCount = 1; expectedCount <= 3; expectedCount++)
        {
            var baselineKernel = await repo.LoadAsync();
            var baseline = baselineKernel.ExportSnapshot();
            var tickKernel = AgentOrchestratorKernel.FromSnapshot(baseline);
            tickKernel.RequestHumanInputDeduplicated(
                goal.Id,
                task.Id,
                "Expand scope?",
                blockerFingerprint: "unchanged-blocker");
            var current = tickKernel.ExportSnapshot();

            await repo.SaveGoalSnapshotsWithMergeAsync(
                [new GoalSnapshotSaveRequest(
                    baseline.Goals.Single(),
                    current.Goals.Single(),
                    current.HumanInputRequests)]);

            var restored = await repo.LoadAsync();
            Assert.Equal(expectedCount, restored.GetHumanInputRequest(request.Id).SuppressionCount);
        }

        var final = await repo.LoadAsync();
        Assert.Equal(WorkTaskStatus.Failed, final.GetTask(goal.Id, task.Id).Status);
        Assert.Single(final.GetGoal(goal.Id).Timeline.Where(item =>
            item.Kind == ProgressKind.TaskFailed &&
            item.Message.Contains("suppression threshold 3 reached", StringComparison.Ordinal)));
    }

    [Xunit.Fact]
    public async Task TickMerge_stale_open_request_preserves_answer_without_reviving_pre_answer_count()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Preserve an out-of-band answer", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var request = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Expand scope?",
            blockerFingerprint: "unchanged-blocker").Request;
        await repo.SaveAsync(kernel);

        var staleKernel = await repo.LoadAsync();
        var staleBaseline = staleKernel.ExportSnapshot();
        var operatorKernel = await repo.LoadAsync();
        operatorKernel.SubmitHumanInput(request.Id, "Authorized.");
        await repo.SaveAsync(operatorKernel);

        staleKernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Expand scope?",
            blockerFingerprint: "unchanged-blocker");
        var staleCurrent = staleKernel.ExportSnapshot();
        await repo.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(
                staleBaseline.Goals.Single(),
                staleCurrent.Goals.Single(),
                staleCurrent.HumanInputRequests)]);

        var restored = await repo.LoadAsync();
        var restoredRequest = restored.GetHumanInputRequest(request.Id);
        Assert.True(restoredRequest.IsCompleted);
        Assert.Equal("Authorized.", restoredRequest.Answer);
        Assert.Equal(0, restoredRequest.SuppressionCount);
        Assert.Equal(1, restoredRequest.SuppressionAnswerRevision);
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
        Assert.Equal(TaskOutcomeClass.Success, row.OutcomeClass);
        Assert.Equal("worker-cli", row.DispatchLane);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_persists_producer_outcome_class_for_apparatus_failure")]
    public async Task PersistsProducerOutcomeClassForApparatusFailure()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = RecordDispatchOutcome(
            kernel,
            AgentRole.Developer,
            "OpenAI",
            AgentCatalog.OpenAiSubscriptionModelAlias,
            TaskComplexity.Complex,
            exitCode: 1,
            "Model fit: OpenAI/gpt-5.5 - adequate - implementation - provider failed"); // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
        var task = goal.Tasks.Single();
        kernel.RecordTaskNote(
            goal.Id,
            task.Id,
            "CLASSIFIER rule=silent-launch-failure; outcome_class=environmental; verdict=LaunchFailure");

        await repo.SaveAsync(kernel);

        var row = Assert.Single(await repo.ListModelFitHistoryAsync());
        Assert.Equal("silent-launch-failure", row.OutcomeRule);
        Assert.Equal(TaskOutcomeClass.Environmental, row.OutcomeClass);
        Assert.NotEqual(TaskOutcomeClass.RealFailure, row.OutcomeClass);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_TransactGoalAsync_updates_model_fit_history_rows")]
    public async Task TransactGoalAsyncUpdatesModelFitHistoryRows()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "task", AgentRole.Developer);
        var goal = kernel.CreateGoal("Goal-scoped model fit", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        await repo.SaveAsync(kernel);

        await repo.TransactGoalAsync<bool>(
            goal.Id,
            (snapshot, _) =>
            {
                Assert.NotNull(snapshot);
                var transactionKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot!], []));
                var dispatch = new TaskDispatchRecord(
                    "worker-cli",
                    "worker",
                    "C:\\work",
                    DateTimeOffset.Parse("2026-07-01T12:00:00Z"),
                    "OpenAI",
                    AgentCatalog.OpenAiSubscriptionModelAlias,
                    TaskComplexity: TaskComplexity.Complex);
                transactionKernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
                transactionKernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
                    dispatch.Command,
                    dispatch.WorkingDirectory,
                    0,
                    "Model fit: OpenAI/gpt-5.5 - adequate - implementation - scoped edit", // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
                    string.Empty,
                    DateTimeOffset.Parse("2026-07-01T12:00:01Z")));
                var updated = transactionKernel.ExportSnapshot().Goals.Single(updatedGoal => updatedGoal.Id == goal.Id.Value);
                return Task.FromResult((true, (GoalSnapshot?)updated, true));
            });

        var row = Assert.Single(await repo.ListModelFitHistoryAsync());
        Assert.Equal(goal.Id.Value, row.GoalId);
        Assert.Equal(task.Id.Value, row.TaskId);
        Assert.Equal("OpenAI", row.ProviderName);
        Assert.Equal(AgentCatalog.OpenAiSubscriptionModelAlias, row.ModelName);
        Assert.Equal(ModelFitHistory.Adequate, row.SelfRating);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_scorecard_reflects_model_fit_history_store")]
    public async Task ScorecardReflectsModelFitHistoryStore()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        RecordDispatchOutcome(kernel, AgentRole.Developer, "OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, TaskComplexity.Complex, 1,
            "Model fit: OpenAI/gpt-5.5 - adequate - implementation - failed despite fit"); // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
        RecordDispatchOutcome(kernel, AgentRole.Developer, "OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, TaskComplexity.Complex, 1,
            "Model fit: OpenAI/gpt-5.5 - underpowered - implementation - missed repo context"); // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
        RecordDispatchOutcome(kernel, AgentRole.Developer, "OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, TaskComplexity.Complex, 0,
            "Model fit: OpenAI/gpt-5.5 - adequate - implementation - completed"); // Deliberate fixture text pins historical/parser behavior independently of the live catalog.

        await repo.SaveAsync(kernel);

        var scorecard = await repo.BuildModelOutcomeScorecardAsync();
        var record = scorecard.Single(r => r.ProviderName == "OpenAI" && r.ModelName == AgentCatalog.OpenAiSubscriptionModelAlias);
        Assert.Equal("worker-cli", record.DispatchLane);
        Assert.Equal(1, record.Completed);
        Assert.Equal(2, record.Failed);
        Assert.Equal(2, record.SelfRatedAdequate);
        Assert.Equal(1, record.SelfRatedUnderpowered);
        Assert.Equal(0, record.RealFailures);
        Assert.Equal(2, record.UnknownEraFailures);
        Assert.Equal(0, record.Divergence);
        Assert.Equal(ModelOutcomeRecommendation.Neutral, record.Recommendation);
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

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_idempotent_schema_migration_adds_state_and_outbox_columns")]
    public void IdempotentSchemaMigrationAddsVersionAndOutcomeColumns()
    {
        // Simulate a DB created by an old binary (no version column) by creating schema manually.
        var db = TempDb(migrate: false);
        using var setupConn = new SqliteConnection($"Data Source={db};Mode=ReadWriteCreate;Pooling=False;");
        setupConn.Open();
        // Microsoft.Data.Sqlite executes only one statement per ExecuteNonQuery; split each DDL.
        static void Exec(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        Exec(setupConn, "PRAGMA journal_mode=WAL");
        Exec(setupConn, "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        Exec(setupConn, "CREATE TABLE goals (id TEXT PRIMARY KEY, status TEXT NOT NULL, objective TEXT NOT NULL, source_backlog_item_id TEXT NULL, updated_at TEXT NOT NULL, snapshot_json TEXT NOT NULL)");
        Exec(setupConn, "CREATE TABLE human_input_requests (id TEXT PRIMARY KEY, goal_id TEXT NOT NULL, snapshot_json TEXT NOT NULL)");
        Exec(setupConn, "CREATE TABLE model_fit_history (goal_id TEXT NOT NULL, task_id TEXT NOT NULL, role TEXT NOT NULL, provider_name TEXT NOT NULL, model_name TEXT NOT NULL, complexity TEXT NULL, task_shape TEXT NULL, outcome TEXT NOT NULL, self_rating TEXT NOT NULL, timestamp TEXT NOT NULL, PRIMARY KEY (goal_id, task_id, timestamp))");
        Exec(setupConn, "CREATE TABLE state_outbox (id TEXT PRIMARY KEY, kind TEXT NOT NULL, payload_json TEXT NOT NULL, created_at TEXT NOT NULL)");
        Exec(setupConn, "CREATE INDEX ix_state_outbox_kind ON state_outbox(kind)");
        Exec(setupConn, "CREATE INDEX ix_goals_source_backlog_item_id ON goals(source_backlog_item_id)");
        Exec(setupConn, "INSERT INTO meta (key, value) VALUES ('schema_version', '1')");
        setupConn.Close();

        // The explicit authority path upgrades the old schema without constructor writes.
        _ = StateDbMigrations.EnsureUpToDate(db);

        using var checkConn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;");
        checkConn.Open();
        var columns = QueryStrings(checkConn, "SELECT name FROM pragma_table_info('goals') ORDER BY cid");
        Assert.Contains("version", columns);
        var historyColumns = QueryStrings(checkConn, "SELECT name FROM pragma_table_info('model_fit_history') ORDER BY cid");
        Assert.Contains("outcome_rule", historyColumns);
        Assert.Contains("outcome_class", historyColumns);
        Assert.Contains("dispatch_lane", historyColumns);
        var outboxColumns = QueryStrings(checkConn, "SELECT name FROM pragma_table_info('state_outbox') ORDER BY cid");
        Assert.Contains("quarantined_at", outboxColumns);
        Assert.Contains("quarantine_reason", outboxColumns);
        Assert.Contains("processing_token", outboxColumns);
        Assert.Contains("processing_started_at", outboxColumns);
        Assert.Equal(
            ["backlog_item_id", "owner_goal_id", "coverage", "version", "updated_at"],
            QueryStrings(checkConn, "SELECT name FROM pragma_table_info('source_backlog_claims') ORDER BY cid"));
        Assert.Contains(
            "goal_replacement_lineage",
            QueryStrings(checkConn, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name"));
        Assert.Contains(
            "goal_replacement_audit",
            QueryStrings(checkConn, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name"));
        var replacementAuditColumns = QueryStrings(
            checkConn,
            "SELECT name FROM pragma_table_info('goal_replacement_audit') ORDER BY cid");
        Assert.Contains("objective_hash", replacementAuditColumns);
        Assert.Contains("ordered_roles", replacementAuditColumns);
        Assert.Contains("assigned_agents", replacementAuditColumns);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_successor_preflight_allows_outbox_quarantine_column_migration")]
    public void SuccessorPreflightAllowsOutboxQuarantineColumnMigration()
    {
        var db = TempDb(migrate: false);
        CreateVersion7StateOutboxFixture(db);

        Assert.False(StateDbMigrations.IsUpToDate(db));
        Assert.True(StateDbMigrations.HasPublishedMigrations(db));

        Assert.Equal(
            SqliteOrchestratorStateRepository.CurrentSchemaVersion,
            SqliteOrchestratorStateRepository.ValidateReadOnlySchema(db));

        _ = StateDbMigrations.EnsureUpToDate(db);

        using var checkConn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;");
        checkConn.Open();
        Assert.Equal(
            ["id", "kind", "payload_json", "created_at", "quarantined_at", "quarantine_reason", "processing_token", "processing_started_at"],
            QueryStrings(checkConn, "SELECT name FROM pragma_table_info('state_outbox') ORDER BY cid"));
        Assert.True(StateDbMigrations.IsUpToDate(db));
    }

    [Xunit.Fact]
    public async Task Version7OutboxMigrationAddsLeaseColumnsAndDrainsExactlyOnce()
    {
        var db = TempDb(migrate: false);
        CreateVersion7StateOutboxFixture(db, includeOutboxIndex: false);

        using (var setupConn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;"))
        {
            setupConn.Open();
            Exec(setupConn, "INSERT INTO state_outbox (id, kind, payload_json, created_at) VALUES ('legacy-message', 'acceptance-retry-audit', '{}', '2026-07-26T00:00:00.0000000+00:00')");
            Assert.Equal(
                ["1", "2", "3", "4", "5", "6", "7"],
                QueryStrings(setupConn, "SELECT CAST(migration_number AS TEXT) FROM schema_migrations ORDER BY migration_number"));
            Assert.Equal(
                ["id", "kind", "payload_json", "created_at"],
                QueryStrings(setupConn, "SELECT name FROM pragma_table_info('state_outbox') ORDER BY cid"));
        }

        Assert.False(StateDbMigrations.IsUpToDate(db));
        var repository = new SqliteOrchestratorStateRepository(db);
        var deliveryCount = 0;
        var missingColumn = await Assert.ThrowsAsync<SqliteException>(() =>
            repository.TryProcessOutboxMessageAsync(
                "legacy-message",
                (_, _) =>
                {
                    deliveryCount++;
                    return Task.FromResult(OrchestratorStateOutboxProcessingResult.Completed);
                }));
        Assert.Contains("no such column: quarantined_at", missingColumn.Message, StringComparison.Ordinal);
        Assert.Equal(0, deliveryCount);

        _ = StateDbMigrations.EnsureUpToDate(db);
        _ = StateDbMigrations.EnsureUpToDate(db);

        using (var checkConn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;"))
        {
            checkConn.Open();
            Assert.Equal(
                ["id", "kind", "payload_json", "created_at", "quarantined_at", "quarantine_reason", "processing_token", "processing_started_at"],
                QueryStrings(checkConn, "SELECT name FROM pragma_table_info('state_outbox') ORDER BY cid"));
            Assert.Equal(
                ["ix_state_outbox_kind"],
                QueryStrings(checkConn, "SELECT name FROM sqlite_master WHERE type = 'index' AND name = 'ix_state_outbox_kind'"));
            Assert.Equal(
                ["state-outbox-lease-columns"],
                QueryStrings(checkConn, "SELECT name FROM schema_migrations WHERE migration_number = 8"));
        }

        Assert.True(await repository.TryProcessOutboxMessageAsync(
            "legacy-message",
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(OrchestratorStateOutboxProcessingResult.Completed);
            }));
        Assert.False(await repository.TryProcessOutboxMessageAsync(
            "legacy-message",
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(OrchestratorStateOutboxProcessingResult.Completed);
            }));
        Assert.Equal(1, deliveryCount);
        Assert.Empty(await repository.ListOutboxMessagesAsync("acceptance-retry-audit"));

        static void Exec(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_backfills_outcome_columns_from_classifier_timeline")]
    public async Task BackfillsOutcomeColumnsFromClassifierTimeline()
    {
        var db = TempDb(migrate: false);
        var kernel = new AgentOrchestratorKernel();
        var known = RecordDispatchOutcome(
            kernel,
            AgentRole.Developer,
            "OpenAI",
            AgentCatalog.OpenAiSubscriptionModelAlias,
            TaskComplexity.Complex,
            exitCode: 1,
            "Model fit: OpenAI/gpt-5.5 - adequate - implementation - provider failed"); // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
        var knownTask = known.Tasks.Single();
        kernel.RecordTaskNote(known.Id, knownTask.Id, "CLASSIFIER rule=provider-connectivity; verdict=ProviderConnectivity");
        var unknown = RecordDispatchOutcome(
            kernel,
            AgentRole.Developer,
            "OpenAI",
            AgentCatalog.OpenAiSubscriptionModelAlias,
            TaskComplexity.Complex,
            exitCode: 1,
            "Model fit: OpenAI/gpt-5.5 - adequate - implementation - old failure"); // Deliberate fixture text pins historical/parser behavior independently of the live catalog.

        SeedOldSchemaState(db, kernel, unknown.Id.Value);
        var before = ReadOutcomeClassCounts(db, hasOutcomeColumns: false);

        _ = StateDbMigrations.EnsureUpToDate(db);
        var repo = new SqliteOrchestratorStateRepository(db);
        var after = ReadOutcomeClassCounts(db, hasOutcomeColumns: true);
        var rows = await repo.ListModelFitHistoryAsync();

        Assert.Equal("before: missing outcome columns", before);
        Assert.Equal("environmental=1; unknown-era=1", after);
        Assert.Contains(rows, row => row.TaskId == knownTask.Id.Value &&
            row.OutcomeRule == "provider-connectivity" &&
            row.OutcomeClass == TaskOutcomeClass.Environmental);
        Assert.Contains(rows, row => row.GoalId == unknown.Id.Value &&
            row.OutcomeRule is null &&
            row.OutcomeClass == TaskOutcomeClass.UnknownEra);
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

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_SaveGoalSnapshotsAsync_writes_changed_goals_in_one_transaction")]
    public async Task SaveGoalSnapshotsAsync_WritesChangedGoalsInOneTransaction()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goalA = kernel.CreateGoal("Goal A");
        var goalB = kernel.CreateGoal("Goal B");
        var untouched = kernel.CreateGoal("Untouched goal");
        await repo.SaveAsync(kernel);

        var changed = kernel.ExportSnapshot().Goals
            .Where(goal => goal.Id == goalA.Id.Value || goal.Id == goalB.Id.Value)
            .Select(goal => goal with { Objective = goal.Objective + " changed" })
            .ToArray();

        await repo.SaveGoalSnapshotsAsync(changed);

        using var conn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;");
        conn.Open();
        var versions = QueryStrings(conn,
            "SELECT id || ':' || version FROM goals ORDER BY objective");
        Assert.Equal(2, versions.Count(v => v.EndsWith(":2", StringComparison.Ordinal)));
        Assert.Contains($"{untouched.Id.Value}:1", versions);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_SaveAsync_skips_unchanged_goal_rows")]
    public async Task SaveAsyncSkipsUnchangedGoalRows()
    {
        var db = TempDb();
        var diagnosticsPath = DiagnosticsPath(db);
        var repo = new SqliteOrchestratorStateRepository(
            db,
            statementObserver: null,
            new SqliteWriteTelemetryOptions
            {
                DiagnosticsPath = diagnosticsPath,
                WarningHoldThreshold = TimeSpan.Zero,
                MirrorToConductEventStream = false
            });
        var kernel = new AgentOrchestratorKernel();
        var goalA = kernel.CreateGoal("No-change full save A");
        var goalB = kernel.CreateGoal("No-change full save B");
        await repo.SaveAsync(kernel);

        using var beforeConn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;");
        beforeConn.Open();
        var beforeVersions = QueryStrings(beforeConn, "SELECT id || ':' || version FROM goals ORDER BY objective");

        await repo.SaveAsync(kernel);

        using var afterConn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;");
        afterConn.Open();
        var afterVersions = QueryStrings(afterConn, "SELECT id || ':' || version FROM goals ORDER BY objective");
        var receipts = File.ReadAllLines(diagnosticsPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonNode.Parse(line)!.AsObject())
            .ToArray();
        var secondSaveReceipt = receipts.Last(receipt =>
            receipt["eventType"]?.GetValue<string>() == "sqlite-state-write-hold" &&
            receipt["operation"]?.GetValue<string>() == "SaveAsync");

        Assert.Contains($"{goalA.Id.Value}:1", beforeVersions);
        Assert.Contains($"{goalB.Id.Value}:1", beforeVersions);
        Assert.Equal(beforeVersions, afterVersions);
        Assert.Equal(0, secondSaveReceipt["rowsWritten"]?.GetValue<long>());
        Assert.Equal(0, secondSaveReceipt["serializedBytes"]?.GetValue<long>());
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
                    await repo.SaveGoalSnapshotsAsync([snap! with { Objective = "CAS retry goal concurrent update" }], ct);
                }
                return (true, snap, true);
            });

        // Delegate was called at least twice: once with stale version, once after retry.
        Assert.True(delegateCalls >= 2, $"Expected retry on version mismatch, got {delegateCalls} delegate calls");
        var restored = await repo.LoadAsync();
        Assert.Equal("CAS retry goal concurrent update", restored.GetGoal(goal.Id).Objective);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_TransactGoalStateAsync_persists_goal_and_human_wait_together")]
    public async Task TransactGoalStateAsyncPersistsGoalAndHumanWaitTogether()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Validate premise", AgentRole.Planner);
        var goal = kernel.CreateGoal("Persist premise clarification", [task]);
        await repo.SaveAsync(kernel);

        await repo.TransactGoalStateAsync<bool>(
            goal.Id,
            (state, _) =>
            {
                Assert.NotNull(state);
                var transactionKernel = AgentOrchestratorKernel.FromSnapshot(
                    new OrchestratorSnapshot([state.Goal], state.HumanInputRequests));
                transactionKernel.RequestHumanInput(
                    goal.Id,
                    task.Id,
                    "Planner reported premise-invalid; clarify or abandon.");
                var snapshot = transactionKernel.ExportSnapshot();
                var updatedState = new GoalStateSnapshot(
                    snapshot.Goals.Single(),
                    snapshot.HumanInputRequests);
                return Task.FromResult<(bool ShouldSave, GoalStateSnapshot? NewState, bool Result)>(
                    (true, updatedState, true));
            });

        var restored = await repo.LoadAsync();
        var request = Assert.Single(restored.GetPendingHumanInput(goal.Id));
        Assert.Equal(task.Id, request.TaskId);
        Assert.Contains("premise-invalid", request.Question, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, restored.GetTask(goal.Id, task.Id).Status);
        Assert.Contains(
            restored.GetGoal(goal.Id).Timeline,
            item => item.Kind == ProgressKind.HumanInputRequested);
    }

    [Xunit.Fact]
    public async Task TransactGoalStateAsync_serializes_matching_request_creation()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Serialize matching human input", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        await repo.SaveAsync(kernel);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await repo.TransactGoalStateAsync<bool>(
                goal.Id,
                (state, _) =>
                {
                    Assert.NotNull(state);
                    var transactionKernel = AgentOrchestratorKernel.FromSnapshot(
                        new OrchestratorSnapshot([state.Goal], state.HumanInputRequests));
                    transactionKernel.RequestHumanInputDeduplicated(
                        goal.Id,
                        task.Id,
                        attempt == 0 ? "Authorize scope expansion?" : " authorize   SCOPE expansion? ",
                        blockerFingerprint: "unchanged-worker-result");
                    var snapshot = transactionKernel.ExportSnapshot();
                    return Task.FromResult<(bool ShouldSave, GoalStateSnapshot? NewState, bool Result)>(
                        (true, new GoalStateSnapshot(snapshot.Goals.Single(), snapshot.HumanInputRequests), true));
                });
        }

        var restored = await repo.LoadAsync();
        var request = Assert.Single(restored.GetPendingHumanInput(goal.Id));
        Assert.Equal(1, request.SuppressionCount);
        Assert.Equal(new HumanInputRequestCounts(1, 1), restored.GetHumanInputRequestCounts(goal.Id, task.Id));
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_tick_merge_preserves_mid_tick_retry_and_tick_task_state")]
    public async Task TickMergePreservesMidTickRetryAndTickTaskState()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Protect mid-tick retry");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        await repo.SaveAsync(kernel);

        var baseline = kernel.ExportSnapshot().Goals.Single(snapshot => snapshot.Id == goal.Id.Value);
        var tickKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([baseline], []));
        tickKernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("codex-cli", "codex exec", "C:\\work", DateTimeOffset.UtcNow));
        tickKernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(1234, "codex exec", "C:\\work", "out.log", "err.log", "exit.txt", DateTimeOffset.UtcNow, null, null));
        var tickSnapshot = tickKernel.ExportSnapshot().Goals.Single();

        await repo.TransactGoalAsync<bool>(
            goal.Id,
            (stored, _) =>
            {
                var transactionKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([stored!], []));
                transactionKernel.RetryTask(goal.Id, task.Id, "operator retry during conductor tick");
                return Task.FromResult((true, transactionKernel.ExportSnapshot().Goals.Single(), true));
            });

        var results = await repo.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, tickSnapshot)]);

        var result = Assert.Single(results);
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var restored = await repo.LoadAsync();
        var restoredGoal = restored.GetGoal(goal.Id);
        var restoredTask = restored.GetTask(goal.Id, task.Id);
        Assert.Equal(WorkTaskStatus.Running, restoredTask.Status);
        Assert.NotNull(restoredTask.LastDispatch);
        Assert.NotNull(restoredTask.LastProcess);
        Assert.Contains(restoredGoal.Timeline, evt =>
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("operator retry during conductor tick", StringComparison.Ordinal));
        Assert.Contains(restoredGoal.Timeline, evt => evt.Kind == ProgressKind.TaskDispatchRecorded);
        Assert.Contains(restoredGoal.Timeline, evt => evt.Kind == ProgressKind.TaskProcessStarted);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_round_trips_gracefully_detached_process_marker")]
    public async Task SqliteOrchestratorStateRepositoryRoundTripsGracefullyDetachedProcessMarker()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "detached process marker");
        var task = goal.Tasks.Single();
        var startedAt = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("local-worker", "worker.exe", "C:\\work", startedAt));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(
                1234,
                "worker.exe",
                "C:\\work",
                "out.log",
                "err.log",
                "exit.txt",
                startedAt,
                null,
                null));
        kernel.RecordTaskProcessGracefullyDetached(
            goal.Id,
            task.Id,
            task.LastProcess! with { WasGracefullyDetachedByConductor = true });

        await repo.SaveAsync(kernel);
        var restored = await repo.LoadAsync();

        var restoredTask = restored.GetTask(goal.Id, task.Id);
        Assert.Equal(WorkTaskStatus.Running, restoredTask.Status);
        Assert.True(restoredTask.LastProcess!.WasGracefullyDetachedByConductor);
    }

    [Xunit.Fact]
    public async Task TickMerge_HumanWait_PersistsAnswerableRequest()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Persist a conductor question",
            [new TaskSpec(TaskId.New(), "Ask before expanding scope", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        await repo.SaveAsync(kernel);

        var baseline = kernel.ExportSnapshot().Goals.Single();
        var tickKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([baseline], []));
        var request = tickKernel.RequestHumanInput(goal.Id, goal.Tasks.Single().Id, "Should scope expand?");
        var tickState = tickKernel.ExportSnapshot();
        var tickSnapshot = tickState.Goals.Single();

        await repo.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, tickSnapshot, tickState.HumanInputRequests)]);

        var restored = await repo.LoadAsync();
        var persisted = Assert.Single(restored.GetPendingHumanInput(goal.Id));
        Assert.Equal(request.Id, persisted.Id);
        Assert.Equal(request.Question, persisted.Question);
        restored.SubmitHumanInput(persisted.Id, "Keep the existing scope.");
        Assert.Equal(WorkTaskStatus.Assigned, restored.GetTask(goal.Id, goal.Tasks.Single().Id).Status);
    }

    [Xunit.Fact]
    public async Task TickMerge_ParkedHumanWait_PersistsWithoutOrphanState()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Persist a parked operator wait",
            [new TaskSpec(TaskId.New(), "Await an operator decision", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        await repo.SaveAsync(kernel);
        var baseline = kernel.ExportSnapshot().Goals.Single();
        kernel.RequestHumanInput(goal.Id, goal.Tasks.Single().Id, "Proceed?");
        kernel.ParkGoal(goal.Id, "deferred by operator");
        var parkedState = kernel.ExportSnapshot();

        var results = await repo.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, parkedState.Goals.Single(), parkedState.HumanInputRequests)]);

        Assert.Equal(GoalSnapshotSaveDisposition.Saved, Assert.Single(results).Disposition);
        var restored = await repo.LoadAsync();
        Assert.Equal(GoalStatus.Parked, restored.GetGoal(goal.Id).Status);
        Assert.Equal(WorkTaskStatus.Assigned, restored.GetTask(goal.Id, goal.Tasks.Single().Id).Status);
        Assert.Empty(restored.GetPendingHumanInput(goal.Id));
    }

    [Xunit.Fact]
    public async Task GoalCheckpoint_HumanWait_RemainsAnswerable()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Checkpoint an operator question",
            [new TaskSpec(TaskId.New(), "Ask before proceeding", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        await repo.SaveAsync(kernel);
        var expected = kernel.RequestHumanInput(goal.Id, goal.Tasks.Single().Id, "Proceed?");

        CliPersistentStateRunner.PersistSingleGoalSnapshot(repo, kernel, goal.Id);

        var restored = await repo.LoadAsync();
        var actual = Assert.Single(restored.GetPendingHumanInput(goal.Id));
        Assert.Equal(expected.Id, actual.Id);
        restored.SubmitHumanInput(actual.Id, "Proceed.");
        Assert.Equal(WorkTaskStatus.Assigned, restored.GetTask(goal.Id, goal.Tasks.Single().Id).Status);
    }

    [Xunit.Fact]
    public async Task SweepCheckpoint_IgnoresOtherGoalRequests()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal("Persist one swept goal");
        var other = kernel.CreateGoal(
            "Keep another goal's request",
            [new TaskSpec(TaskId.New(), "Await input", AgentRole.Developer)]);
        kernel.ActivateGoal(target.Id, AgentCatalog.Default().Agents);
        kernel.ActivateGoal(other.Id, AgentCatalog.Default().Agents);
        var otherRequest = kernel.RequestHumanInput(other.Id, other.Tasks.Single().Id, "Other answer?");
        await repo.SaveAsync(kernel);

        CliPersistentStateRunner.PersistSweepChanges(kernel, repo, [target.Id]);

        var restored = await repo.LoadAsync();
        Assert.Equal(otherRequest.Id, Assert.Single(restored.GetPendingHumanInput(other.Id)).Id);
    }

    [Xunit.Fact]
    public async Task LoadAsync_MissingHumanRequest_RepairsAnswerableWait()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Repair persisted human wait",
            [new TaskSpec(TaskId.New(), "Ask before proceeding", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        var missing = kernel.RequestHumanInput(goal.Id, task.Id, "Should work continue?");
        await repo.SaveAsync(kernel);

        using (var connection = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM human_input_requests WHERE id = $id";
            command.Parameters.AddWithValue("$id", missing.Id.Value);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var restored = await repo.LoadAsync();

        var repaired = Assert.Single(restored.GetPendingHumanInput(goal.Id));
        Assert.Equal(task.Id, repaired.TaskId);
        Assert.Equal("Should work continue?", repaired.Question);
        restored.SubmitHumanInput(repaired.Id, "Continue.");
        Assert.Equal(WorkTaskStatus.Assigned, restored.GetTask(goal.Id, task.Id).Status);
    }

    [Xunit.Fact]
    public async Task SaveGoalSnapshots_HumanWait_IsRejected()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Reject partial human wait",
            [new TaskSpec(TaskId.New(), "Ask first", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RequestHumanInput(goal.Id, goal.Tasks.Single().Id, "Proceed?");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repo.SaveGoalSnapshotsAsync(kernel.ExportSnapshot().Goals));

        Assert.Contains("persist it with its human-input requests", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task TickMerge_InconsistentHumanWait_IsRejected()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Reject an incomplete checkpoint",
            [new TaskSpec(TaskId.New(), "Ask first", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        await repo.SaveAsync(kernel);
        var baseline = kernel.ExportSnapshot().Goals.Single();
        kernel.RequestHumanInput(goal.Id, goal.Tasks.Single().Id, "Proceed?");
        var waiting = kernel.ExportSnapshot().Goals.Single();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repo.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, waiting, [])]));

        Assert.Contains("has no open human-input request", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_tick_merge_never_replaces_newer_dispatch_attempt_with_stale_completion")]
    public async Task TickMergeNeverReplacesNewerDispatchAttemptWithStaleCompletion()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Protect newer dispatch attempt");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        var firstStartedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("claude-cli", "old command", "C:\\work", firstStartedAt));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(1234, "old command", "C:\\work", "old.out", "old.err", "old.exit", firstStartedAt, null, null));
        await repo.SaveAsync(kernel);

        var baseline = kernel.ExportSnapshot().Goals.Single(snapshot => snapshot.Id == goal.Id.Value);
        var tickKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([baseline], []));
        tickKernel.RecordTaskProcessRefreshed(
            goal.Id,
            task.Id,
            tickKernel.GetTask(goal.Id, task.Id).LastProcess! with
            {
                CompletedAt = firstStartedAt.AddMinutes(3),
                ExitCode = 0
            },
            new TaskVerificationRecord(
                "old command",
                "C:\\work",
                0,
                "completed old attempt",
                string.Empty,
                firstStartedAt.AddMinutes(3)));
        Assert.Equal(WorkTaskStatus.Completed, tickKernel.GetTask(goal.Id, task.Id).Status);
        var staleTickSnapshot = tickKernel.ExportSnapshot().Goals.Single();

        var secondStartedAt = firstStartedAt.AddMinutes(5);
        await repo.TransactGoalAsync<bool>(
            goal.Id,
            (stored, _) =>
            {
                var operatorKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([stored!], []));
                operatorKernel.RequeueInterruptedDispatch(
                    goal.Id,
                    task.Id,
                    "recover old attempt",
                    RetryCause.ProviderInterruption);
                operatorKernel.RecordTaskDispatch(
                    goal.Id,
                    task.Id,
                    new TaskDispatchRecord("claude-cli", "new command", "C:\\work", secondStartedAt));
                operatorKernel.RecordTaskProcessStarted(
                    goal.Id,
                    task.Id,
                    new TaskProcessRecord(5678, "new command", "C:\\work", "new.out", "new.err", "new.exit", secondStartedAt, null, null));
                return Task.FromResult((true, operatorKernel.ExportSnapshot().Goals.Single(), true));
            });

        var results = await repo.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, staleTickSnapshot)]);

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, Assert.Single(results).Disposition);
        var restored = await repo.LoadAsync();
        var restoredTask = restored.GetTask(goal.Id, task.Id);
        Assert.Equal("new command", restoredTask.LastDispatch!.Command);
        Assert.Equal(secondStartedAt, restoredTask.LastDispatch.DispatchedAt);
        Assert.Equal(5678, restoredTask.LastProcess!.ProcessId);
        Assert.Equal(secondStartedAt, restoredTask.LastProcess.StartedAt);
        Assert.True(restoredTask.LastProcess.IsRunning);
        Assert.Equal(WorkTaskStatus.Running, restoredTask.Status);
        Assert.Null(restoredTask.LastVerification);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_tick_merge_preserves_retry_tombstone_over_stale_completion")]
    public async Task TickMergePreservesRetryTombstoneOverStaleCompletion()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Protect retry tombstone");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("claude-cli", "old command", "C:\\work", startedAt));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(1234, "old command", "C:\\work", "old.out", "old.err", "old.exit", startedAt, null, null));
        await repo.SaveAsync(kernel);

        var baseline = kernel.ExportSnapshot().Goals.Single(snapshot => snapshot.Id == goal.Id.Value);
        var tickKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([baseline], []));
        tickKernel.RecordTaskProcessRefreshed(
            goal.Id,
            task.Id,
            tickKernel.GetTask(goal.Id, task.Id).LastProcess! with
            {
                CompletedAt = startedAt.AddMinutes(3),
                ExitCode = 0
            },
            new TaskVerificationRecord(
                "old command",
                "C:\\work",
                0,
                "completed old attempt",
                string.Empty,
                startedAt.AddMinutes(3)));
        var staleTickSnapshot = tickKernel.ExportSnapshot().Goals.Single();

        await repo.TransactGoalAsync<bool>(
            goal.Id,
            (stored, _) =>
            {
                var operatorKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([stored!], []));
                operatorKernel.RequeueInterruptedDispatch(
                    goal.Id,
                    task.Id,
                    "recover old attempt",
                    RetryCause.ProviderInterruption);
                return Task.FromResult((true, operatorKernel.ExportSnapshot().Goals.Single(), true));
            });

        var results = await repo.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, staleTickSnapshot)]);

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, Assert.Single(results).Disposition);
        var restored = await repo.LoadAsync();
        var restoredTask = restored.GetTask(goal.Id, task.Id);
        Assert.Equal(WorkTaskStatus.Assigned, restoredTask.Status);
        Assert.Null(restoredTask.LastDispatch);
        Assert.Null(restoredTask.LastProcess);
        Assert.Null(restoredTask.LastExecution);
        Assert.Null(restoredTask.LastVerification);
        Assert.Single(restoredTask.VerificationHistory);
        Assert.NotNull(restoredTask.LatestRetryAt);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_tick_merge_preserves_same_attempt_process_refresh_and_verification")]
    public async Task TickMergePreservesSameAttemptProcessRefreshAndVerification()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Merge same-attempt process and verification");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("claude-cli", "same command", "C:\\work", startedAt));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(1234, "same command", "C:\\work", "same.out", "same.err", "same.exit", startedAt, null, null));
        await repo.SaveAsync(kernel);

        var baseline = kernel.ExportSnapshot().Goals.Single(snapshot => snapshot.Id == goal.Id.Value);
        var tickKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([baseline], []));
        tickKernel.RecordTaskProcessRefreshed(
            goal.Id,
            task.Id,
            tickKernel.GetTask(goal.Id, task.Id).LastProcess! with
            {
                CompletedAt = startedAt.AddMinutes(3),
                ExitCode = 0
            },
            new TaskVerificationRecord(
                "same command",
                "C:\\work",
                0,
                "completed same attempt",
                string.Empty,
                startedAt.AddMinutes(3)));
        var tickSnapshot = tickKernel.ExportSnapshot().Goals.Single();

        await repo.TransactGoalAsync<bool>(
            goal.Id,
            (stored, _) =>
            {
                var storeKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([stored!], []));
                storeKernel.RecordTaskProcessRefreshed(
                    goal.Id,
                    task.Id,
                    storeKernel.GetTask(goal.Id, task.Id).LastProcess! with
                    {
                        CompletedAt = startedAt.AddMinutes(4),
                        ExitCode = 0
                    },
                    null);
                return Task.FromResult((true, storeKernel.ExportSnapshot().Goals.Single(), true));
            });

        var results = await repo.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, tickSnapshot)]);

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, Assert.Single(results).Disposition);
        var restored = await repo.LoadAsync();
        var restoredTask = restored.GetTask(goal.Id, task.Id);
        Assert.Equal(WorkTaskStatus.Completed, restoredTask.Status);
        Assert.Equal(startedAt.AddMinutes(4), restoredTask.LastProcess!.CompletedAt);
        Assert.Equal(0, restoredTask.LastProcess.ExitCode);
        Assert.Equal("completed same attempt", restoredTask.LastVerification!.StandardOutput);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_tick_merge_preserves_cancellation_over_same_attempt_completion")]
    public async Task TickMergePreservesCancellationOverSameAttemptCompletion()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Protect same-attempt cancellation");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("claude-cli", "old command", "C:\\work", startedAt));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(1234, "old command", "C:\\work", "old.out", "old.err", "old.exit", startedAt, null, null));
        await repo.SaveAsync(kernel);

        var baseline = kernel.ExportSnapshot().Goals.Single(snapshot => snapshot.Id == goal.Id.Value);
        var tickKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([baseline], []));
        tickKernel.RecordTaskProcessRefreshed(
            goal.Id,
            task.Id,
            tickKernel.GetTask(goal.Id, task.Id).LastProcess! with
            {
                CompletedAt = startedAt.AddMinutes(4),
                ExitCode = 0
            },
            new TaskVerificationRecord(
                "old command",
                "C:\\work",
                0,
                "completed old attempt",
                string.Empty,
                startedAt.AddMinutes(4)));
        var staleTickSnapshot = tickKernel.ExportSnapshot().Goals.Single();

        await repo.TransactGoalAsync<bool>(
            goal.Id,
            (stored, _) =>
            {
                var operatorKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([stored!], []));
                operatorKernel.RecordTaskProcessCancelled(
                    goal.Id,
                    task.Id,
                    operatorKernel.GetTask(goal.Id, task.Id).LastProcess! with
                    {
                        CompletedAt = startedAt.AddMinutes(3),
                        ExitCode = 130,
                        WasCancelled = true
                    });
                return Task.FromResult((true, operatorKernel.ExportSnapshot().Goals.Single(), true));
            });

        var results = await repo.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, staleTickSnapshot)]);

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, Assert.Single(results).Disposition);
        var restored = await repo.LoadAsync();
        var restoredTask = restored.GetTask(goal.Id, task.Id);
        Assert.Equal(WorkTaskStatus.Cancelled, restoredTask.Status);
        Assert.True(restoredTask.LastProcess!.WasCancelled);
        Assert.Equal(130, restoredTask.LastProcess.ExitCode);
        Assert.Null(restoredTask.LastVerification);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_tick_merge_preserves_acceptance_retry_counters")]
    public async Task TickMergePreservesAcceptanceRetryCounters()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Protect acceptance retry counters");
        await repo.SaveAsync(kernel);

        var baseline = kernel.ExportSnapshot().Goals.Single();
        var tickSnapshot = baseline with { AutomaticAcceptanceRetryCount = 1 };

        await repo.TransactGoalAsync<bool>(
            goal.Id,
            (stored, _) => Task.FromResult((
                true,
                stored! with
                {
                    Objective = "Concurrent operator update",
                    OperatorAcceptanceRegateCount = 1
                },
                true)));

        var results = await repo.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, tickSnapshot)]);

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, Assert.Single(results).Disposition);
        var restored = await repo.LoadAsync();
        var restoredGoal = restored.GetGoal(goal.Id);
        Assert.Equal(1, restoredGoal.AutomaticAcceptanceRetryCount);
        Assert.Equal(1, restoredGoal.OperatorAcceptanceRegateCount);
        Assert.Equal("Concurrent operator update", restoredGoal.Objective);
    }

    [Xunit.Fact]
    public async Task TickMergePreservesClarificationRoundCount()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Protect clarification round count");
        await repo.SaveAsync(kernel);

        var baseline = kernel.ExportSnapshot().Goals.Single();
        var tickSnapshot = baseline with { ClarificationRoundCount = 1 };

        await repo.TransactGoalAsync<bool>(
            goal.Id,
            (stored, _) => Task.FromResult((
                true,
                stored! with { Objective = "Concurrent operator update" },
                true)));

        var results = await repo.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, tickSnapshot)]);

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, Assert.Single(results).Disposition);
        var restored = await repo.LoadAsync();
        var restoredGoal = restored.GetGoal(goal.Id);
        Assert.Equal(1, restoredGoal.ClarificationRoundCount);
        Assert.Equal("Concurrent operator update", restoredGoal.Objective);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_tick_merge_store_owned_same_field_conflict_keeps_cli_value")]
    public async Task TickMergeStoreOwnedSameFieldConflictKeepsCliValue()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Protect same-field operator retry");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        await repo.SaveAsync(kernel);

        var baseline = kernel.ExportSnapshot().Goals.Single(snapshot => snapshot.Id == goal.Id.Value);
        var baselineTask = baseline.Tasks.Single(snapshot => snapshot.Id == task.Id.Value);
        var tickSnapshot = baseline with
        {
            Tasks =
            [
                baselineTask with
                {
                    Status = WorkTaskStatus.Running,
                    CriterionRetryCount = 99,
                    CriterionRetryFeedback = ["tick-side stale retry feedback"]
                }
            ]
        };

        await repo.TransactGoalAsync<bool>(
            goal.Id,
            (stored, _) =>
            {
                var transactionKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([stored!], []));
                transactionKernel.RecordCriterionRetryFeedback(
                    goal.Id,
                    task.Id,
                    ["operator-authored retry feedback"]);
                return Task.FromResult((true, transactionKernel.ExportSnapshot().Goals.Single(), true));
            });

        var results = await repo.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, tickSnapshot)]);

        var result = Assert.Single(results);
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var restored = await repo.LoadAsync();
        var restoredTask = restored.GetTask(goal.Id, task.Id);
        Assert.Equal(WorkTaskStatus.Running, restoredTask.Status);
        Assert.Equal(1, restoredTask.CriterionRetryCount);
        var feedback = Assert.Single(restoredTask.CriterionRetryFeedback);
        Assert.Equal("operator-authored retry feedback", feedback);
    }

    [Xunit.Theory(DisplayName = "SqliteOrchestratorStateRepository_tick_merge_preserves_accepted_retry_feedback_lifecycle")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task TickMergePreservesAcceptedRetryFeedbackLifecycle(bool clearFeedback)
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Preserve accepted retry feedback through tick merge");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(candidate => candidate.RequiredRole == AgentRole.Developer);

        if (clearFeedback)
        {
            kernel.RetryTaskWithAuthoritativeFeedback(
                goal.Id,
                task.Id,
                "Accepted operator correction.",
                RetryCause.NewTestFinding);
        }

        await repo.SaveAsync(kernel);
        var baseline = kernel.ExportGoalSnapshot(goal.Id);

        if (clearFeedback)
        {
            kernel.ClearCriterionRetryFeedback(goal.Id, task.Id);
        }
        else
        {
            kernel.RetryTaskWithAuthoritativeFeedback(
                goal.Id,
                task.Id,
                "Accepted operator correction.",
                RetryCause.NewTestFinding);
        }

        var current = kernel.ExportGoalSnapshot(goal.Id);
        await repo.TransactGoalAsync<bool>(
            goal.Id,
            (stored, _) => Task.FromResult((
                true,
                stored! with { Objective = "Concurrent operator update" },
                true)));

        var results = await repo.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, current)]);

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, Assert.Single(results).Disposition);
        var restoredTask = (await repo.LoadAsync()).GetTask(goal.Id, task.Id);
        if (clearFeedback)
        {
            Assert.Null(restoredTask.AcceptedRetryFeedback);
        }
        else
        {
            Assert.Equal("Accepted operator correction.", restoredTask.AcceptedRetryFeedback?.Message);
        }
    }

    [Xunit.Fact(DisplayName = "TickMergeSliceBatchParentUsesStoreOwnedPrecedence")]
    public async Task TickMergeSliceBatchParentUsesStoreOwnedPrecedence()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var kernel = new AgentOrchestratorKernel();
        var baselineParent = kernel.CreateGoal("Baseline parent");
        var storeParent = kernel.CreateGoal("Store parent");
        var tickParent = kernel.CreateGoal("Tick parent");
        var child = kernel.CreateGoal("Child", tasks: null, sliceBatchParentId: baselineParent.Id);
        await repo.SaveAsync(kernel);

        var baseline = kernel.ExportGoalSnapshot(child.Id);
        var tickSnapshot = baseline with { SliceBatchParentId = tickParent.Id.Value };
        await repo.TransactGoalAsync<bool>(
            child.Id,
            (stored, _) => Task.FromResult((
                true,
                stored! with { SliceBatchParentId = storeParent.Id.Value },
                true)));

        var results = await repo.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, tickSnapshot)]);

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, Assert.Single(results).Disposition);
        var restored = await repo.LoadAsync();
        Assert.Equal(storeParent.Id, restored.GetGoal(child.Id).SliceBatchParentId);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_tick_merge_keeps_source_link_id_and_coverage_atomic")]
    public async Task TickMergeKeepsSourceLinkIdAndCoverageAtomic()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Keep source link atomic");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.SetGoalSourceBacklogItemId(goal.Id, "baseline-item");
        await repo.SaveAsync(kernel);

        var baseline = kernel.ExportSnapshot().Goals.Single(snapshot => snapshot.Id == goal.Id.Value);
        var tickSnapshot = baseline with { SourceBacklogCoverage = SourceBacklogCoverage.Slice };

        await repo.TransactGoalAsync<bool>(
            goal.Id,
            (stored, _) =>
            {
                var transactionKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([stored!], []));
                transactionKernel.SetGoalSourceBacklogItemId(goal.Id, "operator-item");
                return Task.FromResult((true, transactionKernel.ExportSnapshot().Goals.Single(), true));
            });

        var results = await repo.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, tickSnapshot)]);

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, Assert.Single(results).Disposition);
        var restoredGoal = (await repo.LoadAsync()).GetGoal(goal.Id);
        Assert.Equal("operator-item", restoredGoal.SourceBacklogItemId);
        Assert.Null(restoredGoal.SourceBacklogCoverage);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_tick_merge_preserves_operator_criteria_corrections")]
    public async Task TickMergePreservesOperatorCriteriaCorrections()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Protect operator criteria correction");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        await repo.SaveAsync(kernel);

        var baseline = kernel.ExportSnapshot().Goals.Single(snapshot => snapshot.Id == goal.Id.Value);
        var tickSnapshot = baseline with { Status = GoalStatus.Active };

        await repo.TransactGoalAsync<bool>(
            goal.Id,
            (stored, _) =>
            {
                var transactionKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([stored!], []));
                transactionKernel.RecordTaskNote(
                    goal.Id,
                    task.Id,
                    "CRITERIA CORRECTION: supersedes=\"full suite required\"; correction=\"focused build-check accepted\"");
                return Task.FromResult((true, transactionKernel.ExportSnapshot().Goals.Single(), true));
            });

        var results = await repo.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, tickSnapshot)]);

        var result = Assert.Single(results);
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var restored = await repo.LoadAsync();
        var restoredGoal = restored.GetGoal(goal.Id);
        var correction = Assert.Single(restoredGoal.EffectiveAcceptanceCriteriaCorrections);
        Assert.Equal("full suite required", correction.SupersededCriterion);
        Assert.Equal("focused build-check accepted", correction.Correction);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_tick_merge_skip_returns_receipt")]
    public async Task TickMergeSkipReturnsReceipt()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var originalTask = new TaskSpec(TaskId.New(), "Original task", AgentRole.Developer);
        var goal = kernel.CreateGoal("Skip unmergeable tick", [originalTask]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        await repo.SaveAsync(kernel);

        var baseline = kernel.ExportSnapshot().Goals.Single();
        var tickKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([baseline], []));
        tickKernel.RecordTaskDispatch(
            goal.Id,
            originalTask.Id,
            new TaskDispatchRecord("codex-cli", "codex exec", "C:\\work", DateTimeOffset.UtcNow));
        var tickSnapshot = tickKernel.ExportSnapshot().Goals.Single();

        var replacementKernel = new AgentOrchestratorKernel();
        replacementKernel.CreateGoal(goal.Id, "Skip unmergeable tick", [new TaskSpec(TaskId.New(), "Replacement task", AgentRole.Developer)]);
        await repo.SaveGoalSnapshotsAsync(replacementKernel.ExportSnapshot().Goals);

        var results = await repo.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, tickSnapshot)]);

        var result = Assert.Single(results);
        Assert.Equal(GoalSnapshotSaveDisposition.Skipped, result.Disposition);
        Assert.NotNull(result.PersistedSnapshot);
        Assert.Contains("no longer contains task", result.Message, StringComparison.Ordinal);
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

        var mutateAStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var taskA = repo.TransactGoalAsync<bool>(
            goalA.Id,
            async (snap, ct) =>
            {
                mutateAStarted.TrySetResult();
                await releaseMutateA.Task.WaitAsync(ct);
                return (true, snap, true);
            });
        await mutateAStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var taskB = repo.TransactGoalAsync<bool>(
            goalB.Id,
            (snap, _) => Task.FromResult((true, snap, true)));
        try
        {
            await taskB.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(taskA.IsCompleted, "Goal A must remain held while goal B completes independently.");
        }
        finally
        {
            releaseMutateA.TrySetResult();
            await Task.WhenAll(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(5));
        }
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

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_lists_conduct_loop_metadata_with_terminal_candidates")]
    public async Task SqliteRepositoryListsConductLoopMetadataWithTerminalCandidates()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var completed = kernel.CreateGoal("Completed audit goal", [new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)]);
        var cleanedUp = kernel.CreateGoal("Cleaned up audit goal", [new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)]);
        var active = kernel.CreateGoal("Active conductor goal");
        var failed = kernel.CreateGoal("Failed conductor goal", [new TaskSpec(TaskId.New(), "Failed", AgentRole.Developer)]);
        var waiting = kernel.CreateGoal("Waiting conductor goal", [new TaskSpec(TaskId.New(), "Input", AgentRole.Developer)]);
        kernel.ActivateGoal(completed.Id, AgentCatalog.Default().Agents);
        kernel.ActivateGoal(cleanedUp.Id, AgentCatalog.Default().Agents);
        kernel.ActivateGoal(active.Id, AgentCatalog.Default().Agents);
        kernel.ActivateGoal(failed.Id, AgentCatalog.Default().Agents);
        kernel.ActivateGoal(waiting.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskVerification(completed.Id, completed.Tasks.Single().Id,
            new TaskVerificationRecord("manual", "C:\\tmp", 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        kernel.RecordTaskVerification(cleanedUp.Id, cleanedUp.Tasks.Single().Id,
            new TaskVerificationRecord("manual", "C:\\tmp", 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        kernel.ReportTaskProgress(failed.Id, failed.Tasks.Single().Id, WorkTaskStatus.Failed, "failed");
        kernel.RequestHumanInput(waiting.Id, waiting.Tasks.Single().Id, "Need operator input");
        await repo.SaveAsync(kernel);

        using (var conn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE goals SET status = $status WHERE id = $id";
            cmd.Parameters.AddWithValue("$status", "CleanedUp");
            cmd.Parameters.AddWithValue("$id", cleanedUp.Id.Value);
            cmd.ExecuteNonQuery();
        }

        var listing = await repo.ListConductLoopGoalMetadataAsync();
        var ids = listing.Select(goal => goal.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Contains(completed.Id.Value, ids);
        Assert.Contains(cleanedUp.Id.Value, ids);
        Assert.Contains(active.Id.Value, ids);
        Assert.Contains(failed.Id.Value, ids);
        Assert.Contains(waiting.Id.Value, ids);
    }

    [Xunit.Fact(DisplayName = "LoadConductLoopKernel_excludes_terminal_goals_from_active_dictionary")]
    public async Task LoadConductLoopKernelExcludesTerminalGoalsFromActiveDictionary()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var snapshots = Enumerable.Range(0, 10)
            .Select(index => BuildTerminalGoalSnapshot(index))
            .ToArray();
        await repo.SaveGoalSnapshotsAsync(snapshots);

        var metadata = (await repo.ListConductLoopGoalMetadataAsync())
            .OrderBy(goal => goal.Objective, StringComparer.Ordinal)
            .ToArray();
        var kernel = CliPersistentStateRunner.LoadConductLoopKernel(repo);

        Assert.Equal(10, metadata.Length);
        Assert.All(metadata, summary =>
        {
            Assert.Equal(GoalStatus.Completed.ToString(), summary.Status);
            Assert.DoesNotContain("FULL_OBJECTIVE_SENTINEL", summary.Objective, StringComparison.Ordinal);
            Assert.DoesNotContain("REFINED_SPEC_SENTINEL", summary.Objective, StringComparison.Ordinal);
            Assert.DoesNotContain("TIMELINE_SENTINEL", summary.Objective, StringComparison.Ordinal);
            Assert.NotNull(summary.ResultCommit);
            Assert.NotNull(summary.CreatedAt);
            Assert.NotNull(summary.TerminatedAt);
        });
        Assert.All(Enumerable.Range(0, 10), index =>
        {
            Assert.Contains(metadata, goal => goal.Objective == $"Terminal {index} title");
            Assert.Contains(metadata, goal => goal.ResultCommit == $"result-{index}");
        });
        Assert.Empty(kernel.Goals);
        Assert.Empty(kernel.ExportSnapshot().Goals);
        Assert.All(snapshots, snapshot =>
        {
            var goalId = new GoalId(snapshot.Id);
            Assert.False(kernel.IsKnownCompletedDependencyGoal(goalId));
            Assert.True(kernel.TryGetKnownDependencyGoalStatus(goalId, out var status));
            Assert.Equal(GoalStatus.Completed.ToString(), status);
        });
    }

    [Xunit.Fact(DisplayName = "LoadConductLoopKernel_keeps_failed_terminal_dependencies_unsatisfied")]
    public async Task LoadConductLoopKernelKeepsFailedTerminalDependenciesUnsatisfied()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var failed = BuildTerminalGoalSnapshot(0) with { Status = GoalStatus.Failed };
        var completed = BuildTerminalGoalSnapshot(1);
        await repo.SaveGoalSnapshotsAsync([failed, completed]);

        var kernel = CliPersistentStateRunner.LoadConductLoopKernel(repo);

        Assert.Empty(kernel.Goals);
        Assert.False(kernel.IsKnownCompletedDependencyGoal(new GoalId(failed.Id)));
        Assert.True(kernel.TryGetKnownDependencyGoalStatus(new GoalId(failed.Id), out var failedStatus));
        Assert.Equal(GoalStatus.Failed.ToString(), failedStatus);
        Assert.False(kernel.IsKnownCompletedDependencyGoal(new GoalId(completed.Id)));
        Assert.True(kernel.TryGetKnownDependencyGoalStatus(new GoalId(completed.Id), out var completedStatus));
        Assert.Equal(GoalStatus.Completed.ToString(), completedStatus);
    }

    [Xunit.Fact(DisplayName = "ListConductLoopGoalMetadata_caps_overlong_first_line_title")]
    public async Task ListConductLoopGoalMetadataCapsOverlongFirstLineTitle()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var snapshot = BuildTerminalGoalSnapshot(0) with
        {
            Objective = new string('x', 300) + "\nFULL_OBJECTIVE_SENTINEL"
        };
        await repo.SaveGoalSnapshotsAsync([snapshot]);

        var summary = (await repo.ListConductLoopGoalMetadataAsync()).Single();

        Assert.Equal(240, summary.Objective.Length);
        Assert.DoesNotContain("FULL_OBJECTIVE_SENTINEL", summary.Objective, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "LoadGoalsAsync_hydrates_full_terminal_aggregate_on_demand")]
    public async Task LoadGoalsAsyncHydratesFullTerminalAggregateOnDemand()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var snapshot = BuildTerminalGoalSnapshot(0);
        await repo.SaveGoalSnapshotsAsync([snapshot]);
        var goalId = new GoalId(snapshot.Id);

        var conductKernel = CliPersistentStateRunner.LoadConductLoopKernel(repo);
        Assert.Empty(conductKernel.Goals);
        Assert.False(conductKernel.IsKnownCompletedDependencyGoal(goalId));

        var hydrated = await repo.LoadGoalsAsync([goalId]);
        var goal = hydrated.Goals.Single();

        Assert.False(goal.IsMetadataOnly);
        Assert.Contains("FULL_OBJECTIVE_SENTINEL_0", goal.Objective, StringComparison.Ordinal);
        Assert.Contains(goal.Timeline, evt => evt.Message == "TIMELINE_SENTINEL_0");
        Assert.Equal("REFINED_SPEC_SENTINEL_0", goal.RefinedSpec?.BehavioralContract);
        Assert.Equal(["OPERATOR_ACCEPTANCE_SENTINEL_0"], goal.RefinedSpec?.OperatorOwnedAcceptanceCriteria);
        var question = Assert.Single(goal.RefinedSpec!.OpenQuestions);
        Assert.Equal("QUESTION_CRITERION_SENTINEL_0", question.Criterion);
        Assert.Equal("high", question.BlastRadius);
        Assert.Contains(goal.Tasks, task => task.Description == "TASK_DESCRIPTION_SENTINEL_0");
    }

    [Xunit.Fact]
    public async Task SpawnRegistryWritesJoinAmbientStateTransactionConnection()
    {
        var db = TempDb();
        var repository = new SqliteOrchestratorStateRepository(db);
        var registry = new SpawnRegistry(db);
        var identity = new SpawnProcessIdentity(
            424242,
            DateTimeOffset.Parse("2026-07-30T12:00:00Z"),
            @"C:\tools\worker.exe");

        await repository.TransactAsync((_, _) =>
        {
            registry.Register("ambient-register", identity);
            return Task.FromResult((ShouldSave: false, Result: true));
        });
        var active = Assert.Single(registry.ListActive());

        await repository.TransactAsync((_, _) =>
        {
            registry.RecordDiagnostic(active.Id, "ambient-diagnostic");
            registry.MarkReleased(identity.ProcessId, "ambient-release");
            return Task.FromResult((ShouldSave: false, Result: true));
        });

        Assert.Empty(registry.ListActive());
        using var connection = StateDbConnectionFactory.Open(db, StateDbConnectionProfile.QueryOnlyRead);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT released_at, last_diagnostic
            FROM spawn_registry
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", active.Id);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.False(reader.IsDBNull(0));
        Assert.Equal("ambient-release", reader.GetString(1));
    }

    [Xunit.Fact]
    public async Task SpawnRegistryWriteRollsBackWithAmbientStateTransaction()
    {
        var db = TempDb();
        var repository = new SqliteOrchestratorStateRepository(db);
        var registry = new SpawnRegistry(db);
        var identity = new SpawnProcessIdentity(
            424243,
            DateTimeOffset.Parse("2026-07-30T12:01:00Z"),
            @"C:\tools\worker.exe");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.TransactAsync<bool>((_, _) =>
            {
                registry.Register("ambient-rollback", identity);
                throw new InvalidOperationException("rollback");
            }));

        Assert.Empty(registry.ListActive());
    }

    [Xunit.Fact]
    public async Task AmbientStateWriteSessionCapturedFromEndedNestedScopeCannotFallThroughToOuterSession()
    {
        var outerDb = TempDb();
        var nestedDb = TempDb();
        using var outerConnection = StateDbConnectionFactory.Open(outerDb, StateDbConnectionProfile.ReadWrite);
        using var nestedConnection = StateDbConnectionFactory.Open(nestedDb, StateDbConnectionProfile.ReadWrite);
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actionRan = false;
        Task<bool> delayed;

        using (StateDbWriteSession.Enter(outerDb, outerConnection))
        {
            using (StateDbWriteSession.Enter(nestedDb, nestedConnection))
            {
                delayed = Task.Run(async () =>
                {
                    captured.SetResult();
                    await proceed.Task;
                    return StateDbWriteSession.TryExecute(outerDb, _ => actionRan = true);
                });
                await captured.Task;
            }

            proceed.SetResult();
            Assert.False(await delayed);
        }

        Assert.False(actionRan);
    }

    [Xunit.Fact]
    public void AmbientStateWriteSessionDoesNotShareConnectionAcrossStores()
    {
        var firstDb = TempDb();
        var secondDb = TempDb();
        using var connection = StateDbConnectionFactory.Open(firstDb, StateDbConnectionProfile.ReadWrite);
        using var session = StateDbWriteSession.Enter(firstDb, connection);
        var actionRan = false;

        Assert.False(StateDbWriteSession.TryExecute(secondDb, _ => actionRan = true));
        Assert.False(actionRan);
    }

    [Xunit.Fact]
    public async Task AmbientStateWriteSessionSerializesParallelSpawnRegistryMutations()
    {
        var db = TempDb();
        var registry = new SpawnRegistry(db);
        using var connection = StateDbConnectionFactory.Open(db, StateDbConnectionProfile.ReadWrite);
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondAttempted = new ManualResetEventSlim();
        using var secondFinished = new ManualResetEventSlim();
        var triggerInvocationCount = 0;
        connection.CreateFunction(
            "hold_spawn_insert",
            () =>
            {
                if (Interlocked.Increment(ref triggerInvocationCount) == 1)
                {
                    firstEntered.Set();
                    releaseFirst.Wait();
                }

                return 0;
            });
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TEMP TRIGGER serialize_spawn_registry_insert
                BEFORE INSERT ON spawn_registry
                BEGIN
                    SELECT hold_spawn_insert();
                END
                """;
            command.ExecuteNonQuery();
        }

        using var session = StateDbWriteSession.Enter(db, connection);
        var first = Task.Factory.StartNew(
            () => registry.Register(
                "parallel-first",
                new SpawnProcessIdentity(
                    424244,
                    DateTimeOffset.Parse("2026-07-30T12:02:00Z"),
                    @"C:\tools\worker.exe")),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        var firstArrived = firstEntered.Wait(TimeSpan.FromSeconds(5));
        Task second = Task.CompletedTask;
        var secondStarted = false;
        var secondCompletedEarly = false;
        try
        {
            if (firstArrived)
            {
                second = Task.Factory.StartNew(
                    () =>
                    {
                        secondAttempted.Set();
                        registry.Register(
                            "parallel-second",
                            new SpawnProcessIdentity(
                                424245,
                                DateTimeOffset.Parse("2026-07-30T12:03:00Z"),
                                @"C:\tools\worker.exe"));
                        secondFinished.Set();
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                secondStarted = secondAttempted.Wait(TimeSpan.FromSeconds(5));
                secondCompletedEarly = secondFinished.Wait(TimeSpan.FromMilliseconds(100));
            }
        }
        finally
        {
            releaseFirst.Set();
        }

        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(firstArrived);
        Assert.True(secondStarted);
        Assert.False(secondCompletedEarly);
        Assert.True(secondFinished.IsSet);
        Assert.Equal(2, triggerInvocationCount);
    }

    [Xunit.Fact]
    public void CleanupStateWriteJoinsAmbientStateDatabaseConnection()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        using var connection = StateDbConnectionFactory.Open(
            workspace.SqliteStatePath,
            StateDbConnectionProfile.ReadWrite);
        var ambientConnectionUsed = false;
        connection.CreateFunction(
            "mark_cleanup_state_write",
            () =>
            {
                ambientConnectionUsed = true;
                return 0;
            });
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TEMP TRIGGER observe_cleanup_state_write
                BEFORE INSERT ON worktree_cleanup_backoff
                BEGIN
                    SELECT mark_cleanup_state_write();
                END
                """;
            command.ExecuteNonQuery();
        }

        using (StateDbWriteSession.Enter(workspace.SqliteStatePath, connection))
        {
            var backoff = GoalWorktrees.RecordGoalCleanupNeeded(
                root,
                GoalId.New(),
                "remove:ambient-write-test");
            Assert.NotNull(backoff);
        }

        Assert.True(ambientConnectionUsed);
    }

    private static string TempDb(bool migrate = true)
    {
        var dir = CreateTempDirectory();
        var path = Path.Combine(dir, "state.db");
        if (migrate)
            _ = StateDbMigrations.EnsureUpToDate(path);
        return path;
    }

    private static GoalSnapshot BuildTerminalGoalSnapshot(int index)
    {
        var goalId = $"terminal-{index:D2}-0000000000000000000000";
        var taskId = $"task-{index:D2}";
        var occurredAt = DateTimeOffset.Parse("2026-07-23T00:00:00Z").AddMinutes(index);
        return new GoalSnapshot(
            goalId,
            $"Terminal {index} title\nFULL_OBJECTIVE_SENTINEL_{index}",
            GoalStatus.Completed,
            [
                new TaskSnapshot(
                    taskId,
                    $"TASK_DESCRIPTION_SENTINEL_{index}",
                    AgentRole.Developer,
                    WorkTaskStatus.Completed,
                    null,
                    null,
                    null,
                    [],
                    new TaskDispatchSnapshot(
                        "worker",
                        "cmd",
                        "C:\\repo",
                        occurredAt,
                        ResultCommit: $"result-{index}"),
                    null)
            ],
            [new ProgressEventSnapshot(goalId, taskId, ProgressKind.TaskCompleted, $"TIMELINE_SENTINEL_{index}", occurredAt)],
            RefinedSpec: new RefinedSpecSnapshot(
                $"REFINED_SPEC_SENTINEL_{index}",
                [$"ACCEPTANCE_SENTINEL_{index}"],
                VerificationClass.TestVerifiable.ToString(),
                [],
                [new RefinedSpecOpenQuestionSnapshot(
                    $"question-{index}",
                    $"QUESTION_SENTINEL_{index}",
                    "feasibility",
                    "Open",
                    Criterion: $"QUESTION_CRITERION_SENTINEL_{index}",
                    BlastRadius: "high")],
                [$"OPERATOR_ACCEPTANCE_SENTINEL_{index}"]));
    }

    private static string DiagnosticsPath(string dbPath) =>
        Path.Combine(
            Path.GetDirectoryName(dbPath) ?? ".",
            "logs",
            SqliteWriteTelemetry.DiagnosticsFileName);

    private static (int ExitCode, string Output) RunSqliteTool(params string[] arguments)
    {
        var exitCode = 0;
        string outputText = "";
        var errorText = CaptureConsoleError(() =>
        {
            outputText = CaptureConsole(() =>
            {
                exitCode = OrchestratorSqliteTools.RunAsync(arguments).GetAwaiter().GetResult();
            });
        });

        return (exitCode, outputText + errorText);
    }

    private static bool IsWriteCategoryStartupStatement(string sql)
    {
        var trimmed = sql.TrimStart();
        return trimmed.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("INSERT ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("ALTER ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("DROP ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("PRAGMA journal_mode=", StringComparison.OrdinalIgnoreCase);
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

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var command = conn.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void ExecuteSql(SqliteConnection conn, string sql)
    {
        using var command = conn.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void SeedOldSchemaState(string db, AgentOrchestratorKernel kernel, string? stripClassifierTimelineForGoalId = null)
    {
        using var conn = new SqliteConnection($"Data Source={db};Mode=ReadWriteCreate;Pooling=False;");
        conn.Open();
        Exec(conn, "PRAGMA journal_mode=WAL");
        Exec(conn, "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        Exec(conn, "CREATE TABLE goals (id TEXT PRIMARY KEY, status TEXT NOT NULL, objective TEXT NOT NULL, source_backlog_item_id TEXT NULL, updated_at TEXT NOT NULL, snapshot_json TEXT NOT NULL)");
        Exec(conn, "CREATE TABLE human_input_requests (id TEXT PRIMARY KEY, goal_id TEXT NOT NULL, snapshot_json TEXT NOT NULL)");
        Exec(conn, "CREATE TABLE model_fit_history (goal_id TEXT NOT NULL, task_id TEXT NOT NULL, role TEXT NOT NULL, provider_name TEXT NOT NULL, model_name TEXT NOT NULL, complexity TEXT NULL, task_shape TEXT NULL, outcome TEXT NOT NULL, self_rating TEXT NOT NULL, timestamp TEXT NOT NULL, PRIMARY KEY (goal_id, task_id, timestamp))");
        Exec(conn, "INSERT INTO meta (key, value) VALUES ('schema_version', '1')");

        var options = new JsonSerializerOptions { WriteIndented = false };
        options.Converters.Add(new JsonStringEnumConverter());
        var updatedAt = DateTimeOffset.UtcNow.ToString("O");
        foreach (var exportedGoal in kernel.ExportSnapshot().Goals)
        {
            var goal = string.Equals(exportedGoal.Id, stripClassifierTimelineForGoalId, StringComparison.Ordinal)
                ? exportedGoal with
                {
                    Timeline = exportedGoal.Timeline
                        .Where(evt => !evt.Message.Contains("CLASSIFIER rule=", StringComparison.OrdinalIgnoreCase))
                        .ToArray()
                }
                : exportedGoal;
            using var goalCmd = conn.CreateCommand();
            goalCmd.CommandText = """
                INSERT INTO goals (id, status, objective, source_backlog_item_id, updated_at, snapshot_json)
                VALUES ($id, $status, $objective, $source_backlog_item_id, $updated_at, $json)
                """;
            goalCmd.Parameters.AddWithValue("$id", goal.Id);
            goalCmd.Parameters.AddWithValue("$status", goal.Status.ToString());
            goalCmd.Parameters.AddWithValue("$objective", goal.Objective);
            goalCmd.Parameters.AddWithValue("$source_backlog_item_id", (object?)goal.SourceBacklogItemId ?? DBNull.Value);
            goalCmd.Parameters.AddWithValue("$updated_at", updatedAt);
            goalCmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(goal, options));
            goalCmd.ExecuteNonQuery();
        }

        foreach (var row in ModelFitHistory.FromGoals(kernel.Goals))
        {
            using var rowCmd = conn.CreateCommand();
            rowCmd.CommandText = """
                INSERT INTO model_fit_history (goal_id, task_id, role, provider_name, model_name, complexity, task_shape, outcome, self_rating, timestamp)
                VALUES ($goal_id, $task_id, $role, $provider_name, $model_name, $complexity, $task_shape, $outcome, $self_rating, $timestamp)
                """;
            rowCmd.Parameters.AddWithValue("$goal_id", row.GoalId);
            rowCmd.Parameters.AddWithValue("$task_id", row.TaskId);
            rowCmd.Parameters.AddWithValue("$role", row.Role.ToString());
            rowCmd.Parameters.AddWithValue("$provider_name", row.ProviderName);
            rowCmd.Parameters.AddWithValue("$model_name", row.ModelName);
            rowCmd.Parameters.AddWithValue("$complexity", row.Complexity?.ToString() ?? (object)DBNull.Value);
            rowCmd.Parameters.AddWithValue("$task_shape", row.TaskShape ?? (object)DBNull.Value);
            rowCmd.Parameters.AddWithValue("$outcome", row.Outcome.ToString());
            rowCmd.Parameters.AddWithValue("$self_rating", row.SelfRating);
            rowCmd.Parameters.AddWithValue("$timestamp", row.Timestamp.ToString("O"));
            rowCmd.ExecuteNonQuery();
        }

        static void Exec(SqliteConnection c, string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    private static string ReadOutcomeClassCounts(string db, bool hasOutcomeColumns)
    {
        using var conn = new SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False;");
        conn.Open();
        if (!hasOutcomeColumns)
        {
            var columns = QueryStrings(conn, "SELECT name FROM pragma_table_info('model_fit_history') ORDER BY cid");
            return columns.Contains("outcome_class", StringComparer.Ordinal)
                ? "before: unexpected outcome columns"
                : "before: missing outcome columns";
        }

        return string.Join("; ", QueryStrings(
            conn,
            "SELECT outcome_class || '=' || COUNT(*) FROM model_fit_history GROUP BY outcome_class ORDER BY outcome_class"));
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
            TaskComplexity: complexity,
            DispatchLane: "worker-cli");
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
