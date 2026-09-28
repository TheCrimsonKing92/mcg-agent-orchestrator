using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.PostLandingCanary)]
public sealed class PostLandingCanaryOperatorItemOwnershipTests
{
    [Xunit.Fact]
    public async Task ReadWaitsForCancelledResolutionBeforeReturningHealth()
    {
        using var fixture = new CircuitFixture();
        var entered = Signal();
        var cancelled = Signal();
        var release = Signal();
        var finished = Signal();
        var inFlight = 0;
        var store = new FakeCollaborationItemStore
        {
            ResolveOverride = async token =>
            {
                Interlocked.Increment(ref inFlight);
                entered.TrySetResult();
                using var registration = token.Register(() => cancelled.TrySetResult());
                try
                {
                    await release.Task;
                    return false;
                }
                finally
                {
                    Interlocked.Decrement(ref inFlight);
                    finished.TrySetResult();
                }
            }
        };
        var expected = new AcceptanceEngineCircuitBreaker(fixture.Events).Read();
        var circuit = new AcceptanceEngineCircuitBreaker(fixture.Events, store);

        var read = Task.Run(() => circuit.Read());
        await entered.Task;
        try
        {
            var firstCompleted = await Task.WhenAny(read, cancelled.Task);
            Xunit.Assert.Same(cancelled.Task, firstCompleted);
            Xunit.Assert.False(read.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await finished.Task;
        }
        var actual = await read;

        Xunit.Assert.Equal(0, Volatile.Read(ref inFlight));
        Xunit.Assert.Equal(expected.Health, actual.Health);
        Xunit.Assert.Equal(expected.FailureReason, actual.FailureReason);
    }

    [Xunit.Fact]
    public async Task ConcurrentReadsResolveAtMostOncePerEventStoreIdentity()
    {
        using var fixture = new CircuitFixture();
        var entered = Signal();
        var release = Signal();
        var calls = 0;
        var store = new FakeCollaborationItemStore
        {
            ResolveOverride = async _ =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task;
                return false;
            }
        };
        var firstCircuit = new AcceptanceEngineCircuitBreaker(
            fixture.Events, store, operatorItemWriteBudget: Timeout.InfiniteTimeSpan);
        var secondCircuit = new AcceptanceEngineCircuitBreaker(
            fixture.Events, store, operatorItemWriteBudget: Timeout.InfiniteTimeSpan);

        var firstRead = Task.Run(() => firstCircuit.Read());
        await entered.Task;
        var firstReturnedWhileWriteOpen = firstRead.IsCompleted;
        var second = secondCircuit.Read();
        var callsWhileFirstOpen = Volatile.Read(ref calls);
        release.TrySetResult();
        var first = await firstRead;

        Xunit.Assert.False(firstReturnedWhileWriteOpen);
        Xunit.Assert.Equal(AcceptanceEngineHealth.Healthy, first.Health);
        Xunit.Assert.Equal(first.Health, second.Health);
        Xunit.Assert.Equal(1, callsWhileFirstOpen);
        Xunit.Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Xunit.Fact]
    public async Task FailedReadWaitsForCancelledRaiseAndReportsFallback()
    {
        using var fixture = new CircuitFixture();
        var entered = Signal();
        var cancelled = Signal();
        var release = Signal();
        var inFlight = 0;
        var fallbackLines = new List<string>();
        var store = new FakeCollaborationItemStore
        {
            RaiseOverride = async token =>
            {
                Interlocked.Increment(ref inFlight);
                entered.TrySetResult();
                using var registration = token.Register(() => cancelled.TrySetResult());
                try
                {
                    await release.Task;
                    throw new OperationCanceledException(token);
                }
                finally
                {
                    Interlocked.Decrement(ref inFlight);
                }
            }
        };
        var circuit = new AcceptanceEngineCircuitBreaker(
            fixture.Events,
            store,
            stateReader: new FailingStateReader(),
            stateUnavailableFallback: fallbackLines.Add,
            stateReadTotalBudget: Timeout.InfiniteTimeSpan);

        var read = Task.Run(() => circuit.Read());
        await entered.Task;
        await cancelled.Task;
        var returnedBeforeWriteFinished = read.IsCompleted;
        release.TrySetResult();
        var snapshot = await read;

        Xunit.Assert.False(returnedBeforeWriteFinished);
        Xunit.Assert.Equal(0, Volatile.Read(ref inFlight));
        Xunit.Assert.Equal(AcceptanceEngineHealth.Unavailable, snapshot.Health);
        Xunit.Assert.Equal("state-unavailable", snapshot.FailureReason);
        var fallback = Xunit.Assert.Single(fallbackLines);
        Xunit.Assert.Contains("result=operator-item-error", fallback);
        Xunit.Assert.Contains("item-error=TimeoutException", fallback);
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FailingStateReader : IAcceptanceEngineStateReader
    {
        public Task<IReadOnlyList<PostLandingCanaryEvent>> ReadProjectionEventsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<PostLandingCanaryEvent>>(
                new InvalidOperationException("simulated unreadable state"));
    }

    private sealed class CircuitFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "mcg-canary-operator-item-tests", Guid.NewGuid().ToString("N"));

        internal CircuitFixture()
        {
            Directory.CreateDirectory(_root);
            var path = OrchestratorWorkspace.ForDirectory(_root).RunEventStorePath;
            Events = new PostLandingCanaryEventStore(new SqliteRunEventStore(path), path);
        }

        internal PostLandingCanaryEventStore Events { get; }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
