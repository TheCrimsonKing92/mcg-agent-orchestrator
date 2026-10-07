using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fake owns its temporary root and event-gated transport.
public sealed class SshRemoteLaneHandleTestsShortCancelPath
{
    [Xunit.Fact]
    public async Task LongLaneFallbackCopiesShortLocalMarkerToFullRemoteName()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var request = FakeSshRemoteLaneTransport.Request with { Lane = "infrastructure tests: Goal worktree cleanup" };
        var laneKey = SshRemoteLaneExecutor.LaneKey(request.Lane);
        Assert.True(laneKey.Length >= 40);

        var submission = await fake.Executor().SubmitAsync(request, default);
        var handle = fake.Handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
        await fake.PollOnce(new(null, Exit: 1));
        var before = fake.Calls.ToArray();
        Assert.Equal(4, before.Length);
        Assert.Equal("job.json", before[1].Arguments[3]);

        handle.RequestQueuedJobCancellation();
        var cancellation = handle.QueuedJobCancellation;
        Assert.NotNull(cancellation);
        try { await cancellation.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: queued cancel copy completed"); }

        var after = fake.Calls.ToArray();
        Assert.Equal(before.Length + 1, after.Length);
        for (var index = 0; index < before.Length; index++)
        {
            Assert.Equal(before[index].Arguments, after[index].Arguments);
            Assert.Equal(before[index].Directory, after[index].Directory);
            Assert.Equal(before[index].Bound, after[index].Bound);
        }
        var copy = after[^1];
        var remoteName = $"{request.AttemptId}-{laneKey}.cancel";
        Assert.Equal(new[] { SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes", "cancel.txt",
            $"runner:C:/mcg-executor/queue/{remoteName}" }, copy.Arguments);
        Assert.EndsWith($"/queue/{remoteName}", copy.Arguments[4]);
        Assert.Equal(Path.Combine(fake.Root, "attempt", SshRemoteLaneExecutor.StagingFolderName("one", laneKey, 0)), copy.Directory);
        Assert.Equal(TimeSpan.FromSeconds(30), copy.Bound);
        Assert.Equal("", File.ReadAllText(Path.Combine(copy.Directory, "cancel.txt")));
        Assert.False(File.Exists(Path.Combine(copy.Directory, remoteName)));

        handle.RequestQueuedJobCancellation();
        Assert.Same(cancellation, handle.QueuedJobCancellation);
        Assert.Equal(after.Length, fake.Calls.Count);
    }
}
