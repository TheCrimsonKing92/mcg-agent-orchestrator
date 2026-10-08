using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: every test owns its migrated database and non-pooled connections.
public sealed class GoalMetadataTerminalJsonSkipTests
{
    // The pre-change metadata query, including its unguarded Timeline scan.
    private const string LegacyMetadataSql = """
        SELECT
            id,
            status,
            objective,
            updated_at,
            (
                SELECT json_extract(evt.value, '$.OccurredAt')
                FROM json_each(goals.snapshot_json, '$.Timeline') AS evt
                ORDER BY CAST(evt.key AS INTEGER) ASC
                LIMIT 1
            ) AS created_at,
            CASE
                WHEN status = 'Active' AND EXISTS (
                    SELECT 1
                    FROM json_each(goals.snapshot_json, '$.Tasks') AS failed_task
                    WHERE json_extract(failed_task.value, '$.Status') = 'Failed'
                ) THEN 'active-with-failed-task'
                ELSE NULL
            END AS condition
        FROM goals
        ORDER BY updated_at DESC
        """;

    [Fact]
    public async Task ListMetadata_Default_PreservesNonTerminalValuesAndSkipsTerminalTimes()
    {
        using var database = new MetadataDatabase();
        await database.SeedAsync();
        var legacy = await ReadLegacyAsync(database.Path);
        var actual = await database.Repository.ListGoalMetadataAsync();

        Assert.Equal(7, legacy.Count);
        Assert.Equal(legacy.Select(row => row.Id), actual.Select(row => row.Id));
        Assert.Equal(legacy.Where(row => !IsTerminal(row)), actual.Where(row => !IsTerminal(row)));
        Assert.All(legacy, row => Assert.NotNull(row.CreatedAt));
        var terminal = actual.Where(IsTerminal).ToArray();
        Assert.Equal(3, terminal.Length);
        Assert.All(terminal, row =>
        {
            Assert.Null(row.CreatedAt);
            Assert.Null(row.Condition);
            Assert.Equal(WithoutTerminalCreationTime(legacy.Single(old => old.Id == row.Id)), row);
        });
        Assert.Equal(GoalLifecycle.ActiveWithFailedTaskCondition,
            Assert.Single(actual, row => row.Objective == "Active failed task").Condition);
        Assert.Null(Assert.Single(actual, row => row.Objective == "Active healthy task").Condition);
        Assert.NotNull(Assert.Single(actual, row => row.Status == nameof(GoalStatus.Failed)).CreatedAt);
    }

    [Fact]
    public async Task ListMetadata_OptIn_PreservesAllLegacyValuesOnReadOnlyQueries()
    {
        using var database = new MetadataDatabase();
        await database.SeedAsync();
        var legacy = await ReadLegacyAsync(database.Path);
        IOrchestratorStateQueries queries = SqliteOrchestratorStateRepository.OpenReadOnly(database.Path);

        Assert.Equal(legacy, await queries.ListGoalMetadataAsync(includeTerminalCreatedAt: true));
        var defaultRows = await queries.ListGoalMetadataAsync();
        Assert.Equal(legacy.Select(WithoutTerminalCreationTime), defaultRows);
    }

