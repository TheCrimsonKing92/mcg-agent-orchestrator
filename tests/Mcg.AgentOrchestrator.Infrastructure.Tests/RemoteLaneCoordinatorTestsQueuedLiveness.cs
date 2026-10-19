using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using TransportFixture = SshRemoteLaneHandleTestsQueuedLiveness.Fixture;
using Probe = SshRemoteLaneHandleTestsQueuedLiveness.Probe;

// Parallel safe: fixture-owned clocks, occupancy locks, ledgers and event-gated transports.
public sealed class RemoteLaneCoordinatorTestsQueuedLiveness
{
    [Xunit.Fact]
    public async Task MissingStatus_QueuedThenRunning_RetainsLeaseUntilOwnHeartbeatStops()
    {
        await using var fixture = new Fixture();
        var run = fixture.Start();
        var start = fixture.Transport.Fake.Clock.GetUtcNow();
        int[] times = [0, 20, 40, 60, 80, 100, 150, 200];
        for (var index = 0; index < times.Length; index++)
        {
            if (index > 0) fixture.Advance(times[index] - times[index - 1]);
            await fixture.Poll(index < 5 ? new(null, Exit: 1) : Own("h1"),
                fixture.Transport.Listed(index < 2 ? "queued" : "running"));
            if (index >= 5) Assert.Equal(5, fixture.Transport.HandleProbes().Length);
            if (index == 7) break;
            await fixture.DecideHealthy();
            Assert.Empty(fixture.Rows());
            Assert.Empty(fixture.CancelCopies());
        }

        var row = await fixture.Expire(run);
        var decision = row.Attempt!.LeaseDecision!;
        Assert.Equal(start.AddSeconds(200), decision.ObservedAt);
        Assert.Equal(TimeSpan.FromSeconds(100), decision.HeartbeatAge);
        Assert.Equal(start.AddSeconds(100), decision.EffectiveHeartbeat);
        var poll = decision.Snapshot!.Poll;
        Assert.Equal(8, poll.PollCount);
        Assert.Equal(5, poll.FailedPollCount);
        var wait = Assert.IsType<RemoteLaneQueueWait>(poll.QueueWait);
        Assert.Equal(5, wait.Probes);
        Assert.Equal(5, wait.ListedProbes);
        Assert.Equal("running", wait.LastListedAs);
        Assert.Equal(start.AddSeconds(80), wait.LastListedAt);
        Assert.Equal(start.AddSeconds(100), wait.OwnHeartbeatAt);
        Assert.Equal("own-heartbeat", wait.LastReason);
        Assert.Equal(wait, row.Attempt.Poll!.QueueWait);
        using var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(fixture.Ledger)));
        Assert.Equal(5, json.RootElement.GetProperty("attempt").GetProperty("poll")
            .GetProperty("queue_wait").GetProperty("listed_probes").GetInt32());
        Assert.Equal(6, fixture.Transport.AllProbes().Length);
        Assert.Single(fixture.Transport.AllProbes().Where(call => call.Arguments[^1] == "runner-state.json"));
        Assert.Single(fixture.CancelCopies());
    }

    [Xunit.Fact]
    public async Task ListedWithoutHeartbeat_StopsAtCeiling_ExpiresAfterLastRefresh()
    {
        await using var fixture = new Fixture();
        var run = fixture.Start();
        var start = fixture.Transport.Fake.Clock.GetUtcNow();
        for (var seconds = 0; seconds <= 380; seconds += 20)
        {
            if (seconds > 0) fixture.Advance(20);
            await fixture.Poll(new(null, Exit: 1), fixture.Transport.Listed());
            Assert.Equal(Math.Min(seconds / 20 + 1, 16), fixture.Transport.HandleProbes().Length);
            if (seconds == 380) break;
            await fixture.DecideHealthy();
            Assert.Empty(fixture.Rows());
            Assert.Empty(fixture.CancelCopies());
        }

        var row = await fixture.Expire(run);
        var decision = row.Attempt!.LeaseDecision!;
        Assert.Equal(start.AddSeconds(380), decision.ObservedAt);
        Assert.Equal(start.AddSeconds(300), decision.EffectiveHeartbeat);
        Assert.Equal(decision.ObservedAt - start.AddSeconds(300), decision.HeartbeatAge);
        Assert.Equal(TimeSpan.FromSeconds(80), decision.HeartbeatAge);
        var wait = decision.Snapshot!.Poll.QueueWait!;
        Assert.Equal("ceiling-reached", wait.LastReason);
        Assert.Equal(16, wait.Probes);
        Assert.Equal(16, wait.ListedProbes);
        Assert.Equal(300, wait.CeilingSeconds);
        Assert.Equal(start.AddSeconds(300), wait.LastListedAt);
        Assert.Null(wait.OwnHeartbeatAt);
        Assert.Single(fixture.CancelCopies());
    }

    [Xunit.Theory]
    [Xunit.InlineData("unlisted", "not-listed")]
    [Xunit.InlineData("old", "stale-state")]
    [Xunit.InlineData("future", "stale-state")]
    [Xunit.InlineData("malformed", "unreadable")]
    [Xunit.InlineData("failed", "unreadable")]
    [Xunit.InlineData("throw", "unreadable")]
    public async Task UnusableQueueState_MatchesProbeOffLeaseAndCounters(string scenario, string reason)
    {
        await using var fixture = new Fixture();
        await using var baseline = new Fixture(false);
        var run = fixture.Start();
        var baselineRun = baseline.Start();
        for (var index = 0; index < 5; index++)
        {
            if (index > 0) { fixture.Advance(20); baseline.Advance(20); }
            await fixture.Poll(new(null, Exit: 1), NegativeProbe(fixture.Transport, scenario));
            await baseline.Poll(new(null, Exit: 1), NegativeProbe(baseline.Transport, scenario));
            // A throwing runner-state copy must leave the real handle alive and without a fault.
            Assert.Null(fixture.Transport.Handle.TryGetResult());
            if (index == 4) break;
            await fixture.DecideHealthy();
            await baseline.DecideHealthy();
            Assert.Empty(fixture.Rows());
            Assert.Empty(fixture.CancelCopies());
        }

        var row = await fixture.Expire(run);
        var control = await baseline.Expire(baselineRun);
        Assert.Equal(control.Outcome, row.Outcome);
        var actual = row.Attempt!.LeaseDecision!;
        var expected = control.Attempt!.LeaseDecision!;
        Assert.Equal(expected.ObservedAt, actual.ObservedAt);
        Assert.Equal(expected.HeartbeatAge, actual.HeartbeatAge);
        Assert.Equal(TimeSpan.FromSeconds(80), actual.HeartbeatAge);
        Assert.Equal(expected.EffectiveHeartbeat, actual.EffectiveHeartbeat);
        var poll = actual.Snapshot!.Poll;
        Assert.Equal(expected.Snapshot!.Poll.PollCount, poll.PollCount);
        Assert.Equal(expected.Snapshot.Poll.FailedPollCount, poll.FailedPollCount);
        Assert.Equal(5, poll.PollCount);
        Assert.Equal(5, poll.FailedPollCount);
        Assert.Equal(0, poll.QueueWait!.ListedProbes);
        Assert.Equal(5, poll.QueueWait.Probes);
        Assert.Equal(reason, poll.QueueWait.LastReason);
        Assert.Null(expected.Snapshot.Poll.QueueWait);
        Assert.Equal(baseline.CancelCopies().Length, fixture.CancelCopies().Length);
        Assert.Single(fixture.CancelCopies());
        Assert.Empty(baseline.Transport.AllProbes());
    }

    [Xunit.Fact]
    public async Task ChangingOwnHeartbeat_MatchesBaselineWithoutQueueProbesOrFallback()
    {
        await using var fixture = new Fixture();
        await using var baseline = new Fixture(false);
        var run = fixture.Start();
        var baselineRun = baseline.Start();
        for (var index = 0; index < 4; index++)
        {
            if (index > 0) { fixture.Advance(50); baseline.Advance(50); }
            await fixture.Poll(Own("hb-" + index), fixture.Transport.Listed("running"));
            await baseline.Poll(Own("hb-" + index), baseline.Transport.Listed("running"));
            await fixture.DecideHealthy();
            await baseline.DecideHealthy();
            Assert.Equal(baseline.Heartbeats(), fixture.Heartbeats());
            Assert.Equal(fixture.Transport.Fake.Clock.GetUtcNow(), fixture.Heartbeats()[^1]);
            Assert.Empty(fixture.Rows());
            Assert.Empty(baseline.Rows());
            Assert.Empty(fixture.CancelCopies());
            Assert.Empty(baseline.CancelCopies());
        }
        Assert.Empty(fixture.Transport.HandleProbes());
        Assert.Single(fixture.Transport.AllProbes());
        Assert.Empty(baseline.Transport.AllProbes());
        fixture.Stop.Cancel();
        baseline.Stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Completed(run, "healthy run cancellation"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Completed(baselineRun, "baseline cancellation"));
        Assert.Empty(fixture.Rows());
        Assert.Empty(fixture.CancelCopies());
    }

    [Xunit.Fact]
    public async Task ProbeOff_IncidentReplay_PreservesOriginalCallsAndExpiry()
    {
        await using var fixture = new Fixture(false);
        var run = fixture.Start();
        for (var index = 0; index < 5; index++)
        {
            if (index > 0) fixture.Advance(20);
            await fixture.Poll(new(null, Exit: 1), fixture.Transport.Listed());
            if (index < 4) await fixture.DecideHealthy();
        }
        var row = await fixture.Expire(run);
        Assert.Equal(TimeSpan.FromSeconds(80), row.Attempt!.LeaseDecision!.HeartbeatAge);
        Assert.Null(row.Attempt.Poll!.QueueWait);
        var calls = fixture.Transport.Fake.Calls.ToArray();
        Assert.DoesNotContain(calls, call => call.Arguments.Any(arg => arg.Contains("runner-state.json", StringComparison.Ordinal)));
        Assert.Single(calls.Where(call => call.Arguments.Any(arg => arg.EndsWith("/runner.log", StringComparison.Ordinal))));
        var ordered = calls.Where(call => !call.Arguments.Any(arg => arg.EndsWith("/runner.log", StringComparison.Ordinal))).ToArray();
        Assert.Equal(new[] { "push", "job-copy", "trigger", "poll", "poll", "poll", "poll", "poll", "cancel" },
            ordered.Select(CallKind));
    }

    private static string CallKind(FakeSshRemoteLaneTransport.Call call) =>
        call.Arguments.Contains("push") ? "push" : call.Arguments.Contains("job.json") ? "job-copy" :
        call.Arguments.Contains("schtasks") ? "trigger" : call.Arguments[^1].EndsWith(".cancel", StringComparison.Ordinal)
            ? "cancel" : call.Arguments.Any(arg => arg.EndsWith("/status.json", StringComparison.Ordinal)) ? "poll" : "unexpected";
    private static FakeSshRemoteLaneTransport.Poll Own(string heartbeat) => new(FakeSshRemoteLaneTransport.Status(), heartbeat);
    private static Probe NegativeProbe(TransportFixture fixture, string scenario) => scenario switch
    {
        "unlisted" => new(TransportFixture.State(fixture.Fake.Clock.GetUtcNow(), ["r0"], ["q0"])),
        "old" => fixture.Listed(offset: -121),
        "future" => fixture.Listed(offset: 121),
        _ => TransportFixture.Unreadable(scenario)
    };
    private static Check LaneCheck() => new()
    {
        Name = FakeSshRemoteLaneTransport.Request.Lane, Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", FakeSshRemoteLaneTransport.Request.Filter]
    };
    private static Task<T> Completed<T>(Task<T> task, string name) =>
        SshRemoteLaneHandleTestsQueuedLiveness.Completed(task, name);

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly TransportFixture Transport;
        internal readonly CancellationTokenSource Stop = new();
        private readonly RemoteLaneCoordinator _coordinator;
        private readonly TaskCompletionSource<DecisionHandle> _submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DecisionHandle? _handle;
        private Task<RemoteLaneOutcome>? _run;
        internal string Ledger => RemoteExecutorHealthLedger.ResolveStorePath(Transport.Fake.Root);

        internal Fixture(bool probe = true)
        {
            Transport = new(probe);
            var executor = new FakeRemoteLaneExecutor
            {
                Submit = async (request, token) =>
                {
                    var submission = await Transport.Executor.SubmitAsync(request, token);
                    Transport.Fake.Handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
                    _handle = new(Transport.Handle, token);
                    _submitted.SetResult(_handle);
                    return submission with { Handle = _handle };
                }
            };
            _coordinator = new(new([Transport.Entry], [FakeSshRemoteLaneTransport.Request.Lane], null),
                new RemoteLaneCandidateIdentity("attempt-one", "goal", "123456789abcdef", "tree", "main", "manifest"),
                Transport.Fake.Root, Path.Combine(Transport.Fake.Root, "attempt", "result"), executor,
                Transport.Fake.Clock, TimeSpan.Zero, null);
            Assert.True(_coordinator.IsEligible(LaneCheck()));
            Assert.Equal(Transport.Entry, _coordinator.TryClaimIdleExecutor());
        }

        internal Task<RemoteLaneOutcome> Start() => _run = Task.Run(() =>
            _coordinator.RunRemoteAsync(LaneCheck(), Transport.Entry, TimeSpan.FromMinutes(10), Stop.Token));
        internal void Advance(int seconds) => Transport.Fake.Clock.Advance(TimeSpan.FromSeconds(seconds));
        internal async Task Poll(FakeSshRemoteLaneTransport.Poll poll, Probe response)
        {
            await Completed(_submitted.Task, "ssh handle submitted");
            await Transport.Poll(poll, response);
        }
        internal async Task DecideHealthy()
        {
            _handle!.Permit.Writer.TryWrite(true);
            await Completed(_handle.Decisions.Reader.ReadAsync().AsTask(), "coordinator lease decision completed");
        }
        internal DateTimeOffset?[] Heartbeats() => _handle!.Heartbeats.ToArray();
        internal IReadOnlyList<RemoteExecutorHealthRecord> Rows() => RemoteExecutorHealthLedger.ReadAll(Ledger);
        internal FakeSshRemoteLaneTransport.Call[] CancelCopies() => Transport.Fake.Calls.Where(call =>
            call.Arguments[^1].EndsWith(".cancel", StringComparison.Ordinal)).ToArray();
        internal async Task<RemoteExecutorHealthRecord> Expire(Task<RemoteLaneOutcome> run)
        {
            _handle!.Permit.Writer.TryWrite(true);
            var outcome = await Completed(run, "expired lease outcome");
            Assert.Null(outcome.Accepted);
            var row = Assert.Single(Rows());
            Assert.Equal(RemoteLaneOutcomeCode.LeaseExpired, row.Outcome);
            await CancellationCompleted();
            return row;
        }
        private async Task CancellationCompleted()
        {
            var task = Transport.Handle.QueuedJobCancellation;
            Assert.NotNull(task);
            await Completed(task.ContinueWith(completed => { completed.GetAwaiter().GetResult(); return true; },
                TaskScheduler.Default), "cancel copy completed");
        }
        public async ValueTask DisposeAsync()
        {
            Stop.Cancel();
            if (_run is not null)
                try { await Completed(_run, "coordinator stopped during cleanup"); }
                catch (OperationCanceledException) { }
            _coordinator.Dispose();
            if (Transport.Fake.Handle?.QueuedJobCancellation is not null) await CancellationCompleted();
            await Transport.DisposeAsync();
            Stop.Dispose();
        }
    }

    // Gate each iteration before the heartbeat/clock pair, just as the existing queued/busy fixture does.
    private sealed class DecisionHandle(SshRemoteLaneHandle inner, CancellationToken token)
        : IRemoteLaneHandle, IRemoteLaneQueuedJobCancellation, IRemoteLaneAttemptDiagnosticsSource
    {
        internal readonly Channel<bool> Permit = Channel.CreateUnbounded<bool>();
        internal readonly Channel<bool> Decisions = Channel.CreateUnbounded<bool>();
        internal readonly ConcurrentQueue<DateTimeOffset?> Heartbeats = new();
        public DateTimeOffset? NewestHeartbeat
        {
            get
            {
                Completed(Permit.Reader.ReadAsync(token).AsTask(), "lease decision permitted").GetAwaiter().GetResult();
                var heartbeat = inner.NewestHeartbeat;
                Heartbeats.Enqueue(heartbeat);
                return heartbeat;
            }
        }
        public RemoteLaneResult? TryGetResult()
        {
            var result = inner.TryGetResult();
            Decisions.Writer.TryWrite(true);
            return result;
        }
        public void Abandon() => inner.Abandon();
        public void RequestQueuedJobCancellation() => inner.RequestQueuedJobCancellation();
        public RemoteLaneHandleDiagnostics Snapshot() => inner.Snapshot();
        public Task<RemoteLaneRunnerLogCapture> CaptureRunnerLogAsync(CancellationToken cancellationToken) =>
            inner.CaptureRunnerLogAsync(cancellationToken);
    }
}
