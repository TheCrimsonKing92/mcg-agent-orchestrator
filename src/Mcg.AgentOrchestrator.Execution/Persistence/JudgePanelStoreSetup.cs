using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class JudgePanelStoreSetup
{
    public static void Setup(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=False;");
        connection.Open();
        RunNonQuery(connection, "PRAGMA busy_timeout=30000");
        var state = StoreSchemaVersions.Verify(connection, StoreSchemaRegistry.JudgePanel);
        if (state is StoreSchemaState.Current or StoreSchemaState.Newer)
            return;

        RunNonQuery(connection, "BEGIN IMMEDIATE");
        try
        {
            RunNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS panel_cases (
                    case_id TEXT PRIMARY KEY, goal_id TEXT NOT NULL, candidate_sha TEXT NOT NULL,
                    base_sha TEXT NOT NULL, criteria_version TEXT NOT NULL, trigger_id TEXT NOT NULL,
                    key_json TEXT NOT NULL, packet_hash TEXT NOT NULL, enrollment_window INTEGER,
                    admitted INTEGER NOT NULL, status TEXT NOT NULL, claim_token TEXT, claimed_at TEXT,
                    terminal TEXT, reason TEXT, event_written INTEGER NOT NULL DEFAULT 0,
                    UNIQUE(goal_id, candidate_sha, base_sha, criteria_version, trigger_id));
                CREATE INDEX IF NOT EXISTS panel_queue ON panel_cases(status);
                CREATE TABLE IF NOT EXISTS panel_calls (
                    case_id TEXT NOT NULL, judge TEXT NOT NULL, launched INTEGER NOT NULL,
                    outcome TEXT, result_json TEXT, PRIMARY KEY(case_id, judge));
                CREATE TABLE IF NOT EXISTS panel_judge_health (
                    judge TEXT PRIMARY KEY, consecutive_failures INTEGER NOT NULL,
                    state TEXT NOT NULL, suspended_case_id TEXT);
                CREATE TABLE IF NOT EXISTS panel_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS panel_source_cursors (source TEXT PRIMARY KEY, cursor_json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS panel_source_retained (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS panel_goals (ordinal INTEGER PRIMARY KEY,
                    goal_id TEXT NOT NULL UNIQUE, created_at TEXT NOT NULL);
                """);
            StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.JudgePanel);
            RunNonQuery(connection, "COMMIT");
        }
        catch
        {
            try { RunNonQuery(connection, "ROLLBACK"); } catch { }
            throw;
        }
    }

    private static void RunNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
