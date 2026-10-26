using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test owns its storage and manual clock.
public sealed class AttributionOccupancyHeartbeatTests
{
    [Fact]
    public void Start_ValidSlot_WritesOneAcceptanceOccupant()
    {
        using var fixture = new HeartbeatFixture();
        using var writer = fixture.Start();
        var path = Assert.Single(fixture.Files());
        Assert.StartsWith("activity-0.heartbeat-", Path.GetFileName(path));
        Assert.Equal(fixture.Path, path);
        var snapshot = fixture.Snapshot();
        Assert.Equal("running", snapshot.State);
        Assert.Equal("cohort-attribution", snapshot.Phase);
        Assert.Equal(GateHeartbeatRunClass.Acceptance, snapshot.RunClass);
        Assert.Equal(HeartbeatFixture.Cohort, snapshot.RunId);
        Assert.Equal(HeartbeatFixture.Goal, snapshot.GoalId);
        Assert.Equal(HeartbeatFixture.Cohort + " member=0", snapshot.CurrentTarget);
        Assert.Equal(0, snapshot.SlotIndex);
        Assert.Equal(Environment.ProcessId, snapshot.ProcessId);
        Assert.Equal(Environment.ProcessId, snapshot.ChildPid);
        Assert.Equal(fixture.Clock.GetUtcNow(), snapshot.StartedAt);
        Assert.Equal(snapshot.StartedAt, snapshot.LastObservedAt);
        Assert.Equal(snapshot.StartedAt, snapshot.LastProgressAt);
    }

