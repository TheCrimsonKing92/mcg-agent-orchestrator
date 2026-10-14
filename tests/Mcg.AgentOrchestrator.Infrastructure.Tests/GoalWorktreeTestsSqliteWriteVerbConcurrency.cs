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

    public static IEnumerable<object[]> RejectedCommandCases()
    {
        foreach (var dryRun in new[] { false, true })
        {
            foreach (var (selector, diagnostic) in new[]
            {
                ("not-a-goal", "No goal matches prefix 'not-a-goal'."),
                ("a", "Goal prefix 'a' is ambiguous:"),
                (" ", "Goal prefix cannot be empty.")
            })
            {
                string[] statusArgs = ["set-goal-status", selector, "--status", "Active"];
                string[] requeueArgs = ["requeue-task", selector, "--task-number", "1", "--note", "reject"];
                yield return [dryRun ? statusArgs.Append("--dry-run").ToArray() : statusArgs, diagnostic];
                yield return [dryRun ? requeueArgs.Append("--dry-run").ToArray() : requeueArgs, diagnostic];
            }
        }

        yield return [new[] { "set-goal-status", "$goal" }, "Missing required --status <Status>."];
        yield return [new[] { "set-goal-status", "$goal", "--status", "Impossible" }, "Unsupported goal status 'Impossible'"];
        yield return [new[] { "set-goal-status", "--status", "Active" }, "Provide at least one goal id or unique prefix."];
        yield return [new[] { "set-goal-status", "$goal", "--status" }, "Missing value for --status."];
        yield return [new[] { "set-goal-status", "$goal", "--unknown" }, "Unknown option: --unknown"];
        yield return [new[] { "requeue-task", "$goal", "--note", "reject" }, "Missing required --task-number <n>."];
        yield return [new[] { "requeue-task", "$goal", "--task-number", "1" }, "Missing required --note <text>."];
        yield return [new[] { "requeue-task", "--task-number", "1", "--note", "reject" }, "Missing goal prefix."];
        yield return [new[] { "requeue-task", "$goal", "--task-number", "1", "--note" }, "Missing value for --note."];
        yield return [new[] { "requeue-task", "$goal", "--task-number" }, "Missing value for --task-number."];
        yield return [new[] { "requeue-task", "$goal", "--unknown" }, "Unknown option: --unknown"];
        foreach (var number in new[] { "bad", "0", "-1", "2147483648", "99" })
        {
            var diagnostic = number == "99" ? "Task number 99 is out of range" : "--task-number must be a positive integer.";
            yield return [new[] { "requeue-task", "$goal", "--task-number", number, "--note", "reject" }, diagnostic];
        }
        yield return [new[] { "requeue-task", "$goal", "--task-number", "99", "--note", "reject", "--dry-run" }, "Task number 99 is out of range"];
        yield return [new[] { "unknown-command" }, "Unknown command: unknown-command"];
        foreach (var command in new[] { "list-goals", "diagnostics", "set-goal-status", "requeue-task" })
            yield return [new[] { command, "--db" }, "Missing value for --db."];
        yield return [new[] { "diagnostics", "--repo-root" }, "Missing value for --repo-root."];
        yield return [new[] { "list-goals", "--status" }, "Missing value for --status."];
        yield return [new[] { "list-goals", "--status", "Impossible" }, "Unsupported goal status 'Impossible'"];
        yield return [new[] { "list-goals", "--limit" }, "Missing value for --limit."];
        foreach (var limit in new[] { "bad", "0", "-1", "2147483648" })
            yield return [new[] { "list-goals", "--limit", limit }, "--limit must be a positive integer."];
    }

    [Xunit.Theory]
    [Xunit.MemberData(nameof(RejectedCommandCases))]
    public async Task RejectedCommand_InputFailure_ExitsNormallyWithoutWrites(string[] arguments, string diagnostic)
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateActivatedTwoTaskGoal(new GoalId("a0000000000000000000000000000001"));
            kernel.CreateGoal(new GoalId("a0000000000000000000000000000002"), "Ambiguous peer");
            kernel.CreateGoal(new GoalId("b0000000000000000000000000000001"), "Unrelated peer");
            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            await stateRepository.SaveAsync(kernel);
            var before = ReadGoalRows(workspace.SqliteStatePath);
            Assert.Equal(3, before.Length);

            var result = RunOrchestratorSqliteTool(repo, repo,
                arguments.Select(argument => argument == "$goal" ? goal.Id.Value : argument).ToArray());

            Assert.Equal(1, result.ExitCode);
            Assert.Contains(diagnostic, result.Stderr, StringComparison.Ordinal);
            AssertNoMutationOutput(result.Stdout);
            Assert.Equal(before, ReadGoalRows(workspace.SqliteStatePath));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task RequeueTask_MissingTasks_ReportsUnexpectedFailureWithoutWrites(bool dryRun)
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateActivatedTwoTaskGoal();
            kernel.CreateGoal("Unrelated peer");
            await CreateMigratedStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            using (var writer = OpenWriteConnection(workspace.SqliteStatePath))
                UpdateSnapshot(writer, goal.Id.Value, goal.Objective, snapshot => snapshot.Remove("Tasks"));
            var before = ReadGoalRows(workspace.SqliteStatePath);
            Assert.Equal(2, before.Length);
            Assert.False(ReadGoalRow(workspace.SqliteStatePath, goal.Id.Value).Snapshot.ContainsKey("Tasks"));

            string[] arguments = ["requeue-task", goal.Id.Value, "--task-number", "1", "--note", "reject"];
            var result = RunOrchestratorSqliteTool(repo, repo,
                dryRun ? arguments.Append("--dry-run").ToArray() : arguments);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("unexpected error (System.InvalidOperationException)", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("snapshot has no Tasks array.", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("TryBuildRequeuePlan", result.Stderr, StringComparison.Ordinal);
            AssertNoMutationOutput(result.Stdout);
            Assert.Equal(before, ReadGoalRows(workspace.SqliteStatePath));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public async Task SetGoalStatus_LateSnapshotFailure_RollsBackEveryRow()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var first = kernel.CreateGoal("First update must roll back");
            var corrupt = kernel.CreateGoal("Later snapshot defect");
            kernel.CreateGoal("Unrelated peer");
            await CreateMigratedStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            using (var writer = OpenWriteConnection(workspace.SqliteStatePath))
            {
                using var update = writer.CreateCommand();
                update.CommandText = "UPDATE goals SET snapshot_json = '[]' WHERE id = $id";
                update.Parameters.AddWithValue("$id", corrupt.Id.Value);
                Assert.Equal(1, update.ExecuteNonQuery());
            }
            var before = ReadGoalRows(workspace.SqliteStatePath);
            Assert.Equal(3, before.Length);
            Assert.Equal("Draft", before.Single(row => row.Id == first.Id.Value).Status);
            Assert.Equal("[]", before.Single(row => row.Id == corrupt.Id.Value).SnapshotJson);

            // Selector order makes the valid UPDATE happen before the invalid snapshot is read.
            var result = RunOrchestratorSqliteTool(repo, repo,
                "set-goal-status", first.Id.Value, corrupt.Id.Value, "--status", "Active");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("unexpected error (System.InvalidOperationException)", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("SetGoalStatusAsync", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("Origin:", result.Stderr, StringComparison.Ordinal);
            AssertNoMutationOutput(result.Stdout);
            Assert.Equal(before, ReadGoalRows(workspace.SqliteStatePath));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public async Task SetGoalStatus_SqliteFailure_BoundsDiagnosticAndRetainsProvenance()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Rejected by database trigger");
            await CreateMigratedStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            using (var writer = OpenWriteConnection(workspace.SqliteStatePath))
                Execute(writer, $"CREATE TRIGGER reject_update BEFORE UPDATE ON goals BEGIN SELECT RAISE(ABORT, 'trigger-defect-{new string('x', 6000)}'); END");
            var before = ReadGoalRows(workspace.SqliteStatePath);
            Assert.Single(before);

            var result = RunOrchestratorSqliteTool(repo, repo,
                "set-goal-status", goal.Id.Value, "--status", "Active");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("unexpected error (Microsoft.Data.Sqlite.SqliteException)", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("SQLite Error 19, extended 1811", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("trigger-defect-", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("Origin:", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("SetGoalStatusAsync", result.Stderr, StringComparison.Ordinal);
            Assert.EndsWith("[diagnostic truncated]" + Environment.NewLine, result.Stderr, StringComparison.Ordinal);
            Assert.InRange(result.Stderr.Length, 1, 4096);
            AssertNoMutationOutput(result.Stdout);
            Assert.Equal(before, ReadGoalRows(workspace.SqliteStatePath));
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

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateActivatedTwoTaskGoal(GoalId? goalId = null)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            goalId ?? GoalId.New(),
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
        Assert.Equal(expected, actual);
    }

    private static GoalDatabaseRow ReadGoalRow(string databasePath, string goalId) =>
        Assert.Single(ReadGoalRows(databasePath), row => row.Id == goalId);

    private static GoalDatabaseRow[] ReadGoalRows(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, status, objective, source_backlog_item_id, updated_at, snapshot_json, version FROM goals ORDER BY id";
        using var reader = command.ExecuteReader();
        var rows = new List<GoalDatabaseRow>();
        while (reader.Read())
            rows.Add(new GoalDatabaseRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetInt64(6)));
        return rows.ToArray();
    }

    private static void AssertNoMutationOutput(string stdout)
    {
        Assert.DoesNotContain("UPDATED", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("REQUEUED", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("DRY-RUN", stdout, StringComparison.Ordinal);
    }

    private sealed record GoalDatabaseRow(
        string Id, string Status, string Objective, string? SourceBacklogItemId,
        string UpdatedAt, string SnapshotJson, long Version)
    {
        // Keep parsing out of record equality and diagnostic printing for corrupt-row controls.
        internal JsonObject Snapshot => JsonNode.Parse(SnapshotJson) as JsonObject
            ?? throw new InvalidOperationException("Expected goal snapshot object.");
    }
}
