using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = ConductorInvariantObservationReaderTests.Fixture;

// Parallel-safe: all workspace and storage paths are under a fixture-owned GUID temp root.
public sealed class ConductorInvariantReaderWorkspaceWiringTests
{
    [Fact]
    public void ForWorkspace_OperatorLaneUnderAcceptanceRoot_ContributesProgress()
    {
        using var fixture = new Fixture();
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        var now = fixture.Clock.Now;
        Write(workspace.OrchestratorDirectory, "acceptance-gate-attempts", now);
        var reader = ConductorInvariantObservationReader.ForWorkspace(workspace,
            new DotnetBuildStorageRoot(Path.Combine(fixture.Root, "build-storage")));
        Assert.Equal(now.AddMinutes(-1), reader.Read([], now).LastGateProgressAt);
    }

    [Fact]
    public void ForWorkspace_LaneOutsideAcceptanceRoot_DoesNotContributeProgress()
    {
        using var fixture = new Fixture();
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        var now = fixture.Clock.Now;
        Write(workspace.OrchestratorDirectory, "other-attempts", now);
        var reader = ConductorInvariantObservationReader.ForWorkspace(workspace,
            new DotnetBuildStorageRoot(Path.Combine(fixture.Root, "build-storage")));
        Assert.Null(reader.Read([], now).LastGateProgressAt);
    }

    private static void Write(string root, string folder, DateTimeOffset now)
    {
        var path = Path.Combine(root, folder, "operator", "operator-gate-check.gate-heartbeat.json");
        GateHeartbeatArtifacts.Write(path,
            new(null, "test", "lane", null, null, null, "running", now, now, now.AddMinutes(-1), 0, 0, 0));
        File.SetLastWriteTimeUtc(path, now.UtcDateTime);
        Assert.True(File.Exists(path));
    }
}
