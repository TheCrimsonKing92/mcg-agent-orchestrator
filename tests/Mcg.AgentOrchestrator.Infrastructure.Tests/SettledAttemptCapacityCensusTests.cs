using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique filesystem/mutex roots, injected identity and clock, no real processes.
public sealed class SettledAttemptCapacityCensusTests
{
    [Fact]
    public void MatchingTerminalReceipt_ReleasesDuringBusyLeaseWithoutAnyFileMutation()
    {
        using var fixture = new CensusFixture();
        var attempt = fixture.WriteAttempt();
        fixture.WriteReceipt(attempt);
        var before = Snapshot(fixture.GoalDirectory);
        using var holder = new BusyWriter(fixture.GoalDirectory);
        using var second = StorageRetentionMaintenance.TryAcquireAttemptWriterLease(fixture.GoalDirectory);
        Assert.Null(second);

        Assert.Empty(fixture.Coordinator.GetAllCapacityReservingAttempts());

        Assert.Equal(before, Snapshot(fixture.GoalDirectory));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-pid")]
    [InlineData("wrong-attempt-start")]
    [InlineData("running")]
    public void UnsettledOrMismatchedAttempt_HoldsAndLogsBusyReasonOnce(string control)
    {
        using var fixture = new CensusFixture();
        var attempt = fixture.WriteAttempt(control == "running"
            ? ConductorParallelAcceptanceAttemptOutcome.Running : ConductorParallelAcceptanceAttemptOutcome.Passed);
        if (control != "missing")
            fixture.WriteReceipt(attempt, control == "wrong-pid" ? attempt.OwnerProcessId + 1 : null,
                control == "wrong-attempt-start" ? attempt.StartedAt.AddMinutes(1) : null);
        using var holder = new BusyWriter(fixture.GoalDirectory);
        using var second = StorageRetentionMaintenance.TryAcquireAttemptWriterLease(fixture.GoalDirectory);
        Assert.Null(second);

        Assert.Equal(attempt.AttemptId, Assert.Single(fixture.Coordinator.GetAllCapacityReservingAttempts()).AttemptId);
        Assert.Equal(attempt.AttemptId, Assert.Single(fixture.Coordinator.GetAllCapacityReservingAttempts()).AttemptId);

        var hold = Assert.Single(fixture.Events().Where(record => record.EventKind == "acceptance-holder" &&
            record.Detail.StartsWith($"ACCEPTANCE_HOLDER_HOLD attempt={attempt.AttemptId} ", StringComparison.Ordinal)));
        Assert.Contains("reason=attempt-writer-lease-busy", hold.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FreeLease_WritesMissingReceiptAndReleaseEvent_ThenDoesNotRewriteReceipt()
    {
        using var fixture = new CensusFixture();
        var attempt = fixture.WriteAttempt();
        var binding = AcceptanceHolderBinding.For(attempt);
        Assert.False(File.Exists(LegacyAcceptanceHolderSettlement.TerminalReceiptPath(binding)));

        Assert.Empty(fixture.Coordinator.GetAllCapacityReservingAttempts());

        Assert.True(LegacyAcceptanceHolderSettlement.HasTerminalReceipt(binding, attempt.OwnerProcessId,
            attempt.OwnerProcessStartedAt, attempt.OwnerExecutablePath, attempt.StartedAt));
        Assert.Single(fixture.Events().Where(record => record.EventKind == "acceptance-holder" &&
            record.Detail.StartsWith($"ACCEPTANCE_HOLDER_RELEASE attempt={attempt.AttemptId} ", StringComparison.Ordinal)));
        var receipt = LegacyAcceptanceHolderSettlement.TerminalReceiptPath(binding);
        File.SetLastWriteTimeUtc(receipt, fixture.Now.AddDays(-10).UtcDateTime);
        var before = Snapshot(fixture.GoalDirectory);

        Assert.Empty(fixture.Coordinator.GetAllCapacityReservingAttempts());

        Assert.Equal(before, Snapshot(fixture.GoalDirectory));
        Assert.Single(fixture.Events().Where(record => record.Detail.StartsWith(
            $"ACCEPTANCE_HOLDER_RELEASE attempt={attempt.AttemptId} ", StringComparison.Ordinal)));
    }

    [Fact]
    public void V1RunningAttemptWithReleasedHolder_HoldsAndLogsBusyReasonOnce()
    {
        using var fixture = new CensusFixture();
        var attempt = fixture.WriteAttempt(ConductorParallelAcceptanceAttemptOutcome.Running, version: 1);
        var binding = AcceptanceHolderBinding.For(attempt);
        fixture.Lease.Reserve(binding, fixture.Generation);
        Assert.True(fixture.Lease.ReconcileUnclaimed(binding,
            new(attempt.OwnerProcessId, attempt.OwnerProcessStartedAt!.Value, attempt.OwnerExecutablePath!)));
        Assert.False(fixture.Lease.Observe(binding).ReservesCapacity);
        using var holder = new BusyWriter(fixture.GoalDirectory);
        using var second = StorageRetentionMaintenance.TryAcquireAttemptWriterLease(fixture.GoalDirectory);
        Assert.Null(second);

        Assert.Equal(attempt.AttemptId, Assert.Single(fixture.Coordinator.GetAllCapacityReservingAttempts()).AttemptId);
        Assert.Equal(attempt.AttemptId, Assert.Single(fixture.Coordinator.GetAllCapacityReservingAttempts()).AttemptId);

        var hold = Assert.Single(fixture.Events().Where(record => record.EventKind == "acceptance-holder" &&
            record.Detail.StartsWith($"ACCEPTANCE_HOLDER_HOLD attempt={attempt.AttemptId} ", StringComparison.Ordinal)));
        Assert.Contains("reason=attempt-writer-lease-busy", hold.Detail, StringComparison.Ordinal);
    }

    private static string[] Snapshot(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal).Select(path =>
            $"{Path.GetRelativePath(directory, path)}|{new FileInfo(path).Length}|" +
            $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}|{File.GetLastWriteTimeUtc(path):O}")
        .ToArray();

    private sealed class CensusFixture : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-settled-census-" + Guid.NewGuid().ToString("N"));
        private readonly string _goal = Guid.NewGuid().ToString("N");
        private readonly string _eventPath;
        internal DateTimeOffset Now { get; } = DateTimeOffset.Parse("2026-10-16T12:00:00Z");
        internal string GoalDirectory { get; }
        internal SpawnProcessIdentity Generation { get; }
        internal AcceptanceHolderLease Lease { get; }
        internal ConductorParallelAcceptanceAttemptCoordinator Coordinator { get; }

        internal CensusFixture()
        {
            GoalDirectory = Path.Combine(_root, "attempts", _goal);
            Directory.CreateDirectory(GoalDirectory);
            _eventPath = Path.Combine(_root, "events.log");
            Generation = new(909, Now, "conductor.exe");
            Lease = new(utcNow: () => Now, classify: _ => SpawnTrackedProcessStatus.DeadOrRecycled);
            Coordinator = new(Path.GetDirectoryName(GoalDirectory)!, TrunkBranchName.Default,
                utcNow: () => Now, classifyHolder: _ => SpawnTrackedProcessStatus.DeadOrRecycled,
                holderLease: Lease, generationIdentity: Generation, hostBootTime: () => null,
                conductEventLogWriter: new ConductEventLogWriter(_eventPath));
        }

        internal ConductorParallelAcceptanceAttempt WriteAttempt(
            ConductorParallelAcceptanceAttemptOutcome outcome = ConductorParallelAcceptanceAttemptOutcome.Passed, int version = 0)
        {
            var attempt = new ConductorParallelAcceptanceAttempt("legacy", _goal, _goal[..8], 0,
                "branch-sha", "main-sha", Now.AddDays(-2), Now.AddDays(-1), 7115, outcome,
                Path.Combine(GoalDirectory, "out"), Path.Combine(GoalDirectory, "err"),
                Path.Combine(GoalDirectory, "exit"), Path.Combine(GoalDirectory, "heartbeat"),
                Path.Combine(GoalDirectory, "result"), Path.Combine(GoalDirectory, "legacy.attempt.json"),
                ReconciledAt: outcome == ConductorParallelAcceptanceAttemptOutcome.Running ? null : Now.AddDays(-1))
            {
                OwnerProcessStartedAt = Now.AddDays(-2), OwnerExecutablePath = "legacy-dotnet.exe",
                HolderContractVersion = version, HolderCandidateKey = version == 1 ? "fixture-candidate" : null
            };
            File.WriteAllText(attempt.MetadataPath, JsonSerializer.Serialize(attempt, Json));
            return attempt;
        }

        internal void WriteReceipt(ConductorParallelAcceptanceAttempt attempt, int? pid = null, DateTimeOffset? start = null) =>
            Assert.True(LegacyAcceptanceHolderSettlement.RecordTerminal(AcceptanceHolderBinding.For(attempt),
                pid ?? attempt.OwnerProcessId, attempt.OwnerProcessStartedAt, attempt.OwnerExecutablePath,
                start ?? attempt.StartedAt, Now, "fixture-holder-exited"));

        internal ConductEventRecord[] Events() => File.Exists(_eventPath)
            ? File.ReadLines(_eventPath).Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, Json)!).ToArray()
            : [];

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class BusyWriter : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;
        private Exception? _failure;

        internal BusyWriter(string directory)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    using var lease = StorageRetentionMaintenance.TryAcquireAttemptWriterLease(directory);
                    Assert.NotNull(lease);
                    _entered.Set();
                    Assert.True(_release.Wait(TimeSpan.FromSeconds(60)), "Writer lease release was not signaled.");
                }
                catch (Exception ex) { _failure = ex; _entered.Set(); }
            }) { IsBackground = true };
            _thread.Start();
            if (!_entered.Wait(TimeSpan.FromSeconds(60)) || _failure is not null)
            {
                Dispose();
                Assert.Fail("Writer lease acquisition was not signaled.");
            }
        }

        public void Dispose()
        {
            _release.Set();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(60)), "Writer lease thread did not finish.");
            _entered.Dispose();
            _release.Dispose();
            Assert.Null(_failure);
        }
    }
}
