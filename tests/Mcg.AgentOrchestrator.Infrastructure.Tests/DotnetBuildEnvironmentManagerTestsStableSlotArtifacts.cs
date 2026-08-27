using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;
using static DotnetBuildEnvironmentManagerTests;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsStableSlotArtifacts
{
    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_concurrent_stable_slot_acquirers_get_different_slots")]
    public async Task DotnetBuildEnvironmentManagerConcurrentStableSlotAcquirersGetDifferentSlots()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var tasks = new[]
        {
            Task.Run(() => DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(2))),
            Task.Run(() => DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(2)))
        };

        var leases = await Task.WhenAll(tasks);
        try
        {
            Assert.NotEqual(leases[0].Environment.SlotOwnerToken, leases[1].Environment.SlotOwnerToken);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_killed_holder_releases_handle_backed_execution_lease")]
    public void DotnetBuildEnvironmentManagerKilledHolderReleasesHandleBackedExecutionLease()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var slot0 = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        var readyPath = Path.Combine(Path.GetDirectoryName(slot0.ExecutionLockPath)!, $"holder-ready-{Guid.NewGuid():N}.txt");
        var script = $$"""
            $stream = [System.IO.File]::Open('{{EscapePowerShell(slot0.ExecutionLockPath)}}', [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::ReadWrite)
            $stream.Lock(0, 1)
            Set-Content -LiteralPath '{{EscapePowerShell(readyPath)}}' -Value ([string]$PID)
            try { Start-Sleep -Seconds 30 } finally { $stream.Unlock(0, 1); $stream.Dispose() }
            """;
        using var holder = Process.Start(new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "-NoProfile",
                "-NonInteractive",
                "-EncodedCommand",
                EncodePowerShell(script)
            }
        }) ?? throw new InvalidOperationException("Failed to start lease holder process.");

        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!File.Exists(readyPath) && DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }

            Assert.True(File.Exists(readyPath), "Lease holder did not signal readiness.");
            Assert.Throws<DotnetBuildSlotsBusyException>(() =>
                DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(slot0, TimeSpan.FromMilliseconds(100)));

            holder.Kill(entireProcessTree: true);
            Assert.True(holder.WaitForExit(5000), "Lease holder did not exit after kill.");

            using var reacquired = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(slot0, TimeSpan.FromSeconds(2));
            Assert.Equal(slot0.ExecutionLockPath, reacquired.Name);
        }
        finally
        {
            if (!holder.HasExited)
            {
                holder.Kill(entireProcessTree: true);
                holder.WaitForExit(5000);
            }
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_all_stable_slots_leased_returns_slots_busy")]
    public void DotnetBuildEnvironmentManagerAllStableSlotsLeasedReturnsSlotsBusy()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var locks = new List<FileStream>();
        try
        {
            for (var slot = 0; slot < DotnetBuildEnvironmentManager.BuildConcurrencySlotCount; slot++)
            {
                var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(slot);
                locks.Add(DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment));
                Thread.Sleep(5);
            }

            var waits = new List<DotnetBuildStableSlotWait>();
            var clock = new RecordingTimeProvider();
            var delays = new List<TimeSpan>();
            DotnetBuildLeaseAcquisition? result = null;
            LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(path, [], "test");
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                result = DotnetBuildEnvironmentManager.TryAcquireFirstAvailableStableSlotExecutionLock(
                    TimeSpan.FromSeconds(2),
                    waits.Add,
                    timeProvider: clock,
                    sleep: delay =>
                    {
                        delays.Add(delay);
                        clock.Advance(delay);
                    });
            });

            var busy = Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(result);
            Assert.Equal("first-available-stable-slot", busy.WantedBy);
            Assert.Equal(DotnetBuildEnvironmentManager.StableSlotCount, busy.BusySlots.Count);
            Assert.All(busy.BusySlots, slot => Assert.Equal(Environment.ProcessId, slot.OwnerProcessId));
            var wait = Assert.Single(waits);
            Assert.Equal(0, wait.SlotIndex);
            Assert.Equal(Environment.ProcessId, wait.OwnerProcessId);
            Assert.True(delays.Count >= 2, "Expected the bounded acquisition to retry.");
            Assert.All(delays, delay => Assert.Equal(TimeSpan.FromMilliseconds(100), delay));
            Assert.Equal(TimeSpan.FromSeconds(2), TimeSpan.FromTicks(delays.Sum(delay => delay.Ticks)));
            Assert.Contains("SLOTS_BUSY", output);
            Assert.Contains("wantedBy=first-available-stable-slot", output);
        }
        finally
        {
            foreach (var lease in locks)
            {
                lease.Dispose();
            }

            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_gate_held_lease_blocks_script_byte_range_lock_not_open")]
    public void DotnetBuildEnvironmentManagerGateHeldLeaseBlocksScriptByteRangeLockNotOpen()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var slot0 = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        using var gateLease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(slot0);
        var script = $$"""
            $stream = [System.IO.File]::Open('{{EscapePowerShell(slot0.ExecutionLockPath)}}', [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::ReadWrite)
            try {
                $stream.Lock(0, 1)
                try { Write-Output 'acquired' } finally { $stream.Unlock(0, 1) }
                exit 2
            }
            catch [System.IO.IOException] {
                Write-Output 'busy'
                exit 0
            }
            finally {
                $stream.Dispose()
            }
            """;
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "-NoProfile",
                "-NonInteractive",
                "-EncodedCommand",
                EncodePowerShell(script)
            }
        }) ?? throw new InvalidOperationException("Failed to start script lease probe.");

        Assert.True(process.WaitForExit(5000), "Script lease probe did not exit.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(
            process.ExitCode == 0,
            $"Script lease probe exited {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        Assert.Contains("busy", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_classifies_unleased_active_testhost_slot_as_busy")]
    public void DotnetBuildEnvironmentManagerClassifiesUnleasedActiveTesthostSlotAsBusy()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var slot0 = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        using var sleeper = StartSleepProcess();
        DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () => new ProcessCommandLineSnapshot(
            new Dictionary<int, string>
            {
                [sleeper.Id] = $"testhost.exe --artifacts-path \"{slot0.ArtifactsPath}\""
            });

        try
        {
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var ex = Assert.Throws<DotnetBuildSlotsBusyException>(() =>
                    DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(slot0, TimeSpan.Zero));
                Assert.Contains(ex.SlotsBusy.BusySlots, slot => slot.SlotIndex == 0 && slot.OwnerProcessId == sleeper.Id);
            });

            Assert.Contains("SLOTS_BUSY ", output, StringComparison.Ordinal);
            Assert.Contains($"slot-0:pid-{sleeper.Id}", output, StringComparison.Ordinal);
        }
        finally
        {
            DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = null;
            StopProcess(sleeper);
        }
    }

    [Xunit.Fact]
    public void SlotCandidate_UnavailableInspection_IsNotAttributedToSlot()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var slot = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        const int candidateProcessId = 424242;
        var snapshot = new ProcessCommandLineSnapshot(
            new Dictionary<int, ProcessInspectionRecord>
            {
                [candidateProcessId] = new(
                    candidateProcessId,
                    1,
                    "testhost",
                    null,
                    null,
                    null,
                    ProcessInspectionStatus.AccessDenied)
            });

        var holder = DotnetBuildEnvironmentManager.TryFindActiveSlotArtifactConsumer(slot, snapshot);

        Assert.Null(holder);
    }

    [Xunit.Fact]
    public void SlotCandidate_UnrelatedUnavailable_DoesNotMaskMatchingConsumer()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var slot = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        using var sleeper = StartSleepProcess();
        const int unavailableProcessId = 424242;
        var snapshot = new ProcessCommandLineSnapshot(
            new Dictionary<int, ProcessInspectionRecord>
            {
                [unavailableProcessId] = new(
                    unavailableProcessId,
                    1,
                    "dotnet",
                    null,
                    null,
                    null,
                    ProcessInspectionStatus.AccessDenied),
                [sleeper.Id] = new(
                    sleeper.Id,
                    1,
                    "testhost",
                    null,
                    null,
                    $"testhost.exe --artifacts-path \"{slot.ArtifactsPath}\"",
                    ProcessInspectionStatus.Available)
            });

        try
        {
            var holder = Assert.IsType<BuildLockHolder>(
                DotnetBuildEnvironmentManager.TryFindActiveSlotArtifactConsumer(slot, snapshot));

            Assert.Equal(sleeper.Id, holder.ProcessId);
        }
        finally
        {
            StopProcess(sleeper);
        }
    }

    [Xunit.Fact]
    public void StableSlotPoll_UnavailableCandidate_BlocksAllSlotsWithOneOperationSnapshot()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        const int unavailableProcessId = 424242;
        var snapshotCalls = 0;
        DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () =>
        {
            snapshotCalls++;
            return new ProcessCommandLineSnapshot(
                new Dictionary<int, ProcessInspectionRecord>
                {
                    [unavailableProcessId] = new(
                        unavailableProcessId,
                        1,
                        "dotnet",
                        null,
                        null,
                        null,
                        ProcessInspectionStatus.AccessDenied)
                });
        };

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = DotnetBuildEnvironmentManager.TryAcquireFirstAvailableStableSlotExecutionLock(
                    TimeSpan.Zero,
                    slotCount: DotnetBuildEnvironmentManager.BuildConcurrencySlotCount,
                    timeProvider: new RecordingTimeProvider(),
                    sleep: _ => throw new InvalidOperationException("A zero-timeout poll must not sleep.")));

            var busy = Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(result);
            Assert.Equal(DotnetBuildEnvironmentManager.BuildConcurrencySlotCount, busy.BusySlots.Count);
            Assert.All(busy.BusySlots, slot =>
            {
                Assert.Null(slot.OwnerProcessId);
                Assert.Equal(unavailableProcessId, slot.UnavailableProcessId);
                Assert.Equal("dotnet", slot.UnavailableProcessName);
                Assert.Equal(ProcessInspectionStatus.AccessDenied, slot.UnavailableStatus);
            });
            Assert.Equal(1, snapshotCalls);
            Assert.Contains(
                $"pid-unknown:unavailable-pid-{unavailableProcessId}:name-dotnet:status-AccessDenied",
                output,
                StringComparison.Ordinal);
        }
        finally
        {
            DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = null;
        }
    }

    [Xunit.Fact]
    public void StableSlotPoll_EnumerationFailure_BlocksWithTypedNativeCause()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var failure = new ProcessInspectionFailure(
            ProcessInspectionStatus.NativeFailure,
            24,
            "CreateToolhelp32Snapshot");
        DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () =>
            new ProcessCommandLineSnapshot(
                new Dictionary<int, ProcessInspectionRecord>(),
                failure);

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = DotnetBuildEnvironmentManager.TryAcquireFirstAvailableStableSlotExecutionLock(
                    TimeSpan.Zero,
                    slotCount: DotnetBuildEnvironmentManager.BuildConcurrencySlotCount,
                    timeProvider: new RecordingTimeProvider(),
                    sleep: _ => throw new InvalidOperationException("A zero-timeout poll must not sleep.")));

            var busy = Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(result);
            Assert.All(busy.BusySlots, slot =>
            {
                Assert.Null(slot.OwnerProcessId);
                Assert.Null(slot.UnavailableProcessId);
                Assert.Equal(ProcessInspectionStatus.NativeFailure, slot.UnavailableStatus);
                Assert.Equal(24, slot.NativeError);
                Assert.Equal("CreateToolhelp32Snapshot", slot.FailureOperation);
            });
            Assert.Contains(
                "pid-unknown:status-NativeFailure:native-error-24:operation-CreateToolhelp32Snapshot",
                output,
                StringComparison.Ordinal);
        }
        finally
        {
            DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = null;
        }
    }

    [Xunit.Fact]
    public void SlotCandidate_DefaultSnapshotFactory_IsCalledOnce()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var slot = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        var snapshotCalls = 0;
        DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () =>
        {
            snapshotCalls++;
            return ProcessCommandLineSnapshot.Empty;
        };

        try
        {
            Assert.Null(DotnetBuildEnvironmentManager.TryFindActiveSlotArtifactConsumer(slot));
            Assert.Equal(1, snapshotCalls);
        }
        finally
        {
            DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_unleased_slot_consumer_racing_artifact_prep_returns_slots_busy")]
    public void DotnetBuildEnvironmentManagerUnleasedSlotConsumerRacingArtifactPrepReturnsSlotsBusy()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var slot0 = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        var lockedPath = Path.Combine(slot0.ArtifactsPath, "bin", "Mcg.AgentOrchestrator.Infrastructure.Tests", "debug_net10.0", "testhost.exe");
        using var sleeper = StartSleepProcess();
        var snapshotCalls = 0;
        DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () =>
        {
            var call = Interlocked.Increment(ref snapshotCalls);
            return new ProcessCommandLineSnapshot(call == 1
                ? new Dictionary<int, string>()
                : new Dictionary<int, string>
                {
                    [sleeper.Id] = $"vstest.console.exe --artifacts-path \"{slot0.ArtifactsPath}\""
                });
        };
        DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = current =>
        {
            if (current.ExecutionLockPath == slot0.ExecutionLockPath)
            {
                throw new IOException($"The process cannot access the file '{lockedPath}' because it is being used by another process.");
            }
        };
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(sleeper.Id, "vstest.console", $"vstest.console.exe --artifacts-path \"{slot0.ArtifactsPath}\"", true)],
            "test");

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(slot0, TimeSpan.FromSeconds(1)));

            var busy = Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(result);
            Assert.Equal(slot0.LeaseId, busy.WantedBy);
            Assert.Contains(busy.BusySlots, slot => slot.SlotIndex == 0 && slot.OwnerProcessId == sleeper.Id);
            Assert.Contains("LOCK ", output, StringComparison.Ordinal);
            Assert.Contains($"path=\"{lockedPath}\"", output, StringComparison.Ordinal);
            Assert.Contains("SLOTS_BUSY ", output, StringComparison.Ordinal);
            Assert.DoesNotContain("BUILD_LOCK_BLOCKED ", output, StringComparison.Ordinal);
        }
        finally
        {
            DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = null;
            DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = null;
            LockAttribution.AttributeForTests = null;
            StopProcess(sleeper);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_reused_goal_does_not_move_artifacts_for_unleased_test_process")]
    public void DotnetBuildEnvironmentManagerReusedGoalDoesNotMoveArtifactsForUnleasedTestProcess()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var gateGoalId = new GoalId("92000000920000009200000092000000");
        var first = DotnetBuildEnvironmentManager.CreateAttempt(gateGoalId, "first");
        using var sleeper = StartSleepProcess();
        DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () => new ProcessCommandLineSnapshot(
            new Dictionary<int, string>
            {
                [sleeper.Id] = $"vstest.console.exe --artifacts-path \"{first.ArtifactsPath}\""
            });

        try
        {
            var reused = DotnetBuildEnvironmentManager.CreateAttempt(gateGoalId, "gate");

            Assert.True(reused.ReusedGoalLease);
            Assert.Equal(first.ExecutionLockPath, reused.ExecutionLockPath);
            Assert.Equal(first.ArtifactsPath, reused.ArtifactsPath);
        }
        finally
        {
            DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = null;
            StopProcess(sleeper);
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(gateGoalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_reuses_artifacts_for_same_goal_slot")]
    public void DotnetBuildEnvironmentManagerReusesArtifactsForSameGoalSlot()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var goalId = new GoalId("10293847102938471029384710293847");
        try
        {
            var first = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "first");
            using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(first))
            {
                File.WriteAllText(Path.Combine(first.ArtifactsPath, "warm-cache.txt"), "keep");
                Directory.CreateDirectory(Path.Combine(first.ArtifactsPath, "obj"));
                File.WriteAllText(Path.Combine(first.ArtifactsPath, "obj", "stale-cache.txt"), "delete");
                Directory.CreateDirectory(Path.Combine(first.ArtifactsPath, "bin"));
                File.WriteAllText(Path.Combine(first.ArtifactsPath, "bin", "stale.dll"), "delete");
            }

            var second = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "second");
            using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(second))
            {
                Assert.True(File.Exists(Path.Combine(second.ArtifactsPath, "warm-cache.txt")));
            }

            File.WriteAllText(Path.Combine(second.RootPath, "lease", "lease.lock"), "999999");
            var stale = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "stale-owner");
            using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(stale))
            {
                Assert.True(File.Exists(Path.Combine(stale.ArtifactsPath, "warm-cache.txt")));
                Assert.True(File.Exists(Path.Combine(stale.ArtifactsPath, "obj", "stale-cache.txt")));
                Assert.True(File.Exists(Path.Combine(stale.ArtifactsPath, "bin", "stale.dll")));
            }
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_goal_artifacts_are_per_goal_and_build_lock_is_not_an_artifact_root")]
    public void DotnetBuildEnvironmentManagerGoalArtifactsArePerGoalAndBuildLockIsNotAnArtifactRoot()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var firstGoal = new GoalId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var secondGoal = new GoalId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        try
        {
            var first = DotnetBuildEnvironmentManager.CreateAttempt(firstGoal, "gate");
            var second = DotnetBuildEnvironmentManager.CreateAttempt(secondGoal, "gate");
            Assert.Equal(Path.Combine(first.RootPath, "artifacts"), first.ArtifactsPath);
            Assert.Equal(Path.Combine(second.RootPath, "artifacts"), second.ArtifactsPath);
            Assert.NotEqual(first.ArtifactsPath, second.ArtifactsPath);
            Assert.DoesNotContain(
                $"{Path.DirectorySeparatorChar}slots{Path.DirectorySeparatorChar}",
                first.ArtifactsPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                $"{Path.DirectorySeparatorChar}build-slots{Path.DirectorySeparatorChar}",
                first.ExecutionLockPath,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(firstGoal);
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(secondGoal);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironment_derived_artifacts_path_keeps_arguments_consistent")]
    public void DotnetBuildEnvironmentDerivedArtifactsPathKeepsArgumentsConsistent()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var original = DotnetBuildEnvironmentManager.CreateAttempt(null, "derive-artifacts");
        var derivedPath = Path.Combine(original.ArtifactsPath, "main-coverage-baseline");

        var derived = original.DeriveArtifactsPath(derivedPath);

        Assert.Equal(derivedPath, derived.ArtifactsPath);
        Assert.Equal(original.ArtifactsPath, original.Arguments[Array.IndexOf(original.Arguments.ToArray(), "--artifacts-path") + 1]);
        var switches = derived.Arguments
            .Select((argument, index) => (argument, index))
            .Where(item => item.argument.Equals("--artifacts-path", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var artifactSwitch = Assert.Single(switches);
        Assert.Equal(derivedPath, derived.Arguments[artifactSwitch.index + 1]);
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_caps_msbuild_parallelism_per_slot")]
    public void DotnetBuildEnvironmentManagerCapsMsbuildParallelismPerSlot()
    {
        using var rootScope = EnvVarScope.ForIsolatedDotnetRoot();
        using var defaultScope = EnvVarScope.ForVariable(DotnetBuildEnvironmentManager.BuildMaxCpuCountVariable, null);
        var defaultArguments = DotnetBuildEnvironmentManager.CreateAttempt(null, "default-cpu").Arguments;
        var expectedDefault = Math.Max(2, Environment.ProcessorCount / DotnetBuildEnvironmentManager.BuildConcurrencySlotCount);

        Assert.Equal($"-maxcpucount:{expectedDefault}", MaxCpuCountArgument(defaultArguments));
        Assert.NotEqual("-maxcpucount:1", MaxCpuCountArgument(defaultArguments));
        Assert.Contains("-p:BuildInParallel=false", defaultArguments);

        using var configuredScope = EnvVarScope.ForVariable(DotnetBuildEnvironmentManager.BuildMaxCpuCountVariable, "7");
        var configuredArguments = DotnetBuildEnvironmentManager.CreateAttempt(null, "configured-cpu").Arguments;

        Assert.Equal("-maxcpucount:7", MaxCpuCountArgument(configuredArguments));
        Assert.Contains("-p:BuildInParallel=false", configuredArguments);
    }

}
