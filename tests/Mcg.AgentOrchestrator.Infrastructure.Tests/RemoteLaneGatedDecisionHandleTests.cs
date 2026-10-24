using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using TransportFixture = SshRemoteLaneHandleTestsQueuedLiveness.Fixture;

// Parallel safe: fixture-owned transports, clocks, ledgers and cancellation sources.
public sealed class RemoteLaneGatedDecisionHandleTests
{
    [Xunit.Fact]
    public async Task PermittedRead_ReturnsInnerHeartbeatAndRecordsIt()
    {
        await using var transport = new TransportFixture();
        using var stop = new CancellationTokenSource();
        await transport.Submit();
        await transport.Poll(new(FakeSshRemoteLaneTransport.Status(), "h1"), transport.Listed());
        var handle = new RemoteLaneGatedDecisionHandle(transport.Handle, stop.Token);

        handle.Permit.Writer.TryWrite(true);
        var heartbeat = handle.NewestHeartbeat;

        Assert.Equal(transport.Fake.Clock.GetUtcNow(), heartbeat);
        Assert.Equal(heartbeat, Assert.Single(handle.Heartbeats));
    }

    [Xunit.Fact]
    public async Task StopCancelledBeforePermit_ThrowsOperationCanceled()
    {
        await using var transport = new TransportFixture();
        using var stop = new CancellationTokenSource();
        await transport.Submit();
        var handle = new RemoteLaneGatedDecisionHandle(transport.Handle, stop.Token);
        var read = Task.Run(() => handle.NewestHeartbeat);
        try
        {
            await Completed(handle.Reads.Reader.ReadAsync().AsTask(), "heartbeat permit wait started");
            stop.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Completed(read, "heartbeat read cancelled"));
            Assert.Empty(handle.Heartbeats);
        }
        finally
        {
            stop.Cancel();
            try { await Completed(read, "heartbeat read stopped during cleanup"); }
            catch (OperationCanceledException) { }
        }
    }

    [Xunit.Fact]
    public async Task CoordinatorAwaitingPermit_StopCancelled_RecordsNoLedgerRow()
    {
        await using var fixture = new CoordinatorFixture();
        var run = fixture.Start();
        var handle = await Completed(fixture.Submitted.Task, "ssh handle submitted");
        await fixture.Transport.Poll(new(null, Exit: 1), fixture.Transport.Listed());
        await Completed(handle.Reads.Reader.ReadAsync().AsTask(), "coordinator heartbeat permit wait started");

        fixture.Stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Completed(run, "coordinator cancellation"));
        Assert.Empty(RemoteExecutorHealthLedger.ReadAll(fixture.Ledger));
        Assert.Empty(handle.Heartbeats);
    }

    private static Task<T> Completed<T>(Task<T> task, string name) =>
        SshRemoteLaneHandleTestsQueuedLiveness.Completed(task, name);

    private static Check LaneCheck() => new()
    {
        Name = FakeSshRemoteLaneTransport.Request.Lane, Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", FakeSshRemoteLaneTransport.Request.Filter]
    };

    private sealed class CoordinatorFixture : IAsyncDisposable
    {
        internal readonly TransportFixture Transport = new();
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource<RemoteLaneGatedDecisionHandle> Submitted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly RemoteLaneCoordinator _coordinator;
        private Task<RemoteLaneOutcome>? _run;
        internal string Ledger => RemoteExecutorHealthLedger.ResolveStorePath(Transport.Fake.Root);

        internal CoordinatorFixture()
        {
            var executor = new FakeRemoteLaneExecutor
            {
                Submit = async (request, token) =>
                {
                    var submission = await Transport.Executor.SubmitAsync(request, token);
                    Transport.Fake.Handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
                    var handle = new RemoteLaneGatedDecisionHandle(Transport.Handle, Stop.Token);
                    Submitted.SetResult(handle);
                    return submission with { Handle = handle };
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

        public async ValueTask DisposeAsync()
        {
            Stop.Cancel();
            if (_run is not null)
                try { await Completed(_run, "coordinator stopped during cleanup"); }
                catch (OperationCanceledException) { }
            _coordinator.Dispose();
            if (Transport.Fake.Handle?.QueuedJobCancellation is { } cancellation)
                await Completed(cancellation.ContinueWith(completed => { completed.GetAwaiter().GetResult(); return true; },
                    TaskScheduler.Default), "cancel copy completed");
            await Transport.DisposeAsync();
            Stop.Dispose();
        }
    }
}
