using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorStewardStoredTrigger(
    string Key,
    ConductorStewardTrigger Trigger,
    string Status,
    string? Outcome,
    string? IntentId,
    DateTimeOffset FirstOccurrence,
    bool RepeatQuestionRaised);

internal sealed class ConductorStewardTriggerStore(string path)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Pooling = false
    }.ToString();

    internal static string KeyFor(ConductorStewardTrigger trigger) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trigger.Identity))).ToLowerInvariant();

    internal ConductorStewardStoredTrigger Observe(ConductorStewardTrigger trigger)
    {
        using var connection = Open();
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT OR IGNORE INTO steward_triggers
                (key, goal_id, task_id, candidate_sha, kind, trigger_json, first_occurrence, status)
                VALUES ($key, $goal, $task, $sha, $kind, $json, $occurred, 'pending')
                """;
            insert.Parameters.AddWithValue("$key", KeyFor(trigger));
            insert.Parameters.AddWithValue("$goal", trigger.GoalId);
            insert.Parameters.AddWithValue("$task", trigger.TaskId);
            insert.Parameters.AddWithValue("$sha", trigger.CandidateSha);
            insert.Parameters.AddWithValue("$kind", trigger.Kind.ToString());
            insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(trigger));
            insert.Parameters.AddWithValue("$occurred", trigger.OccurredAt.ToString("O"));
            insert.ExecuteNonQuery();
        }
        return Get(connection, KeyFor(trigger))!;
    }

    internal ConductorStewardStoredTrigger? ClaimNext(DateTimeOffset now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var active = connection.CreateCommand();
        active.Transaction = transaction;
        active.CommandText = "SELECT COUNT(*) FROM steward_triggers WHERE status = 'in-flight'";
        if ((long)active.ExecuteScalar()! != 0)
        {
            transaction.Commit();
            return null;
        }
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT pending.key FROM steward_triggers AS pending
            WHERE pending.status = 'pending' AND NOT EXISTS (
                SELECT 1 FROM steward_triggers AS submitted
                WHERE submitted.goal_id = pending.goal_id AND submitted.task_id = pending.task_id
                  AND submitted.candidate_sha = pending.candidate_sha AND submitted.outcome = 'route-submitted')
            ORDER BY pending.rowid LIMIT 1
            """;
        var key = select.ExecuteScalar() as string;
        if (key is null)
        {
            transaction.Commit();
            return null;
        }
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE steward_triggers SET status = 'in-flight', claimed_at = $now WHERE key = $key";
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$key", key);
        update.ExecuteNonQuery();
        transaction.Commit();
        return Get(connection, key);
    }

    internal IReadOnlyList<ConductorStewardStoredTrigger> ExpireStale(DateTimeOffset before)
    {
        using var connection = Open();
        var keys = new List<string>();
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT key FROM steward_triggers WHERE status = 'in-flight' AND claimed_at < $before";
        select.Parameters.AddWithValue("$before", before.ToString("O"));
        using (var reader = select.ExecuteReader())
            while (reader.Read()) keys.Add(reader.GetString(0));
        var result = keys.Select(key => Get(connection, key)!).ToArray();
        foreach (var item in result) MarkServiced(item.Key, "timeout", null);
        return result;
    }

    internal void MarkServiced(string key, string outcome, string? intentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE steward_triggers SET status = 'serviced', outcome = $outcome, intent_id = $intent WHERE key = $key";
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$intent", (object?)intentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    internal void MarkRouteApplied(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE steward_triggers SET outcome = 'route-applied' WHERE key = $key AND outcome = 'route-submitted'";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    internal void MarkRouteRejected(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE steward_triggers SET outcome = 'route-rejected' WHERE key = $key AND outcome = 'route-submitted'";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    internal IReadOnlyList<ConductorStewardStoredTrigger> SubmittedRoutes()
    {
        using var connection = Open();
        return Query(connection, "SELECT key FROM steward_triggers WHERE outcome = 'route-submitted'");
    }

    internal bool HasAppliedRoute(ConductorStewardTrigger trigger)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM steward_triggers
            WHERE goal_id = $goal AND task_id = $task AND candidate_sha = $sha AND outcome = 'route-applied'
            """;
        command.Parameters.AddWithValue("$goal", trigger.GoalId);
        command.Parameters.AddWithValue("$task", trigger.TaskId);
        command.Parameters.AddWithValue("$sha", trigger.CandidateSha);
        return (long)command.ExecuteScalar()! > 0;
    }

    internal void MarkRepeatQuestionRaised(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE steward_triggers SET repeat_question_raised = 1 WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var databasePath = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS steward_triggers (
              key TEXT PRIMARY KEY, goal_id TEXT NOT NULL, task_id TEXT NOT NULL,
              candidate_sha TEXT NOT NULL, kind TEXT NOT NULL, trigger_json TEXT NOT NULL,
              first_occurrence TEXT NOT NULL, status TEXT NOT NULL, claimed_at TEXT,
              outcome TEXT, intent_id TEXT, repeat_question_raised INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS steward_triggers_queue ON steward_triggers(status, first_occurrence);
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    private static ConductorStewardStoredTrigger? Get(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT trigger_json, status, outcome, intent_id, first_occurrence, repeat_question_raised FROM steward_triggers WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new ConductorStewardStoredTrigger(key,
            JsonSerializer.Deserialize<ConductorStewardTrigger>(reader.GetString(0))!,
            reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4)), reader.GetInt64(5) != 0);
    }

    private static IReadOnlyList<ConductorStewardStoredTrigger> Query(SqliteConnection connection, string sql)
    {
        var keys = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read()) keys.Add(reader.GetString(0));
        }
        return keys.Select(key => Get(connection, key)!).ToArray();
    }
}
