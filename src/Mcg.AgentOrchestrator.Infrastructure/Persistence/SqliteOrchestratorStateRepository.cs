using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class SqliteOrchestratorStateRepository : ITransactionalOrchestratorStateRepository
{
    private readonly string _dbPath;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public SqliteOrchestratorStateRepository(string dbPath)
    {
        _dbPath = dbPath;
        EnsureSchema();
    }

    private string ConnectionString => $"Data Source={_dbPath};Mode=ReadWriteCreate;";

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    private void EnsureSchema()
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        RunNonQuery(conn, "PRAGMA journal_mode=WAL");
        RunNonQuery(conn, "PRAGMA busy_timeout=30000");
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS goals (
                id            TEXT PRIMARY KEY,
                status        TEXT NOT NULL,
                objective     TEXT NOT NULL,
                updated_at    TEXT NOT NULL,
                snapshot_json TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS human_input_requests (
                id            TEXT PRIMARY KEY,
                goal_id       TEXT NOT NULL,
                snapshot_json TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, "INSERT OR IGNORE INTO meta (key, value) VALUES ('schema_version', '1')");
    }

    public async Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        return await LoadFromConnectionAsync(conn, cancellationToken);
    }

    public async Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await SetBusyTimeoutAsync(conn, cancellationToken);
        await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
        try
        {
            await WriteSnapshotAsync(conn, kernel, cancellationToken);
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
        }
        catch
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            throw;
        }
    }

    public async Task<T> TransactAsync<T>(
        Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        return await TransactAsync(
            async (kernel, _, token) => await transaction(kernel, token),
            cancellationToken);
    }

    public async Task<T> TransactAsync<T>(
        Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await SetBusyTimeoutAsync(conn, cancellationToken);
        await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
        try
        {
            var kernel = await LoadFromConnectionAsync(conn, cancellationToken);

            async Task CheckpointAsync()
            {
                await WriteSnapshotAsync(conn, kernel, cancellationToken);
            }

            var (shouldSave, result) = await transaction(kernel, CheckpointAsync, cancellationToken);

            if (shouldSave)
                await WriteSnapshotAsync(conn, kernel, cancellationToken);

            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            return result;
        }
        catch
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            throw;
        }
    }

    public async Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var results = new List<GoalSummary>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, status, objective, updated_at FROM goals ORDER BY updated_at DESC";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new GoalSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return results;
    }

    private static async Task<AgentOrchestratorKernel> LoadFromConnectionAsync(
        SqliteConnection conn,
        CancellationToken cancellationToken)
    {
        var goalSnapshots = new List<GoalSnapshot>();
        var humanInputSnapshots = new List<HumanInputRequestSnapshot>();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT snapshot_json FROM goals";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var snap = JsonSerializer.Deserialize<GoalSnapshot>(reader.GetString(0), SerializerOptions);
                if (snap != null) goalSnapshots.Add(snap);
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT snapshot_json FROM human_input_requests";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var snap = JsonSerializer.Deserialize<HumanInputRequestSnapshot>(reader.GetString(0), SerializerOptions);
                if (snap != null) humanInputSnapshots.Add(snap);
            }
        }

        return AgentOrchestratorKernel.FromSnapshot(
            new OrchestratorSnapshot(goalSnapshots, humanInputSnapshots));
    }

    private static async Task WriteSnapshotAsync(
        SqliteConnection conn,
        AgentOrchestratorKernel kernel,
        CancellationToken cancellationToken)
    {
        var snapshot = kernel.ExportSnapshot();
        var updatedAt = DateTimeOffset.UtcNow.ToString("O");

        var existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM goals";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                existingIds.Add(reader.GetString(0));
        }

        var currentIds = snapshot.Goals
            .Select(g => g.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var goal in snapshot.Goals)
        {
            var json = JsonSerializer.Serialize(goal, SerializerOptions);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO goals (id, status, objective, updated_at, snapshot_json)
                VALUES ($id, $status, $objective, $updated_at, $json)
                ON CONFLICT(id) DO UPDATE SET
                    status        = excluded.status,
                    objective     = excluded.objective,
                    snapshot_json = excluded.snapshot_json,
                    updated_at    = CASE WHEN excluded.snapshot_json != goals.snapshot_json
                                         THEN excluded.updated_at
                                         ELSE goals.updated_at END
                """;
            cmd.Parameters.AddWithValue("$id", goal.Id);
            cmd.Parameters.AddWithValue("$status", goal.Status.ToString());
            cmd.Parameters.AddWithValue("$objective", goal.Objective);
            cmd.Parameters.AddWithValue("$updated_at", updatedAt);
            cmd.Parameters.AddWithValue("$json", json);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var id in existingIds.Where(id => !currentIds.Contains(id)))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM goals WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Incremental upsert + scoped delete (mirrors the goals table) instead of a global
        // DELETE-all + re-insert. Harmless while the kernel is whole-aggregate, but a PREREQUISITE
        // for lazy single-goal hydration: once the kernel holds only the touched goal's requests,
        // a global wipe would erase every other goal's pending input.
        var existingRequestIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM human_input_requests";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                existingRequestIds.Add(reader.GetString(0));
        }

        var currentRequestIds = snapshot.HumanInputRequests
            .Select(request => request.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var request in snapshot.HumanInputRequests)
        {
            var json = JsonSerializer.Serialize(request, SerializerOptions);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO human_input_requests (id, goal_id, snapshot_json)
                VALUES ($id, $goal_id, $json)
                ON CONFLICT(id) DO UPDATE SET
                    goal_id       = excluded.goal_id,
                    snapshot_json = excluded.snapshot_json
                """;
            cmd.Parameters.AddWithValue("$id", request.Id);
            cmd.Parameters.AddWithValue("$goal_id", request.GoalId);
            cmd.Parameters.AddWithValue("$json", json);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var id in existingRequestIds.Where(id => !currentRequestIds.Contains(id)))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM human_input_requests WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task SetBusyTimeoutAsync(SqliteConnection conn, CancellationToken cancellationToken)
        => await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);

    private static void RunNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static async Task RunNonQueryAsync(SqliteConnection conn, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed record GoalSummary(string Id, string Status, string Objective, string UpdatedAt);
