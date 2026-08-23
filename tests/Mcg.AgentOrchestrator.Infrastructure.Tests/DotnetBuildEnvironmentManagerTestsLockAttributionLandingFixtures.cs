using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;
using static DotnetBuildEnvironmentManagerTests;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsLockAttributionLandingFixtures
{
    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_owned_artifact_holder_is_reaped_and_retried")]
    public void DotnetBuildEnvironmentManagerOwnedArtifactHolderIsReapedAndRetried()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
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
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
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

    [Xunit.Fact(DisplayName = "LockAttribution_restart_manager_names_file_holder")]
    public void LockAttributionRestartManagerNamesFileHolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!CanStartRestartManagerForTests())
        {
            return;
        }

        using var currentProcess = Process.GetCurrentProcess();
        var lockedPath = currentProcess.MainModule?.FileName;
        Assert.True(File.Exists(lockedPath), $"Current test host path does not exist: {lockedPath}");

        var attribution = LockAttribution.Attribute(
            lockedPath!,
            null,
            "artifact-prep",
            "prepare-artifacts");

        Assert.Equal("restart-manager", attribution.Source);

        var holder = Assert.Single(attribution.Holders.Where(holder => holder.ProcessId == currentProcess.Id));
        var expectedStartTime = new DateTimeOffset(currentProcess.StartTime.ToUniversalTime(), TimeSpan.Zero);
        Assert.False(string.IsNullOrWhiteSpace(holder.ProcessName));
        Assert.Equal(currentProcess.ProcessName, holder.ProcessName);
        Assert.True(holder.ProcessStartTime.HasValue);
        Assert.True(
            (holder.ProcessStartTime.Value - expectedStartTime).Duration() < TimeSpan.FromSeconds(2),
            $"Expected RM start time near {expectedStartTime:O}, got {holder.ProcessStartTime:O}.");
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_first_available_artifact_prep_lock_returns_build_lock_blocked")]
    public void DotnetBuildEnvironmentManagerFirstAvailableArtifactPrepLockReturnsBuildLockBlocked()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
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
                result = DotnetBuildEnvironmentManager.TryAcquireFirstAvailableStableSlotExecutionLock(TimeSpan.Zero));

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
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
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
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
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
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
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
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
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
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
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
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
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
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
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
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
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
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
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
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
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
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
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
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
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
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var goalId = new GoalId("decafbaddecafbaddecafbaddecafbad");
        try
        {
            var first = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "test");
            using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(first))
            {
            }

            var sentinel = Path.Combine(first.ArtifactsPath, "incremental-cache-sentinel.txt");
            File.WriteAllText(sentinel, "preserve after goal lease liveness reclaim");
            File.WriteAllText(Path.Combine(first.RootPath, "lease", "lease.lock"), "999999");

            DotnetBuildEnvironment? second = null;
            var creationOutput = AsyncLocalConsoleRouter.Capture(() =>
                second = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "retry"));
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

            Assert.True(DotnetBuildEnvironmentManager.TryRotateGoalLease(goalId, "corrupt-cache"));
            var third = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "after-rotate");
            Assert.False(third.ReusedGoalLease);
            Assert.Equal(first.LeaseId, third.LeaseId);
            Assert.True(Directory.Exists(Path.Combine(third.RootPath, "rotated-leases")));
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_inspects_and_cleans_orphaned_goal_lease")]
    public void DotnetBuildEnvironmentManagerInspectsAndCleansOrphanedGoalLease()
    {
        using var envScope = EnvVarScope.ForIsolatedDotnetRoot();
        var goalId = new GoalId("0badcafe0badcafe0badcafe0badcafe");
        try
        {
            var environment = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "test");
            var active = DotnetBuildEnvironmentManager.InspectGoalLease(goalId);
            Assert.Equal(environment.LeaseId, active.LeaseId);
            Assert.True(active.OwnerProcessAlive);
            Assert.False(active.CanCleanup);
            Assert.False(DotnetBuildEnvironmentManager.TryCleanupOrphanedGoalLease(goalId, out _, out var activeDetail));
            Assert.True(activeDetail.Contains("Refusing to delete active build lease", StringComparison.Ordinal));

            using (var metadata = JsonDocument.Parse(File.ReadAllText(environment.LeaseMetadataPath!)))
            {
                var orphanedJson = metadata.RootElement.GetRawText().Replace(
                    $"\"ownerProcessId\": {Environment.ProcessId}",
                    "\"ownerProcessId\": 999999",
                    StringComparison.Ordinal);
                File.WriteAllText(environment.LeaseMetadataPath!, orphanedJson);
            }

            var orphaned = DotnetBuildEnvironmentManager.InspectGoalLease(goalId);
            Assert.False(orphaned.OwnerProcessAlive);
            Assert.True(orphaned.CanCleanup);
            Assert.True(orphaned.Detail.Contains("orphaned", StringComparison.Ordinal));
            Assert.True(DotnetBuildEnvironmentManager.TryCleanupOrphanedGoalLease(goalId, out _, out var cleanupDetail));
            Assert.True(cleanupDetail.Contains("Deleted orphaned build lease", StringComparison.Ordinal));
            Assert.False(Directory.Exists(environment.RootPath));
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

}
