using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed partial class GoalWorktreeTestsSqliteTooling
{
    // Parallel-safe: every case owns a unique seeded repository and SQLite database;
    // controlled overlap is scoped to connection handles opened by that case.
    [Xunit.Fact]
    public async Task OrchestratorSqliteToolSetGoalStatusPreservesOverlappingObjectiveChange()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Original objective");
            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            await stateRepository.SaveAsync(kernel);
            var initialVersion = ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value).Version;

            using var writer = OpenWriteConnection(workspace.SqliteStatePath);
            Execute(writer, "BEGIN IMMEDIATE");
            UpdateObjective(writer, goal.Id.Value, "Concurrent objective");

            var exitCode = await RunWithControlledCommitAsync(
                writer,
                "set-goal-status",
                goal.Id.Value,
                "--status",
                "Active",
                "--db",
                workspace.SqliteStatePath);
            Assert.Equal(0, exitCode);

            var row = ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value);
            Assert.Equal("Concurrent objective", row.Objective);
            Assert.Equal("Concurrent objective", row.Snapshot["Objective"]?.GetValue<string>());
            Assert.Equal("Active", row.Status);
            Assert.Equal("Active", row.Snapshot["Status"]?.GetValue<string>());
            Assert.Equal(initialVersion + 2, row.Version);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public async Task OrchestratorSqliteToolSetGoalStatusPreservesSerializedObjectiveChange()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Original objective");
            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            await stateRepository.SaveAsync(kernel);
            var initialVersion = ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value).Version;

            using (var writer = OpenWriteConnection(workspace.SqliteStatePath))
                UpdateObjective(writer, goal.Id.Value, "Serialized objective");

            var result = RunOrchestratorSqliteTool(repo, repo, "set-goal-status", goal.Id.Value, "--status", "Active");

            Assert.True(result.ExitCode == 0, $"exit={result.ExitCode}; stdout={result.Stdout}; stderr={result.Stderr}");
            var row = ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value);
            Assert.Equal("Serialized objective", row.Objective);
            Assert.Equal(row.Objective, row.Snapshot["Objective"]?.GetValue<string>());
            Assert.Equal("Active", row.Status);
            Assert.Equal(row.Status, row.Snapshot["Status"]?.GetValue<string>());
            Assert.Equal(initialVersion + 2, row.Version);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public async Task OrchestratorSqliteToolRequeueTaskPreservesOverlappingGoalAndTaskChanges()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateActivatedTwoTaskGoal();
            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            await stateRepository.SaveAsync(kernel);
            var initialVersion = ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value).Version;

            using var writer = OpenWriteConnection(workspace.SqliteStatePath);
            Execute(writer, "BEGIN IMMEDIATE");
            UpdateSnapshot(writer, goal.Id.Value, "Concurrent objective", snapshot =>
            {
                var tasks = Assert.IsType<JsonArray>(snapshot["Tasks"]);
                var target = Assert.IsType<JsonObject>(tasks[0]);
                target["LastExecution"] = "execution-marker";
                target["LastVerification"] = "verification-marker";
                target["LastDispatch"] = "dispatch-marker";
                target["LastProcess"] = "process-marker";
                target["SubscriptionRetryAfter"] = "retry-marker";
                Assert.IsType<JsonObject>(tasks[1])["Description"] = "Concurrent unrelated task description";
            });

            var exitCode = await RunWithControlledCommitAsync(
                writer,
                "requeue-task",
                goal.Id.Value,
                "--task-number",
                "1",
                "--note",
                "controlled retry",
                "--db",
                workspace.SqliteStatePath);
            Assert.Equal(0, exitCode);

            var row = ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value);
            Assert.Equal("Concurrent objective", row.Objective);
            Assert.Equal(row.Objective, row.Snapshot["Objective"]?.GetValue<string>());
            Assert.Equal("Active", row.Status);
            Assert.Equal(row.Status, row.Snapshot["Status"]?.GetValue<string>());
            Assert.Equal(initialVersion + 2, row.Version);
            AssertRequeueSemantics(row.Snapshot, "Concurrent unrelated task description", "controlled retry");
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public async Task OrchestratorSqliteToolRequeueTaskPreservesSerializedGoalAndTaskChanges()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateActivatedTwoTaskGoal();
            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            await stateRepository.SaveAsync(kernel);
            var initialVersion = ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value).Version;

            using (var writer = OpenWriteConnection(workspace.SqliteStatePath))
            {
                UpdateSnapshot(writer, goal.Id.Value, "Serialized objective", snapshot =>
                {
                    var tasks = Assert.IsType<JsonArray>(snapshot["Tasks"]);
                    Assert.IsType<JsonObject>(tasks[1])["Description"] = "Serialized unrelated task description";
                });
            }

            var result = RunOrchestratorSqliteTool(
                repo,
                repo,
                "requeue-task",
                goal.Id.Value,
                "--task-number",
                "1",
                "--note",
                "serialized retry");

            Assert.True(result.ExitCode == 0, $"exit={result.ExitCode}; stdout={result.Stdout}; stderr={result.Stderr}");
            var row = ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value);
            Assert.Equal("Serialized objective", row.Objective);
            Assert.Equal(row.Objective, row.Snapshot["Objective"]?.GetValue<string>());
            Assert.Equal(initialVersion + 2, row.Version);
            AssertRequeueSemantics(row.Snapshot, "Serialized unrelated task description", "serialized retry");
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public async Task FormerSqliteToolProtocolNegativeControlLosesOverlappingChange()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Original objective");
            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            await stateRepository.SaveAsync(kernel);

            using var staleWriter = OpenWriteConnection(workspace.SqliteStatePath);
            var staleSnapshotJson = ReadSnapshotJson(staleWriter, goal.Id.Value);
            using (var concurrentWriter = OpenWriteConnection(workspace.SqliteStatePath))
                UpdateObjective(concurrentWriter, goal.Id.Value, "Concurrent objective");

            // Defect-mechanism replay only: this intentionally reproduces the former read-before-BEGIN protocol,
            // rather than invoking shipped candidate code, and proves that ordering can replace a newer snapshot.
            Execute(staleWriter, "BEGIN IMMEDIATE");
            var staleSnapshot = JsonNode.Parse(staleSnapshotJson) as JsonObject
                ?? throw new InvalidOperationException("Expected goal snapshot object.");
            staleSnapshot["Status"] = "Active";
            using (var update = staleWriter.CreateCommand())
            {
                update.CommandText = "UPDATE goals SET status = 'Active', snapshot_json = $snapshot, version = version + 1 WHERE id = $id";
                update.Parameters.AddWithValue("$id", goal.Id.Value);
                update.Parameters.AddWithValue("$snapshot", staleSnapshot.ToJsonString());
                Assert.Equal(1, update.ExecuteNonQuery());
            }
            Execute(staleWriter, "COMMIT");

            var row = ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value);
            Assert.Equal("Concurrent objective", row.Objective);
            Assert.Equal("Original objective", row.Snapshot["Objective"]?.GetValue<string>());
            Assert.NotEqual(row.Objective, row.Snapshot["Objective"]?.GetValue<string>());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public async Task OrchestratorSqliteToolWriteVerbsDryRunLeaveRowUnchanged()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateActivatedTwoTaskGoal();
            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            await stateRepository.SaveAsync(kernel);
            var before = ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value);

            var statusResult = RunOrchestratorSqliteTool(repo, repo, "set-goal-status", goal.Id.Value, "--status", "Failed", "--dry-run");
            Assert.True(statusResult.ExitCode == 0, statusResult.Stderr);
            Assert.Contains("DRY-RUN", statusResult.Stdout);
            AssertRowUnchanged(before, ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value));

            var requeueResult = RunOrchestratorSqliteTool(repo, repo, "requeue-task", goal.Id.Value, "--task-number", "1", "--note", "preview", "--dry-run");
            Assert.True(requeueResult.ExitCode == 0, requeueResult.Stderr);
            Assert.Contains("DRY-RUN", requeueResult.Stdout);
            AssertRowUnchanged(before, ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public async Task OrchestratorSqliteToolRejectedWriteOperationsLeaveRowUnchanged()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateActivatedTwoTaskGoal();
            var ambiguousPrefix = goal.Id.Value[..1];
            for (var attempt = 0; attempt < 256; attempt++)
            {
                var candidate = kernel.CreateGoal($"Ambiguous prefix candidate {attempt}");
                if (candidate.Id.Value.StartsWith(ambiguousPrefix, StringComparison.Ordinal))
                    break;
                if (attempt == 255)
                    throw new InvalidOperationException("Could not create a deterministic ambiguous-prefix fixture.");
            }
            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            await stateRepository.SaveAsync(kernel);
            var before = ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value);

            var unsupported = RunOrchestratorSqliteTool(repo, repo, "set-goal-status", goal.Id.Value, "--status", "Impossible");
            Assert.NotEqual(0, unsupported.ExitCode);
            AssertRowUnchanged(before, ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value));

            var missingStatus = RunOrchestratorSqliteTool(repo, repo, "set-goal-status", goal.Id.Value);
            Assert.NotEqual(0, missingStatus.ExitCode);
            AssertRowUnchanged(before, ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value));

            var missing = RunOrchestratorSqliteTool(repo, repo, "set-goal-status", "not-a-goal", "--status", "Active");
            Assert.NotEqual(0, missing.ExitCode);
            Assert.DoesNotContain("UPDATED", missing.Stdout, StringComparison.Ordinal);
            AssertRowUnchanged(before, ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value));

            var ambiguous = RunOrchestratorSqliteTool(repo, repo, "set-goal-status", ambiguousPrefix, "--status", "Active");
            Assert.NotEqual(0, ambiguous.ExitCode);
            Assert.Contains("ambiguous", ambiguous.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UPDATED", ambiguous.Stdout, StringComparison.Ordinal);
            AssertRowUnchanged(before, ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value));

            var outOfRange = RunOrchestratorSqliteTool(repo, repo, "requeue-task", goal.Id.Value, "--task-number", "99", "--note", "reject");
            Assert.NotEqual(0, outOfRange.ExitCode);
            Assert.DoesNotContain("REQUEUED", outOfRange.Stdout, StringComparison.Ordinal);
            AssertRowUnchanged(before, ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public async Task OrchestratorSqliteToolExplicitDatabaseTargetPreservesContractAndVersion()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Explicit database target");
            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            await stateRepository.SaveAsync(kernel);
            var before = ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value);

            var result = RunOrchestratorSqliteTool(repo, null, "set-goal-status", goal.Id.Value, "--status", "Active", "--db", workspace.SqliteStatePath);

            Assert.True(result.ExitCode == 0, $"exit={result.ExitCode}; stdout={result.Stdout}; stderr={result.Stderr}");
            var after = ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value);
            Assert.Equal("Active", after.Status);
            Assert.Equal(after.Status, after.Snapshot["Status"]?.GetValue<string>());
            Assert.Equal(before.Version + 1, after.Version);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private static SqliteConnection OpenWriteConnection(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite;Pooling=False;");
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task<int> RunWithControlledCommitAsync(SqliteConnection writer, params string[] arguments)
    {
        var beginReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowBegin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = false;
        var utilityTask = OrchestratorSqliteTools.RunAsync(arguments, async connection =>
        {
            beginReached.TrySetResult();
            await allowBegin.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "BEGIN IMMEDIATE";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        });

        try
        {
            await beginReached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Execute(writer, "COMMIT");
            committed = true;
            allowBegin.TrySetResult();
            return await utilityTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        finally
        {
            allowBegin.TrySetResult();
            if (!committed)
            {
                try { Execute(writer, "ROLLBACK"); } catch (SqliteException) { }
            }
        }
    }

    private static void UpdateObjective(SqliteConnection connection, string goalId, string objective)
    {
        var snapshotJson = ReadSnapshotJson(connection, goalId);
        var snapshot = JsonNode.Parse(snapshotJson) as JsonObject
            ?? throw new InvalidOperationException("Expected goal snapshot object.");
        snapshot["Objective"] = objective;

        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE goals SET objective = $objective, snapshot_json = $snapshot, version = version + 1 WHERE id = $id";
        update.Parameters.AddWithValue("$id", goalId);
        update.Parameters.AddWithValue("$objective", objective);
        update.Parameters.AddWithValue("$snapshot", snapshot.ToJsonString());
        Assert.Equal(1, update.ExecuteNonQuery());
    }

    private static void UpdateSnapshot(SqliteConnection connection, string goalId, string objective, Action<JsonObject> mutate)
    {
        var snapshot = JsonNode.Parse(ReadSnapshotJson(connection, goalId)) as JsonObject
            ?? throw new InvalidOperationException("Expected goal snapshot object.");
        snapshot["Objective"] = objective;
        mutate(snapshot);

        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE goals SET objective = $objective, snapshot_json = $snapshot, version = version + 1 WHERE id = $id";
        update.Parameters.AddWithValue("$id", goalId);
        update.Parameters.AddWithValue("$objective", objective);
        update.Parameters.AddWithValue("$snapshot", snapshot.ToJsonString());
        Assert.Equal(1, update.ExecuteNonQuery());
    }

    private static string ReadSnapshotJson(SqliteConnection connection, string goalId)
    {
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT snapshot_json FROM goals WHERE id = $id";
        select.Parameters.AddWithValue("$id", goalId);
        return Assert.IsType<string>(select.ExecuteScalar());
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateActivatedTwoTaskGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Original objective",
            [
                new TaskSpec(TaskId.New(), "Target task", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Unrelated task", AgentRole.Tester)
            ]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return (kernel, goal);
    }

    private static void AssertRequeueSemantics(JsonObject snapshot, string unrelatedDescription, string retryNote)
    {
        var tasks = Assert.IsType<JsonArray>(snapshot["Tasks"]);
        var target = Assert.IsType<JsonObject>(tasks[0]);
        var expectedStatus = string.IsNullOrWhiteSpace(target["AssignedAgentId"]?.GetValue<string>()) ? "Pending" : "Assigned";
        Assert.Equal(expectedStatus, target["Status"]?.GetValue<string>());
        Assert.Null(target["LastExecution"]);
        Assert.Null(target["LastVerification"]);
        Assert.Null(target["LastDispatch"]);
        Assert.Null(target["LastProcess"]);
        Assert.Null(target["SubscriptionRetryAfter"]);
        Assert.Equal(unrelatedDescription, Assert.IsType<JsonObject>(tasks[1])["Description"]?.GetValue<string>());

        var timeline = Assert.IsType<JsonArray>(snapshot["Timeline"]);
        var retry = Assert.IsType<JsonObject>(timeline[^1]);
        Assert.Equal("TaskRetried", retry["Kind"]?.GetValue<string>());
        Assert.Equal(retryNote, retry["Message"]?.GetValue<string>());
    }

    private static void AssertRowUnchanged(GoalDatabaseRow expected, GoalDatabaseRow actual)
    {
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Objective, actual.Objective);
        Assert.Equal(expected.SnapshotJson, actual.SnapshotJson);
        Assert.Equal(expected.Version, actual.Version);
    }

    private static GoalDatabaseRow ReadGoalRow(string databasePath, string goalId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, objective, snapshot_json, version FROM goals WHERE id = $id";
        command.Parameters.AddWithValue("$id", goalId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new GoalDatabaseRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            JsonNode.Parse(reader.GetString(2)) as JsonObject
                ?? throw new InvalidOperationException("Expected goal snapshot object."),
            reader.GetInt64(3));
    }

    private sealed record GoalDatabaseRow(string Status, string Objective, string SnapshotJson, JsonObject Snapshot, long Version);
}
