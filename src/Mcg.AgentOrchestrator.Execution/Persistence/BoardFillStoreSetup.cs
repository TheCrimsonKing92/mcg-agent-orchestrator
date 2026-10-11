using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class BoardFillStoreSetup
{
    public static void Setup(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=False;");
        connection.Open();
        RunNonQuery(connection, "PRAGMA busy_timeout=30000");
        var state = StoreSchemaVersions.Verify(connection, StoreSchemaRegistry.BoardFill);
        if (state is StoreSchemaState.Current or StoreSchemaState.Newer)
            return;

        RunNonQuery(connection, "BEGIN IMMEDIATE");
        try
        {
            RunNonQuery(connection, "CREATE TABLE IF NOT EXISTS board_fill_rounds(round_id TEXT PRIMARY KEY, payload TEXT NOT NULL)");
            StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.BoardFill);
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
