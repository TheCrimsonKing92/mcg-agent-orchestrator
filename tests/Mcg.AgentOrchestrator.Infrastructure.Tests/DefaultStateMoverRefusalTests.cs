using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: refusal signals and all files are scoped to a unique fixture.
public sealed class DefaultStateMoverRefusalTests
{
    [Theory]
    [InlineData("lock", "Conductor lock is held")]
    [InlineData("stop", "Conductor stop file is pending")]
    [InlineData("goal", "Board has non-terminal goals")]
    [InlineData("dispatch", "Board has active dispatches")]
    [InlineData("process", "Board has active dispatches")]
    [InlineData("destination", "Destination already exists")]
    [InlineData("hash", "hash verification failed")]
    public async Task Move_UnsafeOrCorruptCopy_RefusesWithoutChangingOriginal(
        string scenario, string reason)
    {
        using var fixture = new DefaultStateMoverFixture();
        await fixture.Seed(activeGoal: scenario == "goal", activeDispatch: scenario == "dispatch", activeProcess: scenario == "process");
        if (scenario == "stop") File.WriteAllText(Path.Combine(fixture.Repository, ConductorBatchLoop.StopFileName), "stop");
        if (scenario == "destination") Directory.CreateDirectory(fixture.Destination);
        var before = DefaultStateMoverFixture.Snapshot(fixture.Source);
        var copied = 0;
        var probe = new FixedProbe(scenario == "lock" ? 12345 : null);
        var mover = fixture.Mover(lockProbe: probe, copyFile: (source, destination) =>
        {
            copied++;
            File.Copy(source, destination);
            if (scenario == "hash") File.AppendAllText(destination, "corruption");
        });
        var result = mover.Move();
        Assert.False(result.Succeeded);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.Refusals, refusal => refusal.Contains(reason));
        Assert.Equal(before, DefaultStateMoverFixture.Snapshot(fixture.Source));
        if (scenario == "destination") Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Destination));
        else Assert.False(Directory.Exists(fixture.Destination));
        Assert.Null(new DirectoryInfo(fixture.Source).LinkTarget);
        Assert.Null(fixture.Registry.GetDefaultStateLocation(fixture.Repository));
        Assert.False(File.Exists(fixture.Registry.RegistryPath));
        Assert.False(Directory.Exists(fixture.Backup));
        if (Directory.Exists(fixture.DataRoot))
            Assert.Empty(Directory.GetDirectories(fixture.DataRoot, "*.staging-*", SearchOption.AllDirectories));
        if (scenario == "hash") Assert.True(copied > 0);
        else Assert.Equal(0, copied);
        Assert.True(probe.Calls > 0);
    }

    [Fact]
    public async Task Move_ConductorStartsDuringCopy_RefusesBeforePromotion()
    {
        using var fixture = new DefaultStateMoverFixture();
        await fixture.Seed();
        var before = DefaultStateMoverFixture.Snapshot(fixture.Source);
        var probe = new FixedProbe(null);
        var result = fixture.Mover(lockProbe: probe, copyFile: (source, destination) =>
        {
            File.Copy(source, destination);
            probe.Owner = 12345; // Deterministic signal at the copy boundary, no timed race.
        }).Move();
        Assert.False(result.Succeeded);
        Assert.Contains(result.Refusals, reason => reason.Contains("Conductor lock is held"));
        Assert.True(probe.Calls >= 2);
        Assert.Equal(before, DefaultStateMoverFixture.Snapshot(fixture.Source));
        Assert.False(Directory.Exists(fixture.Destination));
        Assert.Null(fixture.Registry.GetDefaultStateLocation(fixture.Repository));
        Assert.Empty(Directory.GetDirectories(fixture.DataRoot, "*.staging-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Move_RegistryCommitFails_RenamesOriginalBackAndCleansCopy()
    {
        using var fixture = new DefaultStateMoverFixture();
        await fixture.Seed();
        var before = DefaultStateMoverFixture.Snapshot(fixture.Source);
        // A file at the registry directory permits reads of an absent projects.json,
        // but makes the atomic save fail after the source has been backed up.
        File.WriteAllText(fixture.Registry.RegistryDirectory, "block registry directory");
        var result = fixture.Mover().Move();
        Assert.False(result.Succeeded);
        Assert.Contains(result.PlanLines, line => line == "Original state restored at the old location.");
        Assert.Equal(before, DefaultStateMoverFixture.Snapshot(fixture.Source));
        Assert.False(Directory.Exists(fixture.Destination));
        Assert.False(Directory.Exists(fixture.Backup));
        Assert.Null(fixture.Registry.GetDefaultStateLocation(fixture.Repository));
        Assert.Empty(Directory.GetDirectories(fixture.DataRoot, "*.staging-*", SearchOption.AllDirectories));
    }

    private sealed class FixedProbe(int? owner) : IConductorLockProbe
    {
        internal int? Owner { get; set; } = owner;
        internal int Calls { get; private set; }
        public int? ActiveOwnerPid(OrchestratorWorkspace workspace)
        {
            Calls++;
            return Owner;
        }
    }

    [Fact]
    public async Task Move_TenantConductorLockHeld_RefusesWithoutChangingOriginal()
    {
        using var fixture = new DefaultStateMoverFixture();
        await fixture.Seed();
        var tenant = Path.Combine(fixture.Source, "tenants", "t1");
        using var lease = ConductorLoopLease.Acquire(tenant);
        Assert.True(ConductorLoopLease.IsActive(tenant));
        var before = DefaultStateMoverFixture.Snapshot(fixture.Source);
        var copied = 0;
        var result = fixture.Mover(copyFile: (source, destination) =>
        {
            copied++;
            File.Copy(source, destination);
        }).Move();
        Assert.False(result.Succeeded);
        Assert.Contains(result.Refusals, reason => reason.Contains("Conductor lock is held"));
        Assert.Equal(before, DefaultStateMoverFixture.Snapshot(fixture.Source));
        Assert.Equal(0, copied);
        Assert.True(ConductorLoopLease.IsActive(tenant));
        Assert.False(Directory.Exists(fixture.Destination));
        Assert.Null(fixture.Registry.GetDefaultStateLocation(fixture.Repository));
        Assert.False(File.Exists(fixture.Registry.RegistryPath));
        Assert.False(Directory.Exists(fixture.Backup));
        Assert.False(Directory.Exists(fixture.DataRoot));
    }

    [Fact]
    public async Task Move_UnreadableGoalSnapshot_RefusesWithoutChangingOriginal()
    {
        using var fixture = new DefaultStateMoverFixture();
        await fixture.Seed();
        using (var connection = Mcg.AgentOrchestrator.Infrastructure.StateDbConnectionFactory.Open(
            Path.Combine(fixture.Source, "state.db"), Mcg.AgentOrchestrator.Infrastructure.StateDbConnectionProfile.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE goals SET snapshot_json = 'null'";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var before = DefaultStateMoverFixture.Snapshot(fixture.Source);
        var result = fixture.Mover().Move();
        Assert.False(result.Succeeded);
        Assert.Contains(result.Refusals, reason => reason.Contains("unreadable goal snapshots"));
        Assert.Equal(before, DefaultStateMoverFixture.Snapshot(fixture.Source));
        Assert.False(Directory.Exists(fixture.Destination));
        Assert.Null(fixture.Registry.GetDefaultStateLocation(fixture.Repository));
    }
}
