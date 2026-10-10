using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum OperatorIntentStatus
{
    Pending,
    Claimed,
    Applied,
    Rejected
}

public sealed record ProgressOperatorIntentPayload(
    WorkTaskStatus Status,
    string Message);

public sealed record RetryOperatorIntentPayload(
    string Message,
    RetryRoundKind? RetryRoundKind,
    string AutonomyPolicy = "supervised-auto",
    RetryCause? RetryCause = null);

public sealed record OperatorIntentRecord(
    string Id,
    string IdempotencyKey,
    string Verb,
    string GoalId,
    string? TaskId,
    string PayloadJson,
    IReadOnlyList<string> PayloadFileReferences,
    string Actor,
    string Channel,
    string AuthenticationAssurance,
    DateTimeOffset CreatedAt,
    OperatorIntentStatus Status = OperatorIntentStatus.Pending,
    string? ClaimOwner = null,
    DateTimeOffset? ClaimedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? Outcome = null,
    OperatorActorKind ActorKind = OperatorActorKind.Human);

public sealed record ActionableOperatorIntentSummary(
    string GoalId,
    int Count,
    DateTimeOffset? LatestAt);

public interface IOperatorIntentStore
{
    Task<OperatorIntentRecord> EnqueueAsync(
        OperatorIntentRecord intent,
        CancellationToken cancellationToken = default);

    Task<OperatorIntentRecord?> ClaimNextAsync(
        string goalId,
        string claimOwner,
        CancellationToken cancellationToken = default);

    Task<OperatorIntentRecord?> ClaimNextPendingAsync(
        string goalId,
        string claimOwner,
        CancellationToken cancellationToken = default);

    Task<OperatorIntentRecord?> ClaimNextByVerbAsync(
        string goalId,
        string verb,
        string claimOwner,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        string intentId,
        string claimOwner,
        OperatorIntentStatus status,
        string outcome,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);

