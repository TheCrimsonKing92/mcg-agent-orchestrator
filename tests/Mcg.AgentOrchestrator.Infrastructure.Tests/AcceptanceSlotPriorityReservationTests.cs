using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceSlotPriorityReservationTests : DotnetBuildEnvironmentManagerRootedTestBase
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void WorkersYieldUntilTheirInjectedTimeout(int slot)
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, slot);
        var clock = new RecordingTimeProvider();
        var sleeps = new List<TimeSpan>();
        var timeout = TimeSpan.FromMilliseconds(250);
        var startedAt = clock.GetUtcNow();
        using var reservation = Take(clock);

        Assert.Equal(2, reservation.HeldSlotCount);
        Assert.Throws<DotnetBuildSlotsBusyException>(() =>
            DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(environment, timeProvider: clock,
                sleep: duration => { sleeps.Add(duration); clock.Advance(duration); }, timeout: timeout));

        Assert.NotEmpty(sleeps);
        Assert.True(clock.GetUtcNow() - startedAt >= timeout);
        Assert.True(RootedDotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(StorageRoot, slot));
        Assert.Equal(2, reservation.HeldSlotCount);
    }

    [Fact]
    public void GateReceivesReleasedSlotWhileWorkerYields()
    {
        var worker0 = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0)
            with { LeaseId = "goal-worker0" };
        var worker1 = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 1)
            with { LeaseId = "goal-worker1" };
        using var holder0 = DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(worker0, timeout: TimeSpan.Zero);
        using var holder1 = DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(worker1, timeout: TimeSpan.Zero);
        var clock = new RecordingTimeProvider();
        using var reservation = Take(clock);
        Assert.Equal(2, reservation.HeldSlotCount);

        holder0.Dispose();
        Assert.Throws<DotnetBuildSlotsBusyException>(() =>
            DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(worker0, timeProvider: clock,
                sleep: clock.Advance, timeout: TimeSpan.Zero));
        var result = DotnetBuildEnvironmentManager.TryAcquireFirstAvailableStableSlotExecutionLock(
            TimeSpan.Zero, timeProvider: clock, sleep: clock.Advance, storageRoot: StorageRoot,
            holderLabel: "cohort-gate:waiting");
        using var gate = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(result).Lease;

        Assert.Equal(0, gate.Environment.BuildPermitIndex);
        Assert.False(holder0.IsExecutionLockHeld);
        Assert.True(gate.IsExecutionLockHeld);
        Assert.True(holder1.IsExecutionLockHeld);
        Assert.False(RootedDotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(StorageRoot, 0));
        Assert.False(RootedDotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(StorageRoot, 1));
        Assert.Equal(new[] { gate.Environment.LeaseId, worker1.LeaseId },
            DotnetBuildEnvironmentManager.ReadStableSlotBusyLeaseIds(StorageRoot));
    }

    [Fact]
    public void ExpiryReleasesEverySlotAndDisposeIsIdempotent()
    {
        var clock = new RecordingTimeProvider();
        var maximum = TimeSpan.FromSeconds(1);
        using var reservation = Take(clock, maximum);
        Assert.Equal(2, reservation.HeldSlotCount);
        Assert.False(reservation.ObserveExpiry());
        clock.Advance(maximum + TimeSpan.FromTicks(1));

        Assert.True(reservation.ObserveExpiry());
        Assert.True(reservation.IsExpired);
        Assert.Equal(0, reservation.HeldSlotCount);
        reservation.Dispose();
        Assert.Equal(1, reservation.ReleaseCount);
        AssertWorkersAcquire();
    }

    [Fact]
    public void DisposeReleasesEverySlotBeforeExpiry()
    {
        using var reservation = Take(new RecordingTimeProvider());
        Assert.Equal(2, reservation.HeldSlotCount);
        reservation.Dispose();

        Assert.False(reservation.IsExpired);
        Assert.Equal(0, reservation.HeldSlotCount);
        AssertWorkersAcquire();
    }

    [Fact]
    public void BusyPriorityFileLeavesABestEffortReservationWithoutDelay()
    {
        var path = DotnetBuildEnvironmentManager.AcceptancePriorityLockPath(0, StorageRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var existing = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        existing.Lock(0, 1);
        try
        {
            var sleeps = 0;
            using var reservation = AcceptanceSlotPriorityReservation.Take(StorageRoot, new RecordingTimeProvider(),
                TimeSpan.FromSeconds(50), (_, _) => { sleeps++; return Task.CompletedTask; });
            Assert.Equal(1, reservation.ReservedSlotCount);
            Assert.Equal(1, reservation.HeldSlotCount);
            Assert.Equal(0, sleeps);
            using var second = Take(new RecordingTimeProvider());
            Assert.Equal(0, second.HeldSlotCount);
            using var gate = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.Zero, storageRoot: StorageRoot);
            Assert.True(gate.IsExecutionLockHeld);
        }
        finally
        {
            existing.Unlock(0, 1);
        }
    }

    private AcceptanceSlotPriorityReservation Take(RecordingTimeProvider clock, TimeSpan? maximum = null) =>
        AcceptanceSlotPriorityReservation.Take(StorageRoot, clock, maximum ?? TimeSpan.FromSeconds(50),
            (duration, _) => { clock.Advance(duration); return Task.CompletedTask; });

    private void AssertWorkersAcquire()
    {
        for (var slot = 0; slot < DotnetBuildEnvironmentManager.StableSlotCount; slot++)
        {
            var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, slot);
            using var worker = DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(environment,
                timeProvider: new RecordingTimeProvider(), sleep: _ => throw new InvalidOperationException("Unexpected wait."),
                timeout: TimeSpan.Zero);
            Assert.Equal(slot, worker.Environment.BuildPermitIndex);
        }
    }
}
