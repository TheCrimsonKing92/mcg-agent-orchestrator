using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class TestOwnedProcessStopTests
{
    [Xunit.Fact]
    public void StopsOnlyMatchingRecordedStartTime()
    {
        var startedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var process = new FakeProcess(startedAt);
        var facts = new FakeFacts(process);

        Xunit.Assert.False(TestOwnedProcessStop.StopTreeIfSame(new(4567, startedAt.AddSeconds(1)), facts));
        Xunit.Assert.Equal(0, process.Kills);
        Xunit.Assert.True(TestOwnedProcessStop.StopTreeIfSame(new(4567, startedAt), facts));
        Xunit.Assert.Equal(1, process.Kills);
    }

    private sealed class FakeFacts(FakeProcess process) : ISuccessorProcessFacts
    {
        public ISuccessorProcessHandle? TryOpen(int processId) => process;
    }

    private sealed class FakeProcess(DateTimeOffset startedAt) : ISuccessorProcessHandle
    {
        public bool HasExited => false;
        public DateTimeOffset? StartedAt => startedAt;
        public int Kills { get; private set; }
        public void KillTree() => Kills++;
        public bool WaitForExit(int milliseconds) => true;
        public void Dispose() { }
    }
}
