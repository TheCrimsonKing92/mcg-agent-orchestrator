using System.Text.Json;
using System.Threading.Channels;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: each fixture owns its transport, clock, staging and observation channel.
public sealed class SshRemoteLaneHandleTestsQueuedLiveness
{
    [Xunit.Fact]
    public async Task FailedPoll_ListedJob_ProbesBeforePublishingObservation()
    {
        await using var fixture = new Fixture();
        await fixture.Submit();
        var before = Assert.IsType<RemoteLaneQueueWait>(fixture.Handle.Snapshot().Poll.QueueWait);
        Assert.Equal(0, before.Probes);
        Assert.Empty(fixture.HandleProbes());
        Assert.Single(fixture.AllProbes()); // Only the submit-time occupancy probe.

        var observation = await fixture.Poll(new(null, Exit: 1), fixture.Listed());
        var call = Assert.Single(fixture.HandleProbes());
        Assert.Equal(new[] { SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes",
            "runner:C:/mcg-executor/runner-state.json",
            Path.GetFileName(observation.DestinationFolder) + "/runner-state.json" }, call.Arguments);
        Assert.Equal(Path.GetDirectoryName(observation.DestinationFolder), call.Directory);
        Assert.Equal(TimeSpan.FromSeconds(30), call.Bound);
        Assert.Equal(1, fixture.CompletedProbes);
        Assert.Equal(1, fixture.Handle.Snapshot().Poll.QueueWait!.ListedProbes);
        Assert.Equal(fixture.Fake.Clock.GetUtcNow(), fixture.Handle.NewestHeartbeat);
        Assert.Null(fixture.Handle.Snapshot().Poll.LastHeartbeatChangeAt);
    }