    [Fact]
    public void Observe_AdvancingProgress_WritesFirstThenEveryFifteenSeconds()
    {
        using var fixture = new HeartbeatFixture();
        using var writer = fixture.Start();
        var start = fixture.Clock.GetUtcNow();
        for (var seconds = 5; seconds <= 35; seconds += 5)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            writer.Observe(fixture.Progress(fixture.Clock.GetUtcNow(), $"check-{seconds}"));
            var writeSecond = 5 + (seconds - 5) / 15 * 15;
            var snapshot = fixture.Snapshot();
            Assert.Equal(start.AddSeconds(writeSecond), snapshot.LastObservedAt);
            Assert.Equal(start.AddSeconds(writeSecond), snapshot.LastProgressAt);
            Assert.Equal($"check-{writeSecond}", snapshot.CurrentTarget);
            Assert.Equal(start, snapshot.StartedAt);
            Assert.Equal("cohort-attribution", snapshot.Phase);
            Assert.Single(fixture.Files());
        }
    }

    [Fact]
    public void Observe_RepeatedOrOlderProgress_DoesNotRefreshOrFlushSuppressedAdvance()
    {
        using var fixture = new HeartbeatFixture();
        using var writer = fixture.Start();
        var start = fixture.Clock.GetUtcNow();
        writer.Observe(fixture.Progress(start, "unchanged-start"));
        Assert.Equal(HeartbeatFixture.Cohort + " member=0", fixture.Snapshot().CurrentTarget);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        writer.Observe(fixture.Progress(fixture.Clock.GetUtcNow(), "first"));
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        var suppressed = fixture.Clock.GetUtcNow();
        writer.Observe(fixture.Progress(suppressed, "suppressed"));
        var bytes = File.ReadAllBytes(fixture.Path);
        fixture.Clock.Advance(TimeSpan.FromSeconds(60));
        writer.Observe(fixture.Progress(suppressed, "repeated"));
        writer.Observe(fixture.Progress(start, "older"));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Path));
        Assert.Equal(start.AddSeconds(5), fixture.Snapshot().LastObservedAt);
        Assert.Equal("first", fixture.Snapshot().CurrentTarget);
    }

    [Fact]
    public void ReadStableSlots_StoppedProgress_ExpiresAfterInclusiveTwoMinuteBoundary()
    {
        using var fixture = new HeartbeatFixture();
        using var writer = fixture.Start();
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        writer.Observe(fixture.Progress(fixture.Clock.GetUtcNow()));
        var lastWrite = fixture.Snapshot().LastObservedAt;
        fixture.Clock.Advance(TimeSpan.FromSeconds(120));
        writer.Observe(fixture.Progress(lastWrite));
        var running = GateHeartbeatArtifacts.ReadStableSlots(fixture.Storage, fixture.Clock.GetUtcNow())
            .Where(status => status.Snapshot?.State == "running").ToArray();
        Assert.Equal(fixture.Path, Assert.Single(running).Path);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        writer.Observe(fixture.Progress(lastWrite));
        Assert.DoesNotContain(GateHeartbeatArtifacts.ReadStableSlots(fixture.Storage, fixture.Clock.GetUtcNow()),
            status => status.Snapshot?.State == "running");
        Assert.Empty(fixture.Files());
    }

    [Fact]
    public void Dispose_RepeatedAndLateCallbacks_LeaveNoFile()
    {
        using var fixture = new HeartbeatFixture();
        using var writer = fixture.Start();
        Assert.Single(fixture.Files());
        writer.Dispose();
        Assert.Empty(fixture.Files());
        writer.Dispose();
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        writer.Observe(fixture.Progress(fixture.Clock.GetUtcNow()));
        Assert.Empty(fixture.Files());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(2)]
    public void Start_InvalidSlot_AllOperationsRemainInert(int? slot)
    {
        using var fixture = new HeartbeatFixture();
        using var writer = fixture.Start(slot);
        Assert.Empty(fixture.Files());
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        writer.Observe(fixture.Progress(fixture.Clock.GetUtcNow()));
        Assert.Empty(fixture.Files());
        writer.Dispose();
        Assert.Empty(fixture.Files());
    }

    [Fact]
    public void RunFocusedVerification_OptionalSink_ForwardsOwnerProgressInOrder()
    {
        using var fixture = new HeartbeatFixture();
        Directory.CreateDirectory(fixture.Storage.RootPath);
        var records = Enumerable.Range(1, 3)
            .Select(index => fixture.Progress(fixture.Clock.GetUtcNow().AddSeconds(index), $"target-{index}"))
            .ToArray();
        var verifier = new ProgressVerifier(records);
        var received = new List<AcceptanceGateProgress>();
        var result = Run(received.Add);
        Assert.Same(verifier.Result, result);
        Assert.Equal(3, received.Count);
        for (var index = 0; index < records.Length; index++) Assert.Same(records[index], received[index]);
        received.Clear();
        var withoutSink = AcceptanceExecutionRunner.RunFocusedVerification(verifier, fixture.Storage.RootPath,
            null, "focused", 0, null, false, TestContext.Current.CancellationToken, "main");
        Assert.Same(result, withoutSink);
        Assert.Equal(2, verifier.Calls);
        Assert.Empty(received);

        FocusedEvidenceRunResult Run(Action<AcceptanceGateProgress> sink) =>
            AcceptanceExecutionRunner.RunFocusedVerification(verifier, fixture.Storage.RootPath,
                null, "focused", 0, null, false, TestContext.Current.CancellationToken, "main", progressSink: sink);
    }

    internal sealed class HeartbeatFixture : IDisposable
    {
        internal const string Goal = "attributed-member";
        internal const string Cohort = "cohort-fixture";
        internal readonly ManualRemoteLaneClock Clock = new();
        internal readonly DotnetBuildStorageRoot Storage = new(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"mcg-attribution-{Guid.NewGuid():N}"));
        internal string Path => GateHeartbeatArtifacts.GetRunScopedStableSlotPath(0, Cohort + "|attribution-0", Storage);
        internal AttributionOccupancyHeartbeat Start(int? slot = 0) =>
            AttributionOccupancyHeartbeat.Start(Goal, slot, Cohort, 0, Storage, Clock.GetUtcNow);
        internal string[] Files() => Directory.Exists(Storage.RootPath)
            ? Directory.GetFiles(Storage.RootPath, "activity-*.heartbeat-*.json", SearchOption.AllDirectories) : [];
        internal GateHeartbeatSnapshot Snapshot() => Assert.IsType<GateHeartbeatSnapshot>(GateHeartbeatArtifacts.TryReadSnapshot(Path));
        internal AcceptanceGateProgress Progress(DateTimeOffset progressAt, string target = "check") =>
            new(Goal, "test", target, 0, Environment.ProcessId, Environment.ProcessId,
                Clock.GetUtcNow(), Clock.GetUtcNow(), progressAt, TimeSpan.Zero, 1, "");
        public void Dispose()
        {
            if (Directory.Exists(Storage.RootPath)) Directory.Delete(Storage.RootPath, recursive: true);
        }
    }

    private sealed class ProgressVerifier(AcceptanceGateProgress[] records) : IGoalAcceptanceVerifier
    {
        internal readonly FocusedEvidenceRunResult Result = new("focused", true, true, "complete", []);
        internal int Calls;
        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner) => throw new NotSupportedException();
        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false)
        {
            Calls++;
            var context = Assert.IsAssignableFrom<IAcceptanceRunExecutionContext>(executionOwner);
            foreach (var progress in records) context.ReportProgress(progress);
            return Task.FromResult(Result);
        }
    }
}
