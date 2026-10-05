using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliIntentOnlyAttentionWriteLockTests : CliTaskQueryTestSupport
{
    [Xunit.Fact]
    public async Task AnswerQueuesPendingIntentWhileStateWriterIsHeld()
    {
        var root = CreateTempDirectory();
        try
        {
            var state = await CreateStateAsync(root);
            var item = await RaiseClarificationAsync(state, "answer");
            var before = await ReadStateRowsAsync(state.Workspace.SqliteStatePath);
            await using var writer = await HoldWriterAsync(state.Workspace.SqliteStatePath);
            try
            {
                Xunit.Assert.False(RunCommand(state,
                    ["attention", "answer", state.Goal.Id.Value[..8], item.Id[..8], "Use selected scope"]));
                var intents = SqliteOperatorIntentStore.ForDirectories(
                    state.Workspace.OrchestratorDirectory, state.Workspace.LogDirectory);
                var pending = Xunit.Assert.Single(await intents.ListForGoalAsync(state.Goal.Id.Value));
                Xunit.Assert.Equal(OperatorIntentStatus.Pending, pending.Status);
                Xunit.Assert.Equal(OperatorIntentVerbs.Answer, pending.Verb);
                Xunit.Assert.Equal(state.Goal.Id.Value, pending.GoalId);
                var payload = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(
                    pending.PayloadJson, OperatorIntentJson.Options)!;
                Xunit.Assert.Equal(item.Id, payload.TargetId);
                Xunit.Assert.Equal(OperatorAnswerTargetKind.Clarification, payload.TargetKind);
            }
            finally
            {
                await ReleaseWriterAsync(writer);
            }
            Xunit.Assert.Equal(before, await ReadStateRowsAsync(state.Workspace.SqliteStatePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task BothDismissFormsResolveItemsWhileStateWriterIsHeld()
    {
        var root = CreateTempDirectory();
        try
        {
            var state = await CreateStateAsync(root);
            var byId = await RaiseClarificationAsync(state, "dismiss-item");
            var byGoal = await RaiseClarificationAsync(state, "dismiss-goal");
            var before = await ReadStateRowsAsync(state.Workspace.SqliteStatePath);
            await using var writer = await HoldWriterAsync(state.Workspace.SqliteStatePath);
            try
            {
                Xunit.Assert.False(RunCommand(state, ["attention", "dismiss", "--item", byId.Id]));
                var afterItemDismiss = await state.Collaboration.ListAsync(state.Goal.Id.Value);
                Xunit.Assert.True(CollaborationItemLifecycle.IsTerminal(
                    afterItemDismiss.Single(item => item.Id == byId.Id).Status));
                Xunit.Assert.False(CollaborationItemLifecycle.IsTerminal(
                    afterItemDismiss.Single(item => item.Id == byGoal.Id).Status));

                Xunit.Assert.False(RunCommand(state, ["attention", "dismiss", state.Goal.Id.Value[..8]]));
                var afterGoalDismiss = await state.Collaboration.ListAsync(state.Goal.Id.Value);
                Xunit.Assert.True(CollaborationItemLifecycle.IsTerminal(
                    afterGoalDismiss.Single(item => item.Id == byGoal.Id).Status));
            }
            finally
            {
                await ReleaseWriterAsync(writer);
            }
            Xunit.Assert.Equal(before, await ReadStateRowsAsync(state.Workspace.SqliteStatePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task AttentionListControlStillTakesWholeKernelWriteTransaction()
    {
        var root = CreateTempDirectory();
        try
        {
            var state = await CreateStateAsync(root);
            await using var writer = await HoldWriterAsync(state.Workspace.SqliteStatePath);
            try
            {
                var error = Xunit.Assert.Throws<SqliteException>(() =>
                    RunCommand(state, ["attention", "list"], skipReadOnlyRoute: true));
                Xunit.Assert.Equal(5, error.SqliteErrorCode);
            }
            finally
            {
                await ReleaseWriterAsync(writer);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task DismissDrainsOutboxBeforeResolvingItem()
    {
        var root = CreateTempDirectory();
        try
        {
            var state = await CreateStateAsync(root);
            var item = await RaiseClarificationAsync(state, "drain");
            await using (var seed = OpenStateConnection(state.Workspace.SqliteStatePath))
            {
                await seed.OpenAsync();
                await using var insert = seed.CreateCommand();
                insert.CommandText = "INSERT INTO state_outbox (id, kind, payload_json, created_at) VALUES ('intent-only-attention-drain', $kind, '{}', $created)";
                insert.Parameters.AddWithValue("$kind", GoalOperationJournal.AcceptanceRetryAuditOutboxKind);
                insert.Parameters.AddWithValue("$created", "2026-09-24T00:00:00.0000000+00:00");
                await insert.ExecuteNonQueryAsync();
            }

            Xunit.Assert.False(RunCommand(state, ["attention", "dismiss", "--item", item.Id]));
            Xunit.Assert.True(CollaborationItemLifecycle.IsTerminal(
                (await state.Collaboration.ListAsync(state.Goal.Id.Value)).Single().Status));
            await using var check = OpenStateConnection(state.Workspace.SqliteStatePath);
            await check.OpenAsync();
            await using var query = check.CreateCommand();
            query.CommandText = "SELECT quarantined_at FROM state_outbox WHERE id = 'intent-only-attention-drain'";
            Xunit.Assert.False(string.IsNullOrWhiteSpace((string?)await query.ExecuteScalarAsync()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, "attention", "answer")]
    [Xunit.InlineData(true, "ATTENTION", "AnSwEr", "goal", "item", "text")]
    [Xunit.InlineData(true, "attention", "dismiss", "--item", "item")]
    [Xunit.InlineData(true, "Attention", "DISMISS", "goal")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(false, "attention")]
    [Xunit.InlineData(false, "attention", "list")]
    [Xunit.InlineData(false, "attention", "show")]
    [Xunit.InlineData(false, "readiness")]
    [Xunit.InlineData(false, "answer", "attention")]
    public void IntentOnlyPredicateMatchesOnlyAnswerAndDismiss(bool expected, params string[] args) =>
        Xunit.Assert.Equal(expected, CliPersistentStateRunner.IsIntentOnlyAttentionCommand(args));

    private static async Task<TestState> CreateStateAsync(string root)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Intent-only attention goal");
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        long clock = 0;
        var repository = new SqliteOrchestratorStateRepository(
            workspace.SqliteStatePath,
            statementObserver: null,
            new SqliteWriteTelemetryOptions
            {
                BusyTimeoutMilliseconds = 1,
                BusyRetryBudget = TimeSpan.FromMilliseconds(1),
                MirrorToConductEventStream = false,
                MonotonicMilliseconds = () => clock,
                RetryDelay = (_, delay, _) =>
                {
                    clock += (long)delay.TotalMilliseconds;
                    return Task.CompletedTask;
                }
            });
        return new TestState(workspace, repository, goal,
            CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory));
    }

    private static async Task<CollaborationItem> RaiseClarificationAsync(TestState state, string topic)
    {
        var item = await state.Collaboration.RaiseAsync(
            CollaborationItemType.Clarification, state.Goal.Id.Value, "Scope", "Which scope?",
            $"spec-clarification:{state.Goal.Id.Value}:{topic}");
        Xunit.Assert.False(CollaborationItemLifecycle.IsTerminal(item.Status));
        return item;
    }

    private static bool RunCommand(TestState state, string[] args, bool skipReadOnlyRoute = false)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = state.Goal;
        var changed = true;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            args, state.Repository, state.Workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal,
            skipReadOnlyRoute: skipReadOnlyRoute));
        return changed;
    }

    private static SqliteConnection OpenStateConnection(string path) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());

    private static async Task<SqliteConnection> HoldWriterAsync(string path)
    {
        var writer = OpenStateConnection(path);
        try
        {
            await writer.OpenAsync();
            await using var begin = writer.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE";
            await begin.ExecuteNonQueryAsync();
            return writer;
        }
        catch
        {
            await writer.DisposeAsync();
            throw;
        }
    }

    private static async Task ReleaseWriterAsync(SqliteConnection writer)
    {
        await using var rollback = writer.CreateCommand();
        rollback.CommandText = "ROLLBACK";
        await rollback.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> ReadStateRowsAsync(string path)
    {
        await using var connection = OpenStateConnection(path);
        await connection.OpenAsync();
        var rows = new List<string>();
        foreach (var table in new[] { "goals", "state_outbox" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY id";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var values = new object?[reader.FieldCount];
                for (var index = 0; index < values.Length; index++)
                    values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                rows.Add(table + ":" + JsonSerializer.Serialize(values));
            }
        }
        return rows.ToArray();
    }

    private sealed record TestState(
        OrchestratorWorkspace Workspace,
        SqliteOrchestratorStateRepository Repository,
        Goal Goal,
        CollaborationItemStore Collaboration);
}
