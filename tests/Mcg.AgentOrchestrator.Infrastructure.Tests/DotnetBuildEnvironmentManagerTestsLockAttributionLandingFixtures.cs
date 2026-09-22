using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;
using static DotnetBuildEnvironmentManagerTests;

[Xunit.Collection(TestCollections.DotnetBuildEnvironmentManagerStaticHooks)]
public sealed class DotnetBuildEnvironmentManagerTestsLockAttributionLandingFixtures : DotnetBuildEnvironmentManagerRootedTestBase
{
    public static bool RestartManagerAvailable =>
        OperatingSystem.IsWindows() && CanStartRestartManagerForTests();

    [Xunit.Fact]
    public void LockAttributionProcessFallbackCreatesOneOperationSnapshot()
    {
        var snapshotCalls = 0;
        var snapshotStartedAt = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        LockAttribution.DisableRestartManagerForTests = true;
        LockAttribution.HandleExecutableForTests = Path.Combine(Path.GetTempPath(), $"missing-handle-{Guid.NewGuid():N}.exe");
        LockAttribution.ProcessCommandLineSnapshotForTests = () =>
        {
            snapshotCalls++;
            return new ProcessCommandLineSnapshot(
                new Dictionary<int, ProcessInspectionRecord>
                {
                    [Environment.ProcessId] = new(
                        Environment.ProcessId,
                        1,
                        "dotnet",
                        null,
                        null,
                        "dotnet test C:\\repo\\locked.dll C:\\repo\\.orchestrator-worktrees\\goal",
                        ProcessInspectionStatus.Available),
                    [101] = new(
                        101,
                        1,
                        "dotnet",
                        "C:\\Program Files\\dotnet\\dotnet.exe",
                        snapshotStartedAt,
                        "dotnet test C:\\repo\\locked.dll C:\\repo\\.orchestrator-worktrees\\goal",
                        ProcessInspectionStatus.Available),
                    [202] = new(
                        202,
                        1,
                        "dotnet",
                        null,
                        null,
                        "dotnet test C:\\repo\\.orchestrator-worktrees\\unrelated-goal",
                        ProcessInspectionStatus.Available)
                });
        };

        try
        {
            var attribution = LockAttribution.Attribute("C:\\repo\\locked.dll");

            Assert.Equal("process-snapshot", attribution.Source);
            Assert.Equal(1, snapshotCalls);
            Assert.Equal(2, attribution.Holders.Count);
            Assert.Contains(attribution.Holders, holder => holder.ProcessId == Environment.ProcessId);
            var snapshotHolder = Assert.Single(attribution.Holders, holder => holder.ProcessId == 101);
            Assert.Equal("dotnet", snapshotHolder.ProcessName);
            Assert.Equal(snapshotStartedAt, snapshotHolder.ProcessStartTime);
            Assert.DoesNotContain(attribution.Holders, holder => holder.ProcessId == 202);
        }
        finally
        {
            LockAttribution.ProcessCommandLineSnapshotForTests = null;
            LockAttribution.HandleExecutableForTests = null;
            LockAttribution.DisableRestartManagerForTests = false;
        }
    }

    [Xunit.Fact]
    public void ProcessFallbackPartialIdentityKeepsUnavailableCandidateFailClosed()
    {
        LockAttribution.DisableRestartManagerForTests = true;
        LockAttribution.HandleExecutableForTests = Path.Combine(Path.GetTempPath(), $"missing-handle-{Guid.NewGuid():N}.exe");
        LockAttribution.ProcessCommandLineSnapshotForTests = () =>
            new ProcessCommandLineSnapshot(
                new Dictionary<int, ProcessInspectionRecord>
                {
                    [101] = new(
                        101,
                        1,
                        "dotnet",
                        null,
                        null,
                        "dotnet test C:\\repo\\locked.dll C:\\repo\\.orchestrator-worktrees\\goal",
                        ProcessInspectionStatus.Available),
                    [202] = new(
                        202,
                        1,
                        "dotnet",
                        "C:\\Program Files\\dotnet\\dotnet.exe",
                        DateTimeOffset.Parse("2026-09-01T12:00:00Z"),
                        "dotnet test C:\\repo\\locked.dll C:\\repo\\.orchestrator-worktrees\\goal",
                        ProcessInspectionStatus.AccessDenied)
                });

        try
        {
            var holder = Assert.Single(LockAttribution.Attribute("C:\\repo\\locked.dll").Holders);

            Assert.Equal(101, holder.ProcessId);
            Assert.Equal("dotnet", holder.ProcessName);
            Assert.Null(holder.ProcessStartTime);
            Assert.True(holder.IsOrchestratorOwned);
        }
        finally
        {
            LockAttribution.ProcessCommandLineSnapshotForTests = null;
            LockAttribution.HandleExecutableForTests = null;
            LockAttribution.DisableRestartManagerForTests = false;
        }
    }

    [Xunit.Fact]
    public void LockAttributionProcessFallbackUsesArtifactRootHintOnlyForDescendantLock()
    {
        const string artifactsRoot = "C:\\isolated\\artifacts";
        const string lockedPath = "C:\\isolated\\artifacts\\bin\\Core.dll";
        LockAttribution.DisableRestartManagerForTests = true;
        LockAttribution.HandleExecutableForTests = Path.Combine(Path.GetTempPath(), $"missing-handle-{Guid.NewGuid():N}.exe");
        LockAttribution.ProcessCommandLineSnapshotForTests = () =>
            new ProcessCommandLineSnapshot(
                new Dictionary<int, ProcessInspectionRecord>
                {
                    [101] = new(
                        101,
                        1,
                        "dotnet",
                        null,
                        null,
                        "dotnet test --artifacts-path C:\\isolated\\artifacts C:\\repo\\.orchestrator-worktrees\\goal",
                        ProcessInspectionStatus.Available),
                    [202] = new(
                        202,
                        1,
                        "dotnet",
                        null,
                        null,
                        "dotnet test C:\\repo\\.orchestrator-worktrees\\unrelated-goal",
                        ProcessInspectionStatus.Available)
                });

        try
        {
            var attribution = LockAttribution.Attribute(lockedPath, artifactsRoot);

            var holder = Assert.Single(attribution.Holders);
            Assert.Equal(101, holder.ProcessId);
        }
        finally
        {
            LockAttribution.ProcessCommandLineSnapshotForTests = null;
            LockAttribution.HandleExecutableForTests = null;
            LockAttribution.DisableRestartManagerForTests = false;
        }
    }