    Task<OperatorIntentRecord?> GetAsync(
        string intentId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperatorIntentRecord>> ListForGoalAsync(
        string goalId,
        int limit = 20,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListActionableGoalIdsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, ActionableOperatorIntentSummary>> ListActionableSummariesAsync(
        IReadOnlyCollection<string> goalIds,
        CancellationToken cancellationToken = default);

    void AcknowledgeWake(string intentId);
}

public sealed class SqliteOperatorIntentStore : IOperatorIntentStore
{
    public const string DatabaseFileName = "operator-intents.db";
    public const string WakeFileSuffix = ".operator-intent.wake";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _dbPath;
    private readonly string _wakeDirectory;
    private readonly bool _readOnly;

    internal Action<string> RemoveWakeFile { get; init; } = File.Delete;
    internal Action<string> EmitDiagnostic { get; init; } = Console.Error.WriteLine;

    public SqliteOperatorIntentStore(string dbPath, string wakeDirectory, bool readOnly = false)
    {
        _dbPath = Path.GetFullPath(dbPath);
        _wakeDirectory = Path.GetFullPath(wakeDirectory);
        _readOnly = readOnly;
        if (!readOnly)
        {
            Setup(_dbPath);
            Directory.CreateDirectory(_wakeDirectory);
        }
    }

    public static SqliteOperatorIntentStore ForDirectories(string orchestratorDirectory, string logDirectory) =>
        new(Path.Combine(orchestratorDirectory, DatabaseFileName), logDirectory);

    public static SqliteOperatorIntentStore OpenExisting(string orchestratorDirectory, string logDirectory)
    {
        var dbPath = Path.GetFullPath(Path.Combine(orchestratorDirectory, DatabaseFileName));
        if (!File.Exists(dbPath))
            throw SchemaSetupRequired(dbPath, StoreSchemaState.Missing);

        var store = new SqliteOperatorIntentStore(dbPath, logDirectory, readOnly: true);
        using var conn = store.OpenConnection();
        var state = StoreSchemaVersions.Verify(conn, StoreSchemaRegistry.OperatorIntents);
        if (state != StoreSchemaState.Current)
            throw SchemaSetupRequired(dbPath, state);
        return store;
    }

    private static InvalidOperationException SchemaSetupRequired(string dbPath, StoreSchemaState state) =>
        new($"Operator intents store '{dbPath}' schema is {state} (expected version {StoreSchemaRegistry.OperatorIntents.CurrentVersion}); run setup.");

    private string ConnectionString =>
        new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = _readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

    private void EnsureWritable()
    {
        if (_readOnly)
            throw new InvalidOperationException("Operator intents store is read-only.");
    }

    public async Task<OperatorIntentRecord> EnqueueAsync(
        OperatorIntentRecord intent,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        ValidateNewIntent(intent);
        await using var conn = OpenConnection();
        await using var tx = conn.BeginTransaction();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO operator_intents (
                    id, idempotency_key, verb, goal_id, task_id, payload_json,
                    payload_file_references_json, actor, channel, authentication_assurance,
                    created_at, status, claim_owner, claimed_at, completed_at, outcome, actor_kind
                )
                VALUES (
                    $id, $idempotency_key, $verb, $goal_id, $task_id, $payload_json,
                    $payload_file_references_json, $actor, $channel, $authentication_assurance,
                    $created_at, $status, $claim_owner, $claimed_at, $completed_at, $outcome, $actor_kind
                )
                """;
            BindIntent(cmd, intent);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var persisted = await ReadByIdempotencyKeyAsync(conn, tx, intent.IdempotencyKey, cancellationToken)
            ?? throw new InvalidOperationException("Operator intent enqueue did not persist or resolve an idempotent record.");
        EnsureIdempotentMatch(intent, persisted);
        await tx.CommitAsync(cancellationToken);
        TouchWakeFile(persisted.Id);
        return persisted;
    }

    public async Task<OperatorIntentRecord?> ClaimNextAsync(
        string goalId,
        string claimOwner,
        CancellationToken cancellationToken = default) =>
        await ClaimNextCoreAsync(goalId, null, claimOwner, cancellationToken);

    public async Task<OperatorIntentRecord?> ClaimNextPendingAsync(
        string goalId,
        string claimOwner,
        CancellationToken cancellationToken = default) =>
        await ClaimNextCoreAsync(goalId, null, claimOwner, cancellationToken, pendingOnly: true);

    public async Task<OperatorIntentRecord?> ClaimNextByVerbAsync(
        string goalId,
        string verb,
        string claimOwner,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        return await ClaimNextCoreAsync(goalId, verb, claimOwner, cancellationToken);
    }

    private async Task<OperatorIntentRecord?> ClaimNextCoreAsync(
        string goalId,
        string? verb,
        string claimOwner,
        CancellationToken cancellationToken,
        bool pendingOnly = false)
    {
        EnsureWritable();
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);

        await using var conn = OpenConnection();
        await using var tx = conn.BeginTransaction();
        var intent = await ReadNextActionableAsync(conn, tx, goalId, verb, claimOwner, cancellationToken, pendingOnly);
        if (intent is null)
        {
            await tx.CommitAsync(cancellationToken);
            return null;
        }

        if (intent.Status == OperatorIntentStatus.Pending)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE operator_intents
                SET status = $claimed, claim_owner = $claim_owner, claimed_at = $claimed_at
                WHERE id = $id AND status = $pending
                """;
            cmd.Parameters.AddWithValue("$claimed", OperatorIntentStatus.Claimed.ToString());
            cmd.Parameters.AddWithValue("$claim_owner", claimOwner);
            cmd.Parameters.AddWithValue("$claimed_at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", intent.Id);
            cmd.Parameters.AddWithValue("$pending", OperatorIntentStatus.Pending.ToString());
            var updated = await cmd.ExecuteNonQueryAsync(cancellationToken);
            if (updated != 1)
            {
                throw new InvalidOperationException(
                    $"Operator intent '{intent.Id}' could not be claimed exactly once; updated rows={updated}.");
            }
        }

        await tx.CommitAsync(cancellationToken);
        return (await GetAsync(intent.Id, cancellationToken))
            ?? throw new InvalidOperationException($"Claimed operator intent '{intent.Id}' disappeared.");
    }

    public async Task CompleteAsync(
        string intentId,
        string claimOwner,
        OperatorIntentStatus status,
        string outcome,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (status is not (OperatorIntentStatus.Applied or OperatorIntentStatus.Rejected))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "An operator intent outcome must be Applied or Rejected.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE operator_intents
            SET status = $status, completed_at = $completed_at, outcome = $outcome
            WHERE id = $id AND status = $claimed AND claim_owner = $claim_owner
            """;
        cmd.Parameters.AddWithValue("$status", status.ToString());
        cmd.Parameters.AddWithValue("$completed_at", completedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$outcome", outcome.Trim());
        cmd.Parameters.AddWithValue("$id", intentId);
        cmd.Parameters.AddWithValue("$claimed", OperatorIntentStatus.Claimed.ToString());
        cmd.Parameters.AddWithValue("$claim_owner", claimOwner);
        var updated = await cmd.ExecuteNonQueryAsync(cancellationToken);
        if (updated != 1)
        {
            throw new InvalidOperationException(
                $"Operator intent '{intentId}' completion expected one claimed row for owner '{claimOwner}', updated rows={updated}.");
        }

        AcknowledgeWake(intentId);
    }

    public async Task<OperatorIntentRecord?> GetAsync(
        string intentId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", intentId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadIntent(reader) : null;
    }

    public async Task<IReadOnlyList<OperatorIntentRecord>> ListForGoalAsync(
        string goalId,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Operator intent list limit must be positive.");
        }

        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} WHERE goal_id = $goal_id ORDER BY created_at DESC, id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$goal_id", goalId);
        cmd.Parameters.AddWithValue("$limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var intents = new List<OperatorIntentRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            intents.Add(ReadIntent(reader));
        }

        return intents;
    }

    public async Task<IReadOnlyList<string>> ListActionableGoalIdsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT goal_id
            FROM operator_intents
            WHERE status IN ($pending, $claimed) AND goal_id <> $workspace
            ORDER BY goal_id
            """;
        cmd.Parameters.AddWithValue("$pending", OperatorIntentStatus.Pending.ToString());
        cmd.Parameters.AddWithValue("$claimed", OperatorIntentStatus.Claimed.ToString());
        cmd.Parameters.AddWithValue("$workspace", OperatorIntentScopes.Workspace);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var goalIds = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            goalIds.Add(reader.GetString(0));
        }

        return goalIds;
    }

