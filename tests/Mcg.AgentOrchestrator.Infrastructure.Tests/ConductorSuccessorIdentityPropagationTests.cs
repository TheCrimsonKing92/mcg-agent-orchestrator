using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class ConductorSuccessorIdentityPropagationTests
{
    [Xunit.Fact]
    public void BothLaunchBuildersRecordStartTimeOnce()
    {
        var startedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var request = new ConductLoopLaunchRequest("test", [], "out", "err", "work", 0);
        var reads = 0;
        var detached = ConductorLoopHandoff.CreateDetachedLauncherResult(request, "4567", pid =>
        {
            Xunit.Assert.Equal(4567, pid);
            reads++;
            return startedAt;
        });
        var breakaway = ConductorLoopHandoff.CreateBreakawayLaunchResult(request, 4567, "false", startedAt);

        Xunit.Assert.Equal(1, reads);
        Xunit.Assert.Equal(new ConductorSupervisorProcessIdentity(4567, startedAt), detached.SuccessorIdentity);
        Xunit.Assert.Equal(detached.SuccessorIdentity, breakaway.SuccessorIdentity);
    }

    [Xunit.Fact]
    public void FailedConductorHandoffPassesRecordedIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var startedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        ConductorSupervisorProcessIdentity? stopped = null;
        try
        {
            var options = new ConductLoopHandoffOptions(
                ["conduct", "--loop"], root, Path.Combine(root, ".orchestrator"),
                Path.Combine(root, ".orchestrator", "logs"), Path.Combine(root, "run-events.db"),
                Path.Combine(root, "stop"), 0, 3, () => { },
                SuccessorReadyProbe: (_, _) => true,
                StopFailedSuccessor: identity => stopped = identity);
            _ = ConductorLoopHandoff.TryStartSuccessor(options,
                new ConductorLoopHandoffRequest(1, TimeSpan.FromHours(4), 0),
                request => new ConductLoopLaunchResult(4567, request.StdoutPath, request.StderrPath,
                    StartedAt: startedAt),
                (_, _) => throw new InvalidOperationException("synthetic verification failure"));

            Xunit.Assert.Equal(new ConductorSupervisorProcessIdentity(4567, startedAt), stopped);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Xunit.Fact]
    public async Task FailedSupervisorHandoffPassesRecordedIdentity()
    {
        var fixture = new SupervisorHandoffFixture();
        fixture.Seam.SuppressReadiness = true;

        _ = await fixture.RunAsync();

        Xunit.Assert.Equal([fixture.Seam.Successor], fixture.Seam.Stopped);
    }
}