    [Xunit.Fact]
    public void LockAttributionProcessFallbackEnumerationFailureIsExplicitAndConservative()
    {
        LockAttribution.DisableRestartManagerForTests = true;
        LockAttribution.HandleExecutableForTests = Path.Combine(Path.GetTempPath(), $"missing-handle-{Guid.NewGuid():N}.exe");
        LockAttribution.ProcessCommandLineSnapshotForTests = () =>
            new ProcessCommandLineSnapshot(
                new Dictionary<int, ProcessInspectionRecord>(),
                new ProcessInspectionFailure(
                    ProcessInspectionStatus.NativeFailure,
                    24,
                    "CreateToolhelp32Snapshot"));

        try
        {
            var attribution = LockAttribution.Attribute("C:\\repo\\locked.dll");

            Assert.Equal("process-snapshot-unavailable", attribution.Source);
            var holder = Assert.Single(attribution.Holders);
            Assert.Null(holder.ProcessId);
            Assert.False(holder.IsOrchestratorOwned);
            Assert.Equal(
                "process-inspection-unavailable-NativeFailure-24-CreateToolhelp32Snapshot",
                holder.ProcessName);
        }
        finally
        {
            LockAttribution.ProcessCommandLineSnapshotForTests = null;
            LockAttribution.HandleExecutableForTests = null;
            LockAttribution.DisableRestartManagerForTests = false;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_owned_artifact_holder_is_reaped_and_retried")]
    public void DotnetBuildEnvironmentManagerOwnedArtifactHolderIsReapedAndRetried()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var lockedPath = Path.Combine(environment.ArtifactsPath, "Mcg.AgentOrchestrator.App.dll");
        var prepareAttempts = 0;
        var killedPids = new List<int>();
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
        WorkerProcessJobs.TryKillPidTree = pid =>
        {
            killedPids.Add(pid);
            return true;
        };

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.Zero));

