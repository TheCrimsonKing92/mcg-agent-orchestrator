using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class CollaborationItemStore
{
    public static CollaborationItemStore ForDirectory(string directory) =>
        new(Path.Combine(directory, "collaboration-items.db"));

    private string ConnectionString => $"Data Source={_dbPath};Mode=ReadWriteCreate;Pooling=False;";

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    // Bounded retry on a transient SQLITE_BUSY/LOCKED: busy_timeout (30s) handles the simple lock-wait,
    // but the deadlock-avoidance path can still surface an immediate BUSY; this turns that into a brief
    // wait instead of a fatal throw. Mirrors SqliteOrchestratorStateRepository (matching the loop
    // critical-path stores so a write concurrent with operator-listen never hard-fails).
    private const int MaxBusyRetries = 6;

    private static bool IsTransientLock(SqliteException ex) =>
        ex.SqliteErrorCode == 5 /* SQLITE_BUSY */ || ex.SqliteErrorCode == 6 /* SQLITE_LOCKED */;

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
            CREATE TABLE IF NOT EXISTS collaboration_items (
                id              TEXT PRIMARY KEY,
                type            TEXT NOT NULL,
                goal_id         TEXT,
                status          TEXT NOT NULL,
                subject         TEXT NOT NULL,
                body            TEXT NOT NULL,
                correlation_key TEXT,
                raised_at       TEXT NOT NULL,
                resolved_at     TEXT,
                resolution      TEXT
            )
            """);
        RunNonQuery(conn, """
            CREATE INDEX IF NOT EXISTS idx_collaboration_items_correlation
                ON collaboration_items (correlation_key)
            """);
        RunNonQuery(conn, """
            CREATE INDEX IF NOT EXISTS idx_collaboration_items_goal_id
                ON collaboration_items (goal_id)
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS collaboration_item_actions (
                correlation_key              TEXT NOT NULL,
                action_index                 INTEGER NOT NULL,
                label                        TEXT NOT NULL,
                command                      TEXT NOT NULL,
                requires_confirmation        INTEGER NOT NULL,
                requires_input               INTEGER NOT NULL,
                expected_goal_state_version  INTEGER,
                expires_at                   TEXT NOT NULL,
                consumed_at                  TEXT,
                rendered_content_hash        TEXT,
                PRIMARY KEY (correlation_key, action_index)
            )
            """);
        RunNonQuery(conn, """
            CREATE INDEX IF NOT EXISTS idx_collaboration_item_actions_expiry
                ON collaboration_item_actions (expires_at)
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS collaboration_decision_audit (
                id                           INTEGER PRIMARY KEY AUTOINCREMENT,
                correlation_key              TEXT NOT NULL,
                action_index                 INTEGER,
                actor_id                     TEXT NOT NULL,
                interaction_id               TEXT NOT NULL UNIQUE,
                outcome                      TEXT NOT NULL,
                command                      TEXT,
                rejection_reason             TEXT,
                expected_goal_state_version  INTEGER,
                actual_goal_state_version    INTEGER,
                rendered_content_hash        TEXT,
                decided_at                   TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, """
            CREATE INDEX IF NOT EXISTS idx_collaboration_decision_audit_correlation
                ON collaboration_decision_audit (correlation_key)
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS collaboration_decision_requests (
                id                       TEXT PRIMARY KEY,
                kind                     TEXT NOT NULL,
                goal_id                  TEXT,
                subject                  TEXT NOT NULL,
                rendered_text            TEXT NOT NULL,
                template_version         TEXT NOT NULL,
                evidence_manifest_json   TEXT NOT NULL,
                evidence_manifest_hash   TEXT NOT NULL,
                expires_at               TEXT NOT NULL,
                default_disposition      TEXT NOT NULL,
                blocking_impact_json     TEXT NOT NULL,
                reuse_scopes_json        TEXT NOT NULL,
                created_at               TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, """
            CREATE INDEX IF NOT EXISTS idx_collaboration_decision_requests_goal
                ON collaboration_decision_requests (goal_id)
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS collaboration_decision_request_actions (
                request_id                   TEXT NOT NULL,
                action_ref                   TEXT NOT NULL,
                label                        TEXT NOT NULL,
                kind                         TEXT NOT NULL,
                required_tier                TEXT NOT NULL,
                expires_at                   TEXT NOT NULL,
                expected_goal_state_version  INTEGER,
                PRIMARY KEY (request_id, action_ref)
            )
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS collaboration_decision_receipts (
                id                           TEXT PRIMARY KEY,
                request_id                   TEXT NOT NULL UNIQUE,
                rendered_text                TEXT NOT NULL,
                template_version             TEXT NOT NULL,
                evidence_manifest_json       TEXT NOT NULL,
                evidence_manifest_hash       TEXT NOT NULL,
                actor_id                     TEXT NOT NULL,
                channel                      TEXT NOT NULL,
                authentication_assurance     TEXT NOT NULL,
                expected_goal_state_version  INTEGER,
                action_ref                   TEXT NOT NULL,
                response_value               TEXT NOT NULL,
                selected_reuse_scope         TEXT NOT NULL,
                permanent_policy_proposed    INTEGER NOT NULL,
                recorded_at                  TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS collaboration_notification_deliveries (
                id                 TEXT PRIMARY KEY,
                request_id         TEXT NOT NULL,
                channel            TEXT NOT NULL,
                target             TEXT NOT NULL,
                content_hash       TEXT NOT NULL,
                delivered_at       TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS collaboration_effect_receipts (
                id                           TEXT PRIMARY KEY,
                request_id                   TEXT NOT NULL,
                decision_receipt_id          TEXT NOT NULL,
                action_ref                   TEXT NOT NULL,
                status                       TEXT NOT NULL,
                expected_goal_state_version  INTEGER,
                actual_goal_state_version    INTEGER,
                result                       TEXT NOT NULL,
                recorded_at                  TEXT NOT NULL,
                UNIQUE (request_id, decision_receipt_id, action_ref)
            )
            """);
    }

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
}
