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

internal sealed record ConductorStewardReadyRound(
    ConductorStewardStoredTrigger Stored, string Output, long ClaimedGoalVersion);

internal sealed class ConductorStewardTriggerStore(string path)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Pooling = false
    }.ToString();

    internal static string KeyFor(ConductorStewardTrigger trigger) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trigger.Identity))).ToLowerInvariant();

    internal bool NeedsInspection(string goalId, string taskId, string candidateSha,
        ConductorStewardTriggerKind kind)
    {
        var identity = $"{goalId}:{taskId}:{candidateSha}:{kind}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT outcome, repeat_question_raised FROM steward_triggers WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return true;
        return !reader.IsDBNull(0) && reader.GetString(0) == "route-applied" && reader.GetInt64(1) == 0;
    }

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

    internal ConductorStewardStoredTrigger? ClaimNext(DateTimeOffset now, IReadOnlySet<string> eligibleGoalIds)
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
            SELECT pending.key, pending.goal_id FROM steward_triggers AS pending
            WHERE pending.status = 'pending' AND NOT EXISTS (
                SELECT 1 FROM steward_triggers AS submitted
                WHERE submitted.goal_id = pending.goal_id AND submitted.task_id = pending.task_id
                  AND submitted.candidate_sha = pending.candidate_sha AND submitted.outcome = 'route-submitted')
            ORDER BY pending.rowid
            """;
        string? key = null;
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!eligibleGoalIds.Contains(reader.GetString(1))) continue;
                key = reader.GetString(0);
                break;
            }
        }
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

    internal void ReleaseClaim(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE steward_triggers SET status = 'pending', claimed_at = NULL
            WHERE key = $key AND status = 'in-flight'
            """;
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    internal void PreserveCompletedRound(string key, string output, long claimedGoalVersion)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE steward_triggers SET status = 'ready' WHERE key = $key AND status = 'in-flight'";
        update.Parameters.AddWithValue("$key", key);
        if (update.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Steward trigger {key} is not in flight at completed-round handoff.");
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO steward_ready_rounds (key, output, claimed_goal_version)
            VALUES ($key, $output, $version)
            ON CONFLICT(key) DO UPDATE SET output = excluded.output,
                claimed_goal_version = excluded.claimed_goal_version
            """;
        insert.Parameters.AddWithValue("$key", key);
        insert.Parameters.AddWithValue("$output", output);
        insert.Parameters.AddWithValue("$version", claimedGoalVersion);
        insert.ExecuteNonQuery();
        transaction.Commit();
    }

    internal ConductorStewardReadyRound? ClaimReady(DateTimeOffset now, IReadOnlySet<string> eligibleGoalIds)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT st.key, ready.output, ready.claimed_goal_version, st.goal_id
            FROM steward_triggers AS st
            JOIN steward_ready_rounds AS ready ON ready.key = st.key
            WHERE st.status = 'ready' ORDER BY st.rowid
            """;
        string? key = null;
        string? output = null;
        long version = 0;
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!eligibleGoalIds.Contains(reader.GetString(3))) continue;
                key = reader.GetString(0);
                output = reader.GetString(1);
                version = reader.GetInt64(2);
                break;
            }
        }
        if (key is null)
        {
            transaction.Commit();
            return null;
        }
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE steward_triggers SET status = 'in-flight', claimed_at = $now WHERE key = $key AND status = 'ready'";
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$key", key);
        if (update.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Steward ready round {key} could not be claimed.");
        transaction.Commit();
        return new ConductorStewardReadyRound(Get(connection, key)!, output!, version);
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
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE steward_triggers SET status = 'serviced', outcome = $outcome, intent_id = $intent WHERE key = $key";
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$intent", (object?)intentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM steward_ready_rounds WHERE key = $key";
        delete.Parameters.AddWithValue("$key", key);
        delete.ExecuteNonQuery();
        transaction.Commit();
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
            CREATE TABLE IF NOT EXISTS steward_ready_rounds (
              key TEXT PRIMARY KEY, output TEXT NOT NULL, claimed_goal_version INTEGER NOT NULL);
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
