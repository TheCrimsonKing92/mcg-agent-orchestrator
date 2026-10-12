using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: databases and observations are scoped to this test's owned roots.
public sealed class GoalWorktreeTestsRebaseMergeSeededStateTemplate : GoalWorktreeTestBase
{
    [Xunit.Fact]
    public void SeededRepository_FreshMigration_HasEquivalentSchemaAndArtifacts()
    {
        var copiedRoot = CreateSeededRepository();
        string? freshRoot = null;
        try
        {
            freshRoot = CreateTemporaryRoot();
            var copiedPath = StatePath(copiedRoot);
            var freshPath = StatePath(freshRoot);
            StateDbMigrations.EnsureUpToDate(freshPath);

            var freshSchema = ReadSchema(freshPath);
            Assert.NotEmpty(freshSchema);
            Assert.Equal(freshSchema, ReadSchema(copiedPath));
            var freshMigrations = ReadMigrations(freshPath);
            Assert.Equal(13, freshMigrations.Length);
            Assert.Equal(freshMigrations, ReadMigrations(copiedPath));
            Assert.Equal(ReadJournalMode(freshPath), ReadJournalMode(copiedPath));
            Assert.True(StateDbMigrations.IsUpToDate(freshPath));
            Assert.True(StateDbMigrations.IsUpToDate(copiedPath));
            Assert.Equal(StateFiles(freshPath), StateFiles(copiedPath));
        }
        finally
        {
            DeleteDirectory(copiedRoot);
            if (freshRoot is not null)
                DeleteDirectory(freshRoot);
        }
    }

    [Xunit.Fact]
    public void SeededRepository_RepeatedCalls_ReuseBytesWithoutSharingMutations()
    {
        var firstRoot = CreateSeededRepository();
        string? secondRoot = null;
        string? thirdRoot = null;
        try
        {
            secondRoot = CreateSeededRepository();
            var firstPath = StatePath(firstRoot);
            var secondPath = StatePath(secondRoot);
            var baseline = File.ReadAllBytes(firstPath);
            Assert.NotEqual(firstPath, secondPath);
            Assert.Equal(baseline, File.ReadAllBytes(secondPath));
            Assert.Equal(baseline, SeededStateDatabaseTemplate.Build());

            ExecuteWrite(firstPath, """
                INSERT INTO worktree_cleanup_backoff (path, skip_until_utc, reason)
                VALUES ('seed-mutation', '2099-01-01T00:00:00Z', 'independence test')
                """);
            Assert.Equal(1L, CountMutationRows(firstPath));
            Assert.False(baseline.SequenceEqual(File.ReadAllBytes(firstPath)));
            Assert.Equal(baseline, File.ReadAllBytes(secondPath));

            // Returning bytes for the legacy cohort helper must not expose the cache.
            var snapshot = SeededStateDatabaseTemplate.Build();
            Array.Clear(snapshot);
            thirdRoot = CreateSeededRepository();
            var thirdPath = StatePath(thirdRoot);
            Assert.NotEqual(firstPath, thirdPath);
            Assert.NotEqual(secondPath, thirdPath);
            Assert.Equal(baseline, File.ReadAllBytes(thirdPath));
            Assert.Equal(0L, CountMutationRows(secondPath));
            Assert.Equal(0L, CountMutationRows(thirdPath));
        }
        finally
        {
            DeleteDirectory(firstRoot);
            if (secondRoot is not null)
                DeleteDirectory(secondRoot);
            if (thirdRoot is not null)
                DeleteDirectory(thirdRoot);
        }
    }

