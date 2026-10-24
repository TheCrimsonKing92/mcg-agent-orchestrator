using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel safe: all occupancy locks, ledger files, clocks and transports are fixture-owned.
public sealed class RemoteLaneCoordinatorTestsQueuedAndBusy
{
    [Xunit.Theory]
    [Xunit.InlineData("queued")]
    [Xunit.InlineData("running")]
    public async Task LiveHeartbeat_KeepsLeaseUntilGateEnd(string state)
    {
        await using var fixture = new Fixture();
        var run = fixture.Start();
        for (var poll = 0; poll < 4; poll++)
        {
            if (poll > 0) fixture.Fake.Clock.Advance(TimeSpan.FromSeconds(50));
            await fixture.Poll(state, "heartbeat-" + poll);
            await fixture.DecideHealthy();
        }
        Assert.False(run.IsCompleted);
        Assert.Empty(RemoteExecutorHealthLedger.ReadAll(fixture.Ledger));
        Assert.Empty(fixture.CancelCopies());
        fixture.Stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Completed(run, "coordinator cancellation"));
        Assert.Empty(fixture.CancelCopies());
        Assert.Null(fixture.Fake.Handle!.QueuedJobCancellation);
        fixture.Coordinator.Dispose();
        await fixture.CancellationCompleted();
        Assert.Single(fixture.CancelCopies());
    }

    [Xunit.Theory]
    [Xunit.InlineData("queued")]
    [Xunit.InlineData("running")]
    public async Task StoppedHeartbeat_ExpiresLeaseAndCopiesCancelOnce(string state)
    {
        await using var fixture = new Fixture();
        var run = fixture.Start();
        await fixture.Poll(state, "heartbeat");
        await fixture.DecideHealthy();
        fixture.Fake.Clock.Advance(TimeSpan.FromSeconds(50));
        await fixture.Poll(state, "heartbeat");
        await fixture.DecideHealthy();
        Assert.Empty(RemoteExecutorHealthLedger.ReadAll(fixture.Ledger));
        fixture.Fake.Clock.Advance(TimeSpan.FromSeconds(50));
        await fixture.Poll(state, "heartbeat");
        fixture.PermitDecision();
        var outcome = await Completed(run, "expired lease outcome");
        Assert.Null(outcome.Accepted);
        var row = Assert.Single(RemoteExecutorHealthLedger.ReadAll(fixture.Ledger));
        Assert.Equal(RemoteLaneOutcomeCode.LeaseExpired, row.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(100), row.Attempt!.LeaseDecision!.HeartbeatAge);
        await fixture.CancellationCompleted();
        Assert.Single(fixture.CancelCopies());
        fixture.Coordinator.Dispose();
        Assert.Single(fixture.CancelCopies());
    }

    [Xunit.Fact]
    public async Task BusyExecutor_ReleasesSlotsAndBecomesEligibleAfterLease()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var entry = new RemoteLaneExecutorEntry("one", 60, Slots: 2);
        var executor = new FakeRemoteLaneExecutor
        {
            Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(null, "executor-busy"))
        };
        using var coordinator = CreateCoordinator(fake, executor, entry);
        Assert.Equal(entry, coordinator.TryClaimIdleExecutor());
        var outcome = await coordinator.RunRemoteAsync(Check(), entry, TimeSpan.FromMinutes(10), default);
        Assert.Null(outcome.Handle);
        Assert.Null(outcome.Accepted);
        var ledger = RemoteExecutorHealthLedger.ResolveStorePath(fake.Root);
        var row = Assert.Single(RemoteExecutorHealthLedger.ReadAll(ledger));
        Assert.Equal(RemoteLaneOutcomeCode.ExecutorBusy, row.Outcome);
        Assert.Null(row.FaultOwner);
        using (var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(ledger))))
        {
            Assert.Equal("executor-busy", json.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("fault_owner").ValueKind);
        }
        using (var slot0 = RemoteExecutorOccupancy.TryClaimExclusive(fake.Root, entry.Id, 0))
        using (var slot1 = RemoteExecutorOccupancy.TryClaimExclusive(fake.Root, entry.Id, 1))
        {
            Assert.NotNull(slot0);
            Assert.NotNull(slot1);
        }
        Assert.Null(coordinator.TryClaimIdleExecutor());
        fake.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(coordinator.TryClaimIdleExecutor());
        fake.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(entry, coordinator.TryClaimIdleExecutor());
        Assert.Empty(new RemoteLaneFailureHistory().Read(ledger));
        var report = RemoteExecutorReport.Build([entry.Id],
            [new(row.ObservedAt, row.ExecutorId, row.GateAttemptId, row.Lane, "executor-busy", row.Reason),
             new(row.ObservedAt, row.ExecutorId, "accepted-attempt", row.Lane, "accepted", null)], []);
        Assert.Equal(1.0, Assert.Single(report.Executors).AcceptedShare);
    }

    private static Check Check() => new()
    {
        Name = FakeSshRemoteLaneTransport.Request.Lane, Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", FakeSshRemoteLaneTransport.Request.Filter]
    };

    private static RemoteLaneCoordinator CreateCoordinator(FakeSshRemoteLaneTransport fake,
        IRemoteLaneExecutor executor, RemoteLaneExecutorEntry entry) => new(
        new([entry], [FakeSshRemoteLaneTransport.Request.Lane], null),
        new RemoteLaneCandidateIdentity("attempt-one", "goal", "123456789abcdef", "tree", "main", "manifest"),
        fake.Root, Path.Combine(fake.Root, "attempt", "result"), executor, fake.Clock, TimeSpan.FromMilliseconds(1), null);

    private static async Task<T> Completed<T>(Task<T> task, string name)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly FakeSshRemoteLaneTransport Fake = new();
        internal readonly CancellationTokenSource Stop = new();
        internal readonly RemoteLaneCoordinator Coordinator;
        private readonly RemoteLaneExecutorEntry _entry = new("one", 60);
        private readonly TaskCompletionSource<RemoteLaneGatedDecisionHandle> _submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private RemoteLaneGatedDecisionHandle? _handle;
        private Task<RemoteLaneOutcome>? _run;
        internal string Ledger => RemoteExecutorHealthLedger.ResolveStorePath(Fake.Root);

        internal Fixture()
        {
            var ssh = Fake.Executor();
            var executor = new FakeRemoteLaneExecutor
            {
                Submit = async (request, token) =>
                {
                    var submission = await ssh.SubmitAsync(request, token);
                    Fake.Handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
                    _handle = new(Fake.Handle, Stop.Token);
                    _submitted.SetResult(_handle);
                    return submission with { Handle = _handle };
                }
            };
            Coordinator = CreateCoordinator(Fake, executor, _entry);
            Assert.True(Coordinator.IsEligible(Check()));
            Assert.Equal(_entry, Coordinator.TryClaimIdleExecutor());
        }

        internal Task<RemoteLaneOutcome> Start() => _run = Task.Run(() =>
            Coordinator.RunRemoteAsync(Check(), _entry, TimeSpan.FromMinutes(10), Stop.Token));
        internal async Task Poll(string state, string heartbeat)
        {
            await Completed(_submitted.Task, "ssh handle submitted");
            await Fake.PollOnce(new(FakeSshRemoteLaneTransport.Status(state), heartbeat));
        }
        internal void PermitDecision() => _handle!.Permit.Writer.TryWrite(true);
        internal async Task DecideHealthy()
        {
            PermitDecision();
            await Completed(_handle!.Decisions.Reader.ReadAsync().AsTask(), "coordinator lease decision completed");
        }
        internal IEnumerable<FakeSshRemoteLaneTransport.Call> CancelCopies() => Fake.Calls.Where(
            call => call.Arguments[^1].EndsWith(".cancel", StringComparison.Ordinal));
        internal async Task CancellationCompleted()
        {
            var task = Fake.Handle!.QueuedJobCancellation;
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
            Coordinator.Dispose();
            if (Fake.Handle?.QueuedJobCancellation is not null) await CancellationCompleted();
            await Fake.DisposeAsync();
            Stop.Dispose();
        }
    }

}
