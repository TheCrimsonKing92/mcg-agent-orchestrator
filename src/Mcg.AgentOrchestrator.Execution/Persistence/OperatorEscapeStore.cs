using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record OperatorEscape(
    string Id, string GoalId, string Reason, IReadOnlyList<EvidenceManifestEntry> Evidence,
    string? FoundByGoalId, string Actor, OperatorActorKind ActorKind, string Channel,
    DateTimeOffset RecordedAt);

public sealed class SqliteOperatorEscapeStore(string databasePath)
{
    public const string DatabaseFileName = "operator-escapes.db";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _databasePath = Path.GetFullPath(databasePath);

    public bool HasRecordSource(string sourceIntentId)
    {
        if (!File.Exists(_databasePath)) return false;
        using var connection = OpenReadOnly();
        return HasSource(connection, sourceIntentId);
    }

    public bool TryAppendEscape(OperatorEscape record, string sourceIntentId)
    {
        using var connection = OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO escapes
                (id, source_intent_id, goal_id, reason, evidence_json, found_by_goal_id,
                 actor, actor_kind, channel, recorded_at)
            VALUES ($id, $source, $goal, $reason, $evidence, $found,
                    $actor, $kind, $channel, $at)
            """;
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$source", sourceIntentId);
        command.Parameters.AddWithValue("$goal", record.GoalId);
        command.Parameters.AddWithValue("$reason", record.Reason);
        command.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(record.Evidence, JsonOptions));
        command.Parameters.AddWithValue("$found", (object?)record.FoundByGoalId ?? DBNull.Value);
        command.Parameters.AddWithValue("$actor", record.Actor);
        command.Parameters.AddWithValue("$kind", record.ActorKind.ToString());
        command.Parameters.AddWithValue("$channel", record.Channel);
        command.Parameters.AddWithValue("$at", record.RecordedAt.ToString("O", CultureInfo.InvariantCulture));
        var inserted = command.ExecuteNonQuery() == 1;
        if (!inserted && !HasSource(connection, sourceIntentId))
            throw new InvalidOperationException($"Escape id '{record.Id}' is already owned by another intent.");
        return inserted;
    }

    public IReadOnlyList<OperatorEscape> List()
    {
        if (!File.Exists(_databasePath)) return [];
        using var connection = OpenReadOnly();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, goal_id, reason, evidence_json, found_by_goal_id,
                   actor, actor_kind, channel, recorded_at
            FROM escapes ORDER BY recorded_at, id
            """;
        using var reader = command.ExecuteReader();
        var records = new List<OperatorEscape>();
        while (reader.Read())
            records.Add(new OperatorEscape(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                JsonSerializer.Deserialize<EvidenceManifestEntry[]>(reader.GetString(3), JsonOptions) ?? [],
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
                Enum.Parse<OperatorActorKind>(reader.GetString(6)), reader.GetString(7),
                DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture)));
        return records;
    }

    private SqliteConnection OpenWritable()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS escapes (
                id TEXT PRIMARY KEY, source_intent_id TEXT NOT NULL UNIQUE,
                goal_id TEXT NOT NULL, reason TEXT NOT NULL, evidence_json TEXT NOT NULL,
                found_by_goal_id TEXT, actor TEXT NOT NULL, actor_kind TEXT NOT NULL,
                channel TEXT NOT NULL, recorded_at TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    private SqliteConnection OpenReadOnly()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static bool HasSource(SqliteConnection connection, string sourceIntentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM escapes WHERE source_intent_id=$source)";
        command.Parameters.AddWithValue("$source", sourceIntentId);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }
}