            var acquired = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(result);
            acquired.Lease.Dispose();
            Assert.Equal(2, prepareAttempts);
            Assert.Equal([987654321], killedPids);
            Assert.Contains("LOCK ", output, StringComparison.Ordinal);
            Assert.DoesNotContain("BUILD_LOCK_BLOCKED ", output, StringComparison.Ordinal);
        }
        finally
        {
            WorkerProcessJobs.TryKillPidTree = originalKill;
            DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = null;
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_no_holder_artifact_prep_lock_retries_and_acquires")]
    public void DotnetBuildEnvironmentManagerNoHolderArtifactPrepLockRetriesAndAcquires()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var lockedPath = Path.Combine(environment.ArtifactsPath, "Mcg.AgentOrchestrator.Core.dll");
        var fakeTimeProvider = new RecordingTimeProvider();
        var startedAt = fakeTimeProvider.GetUtcNow();
        var prepareAttempts = 0;
        DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = current =>
        {
            if (current.ExecutionLockPath == environment.ExecutionLockPath &&
                Interlocked.Increment(ref prepareAttempts) <= 2)
            {
                throw new UnauthorizedAccessException($"Access to the path '{lockedPath}' is denied.");
            }
        };
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(null, "unknown-probe-timeout", null, false)],
            "handle64-timeout");

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                    environment,
                    TimeSpan.FromSeconds(2),
                    timeProvider: fakeTimeProvider,
                    sleep: fakeTimeProvider.Advance));

            var acquired = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(result);
            acquired.Lease.Dispose();
            Assert.Equal(3, prepareAttempts);
            Assert.Equal(TimeSpan.FromMilliseconds(200), fakeTimeProvider.GetUtcNow() - startedAt);
            Assert.Contains("LOCK ", output, StringComparison.Ordinal);
            Assert.Contains("holderName=\"unknown-probe-timeout\"", output, StringComparison.Ordinal);
            Assert.DoesNotContain("BUILD_LOCK_BLOCKED ", output, StringComparison.Ordinal);
        }
        finally
        {
            DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = null;
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "LockAttribution_handle_probe_timeout_returns_unknown_without_wedging")]
    public void LockAttributionHandleProbeTimeoutReturnsUnknownWithoutWedging()
    {
        ConfigureTimeoutProbeForTests();

        try
        {
            var diagnostics = new LockAttributionDiagnosticCollector();
            var attribution = LockAttribution.AttributeWithDiagnosticsForTests(
                Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dll"),
                null,
                "artifact-prep",
                "prepare-artifacts",
                diagnostics);
            var receipt = diagnostics.Receipt;
            Console.WriteLine(receipt.Format());

            var holder = Assert.Single(attribution.Holders);
            Assert.Equal("handle64-timeout", attribution.Source);
            Assert.Null(holder.ProcessId);
            Assert.Equal("unknown-probe-timeout", holder.ProcessName);
            Assert.Equal("artifact-prep", attribution.Phase);
            Assert.Equal("prepare-artifacts", attribution.Operation);
            Assert.Null(receipt.Hooks.AttributeOverride);
            Assert.NotNull(receipt.Hooks.HandleExecutable);
            Assert.Equal(TimeSpan.FromMilliseconds(200), receipt.Hooks.HandleProbeTimeout);
            Assert.NotNull(receipt.Hooks.ConfigureHandleProbe);
            Assert.False(receipt.Hooks.DisableRestartManager);
            Assert.Equal(LockAttributionDiagnosticBranch.HandleProbe, receipt.Branch);
            Assert.Equal(LockAttributionDiagnosticClassification.HandleTimeoutUnknown, receipt.Classification);
            Assert.Contains(receipt.Events, item => item is { Stage: "handle-configure", Outcome: "invoked" });
            Assert.Contains(receipt.Events, item => item is { Stage: "handle-process-start", Outcome: "started" });
            Assert.Contains(receipt.Events, item => item is { Stage: "handle-wait", Outcome: "timed-out" });
            Assert.Contains(receipt.Events, item => item.Stage == "handle-kill");
            Assert.Contains(receipt.Events, item => item.Stage == "handle-reap");
        }
        finally
        {
            ClearLockAttributionTestHooks();
        }
    }

    [Xunit.Fact]
    public void LockAttributionIntentionalEmptyOverlapSelectsInjectedBranch()
    {
        ConfigureTimeoutProbeForTests();
        using var timeoutHooksReady = new ManualResetEventSlim();
        using var emptyOverrideInstalled = new ManualResetEventSlim();
        var diagnostics = new LockAttributionDiagnosticCollector();
        Task<BuildLockAttribution>? attributionTask = null;

        try
        {
            attributionTask = Task.Run(() =>
            {
                timeoutHooksReady.Set();
                if (!emptyOverrideInstalled.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The intentional empty override was not installed.");
                }

                return LockAttribution.AttributeWithDiagnosticsForTests(
                    Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dll"),
                    null,
                    "artifact-prep",
                    "intentional-empty-overlap",
                    diagnostics);
            });

            Assert.True(timeoutHooksReady.Wait(TimeSpan.FromSeconds(10)), "The timeout-hook participant did not reach its event gate.");
            Assert.Null(LockAttribution.AttributeForTests);
            LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(path, [], "intentional-empty-override");
            emptyOverrideInstalled.Set();

            Assert.True(attributionTask.Wait(TimeSpan.FromSeconds(10)), "The attribution participant did not finish after the override gate opened.");
            var attribution = attributionTask.GetAwaiter().GetResult();
            var receipt = diagnostics.Receipt;
            Console.WriteLine(receipt.Format());

            Assert.Empty(attribution.Holders);
            Assert.Equal("intentional-empty-override", attribution.Source);
            Assert.NotNull(receipt.Hooks.AttributeOverride);
            Assert.NotNull(receipt.Hooks.HandleExecutable);
            Assert.Equal(TimeSpan.FromMilliseconds(200), receipt.Hooks.HandleProbeTimeout);
            Assert.NotNull(receipt.Hooks.ConfigureHandleProbe);
            Assert.False(receipt.Hooks.DisableRestartManager);
            Assert.Equal(LockAttributionDiagnosticBranch.InjectedAttribution, receipt.Branch);
            Assert.Equal(LockAttributionDiagnosticClassification.InjectedEmpty, receipt.Classification);
            Assert.Contains(receipt.Events, item => item is { Stage: "attribute-override", Outcome: "returned-attribution" });
            Assert.DoesNotContain(receipt.Events, item => item.Stage == "handle-process-start");
        }
        finally
        {
            emptyOverrideInstalled.Set();
            if (attributionTask is not null)
            {
                try { attributionTask.Wait(TimeSpan.FromSeconds(10)); } catch { }
            }

            ClearLockAttributionTestHooks();
        }
    }

    [Xunit.Fact]
    public void LockAttributionClearedOverrideRestoresTimeoutBranch()
    {
        var injectedDiagnostics = new LockAttributionDiagnosticCollector();
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(path, [], "intentional-empty-override");
        try
        {
            var injected = LockAttribution.AttributeWithDiagnosticsForTests(
                Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dll"),
                null,
                "artifact-prep",
                "sequence-injected",
                injectedDiagnostics);
            Assert.Empty(injected.Holders);
            Assert.Equal(LockAttributionDiagnosticClassification.InjectedEmpty, injectedDiagnostics.Receipt.Classification);
            Console.WriteLine(injectedDiagnostics.Receipt.Format());
        }
        finally
        {
            ClearLockAttributionTestHooks();
        }

        Assert.Null(LockAttribution.AttributeForTests);
        Assert.Null(LockAttribution.HandleExecutableForTests);
        Assert.Null(LockAttribution.HandleProbeTimeoutForTests);
        Assert.Null(LockAttribution.ConfigureHandleProbeForTests);
        Assert.False(LockAttribution.DisableRestartManagerForTests);

        ConfigureTimeoutProbeForTests();
        try
        {
            var timeoutDiagnostics = new LockAttributionDiagnosticCollector();
            var timeout = LockAttribution.AttributeWithDiagnosticsForTests(
                Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dll"),
                null,
                "artifact-prep",
                "sequence-after-clear",
                timeoutDiagnostics);
            var receipt = timeoutDiagnostics.Receipt;
            Console.WriteLine(receipt.Format());

            Assert.Single(timeout.Holders);
            Assert.Null(receipt.Hooks.AttributeOverride);
            Assert.Equal(LockAttributionDiagnosticBranch.HandleProbe, receipt.Branch);
            Assert.Equal(LockAttributionDiagnosticClassification.HandleTimeoutUnknown, receipt.Classification);
        }
        finally
        {
            ClearLockAttributionTestHooks();
        }
    }

    [Xunit.Fact(
        DisplayName = "LockAttribution_handle_probe_returns_results_for_real_held_file",
        Skip = "probe spawn-context hang under managed hosts - tracked by the probe-fix goal; unskip there")]
    public void LockAttributionHandleProbeReturnsResultsForRealHeldFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = ResolveHandleExecutableForTests();
        if (handle is null)
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"mcg-handle64-held-file-{Guid.NewGuid():N}");
        var lockedPath = Path.Combine(root, "held.dll");
        var readyPath = Path.Combine(root, "ready.txt");
        var releasePath = Path.Combine(root, "release.txt");
        Process? holder = null;
        LockAttribution.HandleExecutableForTests = handle;
        LockAttribution.HandleProbeTimeoutForTests = TimeSpan.FromSeconds(10);
        LockAttribution.DisableRestartManagerForTests = true;
        try
        {
            holder = StartFileHolder(lockedPath, readyPath, releasePath);
            Assert.True(SpinWait.SpinUntil(() => File.Exists(readyPath), TimeSpan.FromSeconds(10)), "file holder did not become ready");

            var elapsed = Stopwatch.StartNew();
            var attribution = LockAttribution.Attribute(
                lockedPath,
                "mcg-dotnet-isolated",
                "artifact-prep",
                "prepare-artifacts");
            elapsed.Stop();

            var diagnostic = FormatAttributionDiagnostic(handle, lockedPath, holder.Id, elapsed.Elapsed, attribution);
            Assert.True(
                string.Equals("handle64", attribution.Source, StringComparison.Ordinal),
                $"Expected source handle64 but got {attribution.Source}. {diagnostic}");
            Assert.True(attribution.Holders.Any(attributedHolder => attributedHolder.ProcessId == holder.Id), diagnostic);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"handle64 probe exceeded bound. {diagnostic}");
            Assert.Equal("artifact-prep", attribution.Phase);
            Assert.Equal("prepare-artifacts", attribution.Operation);
        }
        finally
        {
            try { File.WriteAllText(releasePath, "release"); } catch { }
            if (holder is not null)
            {
                StopProcess(holder);
                holder.Dispose();
            }

            LockAttribution.HandleExecutableForTests = null;
            LockAttribution.HandleProbeTimeoutForTests = null;
            LockAttribution.DisableRestartManagerForTests = false;
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(
        DisplayName = "LockAttribution_restart_manager_names_file_holder",
        Skip = "Requires Windows Restart Manager.",
        SkipUnless = nameof(RestartManagerAvailable))]
    public void LockAttributionRestartManagerNamesFileHolder()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var currentProcessPath = currentProcess.MainModule?.FileName;
        Assert.True(File.Exists(currentProcessPath), $"Current test host path does not exist: {currentProcessPath}");
        var root = Path.Combine(Path.GetTempPath(), "mcg-rm-attribution-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var lockedPath = Path.Combine(root, "held.bin");
        File.WriteAllText(lockedPath, "held");

        try
        {
            using var heldFile = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var attribution = LockAttribution.Attribute(
                lockedPath,
                null,
                "artifact-prep",
                "prepare-artifacts");

            Assert.Equal("restart-manager", attribution.Source);

            var holder = Assert.Single(attribution.Holders.Where(holder => holder.ProcessId == currentProcess.Id));
            var expectedStartTime = new DateTimeOffset(currentProcess.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var expectedProcessName = FileVersionInfo.GetVersionInfo(currentProcessPath!).FileDescription;
            if (string.IsNullOrWhiteSpace(expectedProcessName))
            {
                expectedProcessName = currentProcess.ProcessName;
            }

            Assert.Equal(expectedProcessName, holder.ProcessName);
            Assert.True(holder.ProcessStartTime.HasValue);
            Assert.True(
                (holder.ProcessStartTime.Value - expectedStartTime).Duration() < TimeSpan.FromSeconds(2),
                $"Expected RM start time near {expectedStartTime:O}, got {holder.ProcessStartTime:O}.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_first_available_artifact_prep_lock_returns_build_lock_blocked")]
    public void DotnetBuildEnvironmentManagerFirstAvailableArtifactPrepLockReturnsBuildLockBlocked()
    {
        var lockedPathByLeaseId = new Dictionary<string, string>(StringComparer.Ordinal);
        var shutdownCount = 0;
        var originalKill = WorkerProcessJobs.TryKillPidTree;
        DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = current =>
        {
            var lockedPath = Path.Combine(current.ArtifactsPath, "Mcg.AgentOrchestrator.App.dll");
            lockedPathByLeaseId[current.LeaseId] = lockedPath;
            throw new UnauthorizedAccessException($"Access to the path '{lockedPath}' is denied.");
        };
        DotnetBuildEnvironmentManager.ShutdownBuildServersForTests = () => shutdownCount++;
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(24680, "VBCSCompiler", "VBCSCompiler.exe -pipename:first-available", true)],
            "test");
        WorkerProcessJobs.TryKillPidTree = _ => false;

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = RootedDotnetBuildEnvironmentManager.TryAcquireFirstAvailableStableSlotExecutionLock(StorageRoot, TimeSpan.Zero));

            var blocked = Assert.IsType<DotnetBuildLeaseAcquisition.BuildLockBlocked>(result);
            Assert.True(lockedPathByLeaseId.TryGetValue(blocked.WantedBy, out var lockedPath), $"Unexpected lease id {blocked.WantedBy}.");
            Assert.Equal(lockedPath, blocked.Attribution.Path);
            var holder = Assert.Single(blocked.Attribution.Holders);
            Assert.Equal(24680, holder.ProcessId);
            Assert.Equal("VBCSCompiler", holder.ProcessName);
            Assert.True(holder.IsOrchestratorOwned);
            Assert.Equal(1, shutdownCount);
            Assert.Contains("LOCK ", output, StringComparison.Ordinal);
            Assert.Contains($"path=\"{lockedPath}\"", output, StringComparison.Ordinal);
            Assert.Contains("holderPid=24680", output, StringComparison.Ordinal);
            Assert.Contains("BUILD_LOCK_BLOCKED ", output, StringComparison.Ordinal);
            Assert.DoesNotContain("SLOTS_BUSY ", output, StringComparison.Ordinal);
        }
        finally
        {
            WorkerProcessJobs.TryKillPidTree = originalKill;
            DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = null;
            DotnetBuildEnvironmentManager.ShutdownBuildServersForTests =
                AssemblyBuildServerShutdownIsolation.SafeDefault;
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_unowned_artifact_holder_blocks_without_reaper")]
    public void DotnetBuildEnvironmentManagerUnownedArtifactHolderBlocksWithoutReaper()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var lockedPath = Path.Combine(environment.ArtifactsPath, "Mcg.AgentOrchestrator.App.dll");
        var killAttempts = 0;
        var originalKill = WorkerProcessJobs.TryKillPidTree;
        DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = current =>
        {
            if (current.ExecutionLockPath == environment.ExecutionLockPath)
            {
                throw new UnauthorizedAccessException($"Access to the path '{lockedPath}' is denied.");
            }
        };
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(987654322, "dotnet", "dotnet test --artifacts-path external", false)],
            "test");
        WorkerProcessJobs.TryKillPidTree = _ =>
        {
            killAttempts++;
            return true;
        };

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.Zero));

            var blocked = Assert.IsType<DotnetBuildLeaseAcquisition.BuildLockBlocked>(result);
            Assert.Equal(lockedPath, blocked.Attribution.Path);
            var holder = Assert.Single(blocked.Attribution.Holders);
            Assert.Equal(987654322, holder.ProcessId);
            Assert.False(holder.IsOrchestratorOwned);
            Assert.Equal(0, killAttempts);
            Assert.Contains("BUILD_LOCK_BLOCKED ", output, StringComparison.Ordinal);
        }
        finally
        {
            WorkerProcessJobs.TryKillPidTree = originalKill;
            DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = null;
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_self_held_artifact_lock_is_not_build_lock_blocked")]
    public void DotnetBuildEnvironmentManagerSelfHeldArtifactLockIsNotBuildLockBlocked()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        Directory.CreateDirectory(Path.Combine(environment.ArtifactsPath, "bin", "Mcg.AgentOrchestrator.Core", "debug"));
        var lockedPath = Path.Combine(
            environment.ArtifactsPath,
            "bin",
            "Mcg.AgentOrchestrator.Core",
            "debug",
            "Mcg.AgentOrchestrator.Core.dll");
        File.WriteAllText(lockedPath, "loaded by active testhost");
        using var held = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(path, [], "test");

        try
        {
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    WriteForeignOwnerMarker(environment.ArtifactsPath);
                    using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(
                        environment,
                        TimeSpan.FromSeconds(1));
                    Assert.Equal(environment.ExecutionLockPath, lease.Name);
                }
            });

            Assert.Contains("LOCK ", output, StringComparison.Ordinal);
            Assert.DoesNotContain("BUILD_LOCK_BLOCKED ", output, StringComparison.Ordinal);
            Assert.Contains("decision=preserved", output, StringComparison.Ordinal);
            Assert.Contains("reason=wipe-skipped-self-held-lock", output, StringComparison.Ordinal);
            Assert.DoesNotContain("decision=wiped", output, StringComparison.Ordinal);
            Assert.True(File.Exists(lockedPath));
            var ownerMarkerPath = Path.Combine(environment.ArtifactsPath, ".mcg-artifacts-owner.json");
            if (File.Exists(ownerMarkerPath))
            {
                using var ownerMarker = JsonDocument.Parse(File.ReadAllText(ownerMarkerPath));
                Assert.Equal("foreign-owner", ownerMarker.RootElement.GetProperty("ownerToken").GetString());
            }
        }
        finally
        {
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_self_held_landing_fixture_lock_is_not_build_lock_blocked")]
    public void DotnetBuildEnvironmentManagerSelfHeldLandingFixtureLockIsNotBuildLockBlocked()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var (fixtureRoot, lockedPath) = CreateLandingFixtureLockPath();
        var fakeTimeProvider = new RecordingTimeProvider();
        var startedAt = fakeTimeProvider.GetUtcNow();
        var prepareAttempts = 0;
        DotnetBuildEnvironmentManager.RegisterCurrentLandingTestFixtureRoot(lockedPath);
        DotnetBuildEnvironmentManager.WriteLandingTestFixtureMarkerForTests(fixtureRoot);
        DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = current =>
        {
            if (current.ExecutionLockPath == environment.ExecutionLockPath &&
                Interlocked.Increment(ref prepareAttempts) == 1)
            {
                throw new UnauthorizedAccessException($"Access to the path '{lockedPath}' is denied.");
            }
        };
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(path, [], "test");

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            void AdvancePastDeadline(TimeSpan _)
            {
                fakeTimeProvider.Advance(TimeSpan.FromSeconds(2));
            }

            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                    environment,
                    TimeSpan.FromSeconds(1),
                    timeProvider: fakeTimeProvider,
                    sleep: AdvancePastDeadline));

            var busy = Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(result);
            Assert.Equal(environment.LeaseId, busy.WantedBy);
            Assert.Equal(2, prepareAttempts);
            Assert.True(fakeTimeProvider.GetUtcNow() - startedAt >= TimeSpan.FromSeconds(1));
            Assert.Contains("LOCK ", output, StringComparison.Ordinal);
            Assert.DoesNotContain("BUILD_LOCK_BLOCKED ", output, StringComparison.Ordinal);
            Assert.Contains("SLOTS_BUSY ", output, StringComparison.Ordinal);
        }
        finally
        {
            DotnetBuildEnvironmentManager.ClearCurrentLandingTestFixtureRootsForTests();
            DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = null;
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_marked_landing_fixture_from_other_process_is_transient")]
    public void DotnetBuildEnvironmentManagerMarkedLandingFixtureFromOtherProcessIsTransient()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var (fixtureRoot, lockedPath) = CreateLandingFixtureLockPath();
        Directory.CreateDirectory(Path.GetDirectoryName(lockedPath)!);
        var readyPath = Path.Combine(fixtureRoot, "holder-ready.txt");
        var releasePath = Path.Combine(fixtureRoot, "holder-release.txt");
        using var holder = StartFileHolder(lockedPath, readyPath, releasePath);
        Assert.True(SpinWait.SpinUntil(() => File.Exists(readyPath), TimeSpan.FromSeconds(10)), "Fixture holder did not signal readiness.");
        DotnetBuildEnvironmentManager.WriteLandingTestFixtureMarkerForTests(fixtureRoot, holder.Id);
        var fakeTimeProvider = new RecordingTimeProvider();
        var prepareAttempts = 0;
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
            [new BuildLockHolder(holder.Id, holder.ProcessName, "foreign fixture holder", false)],
            "test");

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            void AdvanceClock(TimeSpan delay)
            {
                fakeTimeProvider.Advance(delay);
            }

            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                    environment,
                    TimeSpan.FromSeconds(1),
                    timeProvider: fakeTimeProvider,
                    sleep: AdvanceClock));

            var acquired = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(result);
            acquired.Lease.Dispose();
            Assert.Equal(2, prepareAttempts);
            Assert.Contains("LOCK ", output, StringComparison.Ordinal);
            Assert.DoesNotContain("BUILD_LOCK_BLOCKED ", output, StringComparison.Ordinal);
        }
        finally
        {
            File.WriteAllText(releasePath, "release");
            if (!holder.HasExited)
            {
                holder.Kill(entireProcessTree: true);
            }

            holder.WaitForExit(5000);
            DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = null;
            LockAttribution.AttributeForTests = null;
            TryDeleteDirectory(fixtureRoot);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_persistent_marked_landing_fixture_lock_returns_slots_busy_bounded")]
    public void DotnetBuildEnvironmentManagerPersistentMarkedLandingFixtureLockReturnsSlotsBusyBounded()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var (fixtureRoot, lockedPath) = CreateLandingFixtureLockPath();
        Directory.CreateDirectory(Path.GetDirectoryName(lockedPath)!);
        var readyPath = Path.Combine(fixtureRoot, "holder-ready.txt");
        var releasePath = Path.Combine(fixtureRoot, "holder-release.txt");
        using var holder = StartFileHolder(lockedPath, readyPath, releasePath);
        Assert.True(SpinWait.SpinUntil(() => File.Exists(readyPath), TimeSpan.FromSeconds(10)), "Fixture holder did not signal readiness.");
        DotnetBuildEnvironmentManager.WriteLandingTestFixtureMarkerForTests(fixtureRoot, holder.Id);
        var prepareAttempts = 0;
        DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = current =>
        {
            if (current.ExecutionLockPath == environment.ExecutionLockPath)
            {
                Interlocked.Increment(ref prepareAttempts);
                throw new UnauthorizedAccessException($"Access to the path '{lockedPath}' is denied.");
            }
        };

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.FromSeconds(5)));

            var busy = Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(result);
            Assert.Equal(environment.LeaseId, busy.WantedBy);
            Assert.Equal(3, prepareAttempts);
            Assert.False(holder.HasExited);
            Assert.Contains("SLOTS_BUSY ", output, StringComparison.Ordinal);
            Assert.Contains("LOCK ", output, StringComparison.Ordinal);
            Assert.DoesNotContain("BUILD_LOCK_BLOCKED ", output, StringComparison.Ordinal);
        }
        finally
        {
            File.WriteAllText(releasePath, "release");
            if (!holder.HasExited)
            {
                holder.Kill(entireProcessTree: true);
            }

            holder.WaitForExit(5000);
            DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = null;
            TryDeleteDirectory(fixtureRoot);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_stale_landing_fixture_marker_is_debris_not_blocker")]
    public void DotnetBuildEnvironmentManagerStaleLandingFixtureMarkerIsDebrisNotBlocker()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var (fixtureRoot, lockedPath) = CreateLandingFixtureLockPath();
        Directory.CreateDirectory(Path.GetDirectoryName(lockedPath)!);
        DotnetBuildEnvironmentManager.WriteLandingTestFixtureMarkerForTests(
            fixtureRoot,
            987654320,
            DateTimeOffset.UtcNow.AddHours(-3));
        var prepareAttempts = 0;
        DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = current =>
        {
            if (current.ExecutionLockPath == environment.ExecutionLockPath &&
                Interlocked.Increment(ref prepareAttempts) == 1)
            {
                throw new UnauthorizedAccessException($"Access to the path '{lockedPath}' is denied.");
            }
        };
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(path, [], "test");

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.FromSeconds(1)));

            var acquired = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(result);
            acquired.Lease.Dispose();
            Assert.Equal(2, prepareAttempts);
            Assert.Contains("LOCK ", output, StringComparison.Ordinal);
            Assert.DoesNotContain("BUILD_LOCK_BLOCKED ", output, StringComparison.Ordinal);
        }
        finally
        {
            DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = null;
            LockAttribution.AttributeForTests = null;
            TryDeleteDirectory(fixtureRoot);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_stale_landing_fixture_marker_with_live_holder_blocks_bounded")]
    public void DotnetBuildEnvironmentManagerStaleLandingFixtureMarkerWithLiveHolderBlocksBounded()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var (fixtureRoot, lockedPath) = CreateLandingFixtureLockPath();
        Directory.CreateDirectory(Path.GetDirectoryName(lockedPath)!);
        DotnetBuildEnvironmentManager.WriteLandingTestFixtureMarkerForTests(
            fixtureRoot,
            987654320,
            DateTimeOffset.UtcNow.AddHours(-3));
        var prepareAttempts = 0;
        DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = current =>
        {
            if (current.ExecutionLockPath == environment.ExecutionLockPath)
            {
                Interlocked.Increment(ref prepareAttempts);
                throw new UnauthorizedAccessException($"Access to the path '{lockedPath}' is denied.");
            }
        };
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(987654321, "dotnet", "live foreign stale fixture holder", false)],
            "test");

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.Zero));

            var blocked = Assert.IsType<DotnetBuildLeaseAcquisition.BuildLockBlocked>(result);
            Assert.Equal(lockedPath, blocked.Attribution.Path);
            Assert.Equal(1, prepareAttempts);
            var holder = Assert.Single(blocked.Attribution.Holders);
            Assert.Equal(987654321, holder.ProcessId);
            Assert.False(holder.IsOrchestratorOwned);
            Assert.Contains("BUILD_LOCK_BLOCKED ", output, StringComparison.Ordinal);
            Assert.DoesNotContain("SLOTS_BUSY ", output, StringComparison.Ordinal);
        }
        finally
        {
            DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = null;
            LockAttribution.AttributeForTests = null;
            TryDeleteDirectory(fixtureRoot);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_landing_fixture_creation_path_registers_root")]
    public void DotnetBuildEnvironmentManagerLandingFixtureCreationPathRegistersRoot()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var fixtureRoot = LandingExecutorTests.CreateGitRepository();
        var lockedPath = CreateLandingFixtureLockPath(fixtureRoot);
        var prepareAttempts = 0;
        DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = current =>
        {
            if (current.ExecutionLockPath == environment.ExecutionLockPath &&
                Interlocked.Increment(ref prepareAttempts) == 1)
            {
                throw new UnauthorizedAccessException($"Access to the path '{lockedPath}' is denied.");
            }
        };
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(path, [], "test");

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.FromSeconds(1)));

            var acquired = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(result);
            acquired.Lease.Dispose();
            Assert.Equal(2, prepareAttempts);
            Assert.Contains("LOCK ", output, StringComparison.Ordinal);
            Assert.DoesNotContain("BUILD_LOCK_BLOCKED ", output, StringComparison.Ordinal);
        }
        finally
        {
            DotnetBuildEnvironmentManager.ClearCurrentLandingTestFixtureRootsForTests();
            DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = null;
            LockAttribution.AttributeForTests = null;
            TryDeleteDirectory(fixtureRoot);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_foreign_landing_fixture_holder_blocks")]
    public void DotnetBuildEnvironmentManagerForeignLandingFixtureHolderBlocks()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var (_, lockedPath) = CreateLandingFixtureLockPath();
        var killAttempts = 0;
        var originalKill = WorkerProcessJobs.TryKillPidTree;
        DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = current =>
        {
            if (current.ExecutionLockPath == environment.ExecutionLockPath)
            {
                throw new UnauthorizedAccessException($"Access to the path '{lockedPath}' is denied.");
            }
        };
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(987654324, "dotnet", "dotnet test fixture", false)],
            "test");
        WorkerProcessJobs.TryKillPidTree = _ =>
        {
            killAttempts++;
            return true;
        };

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.Zero));

            var blocked = Assert.IsType<DotnetBuildLeaseAcquisition.BuildLockBlocked>(result);
            Assert.Equal(lockedPath, blocked.Attribution.Path);
            var holder = Assert.Single(blocked.Attribution.Holders);
            Assert.Equal(987654324, holder.ProcessId);
            Assert.False(holder.IsOrchestratorOwned);
            Assert.Equal(0, killAttempts);
            Assert.Contains("BUILD_LOCK_BLOCKED ", output, StringComparison.Ordinal);
        }
        finally
        {
            WorkerProcessJobs.TryKillPidTree = originalKill;
            DotnetBuildEnvironmentManager.ClearCurrentLandingTestFixtureRootsForTests();
            DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = null;
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_recently_created_unregistered_landing_fixture_lock_blocks")]
    public void DotnetBuildEnvironmentManagerRecentlyCreatedUnregisteredLandingFixtureLockBlocks()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var (fixtureRoot, lockedPath) = CreateLandingFixtureLockPath();
        Directory.CreateDirectory(Path.GetDirectoryName(lockedPath)!);
        Directory.SetCreationTimeUtc(fixtureRoot, DateTime.UtcNow);
        DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = current =>
        {
            if (current.ExecutionLockPath == environment.ExecutionLockPath)
            {
                throw new UnauthorizedAccessException($"Access to the path '{lockedPath}' is denied.");
            }
        };
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(path, [], "test");

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.Zero));

            var blocked = Assert.IsType<DotnetBuildLeaseAcquisition.BuildLockBlocked>(result);
            Assert.Equal(lockedPath, blocked.Attribution.Path);
            Assert.Empty(blocked.Attribution.Holders);
            Assert.Contains("BUILD_LOCK_BLOCKED ", output, StringComparison.Ordinal);
        }
        finally
        {
            DotnetBuildEnvironmentManager.ClearCurrentLandingTestFixtureRootsForTests();
            DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = null;
            LockAttribution.AttributeForTests = null;
            TryDeleteDirectory(fixtureRoot);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_unknown_landing_fixture_lock_under_different_run_blocks")]
    public void DotnetBuildEnvironmentManagerUnknownLandingFixtureLockUnderDifferentRunBlocks()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var (currentFixtureRoot, currentLockedPath) = CreateLandingFixtureLockPath();
        var (otherFixtureRoot, lockedPath) = CreateLandingFixtureLockPath();
        Directory.CreateDirectory(Path.GetDirectoryName(currentLockedPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(lockedPath)!);
        Directory.SetCreationTimeUtc(otherFixtureRoot, DateTime.UtcNow.AddDays(-1));
        DotnetBuildEnvironmentManager.RegisterCurrentLandingTestFixtureRoot(currentLockedPath);
        DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = current =>
        {
            if (current.ExecutionLockPath == environment.ExecutionLockPath)
            {
                throw new UnauthorizedAccessException($"Access to the path '{lockedPath}' is denied.");
            }
        };
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(path, [], "test");

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.Zero));

            var blocked = Assert.IsType<DotnetBuildLeaseAcquisition.BuildLockBlocked>(result);
            Assert.Equal(lockedPath, blocked.Attribution.Path);
            Assert.Empty(blocked.Attribution.Holders);
            Assert.Contains("BUILD_LOCK_BLOCKED ", output, StringComparison.Ordinal);
        }
        finally
        {
            DotnetBuildEnvironmentManager.ClearCurrentLandingTestFixtureRootsForTests();
            DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = null;
            LockAttribution.AttributeForTests = null;
            TryDeleteDirectory(currentFixtureRoot);
            TryDeleteDirectory(otherFixtureRoot);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_lease_lock_contention_returns_slots_busy_without_reaper")]
    public void DotnetBuildEnvironmentManagerLeaseLockContentionReturnsSlotsBusyWithoutReaper()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        using var heldLease = new FileStream(
            environment.ExecutionLockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);
        heldLease.Lock(0, 1);
        var attributionAttempts = 0;
        var killAttempts = 0;
        var originalKill = WorkerProcessJobs.TryKillPidTree;
        LockAttribution.AttributeForTests = (path, _) =>
        {
            attributionAttempts++;
            return new BuildLockAttribution(
                path,
                [new BuildLockHolder(987654323, "testhost", "dotnet test --artifacts-path slot-0", true)],
                "test");
        };
        WorkerProcessJobs.TryKillPidTree = _ =>
        {
            killAttempts++;
            return true;
        };

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.Zero));

            var busy = Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(result);
            Assert.Equal(environment.LeaseId, busy.WantedBy);
            Assert.Equal(0, attributionAttempts);
            Assert.Equal(0, killAttempts);
            Assert.Contains("SLOTS_BUSY ", output, StringComparison.Ordinal);
            Assert.DoesNotContain("LOCK ", output, StringComparison.Ordinal);
            Assert.DoesNotContain("BUILD_LOCK_BLOCKED ", output, StringComparison.Ordinal);
        }
        finally
        {
            heldLease.Unlock(0, 1);
            WorkerProcessJobs.TryKillPidTree = originalKill;
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_detects_stale_locks_and_rotates_goal_lease")]
    public void DotnetBuildEnvironmentManagerDetectsStaleLocksAndRotatesGoalLease()
    {
        var goalId = new GoalId("decafbaddecafbaddecafbaddecafbad");
        try
        {
            var first = RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, goalId, "test");
            using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(first))
            {
            }

            var sentinel = Path.Combine(first.ArtifactsPath, "incremental-cache-sentinel.txt");
            File.WriteAllText(sentinel, "preserve after goal lease liveness reclaim");
            File.WriteAllText(Path.Combine(first.RootPath, "lease", "lease.lock"), "999999");

            DotnetBuildEnvironment? second = null;
            var creationOutput = AsyncLocalConsoleRouter.Capture(() =>
                second = RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, goalId, "retry"));
            Assert.NotNull(second);
            Assert.DoesNotContain("decision=", creationOutput, StringComparison.Ordinal);
            var acquisitionOutput = AsyncLocalConsoleRouter.Capture(() =>
            {
                using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(second);
            });

            Assert.True(second.StaleLockCleared);
            Assert.Equal(first.ArtifactsPath, second.ArtifactsPath);
            Assert.Equal(
                "preserve after goal lease liveness reclaim",
                File.ReadAllText(sentinel));
            Assert.Contains("GOAL_LEASE_RECLAIM", acquisitionOutput, StringComparison.Ordinal);
            Assert.Contains("reclaimedPid=999999", acquisitionOutput, StringComparison.Ordinal);
            Assert.Contains("decision=preserved", acquisitionOutput, StringComparison.Ordinal);
            Assert.Contains("reason=goal-lease-dead-holder", acquisitionOutput, StringComparison.Ordinal);
            var repeatAcquisitionOutput = AsyncLocalConsoleRouter.Capture(() =>
            {
                using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(second);
            });
            Assert.DoesNotContain("GOAL_LEASE_RECLAIM", repeatAcquisitionOutput, StringComparison.Ordinal);
            using (var journal = ReadLastLeaseJournalEntry(second))
            {
                Assert.Equal(999999, journal.RootElement.GetProperty("reclaimedProcessId").GetInt32());
            }

            Assert.True(RootedDotnetBuildEnvironmentManager.TryRotateGoalLease(StorageRoot, goalId, "corrupt-cache"));
            var third = RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, goalId, "after-rotate");
            Assert.False(third.ReusedGoalLease);
            Assert.Equal(first.LeaseId, third.LeaseId);
            Assert.True(Directory.Exists(Path.Combine(third.RootPath, "rotated-leases")));
        }
        finally
        {
            RootedDotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(StorageRoot, goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_inspects_and_cleans_orphaned_goal_lease")]
    public void DotnetBuildEnvironmentManagerInspectsAndCleansOrphanedGoalLease()
    {
        var goalId = new GoalId("0badcafe0badcafe0badcafe0badcafe");
        try
        {
            var environment = RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, goalId, "test");
            var active = RootedDotnetBuildEnvironmentManager.InspectGoalLease(StorageRoot, goalId);
            Assert.Equal(environment.LeaseId, active.LeaseId);
            Assert.True(active.OwnerProcessAlive);
            Assert.False(active.CanCleanup);
            Assert.False(RootedDotnetBuildEnvironmentManager.TryCleanupOrphanedGoalLease(StorageRoot, goalId, out _, out var activeDetail));
            Assert.True(activeDetail.Contains("Refusing to delete active build lease", StringComparison.Ordinal));

            using (var metadata = JsonDocument.Parse(File.ReadAllText(environment.LeaseMetadataPath!)))
            {
                var orphanedJson = metadata.RootElement.GetRawText().Replace(
                    $"\"ownerProcessId\": {Environment.ProcessId}",
                    "\"ownerProcessId\": 999999",
                    StringComparison.Ordinal);
                File.WriteAllText(environment.LeaseMetadataPath!, orphanedJson);
            }

            var orphaned = RootedDotnetBuildEnvironmentManager.InspectGoalLease(StorageRoot, goalId);
            Assert.False(orphaned.OwnerProcessAlive);
            Assert.True(orphaned.CanCleanup);
            Assert.True(orphaned.Detail.Contains("orphaned", StringComparison.Ordinal));
            Assert.True(RootedDotnetBuildEnvironmentManager.TryCleanupOrphanedGoalLease(StorageRoot, goalId, out _, out var cleanupDetail));
            Assert.True(cleanupDetail.Contains("Deleted orphaned build lease", StringComparison.Ordinal));
            Assert.False(Directory.Exists(environment.RootPath));
        }
        finally
        {
            RootedDotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(StorageRoot, goalId);
        }
    }

}
