using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class StateDbMigrations
{
    private sealed record Migration(int Number, string Name, Action<SqliteConnection> Apply);

    /// <summary>
    /// Applies numbered state-store migrations. The caller must hold conductor write
    /// authority, or own an isolated bootstrap database that has not been published yet.
    /// Repositories and worker/read processes must never call this implicitly.
    /// </summary>
    public static string EnsureUpToDate(string dbPath) =>
        EnsureUpToDate(dbPath, statementObserver: null, telemetryOptions: null);

    internal static string EnsureUpToDate(
        string dbPath,
        Action<string>? statementObserver,
        SqliteWriteTelemetryOptions? telemetryOptions)
    {
        var repository = new SqliteOrchestratorStateRepository(
            dbPath,
            statementObserver,
            telemetryOptions);
        using var connection = StateDbConnectionFactory.Open(
            dbPath,
            StateDbConnectionProfile.ReadWrite,
            telemetryOptions?.BusyTimeoutMilliseconds,
            statementObserver);
        var journalMode = StateDbConnectionFactory.ReadJournalMode(connection, statementObserver);

        ExecuteNonQuery(connection, "BEGIN IMMEDIATE", statementObserver);
        try
        {
            ExecuteNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    migration_number INTEGER PRIMARY KEY,
                    name             TEXT NOT NULL,
                    applied_at       TEXT NOT NULL
                )
                """, statementObserver);

            var migrations = new Migration[]
            {
                new(1, "core-state-schema", repository.ApplyCoreSchemaMigration),
                new(2, "worktree-cleanup-state", ApplyCleanupStateSchema),
                new(3, "spawn-registry", ApplySpawnRegistrySchema),
                new(4, "backlog-intake-records", ApplyBacklogIntakeSchema),
                new(5, "model-fit-outcome-backfill", repository.ApplyModelFitHistoryBackfillMigration)
            };

            foreach (var migration in migrations)
            {
                if (IsApplied(connection, migration.Number))
                    continue;

                migration.Apply(connection);
                using var record = connection.CreateCommand();
                record.CommandText = """
                    INSERT INTO schema_migrations (migration_number, name, applied_at)
                    VALUES ($number, $name, $applied_at)
                    """;
                record.Parameters.AddWithValue("$number", migration.Number);
                record.Parameters.AddWithValue("$name", migration.Name);
                record.Parameters.AddWithValue(
                    "$applied_at",
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                statementObserver?.Invoke(record.CommandText);
                record.ExecuteNonQuery();
            }

            ExecuteNonQuery(connection, "COMMIT", statementObserver);
            return journalMode;
        }
        catch
        {
            try
            {
                ExecuteNonQuery(connection, "ROLLBACK", statementObserver);
            }
            catch (SqliteException)
            {
            }

            throw;
        }
    }

    private static bool IsApplied(SqliteConnection connection, int migrationNumber)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM schema_migrations
            WHERE migration_number = $number
            """;
        command.Parameters.AddWithValue("$number", migrationNumber);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static void ApplyCleanupStateSchema(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS worktree_cleanup_backoff (
                path TEXT PRIMARY KEY NOT NULL,
                skip_until_utc TEXT NOT NULL,
                reason TEXT NOT NULL
            )
            """, statementObserver: null);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS worktree_cleanup_journal (
                path TEXT PRIMARY KEY NOT NULL,
                first_seen_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                last_operation TEXT NOT NULL,
                last_reason TEXT NOT NULL,
                skip_count INTEGER NOT NULL DEFAULT 0,
                escalated_at_utc TEXT NULL
            )
            """, statementObserver: null);
    }

    private static void ApplySpawnRegistrySchema(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS spawn_registry (
                id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                owner_id           TEXT NOT NULL,
                process_id         INTEGER NOT NULL,
                process_started_at TEXT NOT NULL,
                image_path         TEXT NOT NULL,
                registered_at      TEXT NOT NULL,
                released_at        TEXT NULL,
                last_diagnostic    TEXT NULL
            )
            """, statementObserver: null);
        ExecuteNonQuery(
            connection,
            "CREATE INDEX IF NOT EXISTS ix_spawn_registry_active ON spawn_registry(released_at, process_id)",
            statementObserver: null);
    }

    private static void ApplyBacklogIntakeSchema(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS backlog_intake_records (
                source_backlog_item_id TEXT PRIMARY KEY,
                heading                TEXT NOT NULL,
                status                 TEXT NOT NULL,
                goal_id                TEXT NULL,
                started_at             TEXT NOT NULL,
                last_heartbeat_at      TEXT NOT NULL,
                owner_process_id       INTEGER NULL,
                stdout_path            TEXT NULL,
                stderr_path            TEXT NULL
            )
            """, statementObserver: null);
        ExecuteNonQuery(
            connection,
            "CREATE INDEX IF NOT EXISTS ix_backlog_intake_records_goal_id ON backlog_intake_records(goal_id)",
            statementObserver: null);
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        string sql,
        Action<string>? statementObserver)
    {
        statementObserver?.Invoke(sql);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
