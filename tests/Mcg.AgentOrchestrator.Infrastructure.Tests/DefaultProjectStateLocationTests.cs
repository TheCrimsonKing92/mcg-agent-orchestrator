using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: explicit registry/data roots, no environment mutation.
public sealed class DefaultProjectStateLocationTests
{
    [Fact]
    public void ForDirectory_NoMatchingRecord_PreservesAllLegacyPaths()
    {
        using var fixture = new DefaultStateMoverFixture();
        var before = OrchestratorWorkspace.ForDirectory(fixture.Repository, fixture.Repository, null, fixture.Registry);
        var otherRoot = Path.Combine(fixture.Root, "other-repo");
        var other = new DefaultProjectStateLocation(otherRoot, DefaultProjectStateLocation.DestinationSubpath(otherRoot),
            ".orchestrator.backup-20261008T123000Z", DefaultStateMoverFixture.Timestamp);
        fixture.Registry.RecordDefaultStateLocation(other);
        var after = OrchestratorWorkspace.ForDirectory(fixture.Repository, fixture.Repository, null, fixture.Registry);
        Assert.Equal(before, after);
        Assert.Equal(fixture.Source, before.OrchestratorDirectory);
        Assert.Equal(Path.Combine(fixture.Source, "state.db"), before.SqliteStatePath);
        Assert.Equal(Path.Combine(fixture.Source, "logs"), before.LogDirectory);
        Assert.Equal(Path.Combine(fixture.Source, "backlog.db"), before.BacklogStorePath);
        Assert.False(before.IsProjectScoped);
        Assert.Equal(Path.Combine(fixture.Source, "tenants", "t1"),
            OrchestratorWorkspace.ForDirectory(fixture.Repository, null, "t1", fixture.Registry).OrchestratorDirectory);
    }

    [Fact]
    public async Task ForDirectory_RecordedMove_ResolvesAllStoresAndTenantUnderDataRoot()
    {
        using var fixture = new DefaultStateMoverFixture();
        await fixture.Seed();
        Assert.True(fixture.Mover().Move().Succeeded);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Repository, null, null, fixture.Registry);
        Assert.Equal(fixture.Destination, workspace.OrchestratorDirectory);
        Assert.Equal(Path.Combine(fixture.Destination, "state.db"), workspace.SqliteStatePath);
        Assert.Equal(Path.Combine(fixture.Destination, "logs"), workspace.LogDirectory);
        Assert.Equal(Path.Combine(fixture.Destination, "backlog.db"), workspace.BacklogStorePath);
        Assert.Equal(Path.Combine(fixture.Destination, "agents.json"), workspace.AgentCatalogPath);
        Assert.Equal(fixture.Repository, workspace.RootDirectory);
        Assert.Equal(fixture.Repository, workspace.ExecutionDirectory);
        Assert.False(workspace.IsProjectScoped);
        var tenant = OrchestratorWorkspace.ForDirectory(fixture.Repository, null, "t1", fixture.Registry);
        Assert.Equal(Path.Combine(fixture.Destination, "tenants", "t1", "state.db"), tenant.SqliteStatePath);
        Assert.True(tenant.IsTenantScoped);
        var reopened = new OrchestratorProjectRegistry(fixture.Registry.RegistryDirectory, fixture.DataRoot);
        Assert.Equal(fixture.Registry.GetDefaultStateLocation(fixture.Repository), reopened.GetDefaultStateLocation(fixture.Repository));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData(".")]
    public void RecordDefaultStateLocation_EscapingPath_RejectsWithoutWriting(string relative)
    {
        using var fixture = new DefaultStateMoverFixture();
        var record = new DefaultProjectStateLocation(fixture.Repository, relative,
            ".orchestrator.backup-20261008T123000Z", DefaultStateMoverFixture.Timestamp);
        Assert.Throws<InvalidOperationException>(() => fixture.Registry.RecordDefaultStateLocation(record));
        Assert.False(File.Exists(fixture.Registry.RegistryPath));
    }

    [Fact]
    public void Registry_ProjectCreateAndClear_PreserveOtherRootsAndProjectEntries()
    {
        using var fixture = new DefaultStateMoverFixture();
        var record = new DefaultProjectStateLocation(fixture.Repository,
            DefaultProjectStateLocation.DestinationSubpath(fixture.Repository),
            ".orchestrator.backup-20261008T123000Z", DefaultStateMoverFixture.Timestamp);
        fixture.Registry.RecordDefaultStateLocation(record);
        fixture.Registry.CreateProject("alpha", fixture.Repository);
        Assert.Equal(record, fixture.Registry.GetDefaultStateLocation(fixture.Repository));
        Assert.Throws<ArgumentException>(() => fixture.Registry.CreateProject("default", fixture.Repository));
        fixture.Registry.ClearDefaultStateLocation(Path.Combine(fixture.Root, "unrelated"));
        Assert.Equal(record, fixture.Registry.GetDefaultStateLocation(fixture.Repository));
        fixture.Registry.ClearDefaultStateLocation(fixture.Repository);
        Assert.Null(fixture.Registry.GetDefaultStateLocation(fixture.Repository));
        Assert.Single(fixture.Registry.ListProjects());
        Assert.DoesNotContain("DefaultProjectStateLocations", File.ReadAllText(fixture.Registry.RegistryPath));
    }
}
