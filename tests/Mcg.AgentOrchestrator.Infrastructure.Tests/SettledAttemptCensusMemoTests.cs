using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique temp roots, injected holder identities/clock, no real processes.
public sealed class SettledAttemptCensusMemoTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Census_SettledFilesSkipReads_AndChangedStampReadsAgain(bool singleGoal)
    {
        using var fixture = new CensusFixture();
        var settled = Enumerable.Range(0, 10).Select(index =>
            fixture.WriteAttempt(singleGoal ? "shared-goal" : $"goal-{index}", $"settled-{index}")).ToArray();
        var live = fixture.WriteAttempt(singleGoal ? "shared-goal" : "live-goal", "live",
            ConductorParallelAcceptanceAttemptOutcome.Running);
        foreach (var attempt in settled) fixture.WriteReceipt(attempt);

        IReadOnlyList<ConductorParallelAcceptanceAttempt> Census() => singleGoal
            ? fixture.Coordinator.GetUnreconciledAttempts(["shared-goal"])
            : fixture.Coordinator.GetAllCapacityReservingAttempts();

        Assert.Equal(live.AttemptId, Assert.Single(Census()).AttemptId);
        AssertPaths(settled.Select(attempt => attempt.MetadataPath).Append(live.MetadataPath), fixture.Reads);
        Assert.Equal(0, fixture.Coordinator.SettledCensusSkipCount);
        fixture.ClearObservations();
        Assert.Equal(live.AttemptId, Assert.Single(Census()).AttemptId);
        AssertPaths([live.MetadataPath], fixture.Reads);
        Assert.Equal(10, fixture.Coordinator.SettledCensusSkipCount);
        Assert.All(settled, attempt => Assert.Equal(0, fixture.Classifications.GetValueOrDefault(attempt.AttemptId)));

        var rewritten = settled[3];
        var oldStamp = File.GetLastWriteTimeUtc(rewritten.MetadataPath);
        File.WriteAllText(rewritten.MetadataPath, JsonSerializer.Serialize(rewritten, CensusFixture.Json));
        File.SetLastWriteTimeUtc(rewritten.MetadataPath, oldStamp.AddMinutes(1));
        Assert.True(File.GetLastWriteTimeUtc(rewritten.MetadataPath) > oldStamp);
        fixture.ClearObservations();
        Assert.Equal(live.AttemptId, Assert.Single(Census()).AttemptId);
        AssertPaths([rewritten.MetadataPath, live.MetadataPath], fixture.Reads);
        Assert.Equal(19, fixture.Coordinator.SettledCensusSkipCount);
        Assert.True(fixture.Classifications.GetValueOrDefault(rewritten.AttemptId) > 0);
    }

    [Fact]
    public void TerminalLiveHolder_ReevaluatesUntilDead_ThenSkips()
    {
        using var fixture = new CensusFixture();
        var attempt = fixture.WriteAttempt("goal", "live-terminal");
        fixture.Status[attempt.AttemptId] = SpawnTrackedProcessStatus.LiveMatch;
        for (var call = 0; call < 2; call++)
        {
            fixture.ClearObservations();
            Assert.Equal(attempt.AttemptId, Assert.Single(fixture.Coordinator.GetAllCapacityReservingAttempts()).AttemptId);
            AssertPaths([attempt.MetadataPath], fixture.Reads);
            Assert.True(fixture.Classifications.GetValueOrDefault(attempt.AttemptId) > 0);
            Assert.Equal(0, fixture.Coordinator.SettledCensusSkipCount);
        }
        fixture.Status[attempt.AttemptId] = SpawnTrackedProcessStatus.DeadOrRecycled;
        fixture.ClearObservations();
        Assert.Empty(fixture.Coordinator.GetAllCapacityReservingAttempts());
        AssertPaths([attempt.MetadataPath], fixture.Reads);
        Assert.True(fixture.Classifications.GetValueOrDefault(attempt.AttemptId) > 0);
        fixture.ClearObservations();
        Assert.Empty(fixture.Coordinator.GetAllCapacityReservingAttempts());
        Assert.Empty(fixture.Reads);
        Assert.Empty(fixture.Classifications);
        Assert.Equal(1, fixture.Coordinator.SettledCensusSkipCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasedV1Lease_SkipsTerminalMetadata_ButStillReadsRunning(bool running)
    {
        using var fixture = new CensusFixture(busyWriter: running);
        var attempt = fixture.WriteAttempt("goal", "v1", running
            ? ConductorParallelAcceptanceAttemptOutcome.Running : ConductorParallelAcceptanceAttemptOutcome.Passed, version: 1);
        var binding = AcceptanceHolderBinding.For(attempt);
        fixture.Lease.Reserve(binding, fixture.Generation);
        Assert.True(fixture.Lease.ReconcileUnclaimed(binding,
            new(attempt.OwnerProcessId, attempt.OwnerProcessStartedAt!.Value, attempt.OwnerExecutablePath!)));
        Assert.False(fixture.Lease.Observe(binding).ReservesCapacity);
        for (var call = 0; call < 2; call++)
        {
            fixture.ClearObservations();
            var census = fixture.Coordinator.GetAllCapacityReservingAttempts();
            if (running) Assert.Equal(attempt.AttemptId, Assert.Single(census).AttemptId);
            else Assert.Empty(census);
            AssertPaths(running || call == 0 ? [attempt.MetadataPath] : [], fixture.Reads);
            Assert.Equal(!running && call == 1 ? 1 : 0, fixture.Coordinator.SettledCensusSkipCount);
        }
    }

    [Fact]
    public void FaultedRead_IsObservedAndNeverMemoized()
    {
        using var fixture = new CensusFixture();
        var attempt = fixture.WriteAttempt("goal", "broken");
        File.WriteAllText(attempt.MetadataPath, "{");
        for (var call = 0; call < 2; call++)
        {
            fixture.ClearObservations();
            Assert.Throws<InvalidDataException>(() => fixture.Coordinator.GetAllCapacityReservingAttempts());
            AssertPaths([attempt.MetadataPath], fixture.Reads);
            Assert.Equal(0, fixture.Coordinator.SettledCensusSkipCount);
        }
    }

    [Fact]
    public void Memo_ConcurrentCanonicalPathsAndPruning_PreserveStampContract()
    {
        using var fixture = new CensusFixture();
        var first = fixture.WriteAttempt("goal", "first");
        var second = fixture.WriteAttempt("goal", "second");
        var memo = new SettledAttemptCensusMemo();
        var stamp = File.GetLastWriteTimeUtc(first.MetadataPath);
        Parallel.For(0, 32, _ =>
        {
            memo.Record(first.MetadataPath, stamp, 10);
            Assert.True(memo.IsSettled(Path.Combine(Path.GetDirectoryName(first.MetadataPath)!, ".", "first.attempt.json"), stamp, 10));
        });
        Assert.False(memo.IsSettled(first.MetadataPath, stamp.AddTicks(1), 10));
        Assert.False(memo.IsSettled(first.MetadataPath, stamp, 11));
        memo.Record(second.MetadataPath, stamp, 10);
        memo.DropMissing(Path.GetDirectoryName(first.MetadataPath)!, [second.MetadataPath]);
        Assert.False(memo.IsSettled(first.MetadataPath, stamp, 10));
        Assert.True(memo.IsSettled(second.MetadataPath, stamp, 10));
    }

    private static void AssertPaths(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(expected.OrderBy(path => path, StringComparer.Ordinal), actual.OrderBy(path => path, StringComparer.Ordinal));

    private sealed class CensusFixture : IDisposable
    {
        internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-census-memo-" + Guid.NewGuid().ToString("N"));
        internal DateTimeOffset Now { get; } = DateTimeOffset.Parse("2026-10-16T12:00:00Z");
        internal SpawnProcessIdentity Generation { get; }
        internal AcceptanceHolderLease Lease { get; }
        internal ConductorParallelAcceptanceAttemptCoordinator Coordinator { get; }
        internal List<string> Reads { get; } = [];
        internal Dictionary<string, int> Classifications { get; } = new();
        internal Dictionary<string, SpawnTrackedProcessStatus> Status { get; } = new();

        internal CensusFixture(bool busyWriter = false)
        {
            Directory.CreateDirectory(_root);
            Generation = new(2_000_000_001, Now, "fixture-conductor.exe");
            Lease = new(utcNow: () => Now, classify: _ => SpawnTrackedProcessStatus.DeadOrRecycled);
            Coordinator = new(_root, TrunkBranchName.Default, utcNow: () => Now,
                classifyHolder: attempt =>
                {
                    Classifications[attempt.AttemptId] = Classifications.GetValueOrDefault(attempt.AttemptId) + 1;
                    return Status[attempt.AttemptId];
                }, holderLease: Lease, generationIdentity: Generation, hostBootTime: () => null,
                tryAcquireAttemptWriterLease: busyWriter ? (_, _) => null : null,
                attemptMetadataReadObserver: Reads.Add);
        }

        internal ConductorParallelAcceptanceAttempt WriteAttempt(string goal, string id,
            ConductorParallelAcceptanceAttemptOutcome outcome = ConductorParallelAcceptanceAttemptOutcome.Passed, int version = 0)
        {
            var directory = Path.Combine(_root, goal);
            Directory.CreateDirectory(directory);
            var attempt = new ConductorParallelAcceptanceAttempt(id, goal, goal, 0,
                "branch-sha", "main-sha", Now.AddDays(-2), Now.AddDays(-1), 2_000_000_002, outcome,
                Path.Combine(directory, id + ".out"), Path.Combine(directory, id + ".err"),
                Path.Combine(directory, id + ".exit"), Path.Combine(directory, id + ".heartbeat"),
                Path.Combine(directory, id + ".result"), Path.Combine(directory, id + ".attempt.json"),
                ReconciledAt: outcome == ConductorParallelAcceptanceAttemptOutcome.Running ? null : Now.AddDays(-1))
            {
                OwnerProcessStartedAt = Now.AddDays(-2), OwnerExecutablePath = "fixture-holder.exe",
                HolderContractVersion = version, HolderCandidateKey = version == 1 ? "fixture-candidate" : null
            };
            File.WriteAllText(attempt.MetadataPath, JsonSerializer.Serialize(attempt, Json));
            Status[id] = outcome == ConductorParallelAcceptanceAttemptOutcome.Running
                ? SpawnTrackedProcessStatus.LiveMatch : SpawnTrackedProcessStatus.DeadOrRecycled;
            return attempt;
        }

        internal void WriteReceipt(ConductorParallelAcceptanceAttempt attempt) =>
            Assert.True(LegacyAcceptanceHolderSettlement.RecordTerminal(AcceptanceHolderBinding.For(attempt),
                attempt.OwnerProcessId, attempt.OwnerProcessStartedAt, attempt.OwnerExecutablePath,
                attempt.StartedAt, Now, "fixture-holder-exited"));

        internal void ClearObservations() { Reads.Clear(); Classifications.Clear(); }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
