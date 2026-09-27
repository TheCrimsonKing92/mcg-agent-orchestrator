using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class ConductorSupervisorBuildIdentityTests
{
    [Xunit.Fact]
    public async Task NewSupervisorRecordsAdoptedBuildWhenLeaseArrives()
    {
        var own = new ConductorSupervisorBuildIdentity("commit-b", "C:\\run-b");
        var seam = new SupervisorHandoffFixture.FakeHandoffSeam(own);
        var clock = new SupervisorHandoffFixture.AdvancingClock();
        var token = Guid.NewGuid().ToString("N");
        var record = new ConductorSupervisorHandoffRecord(token,
            new ConductorSupervisorProcessIdentity(901, clock.GetUtcNow()),
            new ConductorSupervisorBuildIdentity("commit-a", "C:\\run-a"),
            new ConductorSupervisorBuildSnapshot("commit-b", own.RunDirectory,
                "C:\\run-b\\Mcg.AgentOrchestrator.App.dll"),
            null, 0, null, 0, "ready.json");
        seam.WriteRecord(record);
        seam.IncomingRecordPath = "record.json";
        var events = new SupervisorHandoffFixture.RecordingEvents();
        var host = new StopHost();
        var supervisor = new ConductorContinuitySupervisor(host, events,
            timeProvider: clock,
            delay: (duration, _) =>
            {
                clock.Advance(duration);
                seam.Acquire(own);
                return Task.CompletedTask;
            },
            supervisorHandoff: new ConductorSupervisorHandoffOptions(seam, own,
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)));

        var exit = await supervisor.RunAsync(["conduct", "--loop"], "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-handoff-inbound-{Guid.NewGuid():N}"),
            "default", "default");

        Assert.Equal(0, exit);
        var build = Assert.Single(events.Events.Where(evt => evt.Operation == "supervisor-build"));
        Assert.Contains("commit-b", build.Detail, StringComparison.Ordinal);
        Assert.Contains("run-b", build.Detail, StringComparison.Ordinal);
        Assert.Contains("handoff", build.Detail, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task FailedLaunchOrReadinessKeepsIncumbentLeaseAndAdoptedChild(bool readinessTimeout)
    {
        var fixture = new SupervisorHandoffFixture();
        fixture.Seam.ThrowOnLaunch = !readinessTimeout;
        fixture.Seam.SuppressReadiness = readinessTimeout;

        var exit = await fixture.RunAsync();

        Assert.Equal(0, exit);
        Assert.Equal(fixture.Seam.Self, fixture.Seam.Owner);
        Assert.Equal(3, fixture.Host.Requests.Count);
        Assert.Contains(fixture.Events.Events, evt => evt.Operation == "supervisor-handoff" &&
            evt.Status == "failed" && evt.Detail.Contains("commit-b", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task ThreeFailedAdoptionsEmitEscalationWithoutStoppingTheChild()
    {
        var fixture = new SupervisorHandoffFixture(stopAtIndex: 4);
        fixture.Seam.ThrowOnLaunch = true;

        var exit = await fixture.RunAsync();

        Assert.Equal(0, exit);
        Assert.Equal(5, fixture.Host.Requests.Count);
        Assert.Equal(3, fixture.Seam.Launches.Count);
        Assert.Equal(fixture.Seam.Self, fixture.Seam.Owner);
        Assert.Contains(fixture.Events.Events, evt => evt.Operation == "supervisor-handoff" &&
            evt.Status == "escalated" && evt.Detail.Contains("consecutiveFailures", StringComparison.Ordinal));
    }

    private sealed class StopHost : IConductorSupervisorProcessHost
    {
        public Task<ConductorSupervisorProcessResult> RunAsync(
            ConductorSupervisorProcessRequest request, CancellationToken cancellationToken)
        {
            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                new ConductorContinuityExitArtifact("stop-file", 0, 0, false));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 902));
        }
    }
}
