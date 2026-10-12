using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class RunEventStoreSetup
{
    public static void Setup(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var isNewDatabase = !File.Exists(dbPath) || new FileInfo(dbPath).Length == 0;
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=False;");
        connection.Open();
        RunNonQuery(connection, "PRAGMA busy_timeout=30000");
        if (isNewDatabase)
            RunNonQuery(connection, "PRAGMA auto_vacuum=INCREMENTAL");
        RunNonQuery(connection, "PRAGMA journal_mode=WAL");
        var state = StoreSchemaVersions.Verify(connection, StoreSchemaRegistry.RunEvents);
        if (state is StoreSchemaState.Current or StoreSchemaState.Newer)
            return;

        RunNonQuery(connection, "BEGIN IMMEDIATE");
        try
        {
            RunNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS run_events (
                    seq          INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id     TEXT NOT NULL UNIQUE,
                    occurred_at  TEXT NOT NULL,
                    event_type   TEXT NOT NULL,
                    goal_id      TEXT NULL,
                    operation    TEXT NULL,
                    status       TEXT NULL,
                    detail       TEXT NULL,
                    payload_json TEXT NULL
                )
                """);
            RunNonQuery(connection, "CREATE INDEX IF NOT EXISTS ix_run_events_goal_seq ON run_events(goal_id, seq)");
            RunNonQuery(connection, "CREATE INDEX IF NOT EXISTS ix_run_events_type_seq ON run_events(event_type, seq)");
            RunNonQuery(connection, "CREATE INDEX IF NOT EXISTS ix_run_events_type_operation_seq ON run_events(event_type, operation, seq)");
            StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.RunEvents);
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
