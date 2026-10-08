using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record OperatorLesson(
    string Id,
    string Situation,
    string Rule,
    IReadOnlyList<string> AppliesTo,
    IReadOnlyList<EvidenceManifestEntry> Evidence,
    string Actor,
    OperatorActorKind ActorKind,
    string Channel,
    DateTimeOffset RecordedAt,
    string? GoalId,
    string? RetireReason,
    string? RetiredBy,
    DateTimeOffset? RetiredAt,
    IReadOnlyList<EvidenceManifestEntry>? RetireEvidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UntilGoalId = null);

public enum OperatorLessonRetireResult { Retired, Replayed, UnknownLesson, AlreadyRetired }

public sealed class SqliteOperatorLessonStore(string databasePath)
{
    public const string DatabaseFileName = "operator-lessons.db";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _databasePath = Path.GetFullPath(databasePath);
    private readonly bool _readOnly;

    private SqliteOperatorLessonStore(string databasePath, bool readOnly) : this(databasePath) =>
        _readOnly = readOnly;

    public static SqliteOperatorLessonStore OpenReadOnly(string databasePath)
    {
        var store = new SqliteOperatorLessonStore(databasePath, readOnly: true);
        using var connection = store.OpenReadOnlyConnection();
        return store;
    }

    public bool HasRecordSource(string sourceIntentId) => HasStoredSource("lessons", sourceIntentId);

    public bool HasRetirementSource(string sourceIntentId) => HasStoredSource("lesson_retirements", sourceIntentId);

