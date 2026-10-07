using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: gates are instance-owned and the fake owns its temporary root.
public sealed class SshRemoteLaneExecutorTestsPushGate
{
    [Fact]
    public async Task PushGate_SerializesSameExecutorWhileOtherExecutorEnters()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var executor = CreateExecutor(fake);
        var firstEntry = executor.EnterPushGateAsync("one");
        Assert.True(firstEntry.IsCompletedSuccessfully);
        using var first = await firstEntry;
        var secondEntry = executor.EnterPushGateAsync("one");
        // test-design-discipline: allow-negative-wait - only disposing first can release this instance's gate.
        Assert.False(secondEntry.IsCompleted);
        var otherEntry = executor.EnterPushGateAsync("two");
        Assert.True(otherEntry.IsCompletedSuccessfully);
        using var other = await otherEntry;
        first.Dispose();
        using var second = await Event(secondEntry, "second push entered after first release");
        first.Dispose();
        var thirdEntry = executor.EnterPushGateAsync("one");
        // test-design-discipline: allow-negative-wait - only disposing second can release the gate; repeated first disposal cannot.
        Assert.False(thirdEntry.IsCompleted);
        second.Dispose();
        using var third = await Event(thirdEntry, "third push entered after second release");
    }

    [Fact]
    public async Task PushGate_CancelledWaitDoesNotTakeOrReleaseHeldGate()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var executor = CreateExecutor(fake);
        using var first = await executor.EnterPushGateAsync("one");
        using var cancellation = new CancellationTokenSource();
        var cancelledEntry = executor.EnterPushGateAsync("one", cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Event(cancelledEntry, "waiting push cancelled"));
        var nextEntry = executor.EnterPushGateAsync("one");
        // test-design-discipline: allow-negative-wait - cancellation of a waiting entrant must leave first holding the gate.
        Assert.False(nextEntry.IsCompleted);
        first.Dispose();
        using var next = await Event(nextEntry, "next push entered after cancellation and release");
    }

    private static SshRemoteLaneExecutor CreateExecutor(FakeSshRemoteLaneTransport fake) => new(
        new([new("one", 60, "ssh", "runner", "admin", "C:/repo/bare.git", "C:/mcg-executor")],
            [FakeSshRemoteLaneTransport.Request.Lane], null),
        fake.Root, Path.Combine(fake.Root, "attempt", "result"), fake.Clock, fake.Transport, fake.Git, TimeSpan.Zero);

    private static async Task<IDisposable> Event(Task<IDisposable> task, string name)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }
}
