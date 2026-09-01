using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class AcceptanceAttemptCancellationProbeTests
{
    [Xunit.Fact]
    public void ConcurrentObserversShareRefreshAndFirstCancellationStaysLatched()
    {
        var time = new RecordingTimeProvider();
        var reads = 0;
        var stopRequested = 0;
        var probe = new AcceptanceAttemptCancellationProbe(
            () =>
            {
                Interlocked.Increment(ref reads);
                return Volatile.Read(ref stopRequested) == 0
                    ? AcceptanceAttemptCancellationDecision.Continue(GoalStatus.Verifying)
                    : AcceptanceAttemptCancellationDecision.Cancel(
                        AcceptanceAttemptCancellationCause.StoppedDisposition,
                        GoalStatus.Parked);
            },
            TimeSpan.FromSeconds(2),
            time);

        Parallel.For(0, 32, _ => Assert.False(probe.ShouldCancel()));
        Assert.Equal(1, Volatile.Read(ref reads));

        Volatile.Write(ref stopRequested, 1);
        Parallel.For(0, 32, _ => Assert.False(probe.ShouldCancel()));
        Assert.Equal(1, Volatile.Read(ref reads));

        time.Advance(TimeSpan.FromSeconds(2));
        Parallel.For(0, 32, _ => Assert.True(probe.ShouldCancel()));
        Assert.Equal(2, Volatile.Read(ref reads));

        Volatile.Write(ref stopRequested, 0);
        time.Advance(TimeSpan.FromSeconds(2));
        Parallel.For(0, 32, _ => Assert.True(probe.ShouldCancel()));
        Assert.Equal(2, Volatile.Read(ref reads));
        Assert.Equal(
            AcceptanceAttemptCancellationCause.StoppedDisposition,
            probe.CancellationDecision?.Cause);
    }
}