    [Fact]
    public async Task ListMetadata_TerminalPoison_SkipsJsonOnDefaultAndIdStatusReads()
    {
        using var database = new MetadataDatabase();
        await database.SeedAsync();
        var legacy = await ReadLegacyAsync(database.Path);
        await using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE goals SET snapshot_json = '{not-json'
                WHERE status IN ('Completed', 'Cancelled', 'Superseded')
                """;
            Assert.Equal(3, await command.ExecuteNonQueryAsync());
        }

        // Negative controls prove the poison is reached by the legacy and opt-in paths.
        var error = await Assert.ThrowsAsync<SqliteException>(() => ReadLegacyAsync(database.Path));
        Assert.Contains("malformed JSON", error.Message);
        await Assert.ThrowsAsync<SqliteException>(() =>
            database.Repository.ListGoalMetadataAsync(includeTerminalCreatedAt: true));

        IOrchestratorStateQueries queries = SqliteOrchestratorStateRepository.OpenReadOnly(database.Path);
        var expected = legacy.Select(WithoutTerminalCreationTime).ToArray();
        for (var read = 0; read < 2; read++)
        {
            Assert.Equal(expected, await queries.ListGoalMetadataAsync());
            Assert.Equal(legacy.Select(row => new GoalSummary(row.Id, row.Status, row.Objective, row.UpdatedAt)),
                await queries.ListGoalIdStatusesAsync());
        }
    }

    [Theory]
    [InlineData(GoalStatus.Draft)]
    [InlineData(GoalStatus.Active)]
    [InlineData(GoalStatus.Failed)]
    public async Task ListMetadata_NonTerminalPoison_StillRejectsMalformedJson(GoalStatus status)
    {
        using var database = new MetadataDatabase();
        await database.SeedAsync();
        await using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE goals SET snapshot_json = '{not-json' WHERE status = $status";
            command.Parameters.AddWithValue("$status", status.ToString());
            Assert.True(await command.ExecuteNonQueryAsync() > 0);
        }

        await Assert.ThrowsAsync<SqliteException>(() => ReadLegacyAsync(database.Path));
        await Assert.ThrowsAsync<SqliteException>(() => database.Repository.ListGoalMetadataAsync());
        await Assert.ThrowsAsync<SqliteException>(() =>
            database.Repository.ListGoalMetadataAsync(includeTerminalCreatedAt: true));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Timeline\":[]}")]
    public async Task ListMetadata_MissingOrEmptyTimeline_PreservesNullCreationTimes(string snapshot)
    {
        using var database = new MetadataDatabase();
        await database.SeedAsync();
        await using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE goals SET snapshot_json = $snapshot";
            command.Parameters.AddWithValue("$snapshot", snapshot);
            Assert.Equal(7, await command.ExecuteNonQueryAsync());
        }

        var legacy = await ReadLegacyAsync(database.Path);
        Assert.All(legacy, row => Assert.Null(row.CreatedAt));
        Assert.Equal(legacy, await database.Repository.ListGoalMetadataAsync());
        Assert.Equal(legacy, await database.Repository.ListGoalMetadataAsync(includeTerminalCreatedAt: true));
    }

    private static bool IsTerminal(GoalSummary row) =>
        row.Status is nameof(GoalStatus.Completed) or nameof(GoalStatus.Cancelled) or nameof(GoalStatus.Superseded);

    private static GoalSummary WithoutTerminalCreationTime(GoalSummary row) => IsTerminal(row)
        ? new GoalSummary(row.Id, row.Status, row.Objective, row.UpdatedAt,
            ResultCommit: row.ResultCommit, TerminatedAt: row.TerminatedAt, Condition: row.Condition)
        : row;

    private static async Task<IReadOnlyList<GoalSummary>> ReadLegacyAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = LegacyMetadataSql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<GoalSummary>();
        while (await reader.ReadAsync())
            rows.Add(new GoalSummary(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                CreatedAt: reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                Condition: reader.IsDBNull(5) ? null : reader.GetString(5)));
        return rows;
    }

    private sealed class MetadataDatabase : IDisposable
    {
        private readonly string _root = SharedTestSupport.CreateTempDirectory();
        internal string Path { get; }
        internal string ConnectionString => $"Data Source={Path};Pooling=False";
        internal SqliteOrchestratorStateRepository Repository { get; }

        internal MetadataDatabase()
        {
            Path = System.IO.Path.Combine(_root, "state.db");
            StateDbMigrations.EnsureUpToDate(Path);
            Repository = new SqliteOrchestratorStateRepository(Path);
        }

        internal async Task SeedAsync()
        {
            var kernel = new AgentOrchestratorKernel();
            (string Objective, GoalStatus Status, WorkTaskStatus TaskStatus)[] cases =
            [
                ("Draft", GoalStatus.Draft, WorkTaskStatus.Pending),
                ("Active healthy task", GoalStatus.Active, WorkTaskStatus.Pending),
                ("Active failed task", GoalStatus.Active, WorkTaskStatus.Failed),
                ("Failed goal", GoalStatus.Failed, WorkTaskStatus.Failed),
                ("Completed", GoalStatus.Completed, WorkTaskStatus.Completed),
                ("Cancelled", GoalStatus.Cancelled, WorkTaskStatus.Failed),
                ("Superseded", GoalStatus.Superseded, WorkTaskStatus.Failed)
            ];
            foreach (var item in cases)
                kernel.CreateGoal(item.Objective, [new TaskSpec(TaskId.New(), "Work", AgentRole.Developer)]);
            var snapshots = kernel.ExportSnapshot().Goals.Select((goal, index) => goal with
            {
                Status = cases[index].Status,
                Tasks = goal.Tasks.Select(task => task with { Status = cases[index].TaskStatus }).ToArray(),
                Timeline =
                [
                    goal.Timeline[0] with { OccurredAt = new DateTimeOffset(2026, 1, index + 1, 0, 0, 0, TimeSpan.FromHours(2)) },
                    goal.Timeline[0] with { OccurredAt = new DateTimeOffset(2026, 1, index + 1, 1, 0, 0, TimeSpan.FromHours(2)) }
                ]
            }).ToArray();
            await Repository.SaveGoalSnapshotsAsync(snapshots);

            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            for (var index = 0; index < snapshots.Length; index++)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE goals SET updated_at = $updated WHERE id = $id";
                command.Parameters.AddWithValue("$updated", $"2026-01-0{index + 1}T00:00:00.0000000+00:00");
                command.Parameters.AddWithValue("$id", snapshots[index].Id);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
    }
}
