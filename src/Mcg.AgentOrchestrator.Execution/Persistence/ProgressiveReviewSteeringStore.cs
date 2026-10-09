using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum ProgressiveReviewSteerIntentStatus
{
    Pending,
    Running,
    Completed
}

public sealed record ProgressiveReviewSteerIntent(
    string Id,
    string GoalId,
    string TaskId,
    string Role,
    string RoundKey,
    string TriggerGlanceId,
    string InputsHash,
    DateTimeOffset GlanceVerdictTimestamp,
    string MisdirectionEvidence,
    string CorrectiveDirection,
    string GuidanceText,
    DateTimeOffset CreatedAt,
    ProgressiveReviewSteerIntentStatus Status = ProgressiveReviewSteerIntentStatus.Pending,
    DateTimeOffset? CompletedAt = null);

public sealed record ProgressiveReviewSteerReceipt(
    string Id,
    string IntentId,
    string GoalId,
    string TaskId,
    string RoundKey,
    string TriggerGlanceId,
    string InputsHash,
    string MisdirectionEvidence,
    string CancelConfirmation,
    string Decision,
    IReadOnlyList<string> AdmissionChecks,
    string GuidanceText,
    int CancelledInputTokens,
    int CancelledOutputTokens,
    int SteeredInputTokens,
    int SteeredOutputTokens,
    long CancelledWallMilliseconds,
    long SteeredWallMilliseconds,
    string Outcome,
    DateTimeOffset CreatedAt);

public interface IProgressiveReviewSteeringStore
{
    Task EnqueueIntentAsync(ProgressiveReviewSteerIntent intent, CancellationToken cancellationToken = default);

    Task<ProgressiveReviewSteerIntent?> ReserveNextPendingAsync(
        string goalId,
        CancellationToken cancellationToken = default);

    Task<int> CountReceiptsForRoundAsync(string roundKey, CancellationToken cancellationToken = default);

    Task CompleteIntentAsync(string intentId, DateTimeOffset completedAt, CancellationToken cancellationToken = default);

    Task AppendReceiptAsync(ProgressiveReviewSteerReceipt receipt, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProgressiveReviewSteerReceipt>> ListReceiptsAsync(CancellationToken cancellationToken = default);
}

public sealed class InMemoryProgressiveReviewSteeringStore : IProgressiveReviewSteeringStore
{
    private readonly List<ProgressiveReviewSteerIntent> _intents = [];
    private readonly List<ProgressiveReviewSteerReceipt> _receipts = [];

    public IReadOnlyList<ProgressiveReviewSteerIntent> Intents => _intents;

    public IReadOnlyList<ProgressiveReviewSteerReceipt> Receipts => _receipts;

    public Task EnqueueIntentAsync(ProgressiveReviewSteerIntent intent, CancellationToken cancellationToken = default)
    {
        if (_intents.Any(candidate => candidate.Id.Equals(intent.Id, StringComparison.Ordinal)))
            return Task.CompletedTask;

        _intents.Add(intent);
        return Task.CompletedTask;
    }

    public Task<ProgressiveReviewSteerIntent?> ReserveNextPendingAsync(
        string goalId,
        CancellationToken cancellationToken = default)
    {
        var index = _intents.FindIndex(candidate =>
            candidate.GoalId.Equals(goalId, StringComparison.Ordinal) &&
            candidate.Status != ProgressiveReviewSteerIntentStatus.Completed);
        if (index < 0)
            return Task.FromResult<ProgressiveReviewSteerIntent?>(null);

        var reserved = _intents[index] with { Status = ProgressiveReviewSteerIntentStatus.Running };
        _intents[index] = reserved;
        return Task.FromResult<ProgressiveReviewSteerIntent?>(reserved);
    }

