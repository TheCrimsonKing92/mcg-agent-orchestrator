using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fact uses its own migrated database under a unique temporary directory.
public sealed class SpawnRegistryOwnerTransferTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 7, 1, 0, 0, TimeSpan.Zero);
    private static readonly SpawnProcessIdentity Worker = new(101, StartedAt, "worker.exe");
    private static readonly SpawnProcessIdentity OldOwner = new(41, StartedAt.AddHours(-1), "old-owner.exe");
    private static readonly SpawnProcessIdentity NewOwner = new(42, StartedAt.AddHours(1), "new-owner.exe");

    [Xunit.Fact]
    public void Transfer_MatchingDetachedRow_UpdatesOnlyOwnerAndDiagnostic()
    {
        using var database = new Database();
        var registry = database.Registry;
        registry.Register("goal:task", Worker, OldOwner);
        Assert.True(registry.TryMarkConductorDetached(Worker, "detached"));
        var before = Assert.Single(registry.ListActive());

        Assert.True(registry.TryTransferDetachedOwner("goal:task", Worker.ProcessId, Worker.StartedAt,
            OldOwner.ProcessId, OldOwner.StartedAt, NewOwner, "adopted"));

        var after = Assert.Single(registry.ListActive());
        Assert.Equal(before with
        {
            OwnerProcessId = NewOwner.ProcessId,
            OwnerProcessStartedAt = NewOwner.StartedAt,
            OwnerProcessImagePath = NewOwner.ImagePath,
            LastDiagnostic = "adopted"
        }, after);
        Assert.Equal(SpawnRegistryLifecycle.ConductorDetached, after.Lifecycle);
        Assert.Equal(Worker.ProcessId, after.ProcessId);
        Assert.Equal(Worker.StartedAt, after.ProcessStartedAt);
        Assert.Null(after.ReleasedAt);

        var snapshot = database.ReadRow();
        Assert.False(registry.TryTransferDetachedOwner("goal:task", Worker.ProcessId, Worker.StartedAt,
            OldOwner.ProcessId, OldOwner.StartedAt, NewOwner, "stale transfer"));
        Assert.Equal(snapshot, database.ReadRow());
    }

    [Xunit.Theory]
    [Xunit.InlineData("owned")]
    [Xunit.InlineData("worker-start")]
    [Xunit.InlineData("released")]
    [Xunit.InlineData("owner-id")]
    [Xunit.InlineData("worker-pid")]
    [Xunit.InlineData("owner-pid")]
    [Xunit.InlineData("owner-start")]
    [Xunit.InlineData("null-owner-pid")]
    [Xunit.InlineData("null-owner-start")]
    public void Transfer_GuardMismatch_LeavesEveryColumnUnchanged(string mismatch)
    {
        using var database = new Database();
        var registry = database.Registry;
        registry.Register("goal:task", Worker, OldOwner);
        if (mismatch != "owned")
            Assert.True(registry.TryMarkConductorDetached(Worker, "detached"));
        if (mismatch == "released")
            registry.MarkReleasedEntry(Assert.Single(registry.ListActive()).Id, "released");
        var before = database.ReadRow();

        var transferred = registry.TryTransferDetachedOwner(
            mismatch == "owner-id" ? "goal:other" : "goal:task",
            mismatch == "worker-pid" ? 102 : Worker.ProcessId,
            mismatch == "worker-start" ? Worker.StartedAt.AddSeconds(1) : Worker.StartedAt,
            mismatch == "null-owner-pid" ? null : mismatch == "owner-pid" ? 43 : OldOwner.ProcessId,
            mismatch == "null-owner-start" ? null : mismatch == "owner-start" ? OldOwner.StartedAt.AddSeconds(1) : OldOwner.StartedAt,
            NewOwner, "must not write");

        Assert.False(transferred);
        Assert.Equal(before, database.ReadRow());
    }

    [Xunit.Fact]
    public void Transfer_NullExpectedOwner_MatchesNullOwnerColumns()
    {
        using var database = new Database();
        var registry = database.Registry;
        registry.Register("goal:task", Worker, null);
        Assert.True(registry.TryMarkConductorDetached(Worker, "detached"));
        var before = Assert.Single(registry.ListActive());
        Assert.Null(before.OwnerProcessId);
        Assert.Null(before.OwnerProcessStartedAt);

        Assert.True(registry.TryTransferDetachedOwner("goal:task", Worker.ProcessId, Worker.StartedAt,
            null, null, NewOwner, "adopted"));

        Assert.Equal(before with
        {
            OwnerProcessId = NewOwner.ProcessId,
            OwnerProcessStartedAt = NewOwner.StartedAt,
            OwnerProcessImagePath = NewOwner.ImagePath,
            LastDiagnostic = "adopted"
        }, Assert.Single(registry.ListActive()));
    }

    private sealed class Database : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(),
            "spawn-registry-owner-transfer", Guid.NewGuid().ToString("N"));
        private readonly string _path;
        public SpawnRegistry Registry { get; }

        public Database()
        {
            Directory.CreateDirectory(_directory);
            _path = Path.Combine(_directory, "state.db");
            _ = StateDbMigrations.EnsureUpToDate(_path);
            Registry = new SpawnRegistry(_path);
        }

        public object[] ReadRow()
        {
            using var connection = StateDbConnectionFactory.Open(_path, StateDbConnectionProfile.QueryOnlyRead);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM spawn_registry";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read(), "The registered row must exist, including after release.");
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            Assert.False(reader.Read(), "Each test owns exactly one registry row.");
            return values;
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