    public async Task<IReadOnlyDictionary<string, ActionableOperatorIntentSummary>> ListActionableSummariesAsync(
        IReadOnlyCollection<string> goalIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goalIds);
        var requested = goalIds
            .Where(goalId => !string.IsNullOrWhiteSpace(goalId))
            .ToHashSet(StringComparer.Ordinal);
        if (requested.Count == 0)
        {
            return new Dictionary<string, ActionableOperatorIntentSummary>(StringComparer.Ordinal);
        }

        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT goal_id, COUNT(*), MAX(COALESCE(claimed_at, created_at))
            FROM operator_intents
            WHERE status IN ($pending, $claimed)
            GROUP BY goal_id
            ORDER BY goal_id
            """;
        cmd.Parameters.AddWithValue("$pending", OperatorIntentStatus.Pending.ToString());
        cmd.Parameters.AddWithValue("$claimed", OperatorIntentStatus.Claimed.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var summaries = new Dictionary<string, ActionableOperatorIntentSummary>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var goalId = reader.GetString(0);
            if (!requested.Contains(goalId))
            {
                continue;
            }

            DateTimeOffset? latestAt = null;
            if (!reader.IsDBNull(2) && DateTimeOffset.TryParse(reader.GetString(2), out var parsed))
            {
                latestAt = parsed;
            }

            summaries[goalId] = new ActionableOperatorIntentSummary(goalId, reader.GetInt32(1), latestAt);
        }

        return summaries;
    }

    public void AcknowledgeWake(string intentId)
    {
        EnsureWritable();
        var wakePath = GetWakePath(intentId);
        if (File.Exists(wakePath))
        {
            try
            {
                RemoveWakeFile(wakePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                EmitDiagnostic($"OPERATOR_INTENT_WAKE_ACK_DEFERRED id={intentId} reason={exception.GetType().Name}");
            }
        }
    }

    internal SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        if (!_readOnly)
            RunNonQuery(conn, "PRAGMA busy_timeout=30000");
        return conn;
    }

    public static void Setup(string dbPath)
    {
        dbPath = Path.GetFullPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        conn.Open();
        RunNonQuery(conn, "PRAGMA busy_timeout=30000");
        var state = StoreSchemaVersions.Verify(conn, StoreSchemaRegistry.OperatorIntents);
        if (state == StoreSchemaState.Newer)
            return;

        RunNonQuery(conn, "PRAGMA journal_mode=WAL");
        RunNonQuery(conn, "PRAGMA synchronous=NORMAL");
        if (state == StoreSchemaState.Current)
            return;

        RunNonQuery(conn, "BEGIN IMMEDIATE");
        try
        {
            RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS operator_intents (
                id                            TEXT PRIMARY KEY,
                idempotency_key               TEXT NOT NULL UNIQUE,
                verb                          TEXT NOT NULL,
                goal_id                       TEXT NOT NULL,
                task_id                       TEXT,
                payload_json                  TEXT NOT NULL,
                payload_file_references_json  TEXT NOT NULL,
                actor                         TEXT NOT NULL,
                channel                       TEXT NOT NULL,
                authentication_assurance      TEXT NOT NULL,
                created_at                    TEXT NOT NULL,
                status                        TEXT NOT NULL,
                claim_owner                   TEXT,
                claimed_at                    TEXT,
                completed_at                  TEXT,
                outcome                       TEXT,
                actor_kind                    TEXT
            )
            """);
            AddColumnIfMissing(conn, "operator_intents", "actor_kind", "TEXT");
            RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_operator_intents_actionable ON operator_intents(goal_id, status, created_at, id)");
            StoreSchemaVersions.UpgradeToCurrent(conn, StoreSchemaRegistry.OperatorIntents);
            RunNonQuery(conn, "COMMIT");
        }
        catch
        {
            try { RunNonQuery(conn, "ROLLBACK"); } catch { }
            throw;
        }
    }

    private async Task<OperatorIntentRecord?> ReadNextActionableAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string goalId,
        string? verb,
        string claimOwner,
        CancellationToken cancellationToken,
        bool pendingOnly)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            {SelectColumns}
            WHERE goal_id = $goal_id
              AND ($verb IS NULL OR verb = $verb)
              AND (status = $pending OR ($pending_only = 0 AND status = $claimed AND claim_owner = $claim_owner))
            ORDER BY CASE status WHEN 'Claimed' THEN 0 ELSE 1 END, created_at, id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$goal_id", goalId);
        cmd.Parameters.AddWithValue("$verb", (object?)verb ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pending", OperatorIntentStatus.Pending.ToString());
        cmd.Parameters.AddWithValue("$claimed", OperatorIntentStatus.Claimed.ToString());
        cmd.Parameters.AddWithValue("$claim_owner", claimOwner);
        cmd.Parameters.AddWithValue("$pending_only", pendingOnly ? 1 : 0);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadIntent(reader) : null;
    }

    private static async Task<OperatorIntentRecord?> ReadByIdempotencyKeyAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"{SelectColumns} WHERE idempotency_key = $idempotency_key";
        cmd.Parameters.AddWithValue("$idempotency_key", idempotencyKey);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadIntent(reader) : null;
    }

    private void TouchWakeFile(string intentId)
    {
        Directory.CreateDirectory(_wakeDirectory);
        File.WriteAllText(GetWakePath(intentId), DateTimeOffset.UtcNow.ToString("O"));
    }

    private string GetWakePath(string intentId) =>
        Path.Combine(_wakeDirectory, $"{intentId}{WakeFileSuffix}");

    private static void ValidateNewIntent(OperatorIntentRecord intent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.IdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.Verb);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.GoalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.PayloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.Actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.Channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.AuthenticationAssurance);
        if (!Enum.IsDefined(intent.ActorKind))
            throw new ArgumentOutOfRangeException(nameof(intent), intent.ActorKind, "Unknown operator actor kind.");
        if (intent.Status != OperatorIntentStatus.Pending)
        {
            throw new ArgumentException("A newly enqueued operator intent must be Pending.", nameof(intent));
        }
    }

    private static void EnsureIdempotentMatch(OperatorIntentRecord requested, OperatorIntentRecord persisted)
    {
        if (!string.Equals(requested.Verb, persisted.Verb, StringComparison.Ordinal) ||
            !string.Equals(requested.GoalId, persisted.GoalId, StringComparison.Ordinal) ||
            !string.Equals(requested.TaskId, persisted.TaskId, StringComparison.Ordinal) ||
            !string.Equals(requested.PayloadJson, persisted.PayloadJson, StringComparison.Ordinal) ||
            !requested.PayloadFileReferences.SequenceEqual(persisted.PayloadFileReferences, StringComparer.Ordinal) ||
            !string.Equals(requested.Actor, persisted.Actor, StringComparison.Ordinal) ||
            requested.ActorKind != persisted.ActorKind ||
            !string.Equals(requested.Channel, persisted.Channel, StringComparison.Ordinal) ||
            !string.Equals(requested.AuthenticationAssurance, persisted.AuthenticationAssurance, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Operator intent idempotency key '{requested.IdempotencyKey}' was already used for a different intent.");
        }
    }

    private static void BindIntent(SqliteCommand cmd, OperatorIntentRecord intent)
    {
        cmd.Parameters.AddWithValue("$id", intent.Id);
        cmd.Parameters.AddWithValue("$idempotency_key", intent.IdempotencyKey);
        cmd.Parameters.AddWithValue("$verb", intent.Verb);
        cmd.Parameters.AddWithValue("$goal_id", intent.GoalId);
        cmd.Parameters.AddWithValue("$task_id", intent.TaskId is null ? DBNull.Value : intent.TaskId);
        cmd.Parameters.AddWithValue("$payload_json", intent.PayloadJson);
        cmd.Parameters.AddWithValue("$payload_file_references_json", JsonSerializer.Serialize(intent.PayloadFileReferences, JsonOptions));
        cmd.Parameters.AddWithValue("$actor", intent.Actor);
        cmd.Parameters.AddWithValue("$channel", intent.Channel);
        cmd.Parameters.AddWithValue("$authentication_assurance", intent.AuthenticationAssurance);
        cmd.Parameters.AddWithValue("$created_at", intent.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$status", intent.Status.ToString());
        cmd.Parameters.AddWithValue("$claim_owner", intent.ClaimOwner is null ? DBNull.Value : intent.ClaimOwner);
        cmd.Parameters.AddWithValue("$claimed_at", intent.ClaimedAt is null ? DBNull.Value : intent.ClaimedAt.Value.ToString("O"));
        cmd.Parameters.AddWithValue("$completed_at", intent.CompletedAt is null ? DBNull.Value : intent.CompletedAt.Value.ToString("O"));
        cmd.Parameters.AddWithValue("$outcome", intent.Outcome is null ? DBNull.Value : intent.Outcome);
        cmd.Parameters.AddWithValue("$actor_kind", intent.ActorKind.ToString());
    }

    private static OperatorIntentRecord ReadIntent(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            JsonSerializer.Deserialize<IReadOnlyList<string>>(reader.GetString(6), JsonOptions) ?? [],
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            DateTimeOffset.Parse(reader.GetString(10)),
            Enum.Parse<OperatorIntentStatus>(reader.GetString(11), ignoreCase: true),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)),
            reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14)),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            ActorKind: reader.IsDBNull(16)
                ? OperatorActorKind.Human
                : Enum.Parse<OperatorActorKind>(reader.GetString(16), ignoreCase: true));

    private const string SelectColumns = """
        SELECT id, idempotency_key, verb, goal_id, task_id, payload_json,
               payload_file_references_json, actor, channel, authentication_assurance,
               created_at, status, claim_owner, claimed_at, completed_at, outcome, actor_kind
        FROM operator_intents
        """;

    private static void RunNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string definition)
    {
        if (ColumnExists(conn, table, column))
            return;

        AddColumnTolerant(conn, table, column, definition);
    }

    internal static void AddColumnTolerant(SqliteConnection conn, string table, string column, string definition)
    {
        try
        {
            RunNonQuery(conn, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
        }
        catch (SqliteException) when (ColumnExists(conn, table, column))
        {
            // Another initializer added the column after our schema check.
        }
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var inspect = conn.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table})";
        using var reader = inspect.ExecuteReader();
        while (reader.Read())
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
