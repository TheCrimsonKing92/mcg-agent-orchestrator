using System.Threading.Channels;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: per-test temp roots, fake transport and clock; no child processes or global state.
public sealed class SshRemoteLaneHandleTestsPollFolderCleanup
{
    [Xunit.Fact]
    public async Task EveryPollOutcomeRemovesItsFolderBeforeTheObservation()
    {
        await using var fixture = new Fixture();
        await fixture.Submit();
        var foreign = FakeSshRemoteLaneTransport.Status("completed");
        foreign["attemptId"] = "foreign-attempt";
        (FakeSshRemoteLaneTransport.Poll Poll, bool AdvancesHeartbeat)[] cases =
        [
            (new(foreign, "foreign-heartbeat"), false),
            (new(FakeSshRemoteLaneTransport.Status(), "h1"), true),
            (new(FakeSshRemoteLaneTransport.Status(), "failed-copy-heartbeat", Exit: 1), false),
            (new(null, "malformed-heartbeat", Malformed: true), false),
            (new(FakeSshRemoteLaneTransport.Status(), "h2"), true)
        ];
        DateTimeOffset? expectedHeartbeat = null;
        var folders = new HashSet<string>();
        foreach (var item in cases)
        {
            fixture.Fake.Clock.Advance(TimeSpan.FromMinutes(1));
            var captured = await fixture.Poll(item.Poll);
            AssertRemoved(captured);
            Assert.True(folders.Add(captured.Observation.DestinationFolder));
            if (item.AdvancesHeartbeat) expectedHeartbeat = fixture.Fake.Clock.GetUtcNow();
            Assert.Equal(expectedHeartbeat, fixture.Handle.NewestHeartbeat);
            Assert.Null(fixture.Handle.TryGetResult());
        }
        Assert.Equal(5, fixture.Handle.Snapshot().Poll.PollCount);
    }

