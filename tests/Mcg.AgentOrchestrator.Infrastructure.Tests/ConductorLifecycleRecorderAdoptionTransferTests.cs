using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private recording store, fixed timestamps and injected delegates.
public sealed class ConductorLifecycleRecorderAdoptionTransferTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 7, 1, 0, 0, TimeSpan.Zero);

    [Xunit.Fact]
    public void Start_AdoptedRow_AppendsAdoptionAfterCensusSummary()
    {
        var store = new RecordingRunEventStore();
        var rows = Rows();
        var calls = 0;
        var recorder = new ConductorLifecycleRecorder(store, generationId: () => "generation-a",
            readAdoptionCensus: () => rows, adoptInheritedWorkers: observed =>
            {
                Assert.Same(rows, observed);
                Assert.Equal("adoption-census-summary", store.Events[^1].Operation);
                calls++;
                return [new WorkerAdoptionTransferResult(rows[0], "adopted")];
            });

        var session = recorder.Start("Conservative", null, StartedAt, null, null);

        Assert.Equal(1, calls);
        Assert.Equal("generation-a", session.GenerationId);
        Assert.Equal(new[] { "start", "adoption-census", "adoption-census", "adoption-census-summary",
            "adoption", "adoption-summary" }, store.Events.Select(evt => evt.Operation));
        AssertMetadata(store.Events);
        Assert.Equal("adopted", store.Events[4].Status);
        Assert.Equal(WorkerAdoptionCensus.FormatRowDetail(rows[0]), store.Events[4].Detail);
        Assert.Equal("complete", store.Events[5].Status);
        Assert.Equal("results=1 adopted=1", store.Events[5].Detail);
    }

    [Xunit.Fact]
    public void Start_ThrowingAdoption_RecordsUnavailableAndReturnsSession()
    {
        var store = new RecordingRunEventStore();
        var calls = 0;
        var recorder = new ConductorLifecycleRecorder(store, generationId: () => "generation-a",
            readAdoptionCensus: Rows, adoptInheritedWorkers: _ =>
            {
                calls++;
                throw new InvalidOperationException("private message");
            });

        var session = recorder.Start("Conservative", null, StartedAt, null, null);

        Assert.Equal(1, calls);
        Assert.Equal("generation-a", session.GenerationId);
        Assert.False(session.IsStopped);
        Assert.Equal(new[] { "start", "adoption-census", "adoption-census", "adoption-census-summary",
            "adoption" }, store.Events.Select(evt => evt.Operation));
        var adoption = Assert.Single(store.Events.Where(evt => evt.Operation == "adoption"));
        Assert.Equal("unavailable", adoption.Status);
        Assert.Equal("exception=InvalidOperationException", adoption.Detail);
        AssertMetadata(store.Events);
    }

    [Xunit.Fact]
    public void Start_ThrowingCensus_DoesNotInvokeAdoption()
    {
        var store = new RecordingRunEventStore();
        var calls = 0;
        var recorder = new ConductorLifecycleRecorder(store, generationId: () => "generation-a",
            readAdoptionCensus: () => throw new IOException("private message"),
            adoptInheritedWorkers: _ => { calls++; return []; });

        var session = recorder.Start("Conservative", null, StartedAt, null, null);

        Assert.Equal(0, calls);
        Assert.False(session.IsStopped);
        Assert.Equal(new[] { "start", "adoption-census" }, store.Events.Select(evt => evt.Operation));
        Assert.Equal("unavailable", store.Events[1].Status);
        AssertMetadata(store.Events);
    }

    [Xunit.Fact]
    public void Start_EmptyAdoption_RecordsZeroSummary()
    {
        var store = new RecordingRunEventStore();
        var calls = 0;
        var recorder = new ConductorLifecycleRecorder(store, readAdoptionCensus: () => [],
            adoptInheritedWorkers: _ => { calls++; return []; });

        recorder.Start("Conservative", null, StartedAt, null, null);

        Assert.Equal(1, calls);
        Assert.Equal(new[] { "start", "adoption-census-summary", "adoption-summary" },
            store.Events.Select(evt => evt.Operation));
        Assert.Equal("complete", store.Events[^1].Status);
        Assert.Equal("results=0 adopted=0", store.Events[^1].Detail);
    }

    [Xunit.Fact]
    public void Start_AdoptionAppendFails_PropagatesStoreFailure()
    {
        var failure = new IOException("store unavailable");
        var store = new RecordingRunEventStore { AdoptionAppendFailure = failure };
        var rows = Rows();
        var recorder = new ConductorLifecycleRecorder(store, readAdoptionCensus: () => rows,
            adoptInheritedWorkers: _ => [new WorkerAdoptionTransferResult(rows[0], "adopted")]);

        Assert.Same(failure, Assert.Throws<IOException>(() =>
            recorder.Start("Conservative", null, StartedAt, null, null)));
        Assert.Equal(new[] { "start", "adoption-census", "adoption-census", "adoption-census-summary" },
            store.Events.Select(evt => evt.Operation));
    }

    private static IReadOnlyList<WorkerAdoptionCensusRow> Rows() =>
    [
        new("goal:task-a", 101, StartedAt, SpawnRegistryLifecycle.ConductorDetached,
            41, StartedAt.AddHours(-1), SpawnOwnerLiveness.DeadOrRecycled, "inheritable", "worker live; owner gone"),
        new("goal:task-b", 102, StartedAt, SpawnRegistryLifecycle.Owned,
            null, null, SpawnOwnerLiveness.Unknown, "identity-unproven", "worker identity unavailable")
    ];

    private static void AssertMetadata(IEnumerable<RunEventAppend> events) => Assert.All(events, evt =>
    {
        Assert.Equal(RunEventTypes.ConductorLifecycle, evt.EventType);
        Assert.Null(evt.GoalId);
        Assert.Equal(StartedAt, evt.OccurredAt);
        using var payload = JsonDocument.Parse(evt.PayloadJson!);
        Assert.Equal("generation-a", payload.RootElement.GetProperty("generationId").GetString());
    });

    private sealed class RecordingRunEventStore : IRunEventStore
    {
        public List<RunEventAppend> Events { get; } = [];
        public Exception? AdoptionAppendFailure { get; init; }

        public Task<RunEventRecord> AppendAsync(RunEventAppend evt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (evt.Operation == "adoption" && AdoptionAppendFailure is { } failure) throw failure;
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
