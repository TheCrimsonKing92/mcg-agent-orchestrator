using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class ProgressiveReviewSteeringStoreSetup
{
    public static void Setup(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=False;");
        conn.Open();
        RunNonQuery(conn, "PRAGMA busy_timeout=30000");
        var state = StoreSchemaVersions.Verify(conn, StoreSchemaRegistry.ProgressiveReviewSteering);
        if (state is StoreSchemaState.Current or StoreSchemaState.Newer)
            return;

        RunNonQuery(conn, "BEGIN IMMEDIATE");
        try
        {
            RunNonQuery(conn, """
                CREATE TABLE IF NOT EXISTS progressive_review_steer_intents (
                    id                        TEXT PRIMARY KEY,
                    goal_id                   TEXT NOT NULL,
                    task_id                   TEXT NOT NULL,
                    role                      TEXT NOT NULL,
                    round_key                 TEXT NOT NULL,
                    trigger_glance_id         TEXT NOT NULL,
                    inputs_hash               TEXT NOT NULL,
                    glance_verdict_timestamp  TEXT NOT NULL,
                    misdirection_evidence     TEXT NOT NULL,
                    corrective_direction      TEXT NOT NULL,
                    guidance_text             TEXT NOT NULL,
                    created_at                TEXT NOT NULL,
                    status                    TEXT NOT NULL,
                    completed_at              TEXT
                )
                """);
            RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_progressive_review_steer_intents_pending ON progressive_review_steer_intents(goal_id, status, created_at)");
            RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_progressive_review_steer_intents_round ON progressive_review_steer_intents(round_key)");
            RunNonQuery(conn, """
                CREATE TABLE IF NOT EXISTS progressive_review_steer_receipts (
                    id                         TEXT PRIMARY KEY,
                    intent_id                  TEXT NOT NULL,
                    goal_id                    TEXT NOT NULL,
                    task_id                    TEXT NOT NULL,
                    round_key                  TEXT NOT NULL,
                    trigger_glance_id          TEXT NOT NULL,
                    inputs_hash                TEXT NOT NULL,
                    misdirection_evidence      TEXT NOT NULL,
                    cancel_confirmation        TEXT NOT NULL,
                    decision                   TEXT NOT NULL,
                    admission_checks_json      TEXT NOT NULL,
                    guidance_text              TEXT NOT NULL,
                    cancelled_input_tokens     INTEGER NOT NULL,
                    cancelled_output_tokens    INTEGER NOT NULL,
                    steered_input_tokens       INTEGER NOT NULL,
                    steered_output_tokens      INTEGER NOT NULL,
                    cancelled_wall_ms          INTEGER NOT NULL,
                    steered_wall_ms            INTEGER NOT NULL,
                    outcome                    TEXT NOT NULL,
                    created_at                 TEXT NOT NULL
                )
                """);
            RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_progressive_review_steer_receipts_round ON progressive_review_steer_receipts(round_key)");
            StoreSchemaVersions.UpgradeToCurrent(conn, StoreSchemaRegistry.ProgressiveReviewSteering);
            RunNonQuery(conn, "COMMIT");
        }
        catch
        {
            try { RunNonQuery(conn, "ROLLBACK"); } catch { }
            throw;
        }
    }

    private static void RunNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
