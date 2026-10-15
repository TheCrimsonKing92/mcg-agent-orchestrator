using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: callbacks and fault instances belong to each case.
public sealed class VerifyingAttemptExecutorReplayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replay_ResultOrRefusal_ReturnsOnceWithoutBusyMessage(bool refuses)
    {
        var expected = refuses ? null : new object();
        var calls = 0;
        var result = VerifyingAttemptExecutor.ExecuteReplay(() =>
        {
            calls++;
            return expected;
        }, out var busyMessage);

        Assert.Same(expected, result);
        Assert.Null(busyMessage);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Replay_WriterBusy_ReturnsOriginalMessageWithoutRetry()
    {
        var fault = new AcceptanceArtifactWriterLeaseBusyException("goal-dir", "passed-attempt");
        var calls = 0;
        var result = VerifyingAttemptExecutor.ExecuteReplay<object>(() =>
        {
            calls++;
            throw fault;
        }, out var busyMessage);

        Assert.Null(result);
        Assert.Equal(fault.Message, busyMessage);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Replay_UnrelatedFault_PropagatesWithoutRetry()
    {
        var fault = new InvalidDataException("invalid receipt");
        var calls = 0;
        Assert.Same(fault, Assert.Throws<InvalidDataException>(() =>
            VerifyingAttemptExecutor.ExecuteReplay<object>(() =>
            {
                calls++;
                throw fault;
            }, out _)));
        Assert.Equal(1, calls);
    }
}