    public Task<int> CountReceiptsForRoundAsync(string roundKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(_receipts.Count(receipt => receipt.RoundKey.Equals(roundKey, StringComparison.Ordinal)));

    public Task CompleteIntentAsync(string intentId, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
    {
        var index = _intents.FindIndex(candidate => candidate.Id.Equals(intentId, StringComparison.Ordinal));
        if (index >= 0)
            _intents[index] = _intents[index] with { Status = ProgressiveReviewSteerIntentStatus.Completed, CompletedAt = completedAt };

        return Task.CompletedTask;
    }

    public Task AppendReceiptAsync(ProgressiveReviewSteerReceipt receipt, CancellationToken cancellationToken = default)
    {
        _receipts.Add(receipt);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProgressiveReviewSteerReceipt>> ListReceiptsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProgressiveReviewSteerReceipt>>(_receipts.ToList());
}

public sealed class SqliteProgressiveReviewSteeringStore : IProgressiveReviewSteeringStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _dbPath;
    private readonly bool _readOnly;

    public SqliteProgressiveReviewSteeringStore(string dbPath) : this(dbPath, readOnly: false)
    {
        ProgressiveReviewSteeringStoreSetup.Setup(dbPath);
    }

    private SqliteProgressiveReviewSteeringStore(string dbPath, bool readOnly)
    {
        _dbPath = dbPath;
        _readOnly = readOnly;
    }

    public static SqliteProgressiveReviewSteeringStore OpenReadOnly(string dbPath)
    {
        if (!File.Exists(dbPath))
            throw SchemaSetupRequired(dbPath, StoreSchemaState.Missing);

        var store = new SqliteProgressiveReviewSteeringStore(dbPath, readOnly: true);
        using var conn = store.OpenConnection();
        var state = StoreSchemaVersions.Verify(conn, StoreSchemaRegistry.ProgressiveReviewSteering);
        if (state != StoreSchemaState.Current)
            throw SchemaSetupRequired(dbPath, state);
        return store;
    }

    private static InvalidOperationException SchemaSetupRequired(string dbPath, StoreSchemaState state) =>
        new($"Progressive review steering store '{dbPath}' schema is {state} (expected version {StoreSchemaRegistry.ProgressiveReviewSteering.CurrentVersion}); run setup.");

    public static SqliteProgressiveReviewSteeringStore ForDirectory(string orchestratorDirectory) =>
        new(Path.Combine(orchestratorDirectory, "progressive-review-steering.db"));

    private string ConnectionString => _readOnly
        ? new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString()
        : $"Data Source={_dbPath};Mode=ReadWriteCreate;Pooling=False;";

    public async Task EnqueueIntentAsync(ProgressiveReviewSteerIntent intent, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO progressive_review_steer_intents (
                id, goal_id, task_id, role, round_key, trigger_glance_id, inputs_hash,
                glance_verdict_timestamp, misdirection_evidence, corrective_direction,
                guidance_text, created_at, status, completed_at
            )
            VALUES (
                $id, $goal_id, $task_id, $role, $round_key, $trigger_glance_id, $inputs_hash,
                $glance_verdict_timestamp, $misdirection_evidence, $corrective_direction,
                $guidance_text, $created_at, $status, $completed_at
            )
            """;
        BindIntent(cmd, intent);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ProgressiveReviewSteerIntent?> ReserveNextPendingAsync(
        string goalId,
        CancellationToken cancellationToken = default)
    {
        if (_readOnly)
            throw new InvalidOperationException("ReserveNextPendingAsync is a writer operation unavailable on a read-only progressive review steering store.");

        await using var conn = OpenConnection();
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        var intent = await ReadNextPendingAsync(conn, goalId, cancellationToken);
        if (intent is null)
        {
            await tx.CommitAsync(cancellationToken);
            return null;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE progressive_review_steer_intents
            SET status = $status
            WHERE id = $id AND status <> $completed
            """;
        cmd.Parameters.AddWithValue("$status", ProgressiveReviewSteerIntentStatus.Running.ToString());
        cmd.Parameters.AddWithValue("$completed", ProgressiveReviewSteerIntentStatus.Completed.ToString());
        cmd.Parameters.AddWithValue("$id", intent.Id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return intent with { Status = ProgressiveReviewSteerIntentStatus.Running };
    }

    public async Task<int> CountReceiptsForRoundAsync(string roundKey, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM progressive_review_steer_receipts WHERE round_key = $round_key";
        cmd.Parameters.AddWithValue("$round_key", roundKey);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    public async Task CompleteIntentAsync(string intentId, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE progressive_review_steer_intents
            SET status = $status, completed_at = $completed_at
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$status", ProgressiveReviewSteerIntentStatus.Completed.ToString());
        cmd.Parameters.AddWithValue("$completed_at", completedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$id", intentId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendReceiptAsync(ProgressiveReviewSteerReceipt receipt, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO progressive_review_steer_receipts (
                id, intent_id, goal_id, task_id, round_key, trigger_glance_id, inputs_hash,
                misdirection_evidence, cancel_confirmation, decision, admission_checks_json,
                guidance_text, cancelled_input_tokens, cancelled_output_tokens, steered_input_tokens,
                steered_output_tokens, cancelled_wall_ms, steered_wall_ms, outcome, created_at
            )
            VALUES (
                $id, $intent_id, $goal_id, $task_id, $round_key, $trigger_glance_id, $inputs_hash,
                $misdirection_evidence, $cancel_confirmation, $decision, $admission_checks_json,
                $guidance_text, $cancelled_input_tokens, $cancelled_output_tokens, $steered_input_tokens,
                $steered_output_tokens, $cancelled_wall_ms, $steered_wall_ms, $outcome, $created_at
            )
            """;
        BindReceipt(cmd, receipt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProgressiveReviewSteerReceipt>> ListReceiptsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, intent_id, goal_id, task_id, round_key, trigger_glance_id, inputs_hash,
                   misdirection_evidence, cancel_confirmation, decision, admission_checks_json,
                   guidance_text, cancelled_input_tokens, cancelled_output_tokens, steered_input_tokens,
                   steered_output_tokens, cancelled_wall_ms, steered_wall_ms, outcome, created_at
            FROM progressive_review_steer_receipts
            ORDER BY created_at, id
            """;
        var receipts = new List<ProgressiveReviewSteerReceipt>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            receipts.Add(ReadReceipt(reader));

        return receipts;
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=30000";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private static async Task<ProgressiveReviewSteerIntent?> ReadNextPendingAsync(
        SqliteConnection conn,
        string goalId,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, goal_id, task_id, role, round_key, trigger_glance_id, inputs_hash,
                   glance_verdict_timestamp, misdirection_evidence, corrective_direction,
                   guidance_text, created_at, status, completed_at
            FROM progressive_review_steer_intents
            WHERE goal_id = $goal_id AND status <> $completed
            ORDER BY CASE status WHEN 'Running' THEN 0 ELSE 1 END, created_at, id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$goal_id", goalId);
        cmd.Parameters.AddWithValue("$completed", ProgressiveReviewSteerIntentStatus.Completed.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadIntent(reader) : null;
    }

    private static void BindIntent(SqliteCommand cmd, ProgressiveReviewSteerIntent intent)
    {
        cmd.Parameters.AddWithValue("$id", intent.Id);
        cmd.Parameters.AddWithValue("$goal_id", intent.GoalId);
        cmd.Parameters.AddWithValue("$task_id", intent.TaskId);
        cmd.Parameters.AddWithValue("$role", intent.Role);
        cmd.Parameters.AddWithValue("$round_key", intent.RoundKey);
        cmd.Parameters.AddWithValue("$trigger_glance_id", intent.TriggerGlanceId);
        cmd.Parameters.AddWithValue("$inputs_hash", intent.InputsHash);
        cmd.Parameters.AddWithValue("$glance_verdict_timestamp", intent.GlanceVerdictTimestamp.ToString("O"));
        cmd.Parameters.AddWithValue("$misdirection_evidence", intent.MisdirectionEvidence);
        cmd.Parameters.AddWithValue("$corrective_direction", intent.CorrectiveDirection);
        cmd.Parameters.AddWithValue("$guidance_text", intent.GuidanceText);
        cmd.Parameters.AddWithValue("$created_at", intent.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$status", intent.Status.ToString());
        cmd.Parameters.AddWithValue("$completed_at", intent.CompletedAt is null ? DBNull.Value : intent.CompletedAt.Value.ToString("O"));
    }

    private static ProgressiveReviewSteerIntent ReadIntent(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7)),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            DateTimeOffset.Parse(reader.GetString(11)),
            Enum.Parse<ProgressiveReviewSteerIntentStatus>(reader.GetString(12), ignoreCase: true),
            reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)));

    private static void BindReceipt(SqliteCommand cmd, ProgressiveReviewSteerReceipt receipt)
    {
        cmd.Parameters.AddWithValue("$id", receipt.Id);
        cmd.Parameters.AddWithValue("$intent_id", receipt.IntentId);
        cmd.Parameters.AddWithValue("$goal_id", receipt.GoalId);
        cmd.Parameters.AddWithValue("$task_id", receipt.TaskId);
        cmd.Parameters.AddWithValue("$round_key", receipt.RoundKey);
        cmd.Parameters.AddWithValue("$trigger_glance_id", receipt.TriggerGlanceId);
        cmd.Parameters.AddWithValue("$inputs_hash", receipt.InputsHash);
        cmd.Parameters.AddWithValue("$misdirection_evidence", receipt.MisdirectionEvidence);
        cmd.Parameters.AddWithValue("$cancel_confirmation", receipt.CancelConfirmation);
        cmd.Parameters.AddWithValue("$decision", receipt.Decision);
        cmd.Parameters.AddWithValue("$admission_checks_json", JsonSerializer.Serialize(receipt.AdmissionChecks, JsonOptions));
        cmd.Parameters.AddWithValue("$guidance_text", receipt.GuidanceText);
        cmd.Parameters.AddWithValue("$cancelled_input_tokens", receipt.CancelledInputTokens);
        cmd.Parameters.AddWithValue("$cancelled_output_tokens", receipt.CancelledOutputTokens);
        cmd.Parameters.AddWithValue("$steered_input_tokens", receipt.SteeredInputTokens);
        cmd.Parameters.AddWithValue("$steered_output_tokens", receipt.SteeredOutputTokens);
        cmd.Parameters.AddWithValue("$cancelled_wall_ms", receipt.CancelledWallMilliseconds);
        cmd.Parameters.AddWithValue("$steered_wall_ms", receipt.SteeredWallMilliseconds);
        cmd.Parameters.AddWithValue("$outcome", receipt.Outcome);
        cmd.Parameters.AddWithValue("$created_at", receipt.CreatedAt.ToString("O"));
    }

    private static ProgressiveReviewSteerReceipt ReadReceipt(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            JsonSerializer.Deserialize<IReadOnlyList<string>>(reader.GetString(10), JsonOptions) ?? [],
            reader.GetString(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetInt32(14),
            reader.GetInt32(15),
            reader.GetInt64(16),
            reader.GetInt64(17),
            reader.GetString(18),
            DateTimeOffset.Parse(reader.GetString(19)));
}
