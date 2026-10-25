using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsPermitWaitQueue : DotnetBuildEnvironmentManagerRootedTestBase
{
    private static BuildPermitClaimant Claim(string key, BuildPermitClaimantKind kind = BuildPermitClaimantKind.Gate,
        int? pid = null, bool ownerBound = false) => new(key, kind, pid ?? Environment.ProcessId, ownerBound);

    [Fact]
    public void RefreshPreservesIdentityAndFirstWaitAcrossQueueInstances()
    {
        var clock = new RecordingTimeProvider();
        var first = Claim("gate:first");
        var queue = new BuildPermitWaitQueue(StorageRoot, clock, _ => true);
        var original = queue.Register(first);
        clock.Advance(TimeSpan.FromMinutes(1));
        var refreshed = new BuildPermitWaitQueue(StorageRoot, clock, _ => true).Register(
            first with { Kind = BuildPermitClaimantKind.WorkerBuild, OwnerPid = 999999, OwnerBound = true });
        Assert.Equal(original with { LastRefreshedAt = clock.GetUtcNow() }, refreshed);
        Assert.Equal(refreshed, JsonSerializer.Deserialize<BuildPermitWaitQueue.Ticket>(File.ReadAllText(queue.TicketPath(first.Key))));
        Assert.Equal(16, Path.GetFileNameWithoutExtension(queue.TicketPath(first.Key)).Length);
    }

    [Fact]
    public void OrderingUsesFirstWaitThenOrdinalKey()
    {
        var clock = new RecordingTimeProvider();
        var queue = new BuildPermitWaitQueue(StorageRoot, clock, _ => true);
        queue.Register(Claim("z-first"));
        clock.Advance(TimeSpan.FromTicks(1));
        queue.Register(Claim("b-second"));
        queue.Register(Claim("a-second"));
        Assert.Equal(new[] { "z-first", "a-second", "b-second" }, queue.Snapshot().Select(t => t.Key));
    }

    [Fact]
    public void GatePrecedenceStartsAtExactlyFiveMinutes()
    {
        var clock = new RecordingTimeProvider();
        var queue = new BuildPermitWaitQueue(StorageRoot, clock, _ => true);
        var worker = Claim("worker", BuildPermitClaimantKind.WorkerBuild, ownerBound: true);
        var gate = Claim("gate");
        queue.Register(worker);
        clock.Advance(TimeSpan.FromMinutes(1));
        queue.Register(gate);
        clock.Advance(TimeSpan.FromMinutes(2));
        queue.Register(worker);
        queue.Register(gate);
        clock.Advance(TimeSpan.FromMinutes(2));
        queue.Register(worker);
        queue.Register(gate);
        clock.Advance(TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1));
        Assert.Equal(new[] { worker.Key, gate.Key }, queue.Snapshot().Select(t => t.Key));
        Assert.False(queue.Admit(gate, 1));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(new[] { gate.Key, worker.Key }, queue.Snapshot().Select(t => t.Key));
        Assert.True(queue.Admit(gate, 1));
        Assert.False(queue.Admit(worker, 1));
    }

    [Fact]
    public void StaleBoundaryDeletesTicketAndRegistrationRestartsWaiting()
    {
        var clock = new RecordingTimeProvider();
        var queue = new BuildPermitWaitQueue(StorageRoot, clock, _ => true);
        var claimant = Claim("stale");
        var original = queue.Register(claimant);
        clock.Advance(BuildPermitWaitQueue.TicketStaleAfter - TimeSpan.FromTicks(1));
        Assert.Single(queue.Snapshot());
        Assert.True(File.Exists(queue.TicketPath(claimant.Key)));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Empty(queue.Snapshot());
        Assert.False(File.Exists(queue.TicketPath(claimant.Key)));
        Assert.Equal(clock.GetUtcNow(), queue.Register(claimant).FirstWaitedAt);
        Assert.NotEqual(original.FirstWaitedAt, queue.Snapshot()[0].FirstWaitedAt);
    }

    [Fact]
    public void DeadOwnerBoundTicketIsRemovedButGateSurvivesItsAttemptPid()
    {
        var clock = new RecordingTimeProvider();
        var alive = new Dictionary<int, bool> { [123] = true };
        var queue = new BuildPermitWaitQueue(StorageRoot, clock, pid => alive.GetValueOrDefault(pid));
        var worker = Claim("worker", BuildPermitClaimantKind.WorkerBuild, 123, true);
        var gate = Claim("gate", pid: 123);
        queue.Register(worker);
        queue.Register(gate);
        alive[123] = false;
        Assert.Equal(gate.Key, Assert.Single(queue.Snapshot()).Key);
        Assert.False(File.Exists(queue.TicketPath(worker.Key)));
        Assert.True(File.Exists(queue.TicketPath(gate.Key)));
    }

    [Fact]
    public void CorruptTicketIsIgnoredAndRemovedOnlyAfterItsOwnExpiry()
    {
        var clock = new RecordingTimeProvider();
        var queue = new BuildPermitWaitQueue(StorageRoot, clock, _ => true);
        var claimant = Claim("corrupt");
        queue.Register(claimant);
        var path = queue.TicketPath(claimant.Key);
        File.WriteAllText(path, "{partial");
        File.SetLastWriteTimeUtc(path, clock.GetUtcNow().UtcDateTime);
        Assert.Empty(queue.Snapshot());
        Assert.True(queue.Admit(Claim("other"), 0));
        clock.Advance(BuildPermitWaitQueue.TicketStaleAfter - TimeSpan.FromTicks(1));
        Assert.Empty(queue.Snapshot());
        Assert.True(File.Exists(path));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Empty(queue.Snapshot());
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 1, true)]
    [InlineData(0, 2, true)]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(1, 2, true)]
    [InlineData(2, 0, false)]
    [InlineData(2, 1, false)]
    [InlineData(2, 2, false)]
    public void AdmissionUsesAheadVersusUnheld(int ahead, int unheld, bool admitted)
    {
        var clock = new RecordingTimeProvider();
        var queue = new BuildPermitWaitQueue(StorageRoot, clock, _ => true);
        for (var i = 0; i < ahead; i++)
        {
            queue.Register(Claim("ahead-" + i));
            clock.Advance(TimeSpan.FromTicks(1));
        }
        var claimant = Claim("claimant");
        queue.Register(claimant);
        Assert.Equal(admitted, queue.Admit(claimant, unheld));
    }

    [Fact]
    public void UnavailableQueueMatchesBaselinePermitWaitAndAcquisition()
    {
        var prior = DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests;
        DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () => new(new Dictionary<int, string>());
        try
        {
            var directory = Path.Combine(StorageRoot.RootPath, "build-slots", "wait-tickets");
            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            File.WriteAllText(directory, "not a directory");
            using var first = Hold(0);
            using var second = Hold(1);
            var environment = DotnetBuildEnvironmentManager.CreateAttempt(GoalId.New(), "fallback", storageRoot: StorageRoot);
            var baselineClock = new RecordingTimeProvider();
            var baselineStart = baselineClock.GetUtcNow();
            var baselineWaits = 0;
            var baseline = DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(environment,
                TimeSpan.FromSeconds(1), timeProvider: baselineClock, sleep: baselineClock.Advance, onWait: () => baselineWaits++);
            var clock = new RecordingTimeProvider();
            var start = clock.GetUtcNow();
            var waits = 0;
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() => result =
                DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(environment,
                    TimeSpan.FromSeconds(1), timeProvider: clock, sleep: clock.Advance,
                    onWait: () => waits++, claimant: Claim("fallback")));
            Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(baseline);
            Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(result);
            Assert.Equal(baselineClock.GetUtcNow() - baselineStart, clock.GetUtcNow() - start);
            Assert.Equal(1, baselineWaits);
            Assert.Equal(baselineWaits, waits);
            Assert.Single(output.Split('\n').Where(line => line.StartsWith("BUILD_PERMIT_QUEUE_UNAVAILABLE ", StringComparison.Ordinal)));
            Assert.DoesNotContain("PERMIT_QUEUE_DEFER", output);
            first.Dispose();
            using var acquiredBaseline = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(environment, TimeSpan.Zero)).Lease;
            var expectedPermit = acquiredBaseline.Environment.BuildPermitIndex;
            acquiredBaseline.Dispose();
            using var acquired = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(environment, TimeSpan.Zero, claimant: Claim("fallback"))).Lease;
            Assert.Equal(expectedPermit, acquired.Environment.BuildPermitIndex);
        }
        finally { DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = prior; }
    }

    [Fact]
    public void FailedTicketRemovalKeepsAcquiredLeaseAndDoesNotBlockNextClaimant()
    {
        var prior = DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests;
        DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () => new(new Dictionary<int, string>());
        try
        {
            var clock = new RecordingTimeProvider();
            var queue = new BuildPermitWaitQueue(StorageRoot, clock, _ => true);
            var claimant = Claim("a-gate");
            queue.Register(claimant);
            // Force a refresh write before acquisition, independently of poll-write throttling.
            clock.Advance(TimeSpan.FromSeconds(1));
            using var denyRemoval = new FileStream(queue.TicketPath(claimant.Key), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite);
            var environment = DotnetBuildEnvironmentManager.CreateAttempt(GoalId.New(), "removal-failure", storageRoot: StorageRoot);
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() => result =
                DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(environment, TimeSpan.Zero,
                    timeProvider: clock, claimant: claimant));
            using var lease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(result).Lease;
            Assert.True(File.Exists(queue.TicketPath(claimant.Key)));
            Assert.Contains("BUILD_PERMIT_QUEUE_UNAVAILABLE", output);
            Assert.Contains("BUILD_PERMIT_QUEUE_REMOVAL_FAILED", output);
            Assert.False(DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(lease.Environment.BuildPermitIndex!.Value, StorageRoot));
            using var next = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(environment, TimeSpan.Zero,
                    timeProvider: clock, claimant: Claim("b-worker", BuildPermitClaimantKind.WorkerBuild, ownerBound: true))).Lease;
            Assert.NotEqual(lease.Environment.BuildPermitIndex, next.Environment.BuildPermitIndex);
            denyRemoval.Dispose();
            lease.Dispose();
            var thirdClaimant = Claim("c-worker", BuildPermitClaimantKind.WorkerBuild, ownerBound: true);
            using var third = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(environment, TimeSpan.Zero,
                    timeProvider: clock, claimant: thirdClaimant)).Lease;
            Assert.False(File.Exists(queue.TicketPath(claimant.Key)));
            Assert.False(File.Exists(queue.TicketPath(claimant.Key) + ".satisfied"));
        }
        finally { DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = prior; }
    }

    [Fact]
    public void PollRefreshCoalescesWritesButPublicRegistrationAlwaysPersistsRefresh()
    {
        var clock = new RecordingTimeProvider();
        var queue = new BuildPermitWaitQueue(StorageRoot, clock, _ => true);
        var claimant = Claim("gate:refresh");
        using var waiter = new BuildPermitWaitQueue.Waiter(queue, claimant);
        Assert.True(waiter.CanScan(() => 0));
        var first = JsonSerializer.Deserialize<BuildPermitWaitQueue.Ticket>(File.ReadAllText(queue.TicketPath(claimant.Key)))!;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(waiter.CanScan(() => 0));
        Assert.Equal(first, JsonSerializer.Deserialize<BuildPermitWaitQueue.Ticket>(File.ReadAllText(queue.TicketPath(claimant.Key))));
        clock.Advance(TimeSpan.FromMilliseconds(900));
        Assert.True(waiter.CanScan(() => 0));
        var refreshed = JsonSerializer.Deserialize<BuildPermitWaitQueue.Ticket>(File.ReadAllText(queue.TicketPath(claimant.Key)))!;
        Assert.Equal(first.FirstWaitedAt, refreshed.FirstWaitedAt);
        Assert.Equal(clock.GetUtcNow(), refreshed.LastRefreshedAt);
        clock.Advance(TimeSpan.FromTicks(1));
        var registered = queue.Register(claimant);
        Assert.Equal(clock.GetUtcNow(), registered.LastRefreshedAt);
        Assert.Equal(registered, JsonSerializer.Deserialize<BuildPermitWaitQueue.Ticket>(File.ReadAllText(queue.TicketPath(claimant.Key))));
    }

    [Fact]
    public async Task QueueLockCollisionRetriesAndPreservesOlderHeadInsteadOfBypassingAdmission()
    {
        var clock = new RecordingTimeProvider();
        var head = Claim("a-gate");
        var later = Claim("b-worker", BuildPermitClaimantKind.WorkerBuild, ownerBound: true);
        var baselineQueue = new BuildPermitWaitQueue(StorageRoot, clock, _ => true);
        baselineQueue.Register(head);
        clock.Advance(TimeSpan.FromSeconds(1));
        using (var baseline = new BuildPermitWaitQueue.Waiter(baselineQueue, later))
            Assert.False(baseline.CanScan(() => 1));

        var collided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var released = new ManualResetEventSlim();
        var queue = new BuildPermitWaitQueue(StorageRoot, clock, _ => true)
        {
            SynchronizationContended = () =>
            {
                collided.TrySetResult();
                if (!released.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("Test did not release its queue lock");
            }
        };
        using var guard = new FileStream(Path.Combine(Path.GetDirectoryName(queue.TicketPath(head.Key))!, ".queue.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        string? output = null;
        var scanCount = 0;
        var pending = Task.Run(() =>
        {
            using var waiter = new BuildPermitWaitQueue.Waiter(queue, later);
            var admitted = true;
            output = AsyncLocalConsoleRouter.Capture(() =>
                admitted = waiter.CanScan(() => { scanCount++; return 1; }));
            return admitted;
        });
        try
        {
            await collided.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            guard.Dispose();
            released.Set();
        }
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, scanCount);
        Assert.Contains("PERMIT_QUEUE_DEFER", output);
        Assert.Contains("head=a-gate", output);
        Assert.DoesNotContain("BUILD_PERMIT_QUEUE_UNAVAILABLE", output);
        Assert.Equal(head.Key, Assert.Single(queue.Snapshot()).Key);
    }

    [Fact]
    public void QueueLockContendedRemovalPublishesSatisfactionAndNeverRevivesConsumedTicket()
    {
        var prior = DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests;
        DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () => new(new Dictionary<int, string>());
        try
        {
            var clock = new RecordingTimeProvider();
            var claimant = Claim("a-gate");
            var collisions = 0;
            var queue = new BuildPermitWaitQueue(StorageRoot, clock, _ => true)
            {
                SynchronizationContended = () => collisions++
            };
            queue.Register(claimant);
            var environment = DotnetBuildEnvironmentManager.CreateAttempt(GoalId.New(), "contended-removal", storageRoot: StorageRoot);
            var acquired = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(environment, TimeSpan.Zero));
            using var lease = acquired.Lease;
            using var waiter = new BuildPermitWaitQueue.Waiter(queue, claimant);
            using (var guard = new FileStream(Path.Combine(Path.GetDirectoryName(queue.TicketPath(claimant.Key))!, ".queue.lock"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                DotnetBuildLeaseAcquisition? completed = null;
                var output = AsyncLocalConsoleRouter.Capture(() => completed = waiter.Complete(acquired));
                Assert.Same(acquired, completed);
                Assert.True(collisions > 0);
                Assert.Contains("BUILD_PERMIT_QUEUE_REMOVAL_FAILED", output);
                Assert.DoesNotContain("BUILD_PERMIT_QUEUE_SATISFACTION_FAILED", output);
                Assert.True(File.Exists(queue.TicketPath(claimant.Key)));
                Assert.True(File.Exists(queue.TicketPath(claimant.Key) + ".satisfied"));
                Assert.False(DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(lease.Environment.BuildPermitIndex!.Value, StorageRoot));
            }
            using var next = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(environment, TimeSpan.Zero,
                    timeProvider: clock, claimant: Claim("b-worker", BuildPermitClaimantKind.WorkerBuild, ownerBound: true))).Lease;
            Assert.NotEqual(lease.Environment.BuildPermitIndex, next.Environment.BuildPermitIndex);
            lease.Dispose();
            using var third = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(environment, TimeSpan.Zero,
                    timeProvider: clock, claimant: Claim("c-worker", BuildPermitClaimantKind.WorkerBuild, ownerBound: true))).Lease;
            Assert.Equal(lease.Environment.BuildPermitIndex, third.Environment.BuildPermitIndex);
            Assert.False(File.Exists(queue.TicketPath(claimant.Key)));
            Assert.False(File.Exists(queue.TicketPath(claimant.Key) + ".satisfied"));
        }
        finally { DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = prior; }
    }

    [Theory]
    [InlineData(BuildPermitClaimantKind.Gate, true)]
    [InlineData(BuildPermitClaimantKind.FocusedEvidence, true)]
    [InlineData(BuildPermitClaimantKind.WorkerBuild, false)]
    public void TimeoutAndCancellationFollowClaimantTicketLifetime(BuildPermitClaimantKind kind, bool retained)
    {
        var prior = DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests;
        DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () => new(new Dictionary<int, string>());
        try
        {
            using var first = Hold(0);
            using var second = Hold(1);
            var clock = new RecordingTimeProvider();
            var queue = new BuildPermitWaitQueue(StorageRoot, clock);
            var claimant = Claim("lifetime", kind, ownerBound: kind == BuildPermitClaimantKind.WorkerBuild);
            var environment = DotnetBuildEnvironmentManager.CreateAttempt(GoalId.New(), "lifetime", storageRoot: StorageRoot);
            Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(
                DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(environment, TimeSpan.Zero,
                    timeProvider: clock, claimant: claimant));
            Assert.Equal(retained, File.Exists(queue.TicketPath(claimant.Key)));
            using var cancellation = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() =>
                DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(environment, TimeSpan.FromSeconds(1),
                    cancellation.Token, timeProvider: clock, claimant: claimant,
                    sleep: _ => cancellation.Cancel()));
            Assert.Equal(retained, File.Exists(queue.TicketPath(claimant.Key)));
        }
        finally { DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = prior; }
    }

    [Fact]
    public void StableSlotAcquisitionHonorsOlderTicketAndLeavesFreePermitUnheld()
    {
        var prior = DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests;
        DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () => new(new Dictionary<int, string>());
        try
        {
            using var held = Hold(1);
            var clock = new RecordingTimeProvider();
            var queue = new BuildPermitWaitQueue(StorageRoot, clock);
            var worker = Claim("worker", BuildPermitClaimantKind.WorkerBuild, ownerBound: true);
            queue.Register(worker);
            clock.Advance(TimeSpan.FromSeconds(1));
            var gate = Claim("gate");
            var waits = 0;
            var result = DotnetBuildEnvironmentManager.TryAcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(1), _ => waits++, timeProvider: clock, sleep: clock.Advance,
                storageRoot: StorageRoot, claimant: gate);
            Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(result);
            Assert.Equal(1, waits);
            Assert.True(DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(0, StorageRoot));
            Assert.True(File.Exists(queue.TicketPath(gate.Key)));
            using var lease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireFirstAvailableStableSlotExecutionLock(
                    TimeSpan.Zero, timeProvider: clock, storageRoot: StorageRoot, claimant: worker)).Lease;
            Assert.Equal(0, lease.Environment.BuildPermitIndex);
            Assert.False(File.Exists(queue.TicketPath(worker.Key)));
        }
        finally { DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = prior; }
    }

    private DotnetBuildEnvironmentLease Hold(int slot) => Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
        DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
            DotnetBuildEnvironmentManager.CreateStableSlotAttempt(slot, storageRoot: StorageRoot), TimeSpan.Zero)).Lease;
}