    [Xunit.Fact]
    public void Validate_MissingFinalMigration_RejectsStaleBytesAndCopyRemainsCurrent()
    {
        var root = CreateTemporaryRoot();
        try
        {
            Directory.CreateDirectory(root);
            var stalePath = Path.Combine(root, "stale.db");
            File.WriteAllBytes(stalePath, SeededStateDatabaseTemplate.Build());
            Assert.Equal(13, ReadMigrations(stalePath).Length);
            Assert.True(SeededStateDatabaseTemplate.Validate(File.ReadAllBytes(stalePath)));
            Assert.Equal(1, ExecuteWrite(stalePath,
                "DELETE FROM schema_migrations WHERE migration_number = 13"));
            Assert.Equal(12, ReadMigrations(stalePath).Length);
            Assert.False(SeededStateDatabaseTemplate.Validate(File.ReadAllBytes(stalePath)));

            var copiedPath = Path.Combine(root, "copy", "state.db");
            SeededStateDatabaseTemplate.CopyTo(copiedPath);
            Assert.True(StateDbMigrations.IsUpToDate(copiedPath));
            Assert.Equal(13, ReadMigrations(copiedPath).Length);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void Validate_UnreadableCandidates_ReturnsFalse()
    {
        Assert.False(SeededStateDatabaseTemplate.Validate(null!));
        Assert.False(SeededStateDatabaseTemplate.Validate([]));
        Assert.False(SeededStateDatabaseTemplate.Validate([1, 2, 3, 4]));
    }

    [Xunit.Fact]
    public void SeededRepository_WarmedTemplate_OpensNoDatabaseForCopying()
    {
        _ = SeededStateDatabaseTemplate.Build();
        var root = CreateTemporaryRoot();
        var controlPath = Path.Combine(root, "control", "state.db");
        var copyContainer = Path.Combine(root, "copies");
        var copyPrefix = Path.GetFullPath(copyContainer) + Path.DirectorySeparatorChar;
        var openedPaths = new ConcurrentQueue<string>();
        var statements = new List<string>();
        void ObserveConnection(SqliteConnection connection, string path)
        {
            if (path.Equals(controlPath, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(copyPrefix, StringComparison.OrdinalIgnoreCase))
                openedPaths.Enqueue(path);
        }

        StateDbConnectionFactory.ConnectionOpenedForDiagnostics += ObserveConnection;
        try
        {
            StateDbMigrations.EnsureUpToDate(controlPath, statements.Add, telemetryOptions: null);
            Assert.Contains(statements, sql => sql.TrimStart().StartsWith(
                "CREATE TABLE", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(13, statements.Count(sql => sql.TrimStart().StartsWith(
                "INSERT INTO schema_migrations", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(controlPath, openedPaths);

            var copiedRoot = CreateSeededRepository(copyContainer);
            var copiedPath = StatePath(copiedRoot);
            Assert.DoesNotContain(openedPaths,
                path => path.StartsWith(copyPrefix, StringComparison.OrdinalIgnoreCase));
            Assert.True(StateDbMigrations.IsUpToDate(copiedPath));
            // Positive control for this exact destination proves the path filter works.
            Assert.Single(openedPaths.Where(path =>
                path.Equals(copiedPath, StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(13, ReadMigrations(copiedPath).Length);
        }
        finally
        {
            StateDbConnectionFactory.ConnectionOpenedForDiagnostics -= ObserveConnection;
            DeleteDirectory(root);
        }
    }

    private static string StatePath(string root) =>
        OrchestratorWorkspace.ForDirectory(root).SqliteStatePath;

    private static string CreateTemporaryRoot() =>
        Path.Combine(Path.GetTempPath(), $"mcg-seeded-state-test-{Guid.NewGuid():N}");

    private static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static (string Type, string Name, string Table, string? Sql)[] ReadSchema(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY type, name, tbl_name, sql";
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string, string, string?)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        return [.. rows];
    }

    private static (int Number, string Name)[] ReadMigrations(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT migration_number, name FROM schema_migrations ORDER BY migration_number";
        using var reader = command.ExecuteReader();
        var rows = new List<(int, string)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        return [.. rows];
    }

    private static string ReadJournalMode(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static string[] StateFiles(string statePath)
    {
        var directory = Path.GetDirectoryName(statePath)!;
        return Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path))
            .Order(StringComparer.Ordinal).ToArray();
    }

    private static int ExecuteWrite(string path, string sql)
    {
        using var connection = Open(path, SqliteOpenMode.ReadWrite);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    private static long CountMutationRows(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM worktree_cleanup_backoff WHERE path = 'seed-mutation'";
        return Assert.IsType<long>(command.ExecuteScalar());
    }
}
