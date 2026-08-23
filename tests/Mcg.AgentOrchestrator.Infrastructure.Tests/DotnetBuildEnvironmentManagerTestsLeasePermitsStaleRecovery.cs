using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;
using static DotnetBuildEnvironmentManagerTests;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsLeasePermitsStaleRecovery
{
    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_serializes_same_goal_lease_execution")]
    public async Task DotnetBuildEnvironmentManagerSerializesSameGoalLeaseExecution()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var goalId = new GoalId("1234abcd1234abcd1234abcd1234abcd");
        try
        {
            var first = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "developer");
            var second = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "tester");
            using var firstLock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(first);
            var secondLockTask = Task.Run(() => DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(second));
            var earlyWinner = await Task.WhenAny(secondLockTask, Task.Delay(200));
            Xunit.Assert.NotEqual(secondLockTask, earlyWinner);

            firstLock.Dispose();
            using var secondLock = await secondLockTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(first.ExecutionLockPath, second.ExecutionLockPath);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_goal_build_permit_is_deterministic_and_guards_goal_artifacts")]
    public void DotnetBuildEnvironmentManagerGoalBuildPermitIsDeterministicAndGuardsGoalArtifacts()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var goalId = new GoalId("89abcdef89abcdef89abcdef89abcdef");
        try
        {
            var gate = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "gate");
            var worker = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "worker-build-check");
            var expectedPermit = goalId.Value[..8]
                .ToLowerInvariant()
                .Sum(ch => (int)ch) %
                DotnetBuildEnvironmentManager.BuildConcurrencySlotCount;

            Assert.Equal(expectedPermit, gate.BuildPermitIndex);
            Assert.Equal(gate.BuildPermitIndex, worker.BuildPermitIndex);
            Assert.Equal(gate.ExecutionLockPath, worker.ExecutionLockPath);
            Assert.Equal(gate.ArtifactsPath, worker.ArtifactsPath);

            using var gateLease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(gate, TimeSpan.Zero)).Lease;
            var blocked = Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(
                DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(worker, TimeSpan.Zero));
            Assert.Contains(blocked.BusySlots, slot => slot.SlotIndex == expectedPermit);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_first_available_build_permit_scans_past_busy_preferred_permit")]
    public void DotnetBuildEnvironmentManagerFirstAvailableBuildPermitScansPastBusyPreferredPermit()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var firstGoalId = new GoalId("89abcdef89abcdef89abcdef89abcdef");
        var secondGoalId = new GoalId("98abcdef98abcdef98abcdef98abcdef");
        try
        {
            var first = DotnetBuildEnvironmentManager.CreateAttempt(firstGoalId, "first");
            var second = DotnetBuildEnvironmentManager.CreateAttempt(secondGoalId, "second");
            Assert.Equal(first.BuildPermitIndex, second.BuildPermitIndex);

            using var firstLease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(first, TimeSpan.Zero)).Lease;
            using var secondLease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(second, TimeSpan.Zero)).Lease;

            Assert.NotEqual(firstLease.Environment.BuildPermitIndex, secondLease.Environment.BuildPermitIndex);
            Assert.Equal(second.ArtifactsPath, secondLease.Environment.ArtifactsPath);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(firstGoalId);
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(secondGoalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_first_available_build_permit_reserves_priority_while_waiting")]
    public void DotnetBuildEnvironmentManagerFirstAvailableBuildPermitReservesPriorityWhileWaiting()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateAttempt(
            new GoalId("89abcdef89abcdef89abcdef89abcdef"),
            "priority-scan");
        using var lease0 = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
            DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0),
                TimeSpan.Zero)).Lease;
        using var lease1 = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
            DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                DotnetBuildEnvironmentManager.CreateStableSlotAttempt(1),
                TimeSpan.Zero)).Lease;

        var acquisition = DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(
            environment,
            TimeSpan.Zero,
            onWait: () =>
            {
                Assert.True(IsByteRangeLocked(lease0.Environment.ExecutionLockPath + ".acceptance-priority.lock"));
                Assert.True(IsByteRangeLocked(lease1.Environment.ExecutionLockPath + ".acceptance-priority.lock"));
            });

        Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(acquisition);
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_early_release_shuts_down_build_servers_before_releasing_permit")]
    public void DotnetBuildEnvironmentManagerEarlyReleaseShutsDownBuildServersBeforeReleasingPermit()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        var shutdowns = 0;
        DotnetBuildEnvironmentManager.ShutdownBuildServersForTests = () => Interlocked.Increment(ref shutdowns);
        try
        {
            using var lease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.Zero)).Lease;

            lease.ReleaseExecutionLock();
            lease.ReleaseExecutionLock();

            Assert.Equal(1, shutdowns);
            Assert.True(DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(0));
        }
        finally
        {
            DotnetBuildEnvironmentManager.ShutdownBuildServersForTests =
                AssemblyBuildServerShutdownIsolation.SafeDefault;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_execution_lock_release_observer_runs_once_after_permit_release")]
    public void DotnetBuildEnvironmentManagerExecutionLockReleaseObserverRunsOnceAfterPermitRelease()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        var notifications = 0;
        bool? permitAvailableDuringNotification = null;
        DotnetBuildEnvironmentManager.ShutdownBuildServersForTests = () => { };
        try
        {
            var lease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.Zero)).Lease;
            lease.RegisterExecutionLockReleaseObserver(() =>
            {
                permitAvailableDuringNotification =
                    DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(0);
                notifications++;
            });

            lease.ReleaseExecutionLock();
            lease.ReleaseExecutionLock();
            lease.Dispose();

            Assert.True(permitAvailableDuringNotification);
            Assert.Equal(1, notifications);
        }
        finally
        {
            DotnetBuildEnvironmentManager.ShutdownBuildServersForTests =
                AssemblyBuildServerShutdownIsolation.SafeDefault;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_execution_lock_release_observer_is_advisory")]
    public void DotnetBuildEnvironmentManagerExecutionLockReleaseObserverIsAdvisory()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        DotnetBuildEnvironmentManager.ShutdownBuildServersForTests = () => { };
        try
        {
            var lease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.Zero)).Lease;
            lease.RegisterExecutionLockReleaseObserver(() => throw new InvalidOperationException("telemetry failed"));

            var exception = Record.Exception(lease.ReleaseExecutionLock);

            Assert.Null(exception);
            Assert.True(DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(0));
        }
        finally
        {
            DotnetBuildEnvironmentManager.ShutdownBuildServersForTests =
                AssemblyBuildServerShutdownIsolation.SafeDefault;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_dispose_shuts_down_build_servers_before_releasing_permit")]
    public void DotnetBuildEnvironmentManagerDisposeShutsDownBuildServersBeforeReleasingPermit()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        bool? permitAvailableDuringShutdown = null;
        DotnetBuildEnvironmentManager.ShutdownBuildServersForTests = () =>
            permitAvailableDuringShutdown =
                DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(0);
        try
        {
            var lease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.Zero)).Lease;

            lease.Dispose();

            Assert.False(permitAvailableDuringShutdown ?? true);
            Assert.True(DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(0));
        }
        finally
        {
            DotnetBuildEnvironmentManager.ShutdownBuildServersForTests =
                AssemblyBuildServerShutdownIsolation.SafeDefault;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_success_cleanup_removes_only_invocation_run_roots")]
    public void DotnetBuildEnvironmentManagerSuccessCleanupRemovesOnlyInvocationRunRoots()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var run = DotnetBuildEnvironmentManager.CreateAttempt(null, "operator-success");
        var goalId = new GoalId("76543210765432107654321076543210");
        var goal = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "gate");
        try
        {
            File.WriteAllText(Path.Combine(run.ArtifactsPath, "receipt.txt"), "success");

            Assert.True(DotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(run));
            Assert.False(Directory.Exists(run.RootPath));
            Assert.False(DotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(goal));
            Assert.True(Directory.Exists(goal.RootPath));
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_first_available_stable_slot_skips_leased_slot_zero")]
    public void DotnetBuildEnvironmentManagerFirstAvailableStableSlotSkipsLeasedSlotZero()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var slot0 = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        using var slot0Lock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(slot0);

        using var selected = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(1));

        Assert.Equal("build-1", selected.Environment.SlotOwnerToken);
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_goal_gate_skips_worker_held_slot_zero")]
    public void DotnetBuildEnvironmentManagerGoalGateSkipsWorkerHeldSlotZero()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var workerSlot0 = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        using var workerLock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(workerSlot0);
        var gateGoalId = new GoalId("90000000900000009000000090000000");
        DotnetBuildEnvironment? gateEnvironment = null;

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            gateEnvironment = DotnetBuildEnvironmentManager.CreateAttempt(gateGoalId, "gate");
            using var gateLock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(gateEnvironment, TimeSpan.FromSeconds(1));
        });

        Assert.NotNull(gateEnvironment);
        Assert.Contains(Path.Combine("build-slots", "build-1.lock"), gateEnvironment.ExecutionLockPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LEASE_ACQUIRE", output);
        Assert.Contains("slot=build-1", output);
        Assert.Contains("lease=goal-90000000", output);
        Assert.Equal(1, CountOccurrences(output, "LEASE_RELEASE"));
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_reused_goal_keeps_per_goal_artifacts_and_build_pool_lock")]
    public void DotnetBuildEnvironmentManagerReusedGoalKeepsPerGoalArtifactsAndBuildPoolLock()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var gateGoalId = new GoalId("91000000910000009100000091000000");
        var first = DotnetBuildEnvironmentManager.CreateAttempt(gateGoalId, "first");
        var reused = DotnetBuildEnvironmentManager.CreateAttempt(gateGoalId, "gate");

        Assert.NotNull(reused);
        Assert.True(reused.ReusedGoalLease);
        Assert.Equal(first.ExecutionLockPath, reused.ExecutionLockPath);
        Assert.Equal(first.ArtifactsPath, reused.ArtifactsPath);
        Assert.Equal(Path.Combine(first.RootPath, "artifacts"), reused.ArtifactsPath);
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_reclaims_dead_pid_execution_lease_without_timeout")]
    public void DotnetBuildEnvironmentManagerReclaimsDeadPidExecutionLeaseWithoutTimeout()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var slot0 = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        Directory.CreateDirectory(Path.GetDirectoryName(slot0.ExecutionLockPath)!);
        File.WriteAllText(slot0.ExecutionLockPath, "999999");

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(slot0, TimeSpan.FromSeconds(1));
        });

        Assert.Contains("LEASE_RECLAIM", output);
        Assert.Contains("reclaimedPid=999999", output);
        Assert.Contains("decision=preserved", output);
        Assert.Contains("reason=artifacts-empty", output);
        Assert.Contains("LEASE_ACQUIRE", output);
        Assert.Contains("LEASE_RELEASE", output);
        Assert.True(File.Exists(LeaseJournalPath(slot0)));
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_stale_lease_reclaim_preserves_intact_incremental_artifacts")]
    public void DotnetBuildEnvironmentManagerStaleLeaseReclaimPreservesIntactIncrementalArtifacts()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var slot0 = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(slot0))
        {
        }

        var cachedDll = Path.Combine(slot0.ArtifactsPath, "bin", "Sample", "debug_net10.0", "Sample.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(cachedDll)!);
        WriteValidPeFile(cachedDll);
        var cachedBytes = File.ReadAllBytes(cachedDll);
        File.SetLastWriteTimeUtc(cachedDll, DateTime.UtcNow.AddMinutes(-5));
        var cachedWriteTime = File.GetLastWriteTimeUtc(cachedDll);
        File.WriteAllText(slot0.ExecutionLockPath, "999999");

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(slot0, TimeSpan.FromSeconds(1));
        });

        Assert.True(File.Exists(cachedDll));
        Assert.Equal(cachedBytes, File.ReadAllBytes(cachedDll));
        Assert.Equal(cachedWriteTime, File.GetLastWriteTimeUtc(cachedDll));
        Assert.Contains("decision=preserved", output);
        Assert.Contains("reason=integrity-ok", output);
        using var journal = ReadLastLeaseJournalEntry(slot0);
        Assert.Equal("preserved", journal.RootElement.GetProperty("decision").GetString());
        Assert.Equal("integrity-ok", journal.RootElement.GetProperty("reason").GetString());
    }

    [Xunit.Theory(DisplayName = "DotnetBuildEnvironmentManager_stale_lease_reclaim_wipes_torn_artifacts")]
    [Xunit.InlineData("missing-owner-marker")]
    [Xunit.InlineData("zero-length-dll")]
    [Xunit.InlineData("invalid-pe-dll")]
    public void DotnetBuildEnvironmentManagerStaleLeaseReclaimWipesTornArtifacts(string tornWrite)
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var slot0 = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(slot0))
        {
        }

        var sentinel = Path.Combine(slot0.ArtifactsPath, "cache-sentinel.txt");
        File.WriteAllText(sentinel, "must be cleared");
        if (tornWrite == "missing-owner-marker")
        {
            File.Delete(Path.Combine(slot0.ArtifactsPath, ".mcg-artifacts-owner.json"));
        }
        else
        {
            var tornDll = Path.Combine(slot0.ArtifactsPath, "bin", "Sample", "debug_net10.0", "Sample.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(tornDll)!);
            File.WriteAllBytes(tornDll, tornWrite == "zero-length-dll" ? [] : "MZ truncated"u8.ToArray());
        }

        File.WriteAllText(slot0.ExecutionLockPath, "999999");
        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(slot0, TimeSpan.FromSeconds(1));
        });

        Assert.False(File.Exists(sentinel));
        Assert.Contains("decision=wiped", output);
        Assert.Contains($"reason={tornWrite}", output);
        using var journal = ReadLastLeaseJournalEntry(slot0);
        Assert.Equal("wiped", journal.RootElement.GetProperty("decision").GetString());
        Assert.Equal(tornWrite, journal.RootElement.GetProperty("reason").GetString());
        Assert.Contains(
            tornWrite == "missing-owner-marker" ? ".mcg-artifacts-owner.json" : "Sample.dll",
            journal.RootElement.GetProperty("triggerPath").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_stale_lease_failed_wipe_records_observed_failure")]
    public void DotnetBuildEnvironmentManagerStaleLeaseFailedWipeRecordsObservedFailure()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment))
        {
        }

        var tornDll = Path.Combine(environment.ArtifactsPath, "bin", "Sample.dll");
        var lockedPath = Path.Combine(environment.ArtifactsPath, "locked-cache.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(tornDll)!);
        File.WriteAllBytes(tornDll, []);
        File.WriteAllText(lockedPath, "held by foreign process");
        using var held = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        File.WriteAllText(environment.ExecutionLockPath, "999999");
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(987654322, "foreign", "foreign cache reader", false)],
            "test");

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                    environment,
                    TimeSpan.Zero));

            Assert.IsType<DotnetBuildLeaseAcquisition.BuildLockBlocked>(result);
            Assert.True(File.Exists(lockedPath));
            Assert.Contains("LEASE_RECLAIM", output, StringComparison.Ordinal);
            Assert.Contains("decision=wipe-failed", output, StringComparison.Ordinal);
            Assert.Contains("reason=wipe-failed", output, StringComparison.Ordinal);
            using var journal = ReadLastLeaseJournalEntry(environment);
            Assert.Equal("wipe-failed", journal.RootElement.GetProperty("decision").GetString());
            Assert.Equal("wipe-failed", journal.RootElement.GetProperty("reason").GetString());
        }
        finally
        {
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_stale_lease_wipe_decision_survives_artifact_prep_retry")]
    public void DotnetBuildEnvironmentManagerStaleLeaseWipeDecisionSurvivesArtifactPrepRetry()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment))
        {
        }

        var sentinel = Path.Combine(environment.ArtifactsPath, "must-be-cleared.txt");
        var tornDll = Path.Combine(environment.ArtifactsPath, "bin", "Sample.dll");
        File.WriteAllText(sentinel, "torn cache");
        Directory.CreateDirectory(Path.GetDirectoryName(tornDll)!);
        File.WriteAllBytes(tornDll, []);
        File.WriteAllText(environment.ExecutionLockPath, "999999");
        var lockedPath = Path.Combine(environment.ArtifactsPath, "locked.dll");
        var prepareAttempts = 0;
        var originalKill = WorkerProcessJobs.TryKillPidTree;
        DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = current =>
        {
            if (current.ExecutionLockPath == environment.ExecutionLockPath &&
                Interlocked.Increment(ref prepareAttempts) == 1)
            {
                throw new UnauthorizedAccessException($"Access to the path '{lockedPath}' is denied.");
            }
        };
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(987654321, "testhost", "dotnet test --artifacts-path slot-0", true)],
            "test");
        WorkerProcessJobs.TryKillPidTree = _ => true;

        try
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(
                environment,
                TimeSpan.FromSeconds(1));

            Assert.Equal(2, prepareAttempts);
            Assert.False(File.Exists(sentinel));
            Assert.False(File.Exists(tornDll));
        }
        finally
        {
            WorkerProcessJobs.TryKillPidTree = originalKill;
            DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = null;
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_rechecks_stale_lease_after_busy_poll")]
    public void DotnetBuildEnvironmentManagerRechecksStaleLeaseAfterBusyPoll()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment))
        {
        }

        var sentinel = Path.Combine(environment.ArtifactsPath, "must-be-cleared.txt");
        var tornDll = Path.Combine(environment.ArtifactsPath, "bin", "Sample.dll");
        File.WriteAllText(sentinel, "torn cache");
        Directory.CreateDirectory(Path.GetDirectoryName(tornDll)!);
        File.WriteAllBytes(tornDll, []);
        using var sleeper = StartSleepProcess();
        File.WriteAllText(
            environment.ExecutionLockPath,
            sleeper.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () => new ProcessCommandLineSnapshot(
            new Dictionary<int, string>
            {
                [sleeper.Id] = $"testhost.exe --artifacts-path \"{environment.ArtifactsPath}\""
            });
        var stopped = false;

        try
        {
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(
                    environment,
                    TimeSpan.FromSeconds(1),
                    sleep: _ =>
                    {
                        if (!stopped)
                        {
                            StopProcess(sleeper);
                            stopped = true;
                        }
                    });
            });

            Assert.True(stopped);
            Assert.False(File.Exists(sentinel));
            Assert.False(File.Exists(tornDll));
            Assert.Contains("LEASE_RECLAIM", output, StringComparison.Ordinal);
            Assert.Contains("decision=wiped", output, StringComparison.Ordinal);
            Assert.Contains("reason=zero-length-dll", output, StringComparison.Ordinal);
        }
        finally
        {
            DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = null;
            StopProcess(sleeper);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_reclaimed_owner_validation_does_not_survive_busy_poll")]
    public void DotnetBuildEnvironmentManagerReclaimedOwnerValidationDoesNotSurviveBusyPoll()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment))
        {
        }

        File.WriteAllText(environment.ExecutionLockPath, "999999");
        using var sleeper = StartSleepProcess();
        DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () => new ProcessCommandLineSnapshot(
            new Dictionary<int, string>
            {
                [sleeper.Id] = $"testhost.exe --artifacts-path \"{environment.ArtifactsPath}\""
            });
        var foreignArtifact = Path.Combine(environment.ArtifactsPath, "foreign-owner-cache.txt");
        var stopped = false;

        try
        {
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(
                    environment,
                    TimeSpan.FromSeconds(1),
                    sleep: _ =>
                    {
                        if (!stopped)
                        {
                            StopProcess(sleeper);
                            WriteForeignOwnerMarker(environment.ArtifactsPath);
                            File.WriteAllText(foreignArtifact, "must not be reused");
                            stopped = true;
                        }
                    });
            });

            Assert.True(stopped);
            Assert.False(File.Exists(foreignArtifact));
            Assert.Contains("ARTIFACT_PREP", output, StringComparison.Ordinal);
            Assert.Contains("decision=wiped", output, StringComparison.Ordinal);
            Assert.Contains("reason=owner-marker-mismatch", output, StringComparison.Ordinal);
            using var journal = ReadLastLeaseJournalEntry(environment);
            Assert.Equal("ARTIFACT_PREP", journal.RootElement.GetProperty("event").GetString());
            Assert.Equal("wiped", journal.RootElement.GetProperty("decision").GetString());
            Assert.Equal("owner-marker-mismatch", journal.RootElement.GetProperty("reason").GetString());
        }
        finally
        {
            DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = null;
            StopProcess(sleeper);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_stale_lease_integrity_probe_retries_transient_io")]
    public void DotnetBuildEnvironmentManagerStaleLeaseIntegrityProbeRetriesTransientIo()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment))
        {
        }

        var cachedDll = Path.Combine(environment.ArtifactsPath, "bin", "Sample.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(cachedDll)!);
        WriteValidPeFile(cachedDll);
        File.WriteAllText(environment.ExecutionLockPath, "999999");
        var probeAttempts = 0;
        DotnetBuildEnvironmentManager.BeforeStaleLeaseIntegrityProbeForTests = attempt =>
        {
            probeAttempts = attempt;
            if (attempt < 3)
            {
                throw new IOException("transient probe contention");
            }
        };

        try
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(
                environment,
                TimeSpan.FromSeconds(1));

            Assert.Equal(3, probeAttempts);
            Assert.True(File.Exists(cachedDll));
        }
        finally
        {
            DotnetBuildEnvironmentManager.BeforeStaleLeaseIntegrityProbeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_stale_lease_journal_contention_is_best_effort_and_drains")]
    public void DotnetBuildEnvironmentManagerStaleLeaseJournalContentionIsBestEffortAndDrains()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment))
        {
        }

        var journalPath = LeaseJournalPath(environment);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        File.WriteAllText(environment.ExecutionLockPath, "999999");
        string output;
        using (var heldJournal = new FileStream(
            journalPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read))
        {
            output = AsyncLocalConsoleRouter.Capture(() =>
            {
                using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(
                    environment,
                    TimeSpan.FromSeconds(1));
            });
        }

        Assert.Contains("journalStatus=pending", output);
        Assert.NotEmpty(Directory.GetFiles(
            Path.GetDirectoryName(journalPath)!,
            $"{Path.GetFileName(journalPath)}.pending-*.jsonl"));

        File.WriteAllText(environment.ExecutionLockPath, "999999");
        using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment, TimeSpan.FromSeconds(1)))
        {
        }

        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(journalPath)!,
            $"{Path.GetFileName(journalPath)}.pending-*.jsonl"));
        Assert.True(File.ReadAllLines(journalPath).Length >= 2);
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_stale_lease_journal_drains_pending_entries_by_recorded_time")]
    public void DotnetBuildEnvironmentManagerStaleLeaseJournalDrainsPendingEntriesByRecordedTime()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment))
        {
        }

        var journalPath = LeaseJournalPath(environment);
        var older = DateTimeOffset.UtcNow.AddMinutes(-10);
        var newer = DateTimeOffset.UtcNow.AddMinutes(-5);
        var directory = Path.GetDirectoryName(journalPath)!;
        var fileName = Path.GetFileName(journalPath);
        File.WriteAllText(
            Path.Combine(directory, $"{fileName}.pending-z.jsonl"),
            CreateLeaseJournalEntry(older, "older") + Environment.NewLine);
        File.WriteAllText(
            Path.Combine(directory, $"{fileName}.pending-a.jsonl"),
            CreateLeaseJournalEntry(newer, "newer") + Environment.NewLine);
        File.WriteAllText(environment.ExecutionLockPath, "999999");

        using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment, TimeSpan.FromSeconds(1)))
        {
        }

        var entries = File.ReadAllLines(journalPath)
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            Assert.True(entries.Length >= 3);
            var recordedAt = entries
                .Select(entry => entry.RootElement.GetProperty("recordedAt").GetDateTimeOffset())
                .ToArray();
            Assert.Equal(recordedAt.Order().ToArray(), recordedAt);
            Assert.Equal("older", entries[0].RootElement.GetProperty("reason").GetString());
            Assert.Equal("newer", entries[1].RootElement.GetProperty("reason").GetString());
        }
        finally
        {
            foreach (var entry in entries)
            {
                entry.Dispose();
            }
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_stale_lease_journal_rotates_at_bounded_size")]
    public void DotnetBuildEnvironmentManagerStaleLeaseJournalRotatesAtBoundedSize()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment))
        {
        }

        var journalPath = LeaseJournalPath(environment);
        File.WriteAllBytes(journalPath, new byte[1_048_576]);
        File.WriteAllText(environment.ExecutionLockPath, "999999");
        using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment, TimeSpan.FromSeconds(1)))
        {
        }

        Assert.NotEmpty(Directory.GetFiles(
            Path.GetDirectoryName(journalPath)!,
            "lease.journal-*.jsonl"));
        Assert.Single(File.ReadAllLines(journalPath));
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_stale_lease_force_clean_escape_hatch_wipes_intact_artifacts")]
    public void DotnetBuildEnvironmentManagerStaleLeaseForceCleanEscapeHatchWipesIntactArtifacts()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        using var forceClean = EnvVarScope.ForVariable(
            DotnetBuildEnvironmentManager.ForceCleanStaleLeaseArtifactsVariable,
            "1");
        var slot0 = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(slot0))
        {
        }

        var cachedDll = Path.Combine(slot0.ArtifactsPath, "bin", "Sample.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(cachedDll)!);
        File.WriteAllText(cachedDll, "intact output");
        File.WriteAllText(slot0.ExecutionLockPath, "999999");

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(slot0, TimeSpan.FromSeconds(1));
        });

        Assert.False(File.Exists(cachedDll));
        Assert.Contains("decision=wiped", output);
        Assert.Contains("reason=operator-forced", output);
        Assert.Contains($"env:{DotnetBuildEnvironmentManager.ForceCleanStaleLeaseArtifactsVariable}", output);
    }

    [Xunit.Fact(DisplayName = "Invoke-IsolatedDotnet_documents_stale_lease_force_clean_escape_hatch")]
    public void InvokeIsolatedDotnetDocumentsStaleLeaseForceCleanEscapeHatch()
    {
        var script = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "scripts",
            "Invoke-IsolatedDotnet.ps1"));

        Assert.Contains(
            DotnetBuildEnvironmentManager.ForceCleanStaleLeaseArtifactsVariable,
            script,
            StringComparison.Ordinal);
        Assert.Contains("operator recovery escape hatch", script, StringComparison.OrdinalIgnoreCase);
    }

}
