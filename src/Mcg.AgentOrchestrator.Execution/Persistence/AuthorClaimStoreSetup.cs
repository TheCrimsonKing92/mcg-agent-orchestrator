using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class AuthorClaimStoreSetup
{
    public static void Setup(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=False;");
        connection.Open();
        RunNonQuery(connection, "PRAGMA busy_timeout=30000");
        var state = StoreSchemaVersions.Verify(connection, StoreSchemaRegistry.AuthorClaims);
        if (state is StoreSchemaState.Current or StoreSchemaState.Newer)
            return;

        RunNonQuery(connection, "BEGIN IMMEDIATE");
        try
        {
            RunNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS author_claims (
                    identity TEXT PRIMARY KEY,
                    claimed_at TEXT NOT NULL,
                    outcome TEXT NOT NULL,
                    intent_id TEXT NULL,
                    item_json TEXT NOT NULL,
                    output TEXT NULL,
                    attempt INTEGER NOT NULL DEFAULT 1
                )
                """);
            var hasAttempt = false;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(author_claims)";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    hasAttempt |= reader.GetString(1) == "attempt";
            }
            if (!hasAttempt)
                RunNonQuery(connection, "ALTER TABLE author_claims ADD COLUMN attempt INTEGER NOT NULL DEFAULT 1");
            StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.AuthorClaims);
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
