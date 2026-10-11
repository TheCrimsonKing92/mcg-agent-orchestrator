using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class StewardTriggerStoreSetup
{
    public static void Setup(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=False;");
        connection.Open();
        RunNonQuery(connection, "PRAGMA busy_timeout=30000");
        var state = StoreSchemaVersions.Verify(connection, StoreSchemaRegistry.StewardTriggers);
        if (state is StoreSchemaState.Current or StoreSchemaState.Newer)
            return;

        RunNonQuery(connection, "BEGIN IMMEDIATE");
        try
        {
            RunNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS steward_triggers (
                  key TEXT PRIMARY KEY, goal_id TEXT NOT NULL, task_id TEXT NOT NULL,
                  candidate_sha TEXT NOT NULL, kind TEXT NOT NULL, trigger_json TEXT NOT NULL,
                  first_occurrence TEXT NOT NULL, status TEXT NOT NULL, claimed_at TEXT,
                  outcome TEXT, intent_id TEXT, repeat_question_raised INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX IF NOT EXISTS steward_triggers_queue ON steward_triggers(status, first_occurrence);
                CREATE TABLE IF NOT EXISTS steward_ready_rounds (
                  key TEXT PRIMARY KEY, output TEXT NOT NULL, claimed_goal_version INTEGER NOT NULL);
                """);
            StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.StewardTriggers);
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
