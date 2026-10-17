using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: all transport, filesystem and clock state belongs to each fixture.
public sealed class SshRemoteLaneHandleTestsStartedJobCancellation
{
    [Xunit.Fact]
    public async Task RunningStatus_Cancellation_CopiesExactlyOnce()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();
        await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status("running")));
        var before = fake.Calls.Count;
        handle.RequestQueuedJobCancellation();
        Assert.NotNull(handle.QueuedJobCancellation);
        try { await handle.QueuedJobCancellation.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: started job cancel copy completed"); }
        var copy = Assert.Single(fake.Calls.Skip(before));
        Assert.Equal(new[] { SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes", "cancel.txt",
            "runner:C:/mcg-executor/queue/attempt-one-infrastructure-tests-cli-lane.cancel" }, copy.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(30), copy.Bound);
        handle.RequestQueuedJobCancellation();
        Assert.Equal(before + 1, fake.Calls.Count);
    }

    [Xunit.Fact]
    public async Task QueuedStatus_ChangingHeartbeat_RefreshesOnlyWhenChanged()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();
        var start = fake.Clock.GetUtcNow();
        await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status("queued"), "q1"));
        Assert.Equal(start, handle.NewestHeartbeat);
        fake.Clock.Advance(TimeSpan.FromSeconds(50));
        await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status("queued"), "q2"));
        Assert.Equal(start.AddSeconds(50), handle.NewestHeartbeat);
        fake.Clock.Advance(TimeSpan.FromSeconds(50));
        await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status("queued"), "q2"));
        Assert.Equal(start.AddSeconds(50), handle.NewestHeartbeat);
    }

    [Xunit.Fact]
    public async Task QueuedStatus_ForeignAttempt_DoesNotRefreshHeartbeat()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();
        var status = FakeSshRemoteLaneTransport.Status("queued");
        status["attemptId"] = "another-attempt";
        await fake.PollOnce(new(status, "q1"));
        Assert.Null(handle.NewestHeartbeat);
    }

    [Xunit.Theory]
    [Xunit.InlineData("completed")]
    [Xunit.InlineData("failed")]
    [Xunit.InlineData("cancelled")]
    public async Task TerminalStatus_Cancellation_DoesNotCopyMarker(string state)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();
        await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status(state)));
        handle.RequestQueuedJobCancellation();
        Assert.Null(handle.QueuedJobCancellation);
        Assert.DoesNotContain(fake.Calls, call => call.Arguments[^1].EndsWith(".cancel", StringComparison.Ordinal));
    }
}
