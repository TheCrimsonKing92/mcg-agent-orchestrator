using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: unique actual stores, explicit registry and invocation-owned lease handles.
public sealed class WorkspaceStoreSetupTests
{
    [Fact]
    public void LaterNewerStorePreventsEarlierUpgradeAndPreservesRowsAndSidecars()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Workspace.OrchestratorDirectory);
        PortfolioStore.Setup(fixture.Workspace.PortfolioStorePath);
        BacklogStore.Setup(fixture.Workspace.BacklogStorePath);
        Execute(fixture.Workspace.PortfolioStorePath, "UPDATE store_schema_versions SET version = 2; CREATE TABLE sentinel(value TEXT); INSERT INTO sentinel VALUES('retained');");
        Execute(fixture.Workspace.BacklogStorePath, "UPDATE store_schema_versions SET version = 99;");
        var before = fixture.Snapshot();
        var plan = StoreSetupRunner.Plan(fixture.Workspace.OrchestratorDirectory, includeState: true);
        Assert.True(plan.Single(entry => entry.StoreName == "portfolio").RequiresMigration);
        Assert.True(plan.Single(entry => entry.StoreName == "backlog").IsNewer);
        Assert.Throws<InvalidOperationException>(() => WorkspaceStoreSetup.Run(fixture.Workspace));
        Assert.Throws<InvalidOperationException>(() => StoreSetupRunner.Run(fixture.Workspace.OrchestratorDirectory));
        Assert.Equal(before, fixture.Snapshot());
        Assert.False(File.Exists(fixture.Workspace.SqliteStatePath));
    }

    [Fact]
    public void ActualConductorLeaseRefusesForwardMigrationButAllowsCurrentSchemaRepeat()
    {
        using var fixture = new Fixture();
        using (var owner = ConductorLoopLease.Acquire(fixture.Workspace.OrchestratorDirectory))
        {
            var before = fixture.Snapshot();
            Assert.Throws<InvalidOperationException>(() => WorkspaceStoreSetup.Run(fixture.Workspace));
            Assert.Equal(before, fixture.Snapshot());
        }
        Assert.NotEmpty(WorkspaceStoreSetup.Run(fixture.Workspace));
        using (var owner = ConductorLoopLease.Acquire(fixture.Workspace.OrchestratorDirectory))
        {
            var before = fixture.Snapshot();
            Assert.NotEmpty(WorkspaceStoreSetup.Run(fixture.Workspace));
            Assert.Equal(before, fixture.Snapshot());
        }
    }

    [Fact]
    public async Task CurrentSetupAndIntentsReadsCoexistWithLiveWalConnections()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Workspace;
        WorkspaceStoreSetup.Run(workspace);
        using var owner = ConductorLoopLease.Acquire(workspace.OrchestratorDirectory);
        using var state = OpenWriter(workspace.SqliteStatePath);
        using var intents = OpenWriter(Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName));
        using var stateCommand = state.CreateCommand();
        stateCommand.CommandText = "CREATE TABLE inspection_sentinel(value TEXT); INSERT INTO inspection_sentinel VALUES('before');";
        stateCommand.ExecuteNonQuery();
        using var intentCommand = intents.CreateCommand();
        intentCommand.CommandText = "BEGIN IMMEDIATE; UPDATE store_schema_versions SET applied_at = 'uncommitted';";
        intentCommand.ExecuteNonQuery();

        Assert.NotEmpty(WorkspaceStoreSetup.Run(workspace));
        var reader = SqliteOperatorIntentStore.OpenExisting(workspace.OrchestratorDirectory, workspace.LogDirectory);
        Assert.Empty(await reader.ListActionableGoalIdsAsync());
        // A new writer still opens and commits while inspection is reading metadata.
        using (var inspection = new StoreInspectionSnapshot(workspace.SqliteStatePath))
        {
            Assert.Equal("wal", inspection.ReadJournalMode());
            using var otherWriter = OpenWriter(workspace.SqliteStatePath);
            using var write = otherWriter.CreateCommand();
            write.CommandText = "INSERT INTO inspection_sentinel VALUES('after');";
            Assert.Equal(1, write.ExecuteNonQuery());
        }
        stateCommand.CommandText = "SELECT COUNT(*) FROM inspection_sentinel";
        Assert.Equal(2L, stateCommand.ExecuteScalar());
        intentCommand.CommandText = "ROLLBACK";
        intentCommand.ExecuteNonQuery();
        Assert.False(StoreSetupRunner.Plan(workspace.OrchestratorDirectory, includeState: true).Any(entry => entry.RequiresMigration));
    }

    private static SqliteConnection OpenWriter(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    [Theory]
    [InlineData("wal")]
    [InlineData("delete")]
    public void QuietInspectionPreservesSourceAndAllowsNewWriter(string journalMode)
    {
        using var fixture = new Fixture();
        WorkspaceStoreSetup.Run(fixture.Workspace);
        var path = Path.Combine(fixture.Workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName);
        Execute(path, $"PRAGMA journal_mode={journalMode}; UPDATE store_schema_versions SET applied_at = 'before';");
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
        var before = fixture.Snapshot();
        using (var inspection = new StoreInspectionSnapshot(path))
        {
            Assert.Equal(journalMode, inspection.ReadJournalMode());
            using var read = inspection.Connection.CreateCommand();
            read.CommandText = "SELECT applied_at FROM store_schema_versions WHERE store_name = 'operator-intents'";
            Assert.Equal("before", read.ExecuteScalar());
            Assert.Equal(before, fixture.Snapshot());

            // This starts after the quiet snapshot is acquired: a FileShare.Read
            // source handle would downgrade this writer and fail the UPDATE.
            using var writer = OpenWriter(path);
            using var write = writer.CreateCommand();
            write.CommandText = "UPDATE store_schema_versions SET applied_at = 'after' WHERE store_name = 'operator-intents'";
            Assert.Equal(1, write.ExecuteNonQuery());
            Assert.Equal("before", read.ExecuteScalar());
        }
        using var fresh = new StoreInspectionSnapshot(path);
        using var verify = fresh.Connection.CreateCommand();
        verify.CommandText = "SELECT applied_at FROM store_schema_versions WHERE store_name = 'operator-intents'";
        Assert.Equal("after", verify.ExecuteScalar());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuietInspection_LockedRange_RetriesWithoutChangingSource(bool releaseLock)
    {
        // Unix fcntl locks are process-owned, so two handles in this test would
        // not reproduce Windows' conflicting per-handle byte locks.
        if (!OperatingSystem.IsWindows()) Assert.Skip("Requires Windows per-handle byte locks.");
        using var fixture = new Fixture();
        WorkspaceStoreSetup.Run(fixture.Workspace);
        var path = fixture.Workspace.BacklogStorePath;
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
        var before = fixture.Snapshot();
        var clock = new InspectionClock();
        var retries = 0;
        using var blocker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        blocker.Lock(0x40000000, 512);
        var locked = true;
        void WaitForRetry(TimeSpan delay)
        {
            retries++;
            if (releaseLock)
            {
                blocker.Unlock(0x40000000, 512);
                locked = false;
            }
            clock.Advance(delay);
        }
        try
        {
            if (releaseLock)
            {
                using var inspection = new StoreInspectionSnapshot(path, clock, WaitForRetry);
                Assert.Equal(StoreSchemaRegistry.Backlog.CurrentVersion,
                    StoreSchemaVersions.Read(inspection.Connection, StoreSchemaRegistry.Backlog.StoreName));
                Assert.Equal(1, retries);
            }
            else
            {
                Assert.Throws<IOException>(() => new StoreInspectionSnapshot(path, clock, WaitForRetry));
                Assert.True(retries > 0);
                Assert.Equal(TimeSpan.FromMilliseconds(StateDbConnectionFactory.DefaultBusyTimeoutMilliseconds),
                    clock.GetElapsedTime(0));
            }
        }
        finally
        {
            if (locked) blocker.Unlock(0x40000000, 512);
        }
        Assert.Equal(before, fixture.Snapshot());
        // Both success and exhaustion release all inspection handles for a later reader.
        using var fresh = new StoreInspectionSnapshot(path);
        Assert.Equal(StoreSchemaRegistry.Backlog.CurrentVersion,
            StoreSchemaVersions.Read(fresh.Connection, StoreSchemaRegistry.Backlog.StoreName));
    }

    private sealed class InspectionClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan delay) => _timestamp += delay.Ticks;
    }

    [Fact]
    public void CurrentSetupCoexistsWithOpenRollbackJournalConnection()
    {
        using var fixture = new Fixture();
        WorkspaceStoreSetup.Run(fixture.Workspace);
        using var owner = ConductorLoopLease.Acquire(fixture.Workspace.OrchestratorDirectory);
        using var writer = OpenWriter(fixture.Workspace.BacklogStorePath);
        using var write = writer.CreateCommand();
        write.CommandText = "PRAGMA journal_mode=DELETE";
        Assert.Equal("delete", write.ExecuteScalar());
        var before = fixture.Snapshot();

        Assert.NotEmpty(WorkspaceStoreSetup.Run(fixture.Workspace));
        Assert.Equal(before, fixture.Snapshot());
        using var inspection = new StoreInspectionSnapshot(fixture.Workspace.BacklogStorePath);
        Assert.Equal(StoreSchemaRegistry.Backlog.CurrentVersion,
            StoreSchemaVersions.Read(inspection.Connection, StoreSchemaRegistry.Backlog.StoreName));
        write.CommandText = "UPDATE store_schema_versions SET applied_at = 'writer-still-writable'";
        Assert.Equal(1, write.ExecuteNonQuery());
    }

    [Fact]
    public void PlanAndUnknownStoreInspectionNeverCreateTargets()
    {
        using var fixture = new Fixture();
        var before = fixture.Snapshot();
        var plan = StoreSetupRunner.Plan(fixture.Workspace.OrchestratorDirectory, includeState: true);
        Assert.NotEmpty(plan);
        Assert.All(plan, entry => Assert.True(entry.RequiresMigration));
        Assert.Equal(before, fixture.Snapshot());
        Directory.CreateDirectory(fixture.Workspace.OrchestratorDirectory);
        File.WriteAllText(fixture.Workspace.BacklogStorePath, "corrupt SQLite");
        before = fixture.Snapshot();
        Assert.Throws<SqliteException>(() => WorkspaceStoreSetup.Run(fixture.Workspace));
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void ExecutionRevalidatesBindingBeforeMigration()
    {
        using var fixture = new Fixture();
        var calls = 0;
        Assert.Throws<InvalidOperationException>(() => WorkspaceStoreSetup.Run(fixture.Workspace, () =>
        {
            if (++calls == 2) throw new InvalidOperationException("selection changed");
        }));
        Assert.Equal(2, calls);
        Assert.False(File.Exists(fixture.Workspace.PortfolioStorePath));
        Assert.False(File.Exists(fixture.Workspace.SqliteStatePath));
        Assert.False(File.Exists(Path.Combine(fixture.Workspace.OrchestratorDirectory, "conduct-loop.lock")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewerStateMigrationPreventsPortfolioUpgrade(bool coreSchema)
    {
        using var fixture = new Fixture();
        StateDbMigrations.EnsureUpToDate(fixture.Workspace.SqliteStatePath);
        Execute(fixture.Workspace.SqliteStatePath, coreSchema ? "UPDATE meta SET value = '99' WHERE key = 'schema_version';" :
            "INSERT INTO schema_migrations(migration_number,name,applied_at) VALUES(99,'future','future');");
        var before = fixture.Snapshot();
        Assert.Throws<InvalidOperationException>(() => WorkspaceStoreSetup.Run(fixture.Workspace));
        Assert.Equal(before, fixture.Snapshot());
        Assert.False(File.Exists(fixture.Workspace.PortfolioStorePath));
    }

    [Fact]
    public void NewerReaderDiagnosticNamesVersionsAndDoesNotPrescribeOlderSetup()
    {
        using var fixture = new Fixture();
        PortfolioStore.Setup(fixture.Workspace.PortfolioStorePath);
        Execute(fixture.Workspace.PortfolioStorePath, "UPDATE store_schema_versions SET version = 99;");
        var error = Assert.Throws<InvalidOperationException>(() => PortfolioStore.OpenReadOnly(fixture.Workspace.PortfolioStorePath));
        var diagnostic = StoreSchemaDiagnostics.Message(error);
        Assert.Contains(fixture.Workspace.PortfolioStorePath, diagnostic);
        Assert.Contains("99", diagnostic);
        Assert.Contains($"version {StoreSchemaRegistry.Portfolio.CurrentVersion}", diagnostic);
        Assert.Contains("compatible runtime", diagnostic);
        Assert.DoesNotContain("run setup", diagnostic);
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("store-preflight-").FullName;
        internal OrchestratorWorkspace Workspace { get; }
        internal Fixture() => Workspace = OrchestratorWorkspace.ForDirectory(_root, null, null,
            new OrchestratorProjectRegistry(Path.Combine(_root, "registry"), Path.Combine(_root, "data")));
        internal string[] Snapshot() => Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(_root, path) +
                (File.Exists(path) ? ":" + HashFile(path) : ":directory")).ToArray();
        private static string HashFile(string path)
        {
            // The lease owns a read/write handle with FileShare.Read. A read-only
            // snapshot must also share its owner's existing write access on Windows.
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
