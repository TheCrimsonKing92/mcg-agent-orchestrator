using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixed identities and pure fake evaluators; no live processes or shared state.
public sealed class WorkerAdoptionCensusTests
{
    [Xunit.Theory]
    [Xunit.InlineData((int)SpawnRegistryLifecycle.Owned, (int)SpawnTrackedProcessStatus.DeadOrRecycled, (int)SpawnOwnerLiveness.Live, true, "current-generation")]
    [Xunit.InlineData((int)SpawnRegistryLifecycle.ConductorDetached, (int)SpawnTrackedProcessStatus.DeadOrRecycled, (int)SpawnOwnerLiveness.DeadOrRecycled, false, "worker-gone")]
    [Xunit.InlineData((int)SpawnRegistryLifecycle.ConductorDetached, (int)SpawnTrackedProcessStatus.Unknown, (int)SpawnOwnerLiveness.DeadOrRecycled, false, "identity-unproven")]
    [Xunit.InlineData((int)SpawnRegistryLifecycle.ConductorDetached, (int)SpawnTrackedProcessStatus.LiveMatch, (int)SpawnOwnerLiveness.DeadOrRecycled, false, "inheritable")]
    [Xunit.InlineData((int)SpawnRegistryLifecycle.RuntimeOwned, (int)SpawnTrackedProcessStatus.LiveMatch, (int)SpawnOwnerLiveness.Unknown, false, "inheritable")]
    [Xunit.InlineData((int)SpawnRegistryLifecycle.GracefullyDetached, (int)SpawnTrackedProcessStatus.LiveMatch, (int)SpawnOwnerLiveness.Unknown, false, "inheritable")]
    [Xunit.InlineData((int)SpawnRegistryLifecycle.Owned, (int)SpawnTrackedProcessStatus.LiveMatch, (int)SpawnOwnerLiveness.DeadOrRecycled, false, "orphaned")]
    [Xunit.InlineData((int)SpawnRegistryLifecycle.ConductorDetached, (int)SpawnTrackedProcessStatus.LiveMatch, (int)SpawnOwnerLiveness.Live, false, "owner-held")]
    [Xunit.InlineData((int)SpawnRegistryLifecycle.Owned, (int)SpawnTrackedProcessStatus.LiveMatch, (int)SpawnOwnerLiveness.Unknown, false, "owner-held")]
    public void Classify_OrderedConditions_ReturnsFirstVerdict(
        int lifecycle, int worker, int owner, bool current, string expected)
    {
        Assert.Equal(expected, WorkerAdoptionCensus.Classify(
            (SpawnRegistryLifecycle)lifecycle, (SpawnTrackedProcessStatus)worker,
            (SpawnOwnerLiveness)owner, current));
    }

    [Xunit.Fact]
    public void Read_ThreeEntries_PreservesIdentityAndRequiresPidAndStartMatch()
    {
        var start = new DateTimeOffset(2026, 10, 7, 1, 0, 0, TimeSpan.Zero);
        var current = new SpawnProcessIdentity(42, start, "current.exe");
        SpawnRegistryEntry[] entries =
        [
            Entry(3, 42, start.AddSeconds(-1)),
            Entry(1, 42, start),
            Entry(2, 99, start)
        ];
        var workerCalls = new List<long>();
        var ownerCalls = new List<long>();

        var rows = WorkerAdoptionCensus.Read(entries,
            entry =>
            {
                workerCalls.Add(entry.Id);
                return (SpawnTrackedProcessStatus.LiveMatch, $"worker-{entry.Id}");
            },
            entry =>
            {
                ownerCalls.Add(entry.Id);
                return (SpawnOwnerLiveness.Unknown, $"owner-{entry.Id}");
            }, current);

        Assert.Equal(new long[] { 3, 1, 2 }, workerCalls);
        Assert.Equal(workerCalls, ownerCalls);
        Assert.Equal(new[] { "owner-held", "current-generation", "owner-held" }, rows.Select(row => row.Verdict));
        Assert.Equal(3, rows.Count);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var row = rows[index];
            Assert.Equal(entry.OwnerId, row.OwnerId);
            Assert.Equal(entry.ProcessId, row.WorkerProcessId);
            Assert.Equal(entry.ProcessStartedAt, row.WorkerStartedAt);
            Assert.Equal(entry.Lifecycle, row.Lifecycle);
            Assert.Equal(entry.OwnerProcessId, row.OwnerProcessId);
            Assert.Equal(entry.OwnerProcessStartedAt, row.OwnerStartedAt);
            Assert.Equal(SpawnOwnerLiveness.Unknown, row.OwnerLiveness);
            Assert.Equal($"worker: worker-{entry.Id}; owner: owner-{entry.Id}", row.Evidence);
        }

        SpawnRegistryEntry Entry(long id, int ownerPid, DateTimeOffset ownerStart) => new(
            id, $"dispatch-{id}", 100 + (int)id, start.AddMinutes(id), "worker.exe",
            start, null, null, ownerPid, ownerStart, "owner.exe", SpawnRegistryLifecycle.Owned);
    }
}
