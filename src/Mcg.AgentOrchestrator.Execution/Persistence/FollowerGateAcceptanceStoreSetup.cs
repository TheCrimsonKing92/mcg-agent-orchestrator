using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class FollowerGateAcceptanceStoreSetup
{
    public static void Setup(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=False;");
        connection.Open();
        RunNonQuery(connection, "PRAGMA busy_timeout=30000");
        var state = StoreSchemaVersions.Verify(connection, StoreSchemaRegistry.FollowerGateAcceptance);
        if (state is StoreSchemaState.Current or StoreSchemaState.Newer)
            return;

        RunNonQuery(connection, "BEGIN IMMEDIATE");
        try
        {
            RunNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS follower_gate_receipts(
                    identity_value TEXT PRIMARY KEY,
                    receipt_id TEXT NOT NULL UNIQUE,
                    leader_goal_id TEXT NOT NULL,
                    follower_goal_id TEXT NOT NULL,
                    payload_json TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS follower_gate_receipts_follower
                    ON follower_gate_receipts(follower_goal_id);
                """);
            StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.FollowerGateAcceptance);
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
