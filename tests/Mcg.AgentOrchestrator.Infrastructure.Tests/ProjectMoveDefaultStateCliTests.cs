using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: invoke the CLI seam with isolated roots and injected conductor signals.
public sealed class ProjectMoveDefaultStateCliTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DryRun_QuietOrBlocked_PrintsPlanAndReasonsWithoutChanges(bool blocked)
    {
        using var fixture = new DefaultStateMoverFixture();
        await fixture.Seed(activeGoal: blocked, activeDispatch: blocked);
        var before = DefaultStateMoverFixture.Snapshot(fixture.Root);
        using var output = new StringWriter();
        var code = ProjectCliCommand.Execute(["project", "move-default-state", "--dry-run"],
            fixture.Registry, fixture.Repository, "some-other-project", output,
            defaultStateMoverFactory: () => fixture.Mover(lockProbe: new Probe(blocked ? 12345 : null),
                stopPending: _ => blocked));
        Assert.Equal(blocked ? 1 : 0, code);
        foreach (var label in new[] { "Source:", "Staging:", "Destination:", "Backup:", "Registry:", "Junction:" })
            Assert.Contains(label, output.ToString());
        Assert.Contains(fixture.Destination, output.ToString());
        if (blocked)
        {
            foreach (var reason in new[] { "Conductor lock is held", "Conductor stop file is pending",
                "Board has non-terminal goals", "Board has active dispatches" })
                Assert.Contains(reason, output.ToString());
        }
        else Assert.DoesNotContain("Refused:", output.ToString());
        Assert.Equal(before, DefaultStateMoverFixture.Snapshot(fixture.Root));
        Assert.False(Directory.Exists(fixture.DataRoot));
        Assert.False(Directory.Exists(fixture.Registry.RegistryDirectory));
    }

    [Fact]
    public async Task Execute_MoveAndUndo_RoutesBothDirectionsAndUndoDryRun()
    {
        using var fixture = new DefaultStateMoverFixture();
        await fixture.Seed();
        using var output = new StringWriter();
        Assert.Equal(0, Execute([]));
        Assert.NotNull(fixture.Registry.GetDefaultStateLocation(fixture.Repository));
        var moved = DefaultStateMoverFixture.Snapshot(fixture.Root);
        Assert.Equal(0, Execute(["--undo", "--dry-run"]));
        Assert.Equal(moved, DefaultStateMoverFixture.Snapshot(fixture.Root));
        Assert.NotNull(new DirectoryInfo(fixture.Source).LinkTarget);
        Assert.Equal(0, Execute(["--undo"]));
        Assert.Null(fixture.Registry.GetDefaultStateLocation(fixture.Repository));
        Assert.Null(new DirectoryInfo(fixture.Source).LinkTarget);

        int Execute(string[] options) => ProjectCliCommand.Execute(
            new[] { "project", "move-default-state" }.Concat(options).ToArray(),
            fixture.Registry, fixture.Repository, null, output, defaultStateMoverFactory: () => fixture.Mover());
    }

    [Fact]
    public void Execute_UnknownFlag_ReportsUsageWithoutMutation()
    {
        using var fixture = new DefaultStateMoverFixture();
        var before = DefaultStateMoverFixture.Snapshot(fixture.Root);
        var error = Assert.Throws<ArgumentException>(() => ProjectCliCommand.Execute(
            ["project", "move-default-state", "--bogus"], fixture.Registry, fixture.Repository, null));
        Assert.Equal("Usage: project move-default-state [--undo] [--dry-run]", error.Message);
        Assert.Equal(before, DefaultStateMoverFixture.Snapshot(fixture.Root));
    }

    private sealed class Probe(int? owner) : IConductorLockProbe
    {
        public int? ActiveOwnerPid(OrchestratorWorkspace workspace) => owner;
    }
}
