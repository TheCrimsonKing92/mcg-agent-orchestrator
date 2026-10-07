using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: recording store, fixed timestamps and injected census; no shared resources.
public sealed class ConductorLifecycleRecorderAdoptionCensusTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 7, 1, 0, 0, TimeSpan.Zero);

    [Xunit.Fact]
    public void Start_TwoRows_AppendsOrderedCensusWithGenerationAndDetails()
    {
        var store = new RecordingRunEventStore();
        WorkerAdoptionCensusRow[] rows =
        [
            new("dispatch-a", 101, StartedAt, SpawnRegistryLifecycle.ConductorDetached,
                41, StartedAt.AddHours(-1), SpawnOwnerLiveness.DeadOrRecycled, "inheritable", "worker live; owner gone"),
            new("dispatch-b", 102, StartedAt, SpawnRegistryLifecycle.Owned,
                null, null, SpawnOwnerLiveness.Unknown, "identity-unproven", "worker identity unavailable")
        ];
        var reads = 0;
        var recorder = new ConductorLifecycleRecorder(store, generationId: () => "generation-a",
            readAdoptionCensus: () => { reads++; return rows; });

        var session = recorder.Start("Conservative", null, StartedAt, null, null);

        Assert.Equal(1, reads);
        Assert.Equal("generation-a", session.GenerationId);
        Assert.Equal(new[] { "start", "adoption-census", "adoption-census", "adoption-census-summary" },
            store.Events.Select(evt => evt.Operation));
        AssertGenerationAndCensusMetadata(store.Events);
        Assert.Equal("inheritable", store.Events[1].Status);
        Assert.Equal("identity-unproven", store.Events[2].Status);
        Assert.Equal("owner=dispatch-a workerPid=101 workerStartedAt=2026-10-07T01:00:00.0000000+00:00 " +
            "lifecycle=ConductorDetached ownerPid=41 ownerStartedAt=2026-10-07T00:00:00.0000000+00:00 " +
            "ownerLiveness=DeadOrRecycled evidence=worker live; owner gone", store.Events[1].Detail);
        Assert.Equal("owner=dispatch-b workerPid=102 workerStartedAt=2026-10-07T01:00:00.0000000+00:00 " +
            "lifecycle=Owned ownerPid=none ownerStartedAt=none ownerLiveness=Unknown " +
            "evidence=worker identity unavailable", store.Events[2].Detail);
        Assert.Equal("complete", store.Events[3].Status);
        Assert.Equal("workers=2 current-generation=0 worker-gone=0 identity-unproven=1 " +
            "inheritable=1 orphaned=0 owner-held=0", store.Events[3].Detail);
    }

    [Xunit.Fact]
    public void Start_ThrowingCensus_RecordsUnavailableAndReturnsSession()
    {
        var store = new RecordingRunEventStore();
        var recorder = new ConductorLifecycleRecorder(store, generationId: () => "generation-a",
            readAdoptionCensus: () => throw new InvalidOperationException("private message"));

        var session = recorder.Start("Conservative", "goal-a", StartedAt, null, null);

        Assert.Equal("generation-a", session.GenerationId);
        Assert.False(session.IsStopped);
        Assert.Equal(new[] { "start", "adoption-census" }, store.Events.Select(evt => evt.Operation));
        Assert.Equal("goal-a", store.Events[0].GoalId);
        Assert.Null(store.Events[1].GoalId);
        Assert.Equal("unavailable", store.Events[1].Status);
        Assert.Equal("exception=InvalidOperationException", store.Events[1].Detail);
        using var payload = JsonDocument.Parse(store.Events[1].PayloadJson!);
        Assert.Equal(session.GenerationId, payload.RootElement.GetProperty("generationId").GetString());
    }

    [Xunit.Fact]
    public void Start_NoCensus_PreservesSingleStartEvent()
    {
        var store = new RecordingRunEventStore();
        var recorder = new ConductorLifecycleRecorder(store, generationId: () => "generation-a");

        var session = recorder.Start("Conservative", "goal-a", StartedAt, null, null);

        Assert.Equal("generation-a", session.GenerationId);
        var evt = Assert.Single(store.Events);
        Assert.Equal("start", evt.Operation);
        Assert.Equal("goal-a", evt.GoalId);
    }

    [Xunit.Fact]
    public void Start_EmptyCensus_RecordsCompleteZeroSummary()
    {
        var store = new RecordingRunEventStore();
        var recorder = new ConductorLifecycleRecorder(store, generationId: () => "generation-a",
            readAdoptionCensus: () => []);

        recorder.Start("Conservative", null, StartedAt, null, null);

        Assert.Equal(new[] { "start", "adoption-census-summary" }, store.Events.Select(evt => evt.Operation));
        AssertGenerationAndCensusMetadata(store.Events);
        Assert.Equal("complete", store.Events[1].Status);
        Assert.Equal("workers=0 current-generation=0 worker-gone=0 identity-unproven=0 " +
            "inheritable=0 orphaned=0 owner-held=0", store.Events[1].Detail);
    }

    [Xunit.Fact]
    public void Start_CensusAppendFails_PropagatesStoreFailure()
    {
        var failure = new IOException("store unavailable");
        var store = new RecordingRunEventStore { CensusAppendFailure = failure };
        var recorder = new ConductorLifecycleRecorder(store,
            readAdoptionCensus: () => []);

        var observed = Assert.Throws<IOException>(() =>
            recorder.Start("Conservative", null, StartedAt, null, null));

        Assert.Same(failure, observed);
        Assert.Equal("start", Assert.Single(store.Events).Operation);
    }

    private static void AssertGenerationAndCensusMetadata(IEnumerable<RunEventAppend> events)
    {
        Assert.All(events, evt =>
        {
            Assert.Equal(RunEventTypes.ConductorLifecycle, evt.EventType);
            Assert.Null(evt.GoalId);
            Assert.Equal(StartedAt, evt.OccurredAt);
            using var payload = JsonDocument.Parse(evt.PayloadJson!);
            Assert.Equal("generation-a", payload.RootElement.GetProperty("generationId").GetString());
            Assert.Equal(0, payload.RootElement.GetProperty("ticks").GetInt32());
            Assert.Equal(StartedAt, payload.RootElement.GetProperty("occurredAt").GetDateTimeOffset());
        });
    }

    private sealed class RecordingRunEventStore : IRunEventStore
    {
        public List<RunEventAppend> Events { get; } = [];
        public Exception? CensusAppendFailure { get; init; }

        public Task<RunEventRecord> AppendAsync(RunEventAppend evt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (evt.Operation != "start" && CensusAppendFailure is { } failure)
                throw failure;
            Events.Add(evt);
            return Task.FromResult(new RunEventRecord(Events.Count,
                evt.EventId ?? "event", evt.OccurredAt ?? DateTimeOffset.MinValue,
                evt.EventType, evt.GoalId, evt.Operation, evt.Status, evt.Detail, evt.PayloadJson));
        }

        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0,
            string? goalId = null, int maxCount = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}
