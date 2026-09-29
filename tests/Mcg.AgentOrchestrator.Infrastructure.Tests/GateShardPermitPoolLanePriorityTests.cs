using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GateShardPermitPoolLanePriorityTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GateWaiterTakesReleasedPermitBeforeEvidence()
    {
        using var fixture = new PermitFixture();
        using var first = fixture.Take();
        using var second = fixture.Take();
        using var gate = fixture.Pool.BeginWait(GateShardLaneClass.Gate);
        using var evidence = fixture.Pool.BeginWait(GateShardLaneClass.Evidence);

        Assert.Equal(GateShardPollOutcome.Waiting, gate.Poll(out var unavailable));
        Assert.Null(unavailable);
        second.Dispose();
        Assert.Equal(GateShardPollOutcome.YieldingToGate, evidence.Poll(out unavailable));
        Assert.Null(unavailable);
        Assert.Equal(GateShardPollOutcome.Acquired, gate.Poll(out var gatePermit));
        Assert.Empty(fixture.WaiterFiles());
        using (gatePermit!)
        {
            Assert.Equal(GateShardPollOutcome.Waiting, evidence.Poll(out unavailable));
            Assert.Null(unavailable);
        }
    }

    [Fact]
    public void GateWaitMarkerIsRemovedWhenWaitEnds()
    {
        using var fixture = new PermitFixture();
        using var first = fixture.Take();
        using var second = fixture.Take();
        using (var gate = fixture.Pool.BeginWait(GateShardLaneClass.Gate))
        {
            Assert.Equal(GateShardPollOutcome.Waiting, gate.Poll(out _));
            Assert.Single(fixture.WaiterFiles());
        }
        Assert.Empty(fixture.WaiterFiles());
    }

    [Fact]
    public void EvidenceTakesFirstFreePermitOnFirstPollWithoutGateWaiter()
    {
        using var fixture = new PermitFixture();
        using var held = fixture.Take();
        using var evidence = fixture.Pool.BeginWait(GateShardLaneClass.Evidence);
        Assert.Equal(GateShardPollOutcome.Acquired, evidence.Poll(out var permit));
        Assert.NotNull(permit);
        permit!.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleGateWaiterIsRemovedAndIgnored(bool reusedPid)
    {
        using var fixture = new PermitFixture();
        var path = fixture.WriteMarker(reusedPid ? 71 : 72, Start);
        using var evidence = fixture.Pool.BeginWait(GateShardLaneClass.Evidence);
        Assert.Equal(GateShardPollOutcome.Acquired, evidence.Poll(out var permit));
        Assert.NotNull(permit);
        Assert.False(File.Exists(path));
        permit!.Dispose();
    }

    [Fact]
    public void MalformedMarkerDoesNotBlockEvidence()
    {
        using var fixture = new PermitFixture();
        var path = fixture.WriteMarker(71, Start, "broken");
        using var evidence = fixture.Pool.BeginWait(GateShardLaneClass.Evidence);
        Assert.Equal(GateShardPollOutcome.Acquired, evidence.Poll(out var permit));
        Assert.False(File.Exists(path));
        permit!.Dispose();
    }

    [Fact]
    public void EvidenceForcesNextFreePermitAfterTenMinutesOfYielding()
    {
        using var fixture = new PermitFixture();
        using var first = fixture.Take();
        using var second = fixture.Take();
        using var gate = fixture.Pool.BeginWait(GateShardLaneClass.Gate);
        using var evidence = fixture.Pool.BeginWait(GateShardLaneClass.Evidence);
        Assert.Equal(GateShardPollOutcome.Waiting, gate.Poll(out _));
        second.Dispose();

        Assert.Equal(GateShardPollOutcome.YieldingToGate, evidence.Poll(out _));
        Assert.Contains("lane_class=evidence permit_priority=yielding-to-gate",
            GateShardLanePriorityProgress.Format(GateShardLaneClass.Evidence, GateShardPollOutcome.YieldingToGate));
        fixture.Clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        Assert.Equal(GateShardPollOutcome.YieldingToGate, evidence.Poll(out _));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(GateShardPollOutcome.AcquiredAfterForcedYield, evidence.Poll(out var permit));
        Assert.Contains("lane_class=evidence permit_priority=forced-after-yield",
            GateShardLanePriorityProgress.Format(GateShardLaneClass.Evidence,
                GateShardPollOutcome.AcquiredAfterForcedYield));
        permit!.Dispose();
    }

    private sealed class PermitFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gate-priority-" + Guid.NewGuid().ToString("N"));
        internal readonly ManualClock Clock = new(Start);
        internal readonly GateShardPermitPool Pool;

        internal PermitFixture()
        {
            Pool = GateShardPermitPool.ForRoot(_root, 2,
                new GateShardProcessFacts(71, pid => pid == 71 ? Start.AddMinutes(1) : null), Clock);
        }

        internal GateShardPermit Take()
        {
            Assert.True(Pool.TryAcquire(out var permit));
            return permit!;
        }

        internal string WriteMarker(int pid, DateTimeOffset start, string? content = null)
        {
            var directory = Path.Combine(_root, "gate-shard-permits", "waiters");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "gate-fake-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, content ?? $"{pid}\n{start:O}\n");
            return path;
        }

        internal string[] WaiterFiles() => Directory.GetFiles(
            Path.Combine(_root, "gate-shard-permits", "waiters"), "gate-*.json");

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }
}
