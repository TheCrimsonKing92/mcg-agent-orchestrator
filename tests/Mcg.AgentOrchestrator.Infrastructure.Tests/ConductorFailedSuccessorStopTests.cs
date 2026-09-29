using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorFailedSuccessorStopTests
{
    [Xunit.Fact]
    public void MatchingIdentityStopsTreeAndConfirmsExit()
    {
        var startedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var process = new FakeProcess(startedAt);
        var lines = new List<string>();

        ConductorLoopHandoff.StopFailedSuccessor(new(4567, startedAt), new FakeFacts(process), lines.Add);

        Xunit.Assert.Equal(1, process.Kills);
        Xunit.Assert.Equal(5000, process.WaitMilliseconds);
        Xunit.Assert.Empty(lines);
    }

    [Xunit.Fact]
    public void DifferentIdentityAndExitedPidAreRefused()
    {
        var startedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var process = new FakeProcess(startedAt.AddSeconds(1));
        var lines = new List<string>();
        var identity = new ConductorSupervisorProcessIdentity(4567, startedAt);

        ConductorLoopHandoff.StopFailedSuccessor(identity, new FakeFacts(process), lines.Add);
        ConductorLoopHandoff.StopFailedSuccessor(identity, new FakeFacts(null), lines.Add);

        Xunit.Assert.Equal(0, process.Kills);
        Xunit.Assert.Equal([
            "SUCCESSOR_STOP_REFUSED pid=4567 reason=identity-mismatch",
            "SUCCESSOR_STOP_REFUSED pid=4567 reason=exited"], lines);
    }

    private sealed class FakeFacts(FakeProcess? process) : ISuccessorProcessFacts
    {
        public ISuccessorProcessHandle? TryOpen(int processId) => process;
    }

    private sealed class FakeProcess(DateTimeOffset startedAt) : ISuccessorProcessHandle
    {
        public bool HasExited { get; private set; }
        public DateTimeOffset? StartedAt => startedAt;
        public int Kills { get; private set; }
        public int WaitMilliseconds { get; private set; }
        public void KillTree() { Kills++; HasExited = true; }
        public bool WaitForExit(int milliseconds) { WaitMilliseconds = milliseconds; return true; }
        public void Dispose() { }
    }
}
