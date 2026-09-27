using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class ConductorContinuitySupervisorInheritedChildTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false, false, false, false)]
    [Xunit.InlineData(true, false, false, false)]
    [Xunit.InlineData(false, true, false, false)]
    [Xunit.InlineData(false, false, true, false)]
    [Xunit.InlineData(false, false, false, true)]
    public async Task InheritedChildUsesNormalExitHandling(
        bool plannedRestart, bool missingBeforeAttach, bool deliberateStop, bool tickStall)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mcg-inherited-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var own = new ConductorSupervisorBuildIdentity("commit-b", "C:\\run-b");
            var seam = new SupervisorHandoffFixture.FakeHandoffSeam(own);
            var clock = new SupervisorHandoffFixture.AdvancingClock();
            var events = new SupervisorHandoffFixture.RecordingEvents();
            var child = new ConductorSupervisorActiveChild(
                new ConductorSupervisorProcessIdentity(901, clock.GetUtcNow()),
                Path.Combine(directory, "inherited-exit.json"),
                Path.Combine(directory, "inherited.out.log"),
                Path.Combine(directory, "inherited.err.log"), 2);
            if (plannedRestart || deliberateStop)
                ConductorContinuityExitArtifact.Write(child.ExitArtifactPath,
                    new ConductorContinuityExitArtifact(deliberateStop ? "stop-file" : "max-duration",
                        1, 3, plannedRestart));
            seam.InheritedChildResult = new ConductorSupervisorProcessResult(
                plannedRestart || deliberateStop ? 0 : 1, child.Process.ProcessId);
            seam.InheritedChildMissingBeforeAttach = missingBeforeAttach;
            seam.InheritedChildWaitForCancellation = tickStall;
            seam.WriteRecord(new ConductorSupervisorHandoffRecord(
                Guid.NewGuid().ToString("N"),
                new ConductorSupervisorProcessIdentity(800, clock.GetUtcNow()),
                new ConductorSupervisorBuildIdentity("commit-a", "C:\\run-a"),
                new ConductorSupervisorBuildSnapshot("commit-b", own.RunDirectory,
                    "C:\\run-b\\Mcg.AgentOrchestrator.App.dll"),
                null, 0, null, 0, Path.Combine(directory, "ready.json"), child));
            seam.IncomingRecordPath = "record.json";
            var host = new StopHost();
            var supervisor = new ConductorContinuitySupervisor(host, events,
                timeProvider: clock,
                delay: async (duration, cancellationToken) =>
                {
                    clock.Advance(duration);
                    seam.Acquire(own);
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                },
                tickStallDelay: (_, _) => Task.CompletedTask,
                dumpCapture: new NoDumpCapture(),
                supervisorHandoff: new ConductorSupervisorHandoffOptions(seam, own,
                    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)));

            var exit = await supervisor.RunAsync(["conduct", "--loop"], "C:\\repo",
                directory, "default", "default");

            Assert.Equal(0, exit);
            Assert.Equal(1, seam.InheritedChildObservations);
            Assert.Equal(deliberateStop ? 0 : 1, host.Launches);
            Assert.Contains(events.Events, evt => evt.Operation == (deliberateStop ? "stopped" : "restart") &&
                evt.Status == (deliberateStop ? "completed" : plannedRestart ? "planned" : "unexpected"));
            if (tickStall)
                Assert.Contains(events.Events, evt => evt.Operation == "tick-stall" &&
                    evt.Status == "detected");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class NoDumpCapture : IConductorDiagnosticDumpCapture
    {
        public Task<ConductorDumpCaptureResult> CaptureAsync(int? processId,
            string outputDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(ConductorDumpCaptureResult.NotCaptured("injected-test"));
    }

    private sealed class StopHost : IConductorSupervisorProcessHost
    {
        internal int Launches { get; private set; }

        public Task<ConductorSupervisorProcessResult> RunAsync(
            ConductorSupervisorProcessRequest request, CancellationToken cancellationToken)
        {
            Launches++;
            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                new ConductorContinuityExitArtifact("stop-file", 0, 0, false));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 902));
        }
    }
}
