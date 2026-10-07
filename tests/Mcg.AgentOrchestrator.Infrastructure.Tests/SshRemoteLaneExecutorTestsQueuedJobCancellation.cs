using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SshRemoteLaneExecutorTestsQueuedJobCancellation
{
    [Xunit.Fact]
    public async Task FailedPollCopiesOneBoundedCancelMarker()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();
        await fake.PollOnce(new(null, Exit: 1));
        var before = fake.Calls.Count;
        handle.RequestQueuedJobCancellation();
        await CancellationCompleted(handle);
        var copy = Assert.Single(fake.Calls.Skip(before));
        Assert.Equal(new[] { SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes",
            "cancel.txt", "runner:C:/mcg-executor/queue/attempt-one-infrastructure-tests-cli-lane.cancel" }, copy.Arguments);
        var jobCopy = Assert.Single(fake.Calls.Where(call => call.Arguments[0] == SshRemoteLaneExecutor.ScpPath &&
            call.Arguments[3].EndsWith(".json", StringComparison.Ordinal) && !call.Arguments[3].EndsWith("/status.json", StringComparison.Ordinal)));
        Assert.Equal(jobCopy.Directory, copy.Directory);
        Assert.Equal("", File.ReadAllText(Path.Combine(copy.Directory, copy.Arguments[3])));
        Assert.Equal(TimeSpan.FromSeconds(30), copy.Bound);
        handle.RequestQueuedJobCancellation();
        await CancellationCompleted(handle);
        Assert.Equal(before + 1, fake.Calls.Count);
    }

    [Xunit.Fact]
    public async Task MatchingRunningStatusSuppressesCancelCopy()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();
        await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status()));
        var before = fake.Calls.Count;
        handle.RequestQueuedJobCancellation();
        Assert.Null(handle.QueuedJobCancellation);
        Assert.Equal(before, fake.Calls.Count);
    }

    [Xunit.Fact]
    public async Task ForeignAttemptStatusDoesNotSuppressCancelCopy()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();
        var status = FakeSshRemoteLaneTransport.Status();
        status["attemptId"] = "another-attempt";
        await fake.PollOnce(new(status));
        handle.RequestQueuedJobCancellation();
        await CancellationCompleted(handle);
        Assert.Single(CancelCopies(fake));
    }

    [Xunit.Fact]
    public async Task CopyExceptionCannotEscapeTheRequestOrItsTask()
    {
        await using var fake = new FakeSshRemoteLaneTransport { ThrowOnCancelCopy = true };
        var handle = await fake.Submit();
        await fake.PollOnce(new(null, Exit: 1));
        Assert.Null(Xunit.Record.Exception(handle.RequestQueuedJobCancellation));
        Assert.Null(await Xunit.Record.ExceptionAsync(() => CancellationCompleted(handle)));
        Assert.Single(CancelCopies(fake));
    }

    [Xunit.Fact]
    public async Task AbandonAloneNeverCopiesCancelMarker()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();
        await fake.PollOnce(new(null, Exit: 1));
        handle.Abandon();
        await handle.PollLoop.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(handle.QueuedJobCancellation);
        Assert.Empty(CancelCopies(fake));
    }

    private static IEnumerable<FakeSshRemoteLaneTransport.Call> CancelCopies(FakeSshRemoteLaneTransport fake) =>
        fake.Calls.Where(call => call.Arguments.Length > 3 && call.Arguments[^1].EndsWith(".cancel", StringComparison.Ordinal));

    private static async Task CancellationCompleted(SshRemoteLaneHandle handle)
    {
        var task = handle.QueuedJobCancellation;
        Assert.NotNull(task);
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: queued cancel copy completed"); }
    }
}
