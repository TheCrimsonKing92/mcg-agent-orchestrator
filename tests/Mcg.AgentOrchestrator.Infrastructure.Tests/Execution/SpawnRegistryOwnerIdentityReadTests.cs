using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all reads and writes use a private migrated database and an injected clock.
public sealed class SpawnRegistryOwnerIdentityReadTests
{
    private readonly TestClock _clock = new(DateTimeOffset.Parse("2026-10-19T08:34:20Z"));

    [Xunit.Fact]
    public void ReadOwner_LatestEligibleRow_MatchesDetachedSelection()
    {
        using var database = new Database();
        var worker = new SpawnProcessIdentity(999999, _clock.UtcNow, "worker.exe");
        var owner = new SpawnProcessIdentity(4242, _clock.UtcNow.AddMinutes(-16), "dotnet.exe");
        database.Registry.Register("goal:task", worker with { StartedAt = worker.StartedAt.AddMinutes(-1) }, owner with { ProcessId = 7 });
        database.SetRegisteredAt(_clock.UtcNow.AddSeconds(-2));
        database.Registry.Register("goal:task", worker, owner);
        database.SetRegisteredAt(_clock.UtcNow.AddSeconds(-1), latestOnly: true);
        Assert.True(database.Registry.TryMarkRuntimeOwned(worker, "fixture runtime ownership"));
        Assert.True(database.Registry.TryMarkConductorDetached(database.Registry.ListActive().Last(), "fixture"));

        Assert.True(database.Registry.WasDetachedByConductor("goal:task", worker.ProcessId, _clock.UtcNow));
        Assert.Equal(owner, database.Registry.TryReadOwnerProcessIdentity("goal:task", worker.ProcessId, _clock.UtcNow));
        Assert.Equal(owner with { ProcessId = 7 }, database.Registry.TryReadOwnerProcessIdentity("goal:task", worker.ProcessId, _clock.UtcNow.AddMilliseconds(-1500)));
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing-file")]
    [Xunit.InlineData("unknown-owner")]
    [Xunit.InlineData("future-row")]
    [Xunit.InlineData("owner_process_id")]
    [Xunit.InlineData("owner_process_started_at")]
    [Xunit.InlineData("owner_process_image_path")]
    public void ReadOwner_MissingOrNullIdentity_ReturnsNull(string fault)
    {
        using var database = new Database();
        if (fault == "missing-file")
        {
            var absent = Path.Combine(database.Root, "absent.db");
            Assert.Null(new SpawnRegistry(absent).TryReadOwnerProcessIdentity("goal:task", 999999, _clock.UtcNow));
            Assert.False(File.Exists(absent));
            return;
        }
        database.Registry.Register("goal:task", new(999999, _clock.UtcNow, "worker.exe"), new(4242, _clock.UtcNow.AddMinutes(-16), "dotnet.exe"));
        database.SetRegisteredAt(fault == "future-row" ? _clock.UtcNow.AddTicks(1) : _clock.UtcNow);
        if (fault.StartsWith("owner_process_", StringComparison.Ordinal)) database.ClearColumn(fault);
        Assert.Null(database.Registry.TryReadOwnerProcessIdentity(fault == "unknown-owner" ? "other" : "goal:task", 999999, _clock.UtcNow));
    }

    internal sealed class Database : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "generation-registry", Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public SpawnRegistry Registry { get; }
        public Database(string? root = null)
        {
            if (root is not null) Root = root;
            Directory.CreateDirectory(Root);
            Path = System.IO.Path.Combine(Root, "state.db");
            StateDbMigrations.EnsureUpToDate(Path);
            Registry = new SpawnRegistry(Path);
        }
        public void SetRegisteredAt(DateTimeOffset at, bool latestOnly = false)
        {
            using var connection = StateDbConnectionFactory.Open(Path, StateDbConnectionProfile.ReadWrite);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE spawn_registry SET registered_at = $at" + (latestOnly ? " WHERE id = (SELECT MAX(id) FROM spawn_registry)" : "");
            command.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
            Assert.True(command.ExecuteNonQuery() > 0);
        }
        internal void ClearColumn(string column)
        {
            using var connection = StateDbConnectionFactory.Open(Path, StateDbConnectionProfile.ReadWrite);
            using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE spawn_registry SET {column} = NULL";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class TestClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }
}
