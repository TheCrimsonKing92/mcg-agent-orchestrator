using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Cli;
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
            ["engineering_practices", "goals", "human_input_requests", "meta", "model_fit_history"],
            QueryStrings(conn, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name"));
        Xunit.Assert.Equal(
            ["ix_engineering_practices_enabled_priority", "ix_goals_status", "ix_model_fit_history_model", "ix_model_fit_history_outcome_class", "ix_model_fit_history_role"],
            QueryStrings(conn, "SELECT name FROM sqlite_master WHERE type = 'index' AND (name = 'ix_goals_status' OR name LIKE 'ix_model_fit_history_%' OR name = 'ix_engineering_practices_enabled_priority') ORDER BY name"));
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

        await Assert.ThrowsAsync<SqliteException>(() =>
            blockedRepo.TransactAsync(
                "blocked-writer-test",
                (kernel, _) => Task.FromResult((ShouldSave: true, Result: kernel.Goals.Count))));

        await holderTask;

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
        var request = kernel.RequestHumanInput(
            goal.Id,
            task.Id,
            "What should I do?",
            HumanWaitKind.ProviderAuth,
            suggestedDefaultAnswer: "unused",
            resumeCommand: "provider auth resume");

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

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_persists_outcome_rule_and_class")]
    public async Task PersistsOutcomeRuleAndClass()
    {
        var db = TempDb();
        var repo = new SqliteOrchestratorStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var goal = RecordDispatchOutcome(
            kernel,
            AgentRole.Developer,
            "OpenAI",
            "gpt-5.5",
            TaskComplexity.Complex,
            exitCode: 1,
            "Model fit: OpenAI/gpt-5.5 - adequate - implementation - provider failed");
        var task = goal.Tasks.Single();
        kernel.RecordTaskNote(goal.Id, task.Id, "CLASSIFIER rule=provider-connectivity; verdict=ProviderConnectivity");

        await repo.SaveAsync(kernel);

        var row = Assert.Single(await repo.ListModelFitHistoryAsync());
        Assert.Equal("provider-connectivity", row.OutcomeRule);
        Assert.Equal(TaskOutcomeClass.Environmental, row.OutcomeClass);
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
                    "gpt-5.5",
                    TaskComplexity: TaskComplexity.Complex);
                transactionKernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
                transactionKernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
                    dispatch.Command,
                    dispatch.WorkingDirectory,
                    0,
                    "Model fit: OpenAI/gpt-5.5 - adequate - implementation - scoped edit",
                    string.Empty,
                    DateTimeOffset.Parse("2026-07-01T12:00:01Z")));
                var updated = transactionKernel.ExportSnapshot().Goals.Single(updatedGoal => updatedGoal.Id == goal.Id.Value);
                return Task.FromResult((true, (GoalSnapshot?)updated, true));
            });

        var row = Assert.Single(await repo.ListModelFitHistoryAsync());
        Assert.Equal(goal.Id.Value, row.GoalId);
        Assert.Equal(task.Id.Value, row.TaskId);
        Assert.Equal("OpenAI", row.ProviderName);
        Assert.Equal("gpt-5.5", row.ModelName);
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
        Assert.Equal("worker-cli", record.DispatchLane);
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

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_idempotent_schema_migration_adds_version_and_outcome_columns")]
    public void IdempotentSchemaMigrationAddsVersionAndOutcomeColumns()
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
        var historyColumns = QueryStrings(checkConn, "SELECT name FROM pragma_table_info('model_fit_history') ORDER BY cid");
        Assert.Contains("outcome_rule", historyColumns);
        Assert.Contains("outcome_class", historyColumns);
        Assert.Contains("dispatch_lane", historyColumns);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_backfills_outcome_columns_from_classifier_timeline")]
    public async Task BackfillsOutcomeColumnsFromClassifierTimeline()
    {
        var db = TempDb();
        var kernel = new AgentOrchestratorKernel();
        var known = RecordDispatchOutcome(
            kernel,
            AgentRole.Developer,
            "OpenAI",
            "gpt-5.5",
            TaskComplexity.Complex,
            exitCode: 1,
            "Model fit: OpenAI/gpt-5.5 - adequate - implementation - provider failed");
        var knownTask = known.Tasks.Single();
        kernel.RecordTaskNote(known.Id, knownTask.Id, "CLASSIFIER rule=provider-connectivity; verdict=ProviderConnectivity");
        var unknown = RecordDispatchOutcome(
            kernel,
            AgentRole.Developer,
            "OpenAI",
            "gpt-5.5",
            TaskComplexity.Complex,
            exitCode: 1,
            "Model fit: OpenAI/gpt-5.5 - adequate - implementation - old failure");

        SeedOldSchemaState(db, kernel, unknown.Id.Value);
        var before = ReadOutcomeClassCounts(db, hasOutcomeColumns: false);

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

    [Xunit.Fact(DisplayName = "LoadConductLoopKernel_loads_terminal_goals_as_metadata_only_stubs")]
    public async Task LoadConductLoopKernelLoadsTerminalGoalsAsMetadataOnlyStubs()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var snapshots = Enumerable.Range(0, 10)
            .Select(index => BuildTerminalGoalSnapshot(index))
            .ToArray();
        await repo.SaveGoalSnapshotsAsync(snapshots);

        var kernel = CliPersistentStateRunner.LoadConductLoopKernel(repo);

        var goals = kernel.Goals.OrderBy(goal => goal.Objective, StringComparer.Ordinal).ToArray();
        Assert.Equal(10, goals.Length);
        Assert.All(goals, goal =>
        {
            Assert.True(goal.IsMetadataOnly);
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.Empty(goal.Tasks);
            Assert.Empty(goal.Timeline);
            Assert.Null(goal.RefinedSpec);
            Assert.DoesNotContain("FULL_OBJECTIVE_SENTINEL", goal.Objective, StringComparison.Ordinal);
            Assert.DoesNotContain("REFINED_SPEC_SENTINEL", goal.Objective, StringComparison.Ordinal);
            Assert.DoesNotContain("TIMELINE_SENTINEL", goal.Objective, StringComparison.Ordinal);
        });
        Assert.All(Enumerable.Range(0, 10), index =>
            Assert.Contains(goals, goal => goal.Objective == $"Terminal {index} title"));
        Assert.Empty(kernel.ExportSnapshot().Goals);
    }

    [Xunit.Fact(DisplayName = "LoadGoalsAsync_hydrates_full_terminal_aggregate_on_demand")]
    public async Task LoadGoalsAsyncHydratesFullTerminalAggregateOnDemand()
    {
        var repo = new SqliteOrchestratorStateRepository(TempDb());
        var snapshot = BuildTerminalGoalSnapshot(0);
        await repo.SaveGoalSnapshotsAsync([snapshot]);
        var goalId = new GoalId(snapshot.Id);

        var conductKernel = CliPersistentStateRunner.LoadConductLoopKernel(repo);
        Assert.True(conductKernel.Goals.Single().IsMetadataOnly);

        var hydrated = await repo.LoadGoalsAsync([goalId]);
        var goal = hydrated.Goals.Single();

        Assert.False(goal.IsMetadataOnly);
        Assert.Contains("FULL_OBJECTIVE_SENTINEL_0", goal.Objective, StringComparison.Ordinal);
        Assert.Contains(goal.Timeline, evt => evt.Message == "TIMELINE_SENTINEL_0");
        Assert.Equal("REFINED_SPEC_SENTINEL_0", goal.RefinedSpec?.BehavioralContract);
        Assert.Contains(goal.Tasks, task => task.Description == "TASK_DESCRIPTION_SENTINEL_0");
    }

    private static string TempDb()
    {
        var dir = CreateTempDirectory();
        return Path.Combine(dir, "state.db");
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
                []));
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