    [Xunit.Fact]
    public async Task CompletedPollRemovesItsFolderAndKeepsFetchedResults()
    {
        await using var fixture = new Fixture();
        await fixture.Submit();
        var captured = await fixture.Poll(new(FakeSshRemoteLaneTransport.Status("completed")));
        await Completed(fixture.Handle.PollLoop, "completed poll loop exited");
        AssertRemoved(captured);
        AssertFetchedResults(fixture.Handle);
        var staging = Path.GetDirectoryName(captured.Observation.DestinationFolder)!;
        Assert.True(Directory.Exists(staging));
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging, "poll-*"));
        Assert.All(fixture.Handle.TryGetResult()!.TestResultPaths,
            path => Assert.Equal(staging, Path.GetDirectoryName(path)));
    }

    [Xunit.Fact]
    public async Task LockedPollFolderDoesNotFaultTheHandle()
    {
        await using var fixture = new Fixture();
        await fixture.Submit();
        fixture.Fake.HoldNextPollFile = "held.txt";
        try
        {
            var locked = await fixture.Poll(new(FakeSshRemoteLaneTransport.Status(), "h1"));
            Assert.NotNull(fixture.Fake.HeldPollFile);
            if (OperatingSystem.IsWindows())
            {
                Assert.True(locked.FolderExists);
                Assert.True(Directory.Exists(locked.Observation.DestinationFolder));
            }
            Assert.Null(fixture.Handle.TryGetResult());

            fixture.Fake.Clock.Advance(TimeSpan.FromMinutes(1));
            var following = await fixture.Poll(new(FakeSshRemoteLaneTransport.Status(), "h2"));
            Assert.False(following.FolderExists);
            Assert.False(Directory.Exists(following.Observation.DestinationFolder));
            Assert.Equal(locked.Observation.PollIndex + 1, following.Observation.PollIndex);
            Assert.Equal(fixture.Fake.Clock.GetUtcNow(), fixture.Handle.NewestHeartbeat);
            Assert.Null(fixture.Handle.TryGetResult());

            fixture.Fake.HeldPollFile!.Dispose();
            var completed = await fixture.Poll(new(FakeSshRemoteLaneTransport.Status("completed")));
            await Completed(fixture.Handle.PollLoop, "completed poll loop exited after file release");
            Assert.False(completed.FolderExists);
            Assert.False(Directory.Exists(completed.Observation.DestinationFolder));
            AssertFetchedResults(fixture.Handle);
        }
        finally
        {
            fixture.Fake.HeldPollFile?.Dispose();
        }
    }

    [Xunit.Fact]
    public async Task QueuedLivenessProbeStillReadsRunnerStateBeforeRemoval()
    {
        await using var fixture = new SshRemoteLaneHandleTestsQueuedLiveness.Fixture();
        Assert.True(fixture.Entry.OccupancyProbe);
        await fixture.Submit();
        var observation = await fixture.Poll(new(null, Exit: 1), fixture.Listed());
        var summary = fixture.Handle.Snapshot().Poll.QueueWait!;
        Assert.Equal(1, summary.ListedProbes);
        Assert.Equal("queued", summary.LastListedAs);
        Assert.Equal("listed-queued", summary.LastReason);
        Assert.Equal(fixture.Fake.Clock.GetUtcNow(), fixture.Handle.NewestHeartbeat);
        Assert.False(Directory.Exists(observation.DestinationFolder));
        var copy = Assert.Single(fixture.HandleProbes());
        Assert.Equal(Path.GetFileName(observation.DestinationFolder) + "/runner-state.json", copy.Arguments[^1]);
    }

    private static void AssertRemoved(CapturedPoll captured)
    {
        Assert.False(captured.FolderExists);
        // Captured synchronously in the callback, before the next iteration creates its folder.
        Assert.Empty(captured.PollEntries);
        Assert.False(Directory.Exists(captured.Observation.DestinationFolder));
    }

    private static void AssertFetchedResults(SshRemoteLaneHandle handle)
    {
        var result = Assert.IsType<RemoteLaneResult>(handle.TryGetResult());
        Assert.Equal(2, result.TestResultPaths.Count);
        Assert.All(result.TestResultPaths, path => Assert.True(File.Exists(path)));
    }

    private sealed record CapturedPoll(SshPollObservation Observation, bool FolderExists, string[] PollEntries);

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly FakeSshRemoteLaneTransport Fake = new();
        private readonly Channel<CapturedPoll> _observations = Channel.CreateUnbounded<CapturedPoll>();
        internal SshRemoteLaneHandle Handle => Fake.Handle!;

        internal async Task Submit()
        {
            var executor = new SshRemoteLaneExecutor(
                new([new("one", 60, "ssh", "runner", "admin", "C:/repo/bare.git", "C:/mcg-executor")],
                    [FakeSshRemoteLaneTransport.Request.Lane], null),
                Fake.Root, Path.Combine(Fake.Root, "attempt", "result"), Fake.Clock, Fake.Transport, Fake.Git,
                TimeSpan.Zero, observation =>
                {
                    var staging = Path.GetDirectoryName(observation.DestinationFolder)!;
                    _observations.Writer.TryWrite(new(observation, Directory.Exists(observation.DestinationFolder),
                        Directory.EnumerateFileSystemEntries(staging, "poll-*").ToArray()));
                });
            var submission = await executor.SubmitAsync(FakeSshRemoteLaneTransport.Request, default);
            Fake.Handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
        }

        internal async Task<CapturedPoll> Poll(FakeSshRemoteLaneTransport.Poll poll)
        {
            await Fake.Polls.Writer.WriteAsync(poll);
            return await SshRemoteLaneHandleTestsQueuedLiveness.Completed(
                _observations.Reader.ReadAsync().AsTask(), "ssh poll cleanup observation");
        }

        public ValueTask DisposeAsync() => Fake.DisposeAsync();
    }

    private static async Task Completed(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }
}
