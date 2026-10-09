using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record DogfoodLogAppend(
    string GoalId,
    string Header,
    string Summary,
    string OperatorGate,
    string ModelFit,
    string RenderedMarkdown,
    DateTimeOffset? RecordedAt = null);

public sealed record DogfoodLogRecord(
    long Sequence,
    string GoalId,
    DateTimeOffset RecordedAt,
    string Header,
    string Summary,
    string OperatorGate,
    string ModelFit,
    string RenderedMarkdown);

public sealed class DogfoodLogStore
{
    private const int MaxBusyRetries = 6;
    private readonly string _dbPath;
    private readonly bool _readOnly;

    public DogfoodLogStore(string dbPath) : this(dbPath, readOnly: false)
    {
        Setup(dbPath);
    }

    private DogfoodLogStore(string dbPath, bool readOnly)
    {
        _dbPath = dbPath;
        _readOnly = readOnly;
    }

    public static DogfoodLogStore OpenReadOnly(string dbPath)
    {
        if (!File.Exists(dbPath))
            throw SchemaSetupRequired(dbPath, StoreSchemaState.Missing);

        var store = new DogfoodLogStore(dbPath, readOnly: true);
        using var conn = store.OpenConnection();
        var state = StoreSchemaVersions.Verify(conn, StoreSchemaRegistry.DogfoodLog);
        if (state != StoreSchemaState.Current)
            throw SchemaSetupRequired(dbPath, state);
        return store;
    }

    private static InvalidOperationException SchemaSetupRequired(string dbPath, StoreSchemaState state) =>
        new($"Dogfood log store '{dbPath}' schema is {state} (expected version {StoreSchemaRegistry.DogfoodLog.CurrentVersion}); run setup.");

    private string ConnectionString => CreateConnectionString(_dbPath, _readOnly);

    private static string CreateConnectionString(string dbPath, bool readOnly) => readOnly
        ? new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString()
        : $"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=False;";

    public async Task<DogfoodLogRecord> UpsertAsync(DogfoodLogAppend entry, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entry.GoalId))
            throw new ArgumentException("Goal id is required.", nameof(entry));
        if (string.IsNullOrWhiteSpace(entry.RenderedMarkdown))
            throw new ArgumentException("Rendered markdown is required.", nameof(entry));

        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await using var tx = await conn.BeginTransactionAsync(cancellationToken);
            var recordedAt = entry.RecordedAt ?? DateTimeOffset.UtcNow;

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = """
                    INSERT INTO dogfood_log (
                        goal_id, recorded_at, header, summary, operator_gate, model_fit, rendered_markdown
                    ) VALUES (
                        $goal_id, $recorded_at, $header, $summary, $operator_gate, $model_fit, $rendered_markdown
                    )
                    ON CONFLICT(goal_id) DO UPDATE SET
                        recorded_at = excluded.recorded_at,
                        header = excluded.header,
                        summary = excluded.summary,
                        operator_gate = excluded.operator_gate,
                        model_fit = excluded.model_fit,
                        rendered_markdown = excluded.rendered_markdown
                    """;
                cmd.Parameters.AddWithValue("$goal_id", entry.GoalId);
                cmd.Parameters.AddWithValue("$recorded_at", recordedAt.ToString("O"));
                cmd.Parameters.AddWithValue("$header", entry.Header);
                cmd.Parameters.AddWithValue("$summary", entry.Summary);
                cmd.Parameters.AddWithValue("$operator_gate", entry.OperatorGate);
                cmd.Parameters.AddWithValue("$model_fit", entry.ModelFit);
                cmd.Parameters.AddWithValue("$rendered_markdown", entry.RenderedMarkdown);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return await GetByGoalIdAsync(entry.GoalId, cancellationToken)
                ?? throw new InvalidOperationException("Dogfood log write did not return a record.");
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<DogfoodLogRecord>> ListRecentAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT seq, goal_id, recorded_at, header, summary, operator_gate, model_fit, rendered_markdown
                FROM dogfood_log
                ORDER BY recorded_at DESC, seq DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
            var results = new List<DogfoodLogRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadRecord(reader));
            return results;
        }, cancellationToken);
    }

    public async Task<DogfoodLogRecord?> GetByGoalIdAsync(string goalId, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT seq, goal_id, recorded_at, header, summary, operator_gate, model_fit, rendered_markdown
            FROM dogfood_log
            WHERE goal_id = $goal_id
            """;
        cmd.Parameters.AddWithValue("$goal_id", goalId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        RunNonQuery(conn, "PRAGMA busy_timeout=30000");
        return conn;
    }

    public static void Setup(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var conn = new SqliteConnection(CreateConnectionString(dbPath, readOnly: false));
        conn.Open();
        RunNonQuery(conn, "PRAGMA busy_timeout=30000");
        var state = StoreSchemaVersions.Verify(conn, StoreSchemaRegistry.DogfoodLog);
        if (state == StoreSchemaState.Newer)
            return;

        RunNonQuery(conn, "PRAGMA journal_mode=WAL");
        if (state == StoreSchemaState.Current)
            return;

        RunNonQuery(conn, "BEGIN IMMEDIATE");
        try
        {
            RunNonQuery(conn, """
                CREATE TABLE IF NOT EXISTS dogfood_log (
                    seq               INTEGER PRIMARY KEY AUTOINCREMENT,
                    goal_id           TEXT NOT NULL UNIQUE,
                    recorded_at       TEXT NOT NULL,
                    header            TEXT NOT NULL,
                    summary           TEXT NOT NULL,
                    operator_gate     TEXT NOT NULL,
                    model_fit         TEXT NOT NULL,
                    rendered_markdown TEXT NOT NULL
                )
                """);
            RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_dogfood_log_recorded_at ON dogfood_log(recorded_at DESC, seq DESC)");
            StoreSchemaVersions.UpgradeToCurrent(conn, StoreSchemaRegistry.DogfoodLog);
            RunNonQuery(conn, "COMMIT");
        }
        catch
        {
            try { RunNonQuery(conn, "ROLLBACK"); } catch { }
            throw;
        }
    }

    private static DogfoodLogRecord ReadRecord(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7));

    private static void RunNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool IsTransientLock(SqliteException ex) =>
        ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6;

    private static async Task<T> WithBusyRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        var delayMs = 50;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (SqliteException ex) when (attempt < MaxBusyRetries && IsTransientLock(ex))
            {
                await Task.Delay(delayMs, ct);
                delayMs = Math.Min(delayMs * 2, 1000);
            }
        }
    }
}
