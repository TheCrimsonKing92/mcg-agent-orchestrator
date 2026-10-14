using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class OperatorDirectiveStoreSetup
{
    public static void Setup(string databasePath)
    {
        var path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = SqliteOperatorDirectiveStore.OpenConnection(path, SqliteOpenMode.ReadWriteCreate);
        Run(connection, "PRAGMA busy_timeout=30000");
        var state = StoreSchemaVersions.Verify(connection, StoreSchemaRegistry.OperatorDirectives);
        if (state is StoreSchemaState.Current or StoreSchemaState.Newer) return;

        Run(connection, "BEGIN IMMEDIATE");
        try
        {
            Run(connection, """
                CREATE TABLE IF NOT EXISTS directives (
                    id TEXT PRIMARY KEY, source_intent_id TEXT NOT NULL UNIQUE,
                    kind TEXT NOT NULL, text TEXT NOT NULL, actor TEXT NOT NULL,
                    actor_kind TEXT NOT NULL, channel TEXT NOT NULL, recorded_at TEXT NOT NULL,
                    expires_at TEXT, until_handoff INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS directive_retirements (
                    directive_id TEXT PRIMARY KEY REFERENCES directives(id),
                    source_intent_id TEXT NOT NULL UNIQUE, reason TEXT NOT NULL,
                    actor TEXT NOT NULL, actor_kind TEXT NOT NULL, channel TEXT NOT NULL,
                    retired_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS directive_handoffs (
                    id TEXT PRIMARY KEY, source_intent_id TEXT NOT NULL UNIQUE, note TEXT,
                    actor TEXT NOT NULL, actor_kind TEXT NOT NULL, channel TEXT NOT NULL,
                    recorded_at TEXT NOT NULL);
                """);
            StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.OperatorDirectives);
            Run(connection, "COMMIT");
        }
        catch
        {
            try { Run(connection, "ROLLBACK"); } catch { }
            throw;
        }
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
