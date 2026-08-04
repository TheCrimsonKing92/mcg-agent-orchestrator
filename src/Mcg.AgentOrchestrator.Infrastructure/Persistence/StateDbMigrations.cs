using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class StateDbMigrations
{
    private sealed record Migration(int Number, string Name, Action<SqliteConnection> Apply);
    private const int CurrentMigrationNumber = 6;

    /// <summary>
    /// Reports whether the published state store already has every numbered migration.
    /// This probe is read-only; callers that observe <see langword="false"/> must acquire
    /// explicit migration authority before calling <see cref="EnsureUpToDate(string)"/>.
    /// </summary>
    public static bool IsUpToDate(string dbPath)
    {
        if (!File.Exists(dbPath))
            return false;

        using var connection = StateDbConnectionFactory.Open(
            dbPath,
            StateDbConnectionProfile.MigrationProbeRead);
        if (!StateDbConnectionFactory.ReadJournalMode(connection)
            .Equals("wal", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var table = connection.CreateCommand();
        table.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_schema
            WHERE type = 'table'
              AND name = 'schema_migrations'
            """;
        if (Convert.ToInt32(table.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            return false;

        using var migrations = connection.CreateCommand();
        migrations.CommandText = """
            SELECT COUNT(*)
            FROM schema_migrations
            WHERE migration_number BETWEEN 1 AND $current
            """;
        migrations.Parameters.AddWithValue("$current", CurrentMigrationNumber);
        return Convert.ToInt32(migrations.ExecuteScalar(), CultureInfo.InvariantCulture) ==
            CurrentMigrationNumber;
    }

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
                new(5, "model-fit-outcome-backfill", repository.ApplyModelFitHistoryBackfillMigration),
                new(CurrentMigrationNumber, "spawn-registry-owner-identity", ApplySpawnRegistryOwnerIdentitySchema)
            };
            ValidateMigrationSequence(migrations);

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

    private static void ValidateMigrationSequence(IReadOnlyList<Migration> migrations)
    {
        if (migrations.Count != CurrentMigrationNumber)
        {
            throw new InvalidOperationException(
                $"State database migrations must define exactly {CurrentMigrationNumber} numbered entries.");
        }

        for (var index = 0; index < migrations.Count; index++)
        {
            var expected = index + 1;
            if (migrations[index].Number != expected)
            {
                throw new InvalidOperationException(
                    $"State database migration sequence must be contiguous; expected {expected}, found {migrations[index].Number}.");
            }
        }
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

    private static void ApplySpawnRegistryOwnerIdentitySchema(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, "ALTER TABLE spawn_registry ADD COLUMN owner_process_id INTEGER NULL", statementObserver: null);
        ExecuteNonQuery(connection, "ALTER TABLE spawn_registry ADD COLUMN owner_process_started_at TEXT NULL", statementObserver: null);
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
