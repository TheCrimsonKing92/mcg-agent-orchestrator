using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = ConductorInvariantObservationReaderTests.Fixture;

// Parallel-safe: workspace, build storage, monitor output and attempt files are fixture-owned.
public sealed class ConductorProgramHealthReaderReuseTests
{
    [Fact]
    public void Read_MalformedGroupedRewrite_RetriesWithoutReusingOldProgress()
    {
        using var fixture = new Fixture();
        var now = fixture.Clock.Now;
        var attempt = fixture.NewAttempt() with { ReconciledAt = now, Detail = "passed" };
        ConductorGroupedGateAttemptCoordinator.Save(attempt);
        Assert.Equal(now, fixture.Reader.Read(fixture.Goals, now).LastGateProgressAt);
        File.WriteAllText(attempt.MetadataPath, "{invalid");
        for (var call = 0; call < 2; call++)
        {
            Assert.Null(fixture.Reader.Read(fixture.Goals, now).LastGateProgressAt);
            Assert.Equal(1, fixture.Reader.LastGroupedAttemptReads);
        }
        ConductorGroupedGateAttemptCoordinator.Save(attempt);
        Assert.Equal(now, fixture.Reader.Read(fixture.Goals, now).LastGateProgressAt);
        Assert.Equal(1, fixture.Reader.LastGroupedAttemptReads);
    }

    [Fact]
    public void EvaluateWithInvariants_RepeatedCalls_RetainsTailAndGroupedCache()
    {
        using var fixture = new Fixture();
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        var now = fixture.Clock.Now;
        var storage = new DotnetBuildStorageRoot(Path.Combine(fixture.Root, "build-storage"));
        var log = workspace.ConductEventsLogPath;
        var writer = new ConductEventLogWriter(log, maxBytes: 2048, utcNow: () => fixture.Clock.Now, rotatedGenerationCount: 2);
        // Monitor-generated advisory events must not alter the input log's byte counts.
        var output = new ConductEventLogWriter(Path.Combine(fixture.Root, "monitor.log"), utcNow: () => fixture.Clock.Now);
        var monitor = ConductorProgramHealthMonitor.ForWorkspace(workspace, output, storage);
        var reader = Assert.IsType<ConductorInvariantObservationReader>(monitor.InvariantReader);
        var coordinator = new ConductorGroupedGateAttemptCoordinator(
            Path.Combine(workspace.OrchestratorDirectory, "grouped-gate-attempts"), generationId: 123,
            launch: _ => throw new InvalidOperationException("No process is launched by this fixture."),
            isProcessAlive: _ => true, stop: _ => true, utcNow: () => fixture.Clock.Now, buildStorageRoot: storage);
        var attempt = coordinator.Create("merge-train", [], "main", "tree", "manifest", "identity",
            fixture.Root, ConductorAutonomyPolicy.Permissive);
        ConductorGroupedGateAttemptCoordinator.Save(attempt);
        writer.Append("replay", null, "ACCEPTANCE_COHORT_INFLIGHT members=fixture");

        monitor.EvaluateWithInvariants(fixture.Goals, null);
        Assert.Same(reader, monitor.InvariantReader);
        Assert.Equal(1, reader.LastGroupedAttemptReads);
        Assert.True(reader.LogTail.LastReadBytes > 0);
        monitor.EvaluateWithInvariants(fixture.Goals, null);
        Assert.Same(reader, monitor.InvariantReader);
        Assert.Equal(0, reader.LastGroupedAttemptReads);
        Assert.Equal(0, reader.LogTail.LastReadBytes);

        var before = new FileInfo(log).Length;
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
        writer.Append("goal", null, $"GOAL goal={fixture.Goals[0].Id.Value[..8]} result=held reason=acceptance_width_1_reached");
        var appendedBytes = new FileInfo(log).Length - before;
        monitor.EvaluateWithInvariants(fixture.Goals, null);
        Assert.Same(reader, monitor.InvariantReader);
        Assert.Equal(appendedBytes, reader.LogTail.LastReadBytes);
        Assert.Equal(0, reader.LastGroupedAttemptReads);

        var reconciled = now.AddMinutes(-1);
        ConductorGroupedGateAttemptCoordinator.Save(attempt with { ReconciledAt = reconciled, Detail = "passed" });
        var observation = reader.Read(fixture.Goals, now);
        Assert.Equal(1, reader.LastGroupedAttemptReads);
        Assert.Equal(reconciled, observation.LastGateProgressAt);
        Assert.Equal(0, reader.LogTail.LastReadBytes);
        reader.Read(fixture.Goals, now);
        Assert.Equal(0, reader.LastGroupedAttemptReads);
        File.Delete(attempt.MetadataPath);
        Assert.Null(reader.Read(fixture.Goals, now).LastGateProgressAt);
    }
}