    [Xunit.Theory]
    [Xunit.InlineData("queued", "plain")]
    [Xunit.InlineData("queued", "suffix")]
    [Xunit.InlineData("queued", "upper")]
    [Xunit.InlineData("running", "plain")]
    [Xunit.InlineData("running", "suffix")]
    [Xunit.InlineData("running", "upper")]
    public async Task FailedPoll_ListedNameVariants_RefreshEveryProbe(string state, string name)
    {
        await using var fixture = new Fixture();
        await fixture.Submit();
        for (var index = 0; index < 2; index++)
        {
            fixture.Fake.Clock.Advance(TimeSpan.FromSeconds(20));
            await fixture.Poll(new(null, Exit: 1), fixture.Listed(state, name));
            Assert.Equal(fixture.Fake.Clock.GetUtcNow(), fixture.Handle.NewestHeartbeat);
            Assert.Equal(index + 1, fixture.Handle.Snapshot().Poll.QueueWait!.ListedProbes);
            Assert.Equal(state, fixture.Handle.Snapshot().Poll.QueueWait!.LastListedAs);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task SuccessfulCopy_MissingOrForeignStatus_StillProbes(bool foreign)
    {
        await using var fixture = new Fixture();
        await fixture.Submit();
        var status = foreign ? FakeSshRemoteLaneTransport.Status() : null;
        if (status is not null) status["attemptId"] = "another-attempt";
        await fixture.Poll(new(status), fixture.Listed());
        Assert.Equal(fixture.Fake.Clock.GetUtcNow(), fixture.Handle.NewestHeartbeat);
        Assert.Single(fixture.HandleProbes());
        Assert.Null(fixture.Handle.Snapshot().Poll.LastHeartbeatChangeAt);
    }

    [Xunit.Theory]
    [Xunit.InlineData(-120, true)]
    [Xunit.InlineData(120, true)]
    [Xunit.InlineData(-121, false)]
    [Xunit.InlineData(121, false)]
    public async Task FailedPoll_FreshnessWindow_IsInclusive(int offset, bool refreshes)
    {
        await using var fixture = new Fixture();
        await fixture.Submit();
        await fixture.Poll(new(null, Exit: 1), fixture.Listed(offset: offset));
        Assert.Equal(refreshes ? fixture.Fake.Clock.GetUtcNow() : (DateTimeOffset?)null,
            fixture.Handle.NewestHeartbeat);
        Assert.Equal(refreshes ? 1 : 0, fixture.Handle.Snapshot().Poll.QueueWait!.ListedProbes);
    }

    [Xunit.Theory]
    [Xunit.InlineData("failed")]
    [Xunit.InlineData("timeout")]
    [Xunit.InlineData("throw")]
    [Xunit.InlineData("cancelled")]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("malformed")]
    public async Task FailedPoll_UnreadableProbe_RecordsFailureWithoutFault(string scenario)
    {
        await using var fixture = new Fixture();
        await fixture.Submit();
        await fixture.Poll(new(null, Exit: 1), Fixture.Unreadable(scenario));
        Assert.Null(fixture.Handle.TryGetResult());
        Assert.Null(fixture.Handle.NewestHeartbeat);
        Assert.Equal("unreadable", fixture.Handle.Snapshot().Poll.QueueWait!.LastReason);
        Assert.Equal(1, fixture.Handle.Snapshot().Poll.QueueWait!.Probes);
        Assert.Equal(1, fixture.Handle.Snapshot().Poll.FailedPollCount);
    }

    [Xunit.Fact]
    public async Task OwnHeartbeat_EndsQueueProbesAndRemainsDiagnosticHeartbeat()
    {
        await using var fixture = new Fixture();
        await fixture.Submit();
        await fixture.Poll(new(null, Exit: 1), fixture.Listed());
        fixture.Fake.Clock.Advance(TimeSpan.FromSeconds(20));
        var ownAt = fixture.Fake.Clock.GetUtcNow();
        await fixture.Poll(new(FakeSshRemoteLaneTransport.Status("queued"), "h1"), fixture.Listed());
        fixture.Fake.Clock.Advance(TimeSpan.FromSeconds(20));
        await fixture.Poll(new(null, Exit: 1), fixture.Listed());
        Assert.Single(fixture.HandleProbes());
        Assert.Equal(ownAt, fixture.Handle.NewestHeartbeat);
        Assert.Equal(ownAt, fixture.Handle.Snapshot().Poll.LastHeartbeatChangeAt);
        Assert.Equal(ownAt, fixture.Handle.Snapshot().Poll.QueueWait!.OwnHeartbeatAt);
        Assert.Equal("own-heartbeat", fixture.Handle.Snapshot().Poll.QueueWait!.LastReason);
    }

    [Xunit.Fact]
    public async Task ProbeOff_BeforeAndAfterFailedPoll_HasNoQueueDiagnosticsOrCopies()
    {
        await using var fixture = new Fixture(false);
        await fixture.Submit();
        Assert.Null(fixture.Handle.Snapshot().Poll.QueueWait);
        await fixture.Poll(new(null, Exit: 1), fixture.Listed());
        Assert.Null(fixture.Handle.Snapshot().Poll.QueueWait);
        Assert.Null(fixture.Handle.NewestHeartbeat);
        Assert.Empty(fixture.AllProbes());
    }

    internal sealed record Probe(string? Text, int Exit = 0, bool TimedOut = false,
        bool Throws = false, bool Cancelled = false);

    // Shared only by the two new SSH integration classes; no global state or real SSH.
    internal sealed class Fixture : IAsyncDisposable
    {
        internal readonly FakeSshRemoteLaneTransport Fake = new();
        internal readonly RemoteLaneExecutorEntry Entry;
        internal readonly SshRemoteLaneExecutor Executor;
        private readonly Channel<SshPollObservation> _observations = Channel.CreateUnbounded<SshPollObservation>();
        private Probe _probe = new(null);
        private int _completedProbes;
        internal int CompletedProbes => Volatile.Read(ref _completedProbes);
        internal SshRemoteLaneHandle Handle => Fake.Handle!;
        private static string JobName => FakeSshRemoteLaneTransport.Request.AttemptId + "-" +
            SshRemoteLaneExecutor.LaneKey(FakeSshRemoteLaneTransport.Request.Lane);

        internal Fixture(bool probe = true)
        {
            Entry = new("one", 60, "ssh", "runner", "admin", "C:/repo/bare.git", "C:/mcg-executor",
                PollSeconds: 20, Slots: 3, OccupancyProbe: probe);
            Executor = new(new([Entry], [FakeSshRemoteLaneTransport.Request.Lane], null), Fake.Root,
                Path.Combine(Fake.Root, "attempt", "result"), Fake.Clock, Transport, Fake.Git, TimeSpan.Zero,
                observation => _observations.Writer.TryWrite(observation));
        }

        internal async Task Submit()
        {
            var submission = await Executor.SubmitAsync(FakeSshRemoteLaneTransport.Request, default);
            Fake.Handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
        }

        private async Task<GoalAcceptanceVerifier.CommandResult> Transport(string[] args, string directory,
            TimeSpan bound, CancellationToken token)
        {
            if (!args[3].EndsWith("/runner-state.json", StringComparison.Ordinal))
                return await Fake.Transport(args, directory, bound, token);
            Fake.Calls.Enqueue(new(args, directory, bound));
            var submit = args[^1] == "runner-state.json";
            var response = submit ? new Probe(State(Fake.Clock.GetUtcNow(), [], [])) : _probe;
            if (response.Throws) throw new IOException("fixture-queue-probe-failed");
            if (response.Cancelled) throw new OperationCanceledException("fixture-probe-bound");
            if (response.Text is not null) File.WriteAllText(Path.Combine(directory, args[^1]), response.Text);
            if (!submit) Interlocked.Increment(ref _completedProbes);
            return new(response.Exit, "", TimedOut: response.TimedOut);
        }

        internal async Task<SshPollObservation> Poll(FakeSshRemoteLaneTransport.Poll poll, Probe response)
        {
            _probe = response;
            await Fake.Polls.Writer.WriteAsync(poll);
            return await Completed(_observations.Reader.ReadAsync().AsTask(), "ssh poll completed");
        }

        internal Probe Listed(string state = "queued", string name = "plain", int offset = 0)
        {
            var job = name == "suffix" ? JobName + ".json" : name == "upper" ? JobName.ToUpperInvariant() : JobName;
            return new(State(Fake.Clock.GetUtcNow().AddSeconds(offset),
                state == "running" ? [job] : [], state == "queued" ? [job] : []));
        }

        internal static string State(DateTimeOffset utc, string[] running, string[] queued) =>
            JsonSerializer.Serialize(new { utc, slots = 3, running, queued });
        internal static Probe Unreadable(string scenario) => new(scenario == "malformed" ? "{" : null,
            Exit: scenario == "failed" ? 1 : 0, TimedOut: scenario == "timeout",
            Throws: scenario == "throw", Cancelled: scenario == "cancelled");
        internal FakeSshRemoteLaneTransport.Call[] AllProbes() => Fake.Calls.Where(call =>
            call.Arguments.Any(arg => arg.EndsWith("/runner-state.json", StringComparison.Ordinal))).ToArray();
        internal FakeSshRemoteLaneTransport.Call[] HandleProbes() => AllProbes().Where(call =>
            call.Arguments[^1] != "runner-state.json").ToArray();
        public ValueTask DisposeAsync() => Fake.DisposeAsync();
    }

    internal static async Task<T> Completed<T>(Task<T> task, string name)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }
}