    public bool TryAppendLesson(OperatorLesson lesson, string sourceIntentId)
    {
        using var connection = OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO lessons
                (id, source_intent_id, situation, rule, applies_to_json, evidence_json,
                 actor, actor_kind, channel, recorded_at, goal_id, until_goal_id)
            VALUES ($id, $source, $situation, $rule, $tags, $evidence,
                    $actor, $kind, $channel, $at, $goal, $until)
            """;
        command.Parameters.AddWithValue("$id", lesson.Id);
        command.Parameters.AddWithValue("$source", sourceIntentId);
        command.Parameters.AddWithValue("$situation", lesson.Situation);
        command.Parameters.AddWithValue("$rule", lesson.Rule);
        command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(lesson.AppliesTo, JsonOptions));
        command.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(lesson.Evidence, JsonOptions));
        command.Parameters.AddWithValue("$actor", lesson.Actor);
        command.Parameters.AddWithValue("$kind", lesson.ActorKind.ToString());
        command.Parameters.AddWithValue("$channel", lesson.Channel);
        command.Parameters.AddWithValue("$at", lesson.RecordedAt.ToString("O"));
        command.Parameters.AddWithValue("$goal", (object?)lesson.GoalId ?? DBNull.Value);
        command.Parameters.AddWithValue("$until", (object?)lesson.UntilGoalId ?? DBNull.Value);
        var inserted = command.ExecuteNonQuery() == 1;
        if (!inserted && !HasSource(connection, "lessons", sourceIntentId))
            throw new InvalidOperationException($"Lesson id '{lesson.Id}' is already owned by another intent.");
        return inserted;
    }

    public OperatorLessonRetireResult TryAppendRetirement(
        string lessonId, string sourceIntentId, string reason,
        IReadOnlyList<EvidenceManifestEntry> evidence, string actor,
        OperatorActorKind actorKind, string channel, DateTimeOffset at)
    {
        EnsureWritable();
        if (!File.Exists(_databasePath)) return OperatorLessonRetireResult.UnknownLesson;
        using var connection = OpenWritable();
        using var transaction = connection.BeginTransaction();
        if (HasSource(connection, "lesson_retirements", sourceIntentId, transaction))
            return OperatorLessonRetireResult.Replayed;
        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT EXISTS(SELECT 1 FROM lessons WHERE id=$id)";
            check.Parameters.AddWithValue("$id", lessonId);
            if (Convert.ToInt32(check.ExecuteScalar()) == 0)
                return OperatorLessonRetireResult.UnknownLesson;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO lesson_retirements
                (lesson_id, source_intent_id, reason, evidence_json, actor, actor_kind, channel, retired_at)
            VALUES ($id, $source, $reason, $evidence, $actor, $kind, $channel, $at)
            """;
        command.Parameters.AddWithValue("$id", lessonId);
        command.Parameters.AddWithValue("$source", sourceIntentId);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(evidence, JsonOptions));
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$kind", actorKind.ToString());
        command.Parameters.AddWithValue("$channel", channel);
        command.Parameters.AddWithValue("$at", at.ToString("O"));
        if (command.ExecuteNonQuery() != 1)
            return OperatorLessonRetireResult.AlreadyRetired;
        transaction.Commit();
        return OperatorLessonRetireResult.Retired;
    }

    public IReadOnlyList<OperatorLesson> List(bool includeRetired = false, string? appliesTo = null)
    {
        if (!File.Exists(_databasePath)) return [];
        using var connection = OpenReadOnlyConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT l.id, l.situation, l.rule, l.applies_to_json, l.evidence_json,
                   l.actor, l.actor_kind, l.channel, l.recorded_at, l.goal_id,
                   r.reason, r.actor, r.retired_at, r.evidence_json, l.until_goal_id
            FROM lessons l LEFT JOIN lesson_retirements r ON r.lesson_id=l.id
            ORDER BY l.recorded_at DESC, l.id
            """;
        using var reader = command.ExecuteReader();
        var lessons = new List<OperatorLesson>();
        while (reader.Read())
        {
            var retired = !reader.IsDBNull(10);
            if (retired && !includeRetired) continue;
            var tags = JsonSerializer.Deserialize<string[]>(reader.GetString(3), JsonOptions) ?? [];
            if (appliesTo is not null && !tags.Contains(appliesTo, StringComparer.OrdinalIgnoreCase)) continue;
            lessons.Add(new OperatorLesson(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), tags,
                JsonSerializer.Deserialize<EvidenceManifestEntry[]>(reader.GetString(4), JsonOptions) ?? [],
                reader.GetString(5), Enum.Parse<OperatorActorKind>(reader.GetString(6)),
                reader.GetString(7), DateTimeOffset.Parse(reader.GetString(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                retired ? reader.GetString(10) : null,
                retired ? reader.GetString(11) : null,
                retired ? DateTimeOffset.Parse(reader.GetString(12)) : null,
                retired ? JsonSerializer.Deserialize<EvidenceManifestEntry[]>(reader.GetString(13), JsonOptions) : null,
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }
        return lessons;
    }

    private SqliteConnection OpenWritable()
    {
        EnsureWritable();
        Setup(_databasePath);
        return OpenConnection(_databasePath, SqliteOpenMode.ReadWrite);
    }

    private void EnsureWritable()
    {
        if (_readOnly)
            throw new InvalidOperationException("Operator lessons store was opened read-only.");
    }

    public static void Setup(string databasePath)
    {
        var path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = OpenConnection(path, SqliteOpenMode.ReadWriteCreate);
        using var command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE";
        command.ExecuteNonQuery();
        try
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS lessons (
                    id TEXT PRIMARY KEY, source_intent_id TEXT NOT NULL UNIQUE,
                    situation TEXT NOT NULL, rule TEXT NOT NULL, applies_to_json TEXT NOT NULL,
                    evidence_json TEXT NOT NULL, actor TEXT NOT NULL, actor_kind TEXT NOT NULL,
                    channel TEXT NOT NULL, recorded_at TEXT NOT NULL, goal_id TEXT, until_goal_id TEXT);
                CREATE TABLE IF NOT EXISTS lesson_retirements (
                    lesson_id TEXT PRIMARY KEY REFERENCES lessons(id), source_intent_id TEXT NOT NULL UNIQUE,
                    reason TEXT NOT NULL, evidence_json TEXT NOT NULL, actor TEXT NOT NULL,
                    actor_kind TEXT NOT NULL, channel TEXT NOT NULL, retired_at TEXT NOT NULL);
                """;
            command.ExecuteNonQuery();
            if (!HasUntilGoalColumn(connection))
            {
                command.CommandText = "ALTER TABLE lessons ADD COLUMN until_goal_id TEXT";
                command.ExecuteNonQuery();
            }
            StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.OperatorLessons);
            command.CommandText = "COMMIT";
            command.ExecuteNonQuery();
        }
        catch
        {
            command.CommandText = "ROLLBACK";
            command.ExecuteNonQuery();
            throw;
        }
    }

    private static bool HasUntilGoalColumn(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('lessons') WHERE name='until_goal_id'";
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private SqliteConnection OpenReadOnlyConnection()
    {
        if (!File.Exists(_databasePath))
            throw SchemaSetupRequired(StoreSchemaState.Missing);
        var connection = OpenConnection(_databasePath, SqliteOpenMode.ReadOnly);
        try
        {
            var state = StoreSchemaVersions.Verify(connection, StoreSchemaRegistry.OperatorLessons);
            if (state != StoreSchemaState.Current)
                throw SchemaSetupRequired(state);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private InvalidOperationException SchemaSetupRequired(StoreSchemaState state) =>
        new($"Operator lessons store '{_databasePath}' schema is {state} (expected version {StoreSchemaRegistry.OperatorLessons.CurrentVersion}); run setup.");

    private static SqliteConnection OpenConnection(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private bool HasStoredSource(string table, string sourceIntentId)
    {
        if (!File.Exists(_databasePath)) return false;
        using var connection = OpenReadOnlyConnection();
        return HasSource(connection, table, sourceIntentId);
    }

    private static bool HasSource(SqliteConnection connection, string table, string source,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table} WHERE source_intent_id=$source)";
        command.Parameters.AddWithValue("$source", source);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }
}
