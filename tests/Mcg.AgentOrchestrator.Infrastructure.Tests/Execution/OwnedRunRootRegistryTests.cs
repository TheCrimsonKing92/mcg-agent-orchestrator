using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class OwnedRunRootRegistryTests
{
    [Xunit.Fact]
    public void Migration_creates_owned_roots_schema()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcg-owned-root-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "state.db");

        _ = StateDbMigrations.EnsureUpToDate(databasePath);

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False;");
        connection.Open();
        using var tablesCommand = connection.CreateCommand();
        tablesCommand.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name";
        using var reader = tablesCommand.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
            tables.Add(reader.GetString(0));

        Assert.Contains("owned_roots", tables);
        using var migrationCommand = connection.CreateCommand();
        migrationCommand.CommandText = "SELECT name FROM schema_migrations WHERE migration_number = 13";
        Assert.Equal("owned-build-run-roots", migrationCommand.ExecuteScalar());
        Assert.True(StateDbMigrations.IsUpToDate(databasePath));
    }

    [Xunit.Fact]
    public void Registry_persists_identity_release_and_cleanup_failure()
    {
        var databasePath = CreateDatabase();
        var registry = new OwnedRunRootRegistry(databasePath);
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var path = Path.Combine(Path.GetTempPath(), "owned-root", Guid.NewGuid().ToString("N"));
        var owner = new SpawnProcessIdentity(31415, now.AddHours(-1), @"C:\tools\dotnet.exe");

        registry.Register(path, OwnedRunRootPurpose.RunAttempt, owner, "goal-1", "dispatch-2", now);
        registry.Release(path, OwnedRunRootReleaseOutcome.Cancelled, now.AddMinutes(1));
        registry.MarkCleanupFailure(path, "scanner holds artifacts.dll", now.AddMinutes(6), now.AddMinutes(1));

        var entry = registry.ReadBatch(0, 10, now.AddMinutes(6)).Single();
        Assert.Equal(Path.GetFullPath(path), entry.CanonicalPath);
        Assert.Equal(owner, entry.Owner);
        Assert.Equal("goal-1", entry.GoalId);
        Assert.Equal("dispatch-2", entry.DispatchId);
        Assert.Equal(OwnedRunRootReleaseOutcome.Cancelled, entry.ReleaseOutcome);
        Assert.Equal(OwnedRunRootCleanupState.Blocked, entry.CleanupState);
        Assert.Equal(1, entry.CleanupAttemptCount);
        Assert.Equal("scanner holds artifacts.dll", entry.LastCleanupHolder);
    }

    [Xunit.Fact]
    public void Stable_slot_release_records_outcome_without_becoming_reapable()
    {
        var databasePath = CreateDatabase();
        var registry = new OwnedRunRootRegistry(databasePath);
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var path = Path.Combine(Path.GetTempPath(), "stable-root", Guid.NewGuid().ToString("N"));
        var owner = new SpawnProcessIdentity(27182, now, @"C:\tools\dotnet.exe");

        registry.Register(path, OwnedRunRootPurpose.StableSlot, owner, null, null, now);
        registry.Release(path, OwnedRunRootReleaseOutcome.Failed, now.AddMinutes(1));

        var entry = registry.ReadBatch(0, 10, now.AddMinutes(2)).Single();
        Assert.Equal(OwnedRunRootCleanupState.Pending, entry.CleanupState);
        Assert.Equal(OwnedRunRootReleaseOutcome.Failed, entry.ReleaseOutcome);
        Assert.Equal(now.AddMinutes(1), entry.ReleasedAt);
    }

    private static string CreateDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcg-owned-root-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "state.db");
        _ = StateDbMigrations.EnsureUpToDate(databasePath);
        return databasePath;
    }
}
