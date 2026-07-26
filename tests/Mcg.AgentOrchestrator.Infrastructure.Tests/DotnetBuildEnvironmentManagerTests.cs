using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTests
{
    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_lease_defaults_use_system_time_and_thread_sleep")]
    public void DotnetBuildEnvironmentManagerLeaseDefaultsUseSystemTimeAndThreadSleep()
    {
        Assert.Same(TimeProvider.System, DotnetBuildEnvironmentManager.DefaultLeaseTimeProviderForTests);
        Assert.Null(DotnetBuildEnvironmentManager.DefaultLeaseSleepForTests.Target);
        Assert.Equal(typeof(Thread), DotnetBuildEnvironmentManager.DefaultLeaseSleepForTests.Method.DeclaringType);
        Assert.Equal(nameof(Thread.Sleep), DotnetBuildEnvironmentManager.DefaultLeaseSleepForTests.Method.Name);
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_reuses_goal_lease_with_metadata_and_cleanup")]
    public void DotnetBuildEnvironmentManagerReusesGoalLeaseWithMetadataAndCleanup()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var goalId = new GoalId("feedbeeffeedbeeffeedbeeffeedbeef");
        try
        {
            var first = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "Acceptance");
            var second = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "Acceptance");

            Assert.Equal(DotnetBuildEnvironmentManager.GoalRoot(goalId), first.RootPath);
            Assert.Equal("goal-feedbeef", first.LeaseId);
            Assert.Equal(first.LeaseId, second.LeaseId);
            Assert.Equal(first.RootPath, second.RootPath);
            Assert.Equal(first.ArtifactsPath, second.ArtifactsPath);
            Assert.True(second.ReusedGoalLease);
            Assert.True(first.ArtifactsPath.Contains(Path.Combine("slots", "slot-"), StringComparison.OrdinalIgnoreCase));
            Assert.True(Directory.Exists(first.ArtifactsPath));
            Assert.True(Directory.Exists(second.ArtifactsPath));
            Assert.False(string.IsNullOrWhiteSpace(second.LeaseMetadataPath));
            Assert.True(File.Exists(second.LeaseMetadataPath));
            Assert.True(first.Arguments.Contains("--artifacts-path"));
            Assert.False(first.Arguments.Contains("--disable-build-servers"));
            Assert.DoesNotContain(first.Arguments, argument => argument.Equals("-p:UseSharedCompilation=false", StringComparison.Ordinal));
            Assert.Contains(first.Arguments, argument => argument.StartsWith("-maxcpucount:", StringComparison.Ordinal) && !argument.Equals("-maxcpucount:1", StringComparison.Ordinal));
            Assert.True(first.Arguments.Contains(first.ArtifactsPath));
            var otherGoalId = new GoalId("cafebabecafebabecafebabecafebabe");
            var other = DotnetBuildEnvironmentManager.CreateAttempt(otherGoalId, "Acceptance");
            Assert.True(other.ArtifactsPath.Contains(Path.Combine("slots", "slot-"), StringComparison.OrdinalIgnoreCase));
            Assert.True(DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(otherGoalId));
            using var metadata = JsonDocument.Parse(File.ReadAllText(second.LeaseMetadataPath!));
            Assert.Equal(goalId.Value, metadata.RootElement.GetProperty("goalId").GetString());
            Assert.Equal(first.LeaseId, metadata.RootElement.GetProperty("leaseId").GetString());
            Assert.Equal(second.ArtifactsPath, metadata.RootElement.GetProperty("artifactsPath").GetString());

            Assert.True(DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId));
            Assert.False(Directory.Exists(first.RootPath));
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_reuses_stable_manual_slot")]
    public void DotnetBuildEnvironmentManagerReusesStableManualSlot()
    {
        var first = DotnetBuildEnvironmentManager.CreateAttempt(null, "Acceptance");
        var second = DotnetBuildEnvironmentManager.CreateAttempt(null, "Retry");

        Assert.Equal("run-slot-manual", first.LeaseId);
        Assert.Equal(first.RootPath, second.RootPath);
        Assert.Equal(first.ArtifactsPath, second.ArtifactsPath);
        Assert.True(first.ArtifactsPath.Contains(Path.Combine("slots", "manual", "artifacts"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(first.ExecutionLockPath, second.ExecutionLockPath);
        Assert.True(first.Arguments.Contains(first.ArtifactsPath));
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_candidate_slot_count_limits_goal_and_stable_attempts")]
    public void DotnetBuildEnvironmentManagerCandidateSlotCountLimitsGoalAndStableAttempts()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var goalId = new GoalId("facefeedfacefeedfacefeedfacefeed");
        try
        {
            var environment = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "Acceptance", slotCount: 1);

            Assert.Contains(Path.Combine("slots", "slot-0", "artifacts"), environment.ArtifactsPath, StringComparison.OrdinalIgnoreCase);
            var error = Assert.Throws<ArgumentOutOfRangeException>(
                () => DotnetBuildEnvironmentManager.CreateStableSlotAttempt(1, slotCount: 1));
            Assert.Contains("requested slot count", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBaseBuildCache_restores_identical_project_outputs_from_main_sha_entry")]
    public void DotnetBaseBuildCacheRestoresIdenticalProjectOutputsFromMainShaEntry()
    {
        var root = CreateTempDirectory();
        var cache = new DotnetBaseBuildCache(Path.Combine(root, "cache"));
        var coldArtifacts = Path.Combine(root, "cold-artifacts");
        var warmArtifacts = Path.Combine(root, "warm-artifacts");
        const string project = "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj";
        try
        {
            WriteProjectArtifacts(coldArtifacts, project, "cold");
            var projectName = Path.GetFileNameWithoutExtension(project);
            var nestedManifest = Path.Combine(coldArtifacts, "bin", projectName, "debug_net10.0", "manifest.json");
            File.WriteAllText(nestedManifest, "legitimate build output");
            var coldHash = DotnetBaseBuildCache.ProjectOutputHash(coldArtifacts, project);
            WriteProjectArtifacts(warmArtifacts, project, "stale");
            var staleFile = Path.Combine(warmArtifacts, "bin", "Mcg.AgentOrchestrator.Core", "debug_net10.0", "stale-extra.txt");
            File.WriteAllText(staleFile, "stale");

            var publish = cache.Publish("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", coldArtifacts, [project]);
            var restore = cache.Restore("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", warmArtifacts, [project]);

            Assert.Single(publish.Projects);
            Assert.True(restore.AllHit);
            Assert.False(File.Exists(staleFile));
            Assert.True(File.Exists(Path.Combine(warmArtifacts, "bin", projectName, "debug_net10.0", "manifest.json")));
            Assert.Equal(coldHash, DotnetBaseBuildCache.ProjectOutputHash(warmArtifacts, project));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBaseBuildCache_main_sha_change_misses_previous_entry")]
    public void DotnetBaseBuildCacheMainShaChangeMissesPreviousEntry()
    {
        var root = CreateTempDirectory();
        var cache = new DotnetBaseBuildCache(Path.Combine(root, "cache"));
        var artifacts = Path.Combine(root, "artifacts");
        const string project = "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj";
        try
        {
            WriteProjectArtifacts(artifacts, project, "sha-one");
            cache.Publish("1111111111111111111111111111111111111111", artifacts, [project]);

            var restore = cache.Restore("2222222222222222222222222222222222222222", Path.Combine(root, "warm"), [project]);

            var receipt = Assert.Single(restore.Projects);
            Assert.Equal("miss", receipt.Status);
            Assert.Equal("not-found", receipt.Reason);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBaseBuildCache_restores_into_slot_artifacts_without_using_cache_as_slot")]
    public void DotnetBaseBuildCacheRestoresIntoSlotArtifactsWithoutUsingCacheAsSlot()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var cacheRoot = Path.Combine(CreateTempDirectory(), "cache");
        var cache = new DotnetBaseBuildCache(cacheRoot);
        var sourceArtifacts = Path.Combine(Path.GetTempPath(), "mcg-cache-source", Guid.NewGuid().ToString("N"));
        const string project = "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj";
        try
        {
            WriteProjectArtifacts(sourceArtifacts, project, "slot");
            cache.Publish("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sourceArtifacts, [project]);
            var slot = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);

            var restore = cache.Restore("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", slot.ArtifactsPath, [project]);

            Assert.True(restore.AllHit);
            Assert.Contains(Path.Combine("slots", "slot-0", "artifacts"), slot.ArtifactsPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(cacheRoot, slot.ArtifactsPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(slot.ArtifactsPath, "bin", "Mcg.AgentOrchestrator.Core.Tests", "debug_net10.0", "cache.txt")));
        }
        finally
        {
            TryDeleteDirectory(cacheRoot);
            TryDeleteDirectory(sourceArtifacts);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBaseBuildCache_evicts_to_bounded_entry_count")]
    public void DotnetBaseBuildCacheEvictsToBoundedEntryCount()
    {
        var root = CreateTempDirectory();
        var cacheRoot = Path.Combine(root, "cache");
        var cache = new DotnetBaseBuildCache(cacheRoot, maxEntries: 2);
        var artifacts = Path.Combine(root, "artifacts");
        const string project = "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj";
        try
        {
            for (var index = 0; index < 3; index++)
            {
                TryDeleteDirectory(artifacts);
                WriteProjectArtifacts(artifacts, project, $"entry-{index}");
                cache.Publish($"{index}{new string('a', 39)}", artifacts, [project]);
                Thread.Sleep(5);
            }

            var entries = Directory.EnumerateDirectories(cacheRoot)
                .Where(path => !Path.GetFileName(path).Equals("_staging", StringComparison.OrdinalIgnoreCase))
                .SelectMany(Directory.EnumerateDirectories)
                .Count();
            Assert.True(entries <= 2);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_artifact_prep_unauthorized_returns_build_lock_blocked_with_holder_identity")]
    public void DotnetBuildEnvironmentManagerArtifactPrepUnauthorizedReturnsBuildLockBlockedWithHolderIdentity()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        var lockedPath = Path.Combine(environment.ArtifactsPath, "Mcg.AgentOrchestrator.App.dll");
        var shutdownCount = 0;
        var originalKill = WorkerProcessJobs.TryKillPidTree;
        DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = current =>
        {
            if (current.ExecutionLockPath == environment.ExecutionLockPath)
            {
                throw new UnauthorizedAccessException($"Access to the path '{lockedPath}' is denied.");
            }
        };
        DotnetBuildEnvironmentManager.ShutdownBuildServersForTests = () => shutdownCount++;
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(2468, "VBCSCompiler", "VBCSCompiler.exe -pipename:slot-0", true)],
            "test");
        WorkerProcessJobs.TryKillPidTree = _ => false;

        try
        {
            DotnetBuildLeaseAcquisition? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.Zero));

            var blocked = Assert.IsType<DotnetBuildLeaseAcquisition.BuildLockBlocked>(result);
            Assert.Equal(environment.LeaseId, blocked.WantedBy);
            Assert.Equal(lockedPath, blocked.Attribution.Path);
            var holder = Assert.Single(blocked.Attribution.Holders);
            Assert.Equal(2468, holder.ProcessId);
            Assert.Equal("VBCSCompiler", holder.ProcessName);
            Assert.Equal("VBCSCompiler.exe -pipename:slot-0", holder.CommandLine);
            Assert.True(holder.IsOrchestratorOwned);
            Assert.Equal(1, shutdownCount);
            Assert.Contains("LOCK ", output, StringComparison.Ordinal);
            Assert.Contains($"path=\"{lockedPath}\"", output, StringComparison.Ordinal);
            Assert.Contains("holderPid=2468", output, StringComparison.Ordinal);
            Assert.Contains("holderName=\"VBCSCompiler\"", output, StringComparison.Ordinal);
            Assert.Contains("phase=\"artifact-prep\"", output, StringComparison.Ordinal);
            Assert.Contains("operation=\"prepare-artifacts\"", output, StringComparison.Ordinal);
            Assert.Contains("BUILD_LOCK_BLOCKED ", output, StringComparison.Ordinal);
            Assert.DoesNotContain("SLOTS_BUSY ", output, StringComparison.Ordinal);
        }
        finally
        {
            WorkerProcessJobs.TryKillPidTree = originalKill;
            DotnetBuildEnvironmentManager.PrepareArtifactsDirectoryForTests = null;
            DotnetBuildEnvironmentManager.ShutdownBuildServersForTests = null;
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_live_attempt_custody_refuses_foreign_owner_takeover")]
    public void DotnetBuildEnvironmentManagerLiveAttemptCustodyRefusesForeignOwnerTakeover()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        using var __ = EnvVarScope.ForVariable(AcceptanceAttemptArtifactCustody.AttemptIdVariable, null);
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        var attemptId = "live-attempt-123";
        var metadataPath = Path.Combine(environment.RootPath, $"{attemptId}.attempt.json");
        var evidencePath = Path.Combine(environment.ArtifactsPath, "TestResults", "completed-lane.trx");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        File.WriteAllText(evidencePath, "receipt");
        WriteForeignOwnerMarker(environment.ArtifactsPath);
        WriteAttemptMetadata(metadataPath, attemptId, "Running", Environment.ProcessId);
        AcceptanceAttemptArtifactCustody.Write(
            environment.ArtifactsPath,
            attemptId,
            metadataPath,
            Environment.ProcessId);

        var exception = Assert.Throws<AcceptanceAttemptArtifactCustodyException>(
            () => DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment, TimeSpan.Zero));

        Assert.Equal(attemptId, exception.AttemptId);
        Assert.Contains(attemptId, exception.Message, StringComparison.Ordinal);
        Assert.Contains(environment.ArtifactsPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(evidencePath));
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_terminal_attempt_custody_allows_foreign_owner_takeover")]
    public void DotnetBuildEnvironmentManagerTerminalAttemptCustodyAllowsForeignOwnerTakeover()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        using var __ = EnvVarScope.ForVariable(AcceptanceAttemptArtifactCustody.AttemptIdVariable, null);
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        var attemptId = "failed-attempt-123";
        var metadataPath = Path.Combine(environment.RootPath, $"{attemptId}.attempt.json");
        var evidencePath = Path.Combine(environment.ArtifactsPath, "TestResults", "stale.trx");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        File.WriteAllText(evidencePath, "stale");
        WriteForeignOwnerMarker(environment.ArtifactsPath);
        WriteAttemptMetadata(metadataPath, attemptId, "Failed", Environment.ProcessId);
        AcceptanceAttemptArtifactCustody.Write(
            environment.ArtifactsPath,
            attemptId,
            metadataPath,
            Environment.ProcessId);

        using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment, TimeSpan.Zero);

        Assert.False(File.Exists(evidencePath));
        Assert.False(File.Exists(AcceptanceAttemptArtifactCustody.MarkerPath(environment.ArtifactsPath)));
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_missing_custody_marker_preserves_foreign_owner_takeover")]
    public void DotnetBuildEnvironmentManagerMissingCustodyMarkerPreservesForeignOwnerTakeover()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        using var __ = EnvVarScope.ForVariable(AcceptanceAttemptArtifactCustody.AttemptIdVariable, null);
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        var evidencePath = Path.Combine(environment.ArtifactsPath, "stale.txt");
        File.WriteAllText(evidencePath, "stale");
        WriteForeignOwnerMarker(environment.ArtifactsPath);

        using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment, TimeSpan.Zero);

        Assert.False(File.Exists(evidencePath));
    }

    [Xunit.Fact(DisplayName = "InvokeWorkerBuildCheck_uses_operator_build_namespace_outside_firewall_test_slots")]
    public void InvokeWorkerBuildCheckUsesOperatorBuildNamespaceOutsideFirewallTestSlots()
    {
        var source = File.ReadAllText(Path.Combine(ResolveRepositoryRoot(), "scripts", "Invoke-WorkerBuildCheck.ps1"));

        Assert.Contains(@"slots\operator-build\$safeGoalPrefix", source, StringComparison.Ordinal);
        Assert.Contains(@"operators\worker-build\$safeGoalPrefix", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-StableSlotName", source, StringComparison.Ordinal);
    }

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
        LockAttribution.HandleExecutableForTests = ResolvePowerShell();
        LockAttribution.HandleProbeTimeoutForTests = TimeSpan.FromMilliseconds(200);
        LockAttribution.ConfigureHandleProbeForTests = (startInfo, _) =>
        {
            startInfo.ArgumentList.Clear();
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("Start-Sleep -Seconds 60");
        };

        try
        {
            var attribution = LockAttribution.Attribute(
                Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dll"),
                null,
                "artifact-prep",
                "prepare-artifacts");

            var holder = Assert.Single(attribution.Holders);
            Assert.Equal("handle64-timeout", attribution.Source);
            Assert.Null(holder.ProcessId);
            Assert.Equal("unknown-probe-timeout", holder.ProcessName);
            Assert.Equal("artifact-prep", attribution.Phase);
            Assert.Equal("prepare-artifacts", attribution.Operation);
        }
        finally
        {
            LockAttribution.HandleExecutableForTests = null;
            LockAttribution.HandleProbeTimeoutForTests = null;
            LockAttribution.ConfigureHandleProbeForTests = null;
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
            DotnetBuildEnvironmentManager.ShutdownBuildServersForTests = null;
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
            Assert.True(File.Exists(lockedPath));
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
            File.WriteAllText(Path.Combine(first.RootPath, "lease", "lease.lock"), "999999");

            var second = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "retry");

            Assert.True(second.StaleLockCleared);
            Assert.Equal(first.ArtifactsPath, second.ArtifactsPath);
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

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_first_available_stable_slot_skips_leased_slot_zero")]
    public void DotnetBuildEnvironmentManagerFirstAvailableStableSlotSkipsLeasedSlotZero()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var slot0 = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0);
        using var slot0Lock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(slot0);

        using var selected = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(1));

        Assert.NotEqual("slot-0", selected.Environment.SlotOwnerToken);
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
        Assert.DoesNotContain(Path.Combine("slots", "slot-0"), gateEnvironment.ExecutionLockPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine("slots", "slot-"), gateEnvironment.ExecutionLockPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LEASE_ACQUIRE", output);
        Assert.Contains("slot=slot-", output);
        Assert.Contains("lease=goal-90000000", output);
        Assert.Equal(1, CountOccurrences(output, "LEASE_RELEASE"));
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_reused_goal_gate_rescans_when_previous_slot_is_held")]
    public void DotnetBuildEnvironmentManagerReusedGoalGateRescansWhenPreviousSlotIsHeld()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var gateGoalId = new GoalId("91000000910000009100000091000000");
        var first = DotnetBuildEnvironmentManager.CreateAttempt(gateGoalId, "first");
        var previousSlot = SlotIndexFromPath(first.ExecutionLockPath);
        var previousSlotEnvironment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(previousSlot);
        using var previousSlotLock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(previousSlotEnvironment);
        DotnetBuildEnvironment? reused = null;

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            reused = DotnetBuildEnvironmentManager.CreateAttempt(gateGoalId, "gate");
            using var gateLock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(reused, TimeSpan.FromSeconds(1));
        });

        Assert.NotNull(reused);
        Assert.True(reused.ReusedGoalLease);
        Assert.NotEqual(first.ExecutionLockPath, reused.ExecutionLockPath);
        Assert.DoesNotContain(Path.Combine("slots", $"slot-{previousSlot}"), reused.ExecutionLockPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LEASE_ACQUIRE", output);
        Assert.Contains("lease=goal-91000000", output);
        Assert.Equal(1, CountOccurrences(output, "LEASE_RELEASE"));
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
        Assert.Contains("LEASE_ACQUIRE", output);
        Assert.Contains("LEASE_RELEASE", output);
    }

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
            for (var slot = 0; slot < DotnetBuildEnvironmentManager.StableSlotCount; slot++)
            {
                var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(slot);
                locks.Add(DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment));
                Thread.Sleep(5);
            }

            var waits = new List<DotnetBuildStableSlotWait>();
            DotnetBuildLeaseAcquisition? result = null;
            LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(path, [], "test");
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                result = DotnetBuildEnvironmentManager.TryAcquireFirstAvailableStableSlotExecutionLock(
                    TimeSpan.FromMilliseconds(150),
                    waits.Add);
            });

            var busy = Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(result);
            Assert.Equal("first-available-stable-slot", busy.WantedBy);
            Assert.Equal(DotnetBuildEnvironmentManager.StableSlotCount, busy.BusySlots.Count);
            Assert.All(busy.BusySlots, slot => Assert.Equal(Environment.ProcessId, slot.OwnerProcessId));
            var wait = Assert.Single(waits);
            Assert.Equal(0, wait.SlotIndex);
            Assert.Equal(Environment.ProcessId, wait.OwnerProcessId);
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

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_reused_goal_gate_rescans_when_previous_slot_has_unleased_testhost")]
    public void DotnetBuildEnvironmentManagerReusedGoalGateRescansWhenPreviousSlotHasUnleasedTesthost()
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
            Assert.NotEqual(first.ExecutionLockPath, reused.ExecutionLockPath);
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
                Assert.False(File.Exists(Path.Combine(stale.ArtifactsPath, "warm-cache.txt")));
                Assert.False(File.Exists(Path.Combine(stale.ArtifactsPath, "obj", "stale-cache.txt")));
                Assert.False(File.Exists(Path.Combine(stale.ArtifactsPath, "bin", "stale.dll")));
            }
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_stable_slot_build_arguments_are_firewall_covered")]
    public void DotnetBuildEnvironmentManagerStableSlotBuildArgumentsAreFirewallCovered()
    {
        var firewallPaths = DotnetBuildEnvironmentManager.StableSlotTesthostFirewallPaths()
            .Select(path => Path.GetFullPath(path.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var slot = 0; slot < DotnetBuildEnvironmentManager.StableSlotCount; slot++)
        {
            var arguments = DotnetBuildEnvironmentManager.StableSlotBuildArguments(slot);
            var artifactsPath = ArgumentValue(arguments, "--artifacts-path");
            foreach (var project in new[] { "Mcg.AgentOrchestrator.Core.Tests", "Mcg.AgentOrchestrator.Infrastructure.Tests" })
            {
                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    var derivedTesthostPath = Path.GetFullPath(Path.Combine(
                        artifactsPath,
                        "bin",
                        project,
                        $"{configuration.ToLowerInvariant()}_net10.0",
                        "testhost.exe"));

                    Assert.True(firewallPaths.Contains(derivedTesthostPath));
                }
            }
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_caps_msbuild_parallelism_per_slot")]
    public void DotnetBuildEnvironmentManagerCapsMsbuildParallelismPerSlot()
    {
        using var defaultScope = EnvVarScope.ForVariable(DotnetBuildEnvironmentManager.BuildMaxCpuCountVariable, null);
        var defaultArguments = DotnetBuildEnvironmentManager.StableSlotBuildArguments(0);
        var expectedDefault = Math.Max(2, Environment.ProcessorCount / DotnetBuildEnvironmentManager.StableSlotCount);

        Assert.Equal($"-maxcpucount:{expectedDefault}", MaxCpuCountArgument(defaultArguments));
        Assert.NotEqual("-maxcpucount:1", MaxCpuCountArgument(defaultArguments));
        Assert.Contains("-p:BuildInParallel=false", defaultArguments);

        using var configuredScope = EnvVarScope.ForVariable(DotnetBuildEnvironmentManager.BuildMaxCpuCountVariable, "7");
        var configuredArguments = DotnetBuildEnvironmentManager.StableSlotBuildArguments(0);

        Assert.Equal("-maxcpucount:7", MaxCpuCountArgument(configuredArguments));
        Assert.Contains("-p:BuildInParallel=false", configuredArguments);
    }

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_forwards_args_when_operator_sandbox_config_is_inherited")]
    public void InvokeIsolatedDotnetForwardsArgsWhenOperatorSandboxConfigIsInherited()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = ResolveRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "Invoke-IsolatedDotnet.ps1");
        var root = CreateTempDirectory();
        var shimDirectory = Path.Combine(root, "shim");
        var workDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(shimDirectory);
        Directory.CreateDirectory(workDirectory);
        try
        {
            var logPath = Path.Combine(root, "dotnet.log");
            var shimPath = Path.Combine(shimDirectory, "dotnet.cmd");
            File.WriteAllText(
                shimPath,
                """
                @echo off
                >> "%DOTNET_SHIM_LOG%" echo cwd=%CD%
                >> "%DOTNET_SHIM_LOG%" echo args=%*
                >> "%DOTNET_SHIM_LOG%" echo repo=%MCG_ORCHESTRATOR_REPOSITORY_ROOT%
                >> "%DOTNET_SHIM_LOG%" echo sandbox=%MCG_WORKER_SANDBOX%
                >> "%DOTNET_SHIM_LOG%" echo account=%MCG_WORKER_ACCOUNT%
                >> "%DOTNET_SHIM_LOG%" echo target=%MCG_WORKER_CREDENTIAL_TARGET%
                exit /b 0
                """);

            var startInfo = new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                WorkingDirectory = workDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-InputFormat");
            startInfo.ArgumentList.Add("None");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-GoalPrefix");
            startInfo.ArgumentList.Add("feedbeef");
            startInfo.ArgumentList.Add("-AttemptName");
            startInfo.ArgumentList.Add("Shim Test");
            startInfo.ArgumentList.Add("test");
            startInfo.ArgumentList.Add("Fake.Tests.csproj");
            startInfo.ArgumentList.Add("--no-restore");
            startInfo.ArgumentList.Add("--filter");
            startInfo.ArgumentList.Add("FullyQualifiedName~FocusedTests");
            startInfo.EnvironmentVariables["PATH"] = shimDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.EnvironmentVariables["DOTNET_SHIM_LOG"] = logPath;
            startInfo.EnvironmentVariables[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = Path.Combine(root, "isolated-dotnet");
            startInfo.EnvironmentVariables[WorkerSandboxOptions.EnabledVariable] = "1";
            startInfo.EnvironmentVariables.Remove(WorkerSandboxOptions.DispatchWorkerVariable);
            startInfo.EnvironmentVariables[WorkerSandboxOptions.AccountVariable] = "sandbox-user";
            startInfo.EnvironmentVariables[WorkerSandboxOptions.CredentialTargetVariable] = "sandbox-target";

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start PowerShell.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10000), "Invoke-IsolatedDotnet.ps1 did not exit within 10 seconds.");
            Assert.True(
                process.ExitCode == 0,
                $"Invoke-IsolatedDotnet.ps1 exited {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");

            var log = File.ReadAllText(logPath);
            Assert.True(log.Contains($"cwd={workDirectory}", StringComparison.OrdinalIgnoreCase));
            Assert.True(log.Contains("args=test Fake.Tests.csproj --no-restore --filter FullyQualifiedName~FocusedTests --artifacts-path ", StringComparison.Ordinal));
            Assert.True(log.Contains("-maxcpucount:", StringComparison.Ordinal));
            Assert.True(log.Contains("-p:BuildInParallel=false", StringComparison.Ordinal));
            Assert.True(log.Contains($"repo={workDirectory}", StringComparison.OrdinalIgnoreCase));
            Assert.True(log.Contains("args=build-server shutdown", StringComparison.Ordinal));
            Assert.DoesNotContain("--disable-build-servers", log);
            Assert.DoesNotContain("-p:UseSharedCompilation=false", log);
            Assert.DoesNotContain("sandbox=1", log);
            Assert.DoesNotContain("account=sandbox-user", log);
            Assert.DoesNotContain("target=sandbox-target", log);
            Assert.True(string.IsNullOrWhiteSpace(stdout), stdout);
            Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_updates_AppDll_git_head_marker_after_successful_rebuild")]
    public void InvokeIsolatedDotnetUpdatesAppDllGitHeadMarkerAfterSuccessfulRebuild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = ResolveRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "Invoke-IsolatedDotnet.ps1");
        var root = CreateTempDirectory();
        var shimDirectory = Path.Combine(root, "shim");
        var workDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(shimDirectory);
        Directory.CreateDirectory(workDirectory);
        try
        {
            RunCommand("git", workDirectory, "init", "--initial-branch=main");
            RunCommand("git", workDirectory, "config", "user.email", "test@example.invalid");
            RunCommand("git", workDirectory, "config", "user.name", "Isolated Dotnet Test");
            File.WriteAllText(Path.Combine(workDirectory, "README.md"), "base");
            RunCommand("git", workDirectory, "add", "README.md");
            RunCommand("git", workDirectory, "commit", "-m", "base");
            var expectedHead = RunCommand("git", workDirectory, "rev-parse", "HEAD").Trim();

            var shimPath = Path.Combine(shimDirectory, "dotnet.cmd");
            File.WriteAllText(
                shimPath,
                """
                @echo off
                if "%~1"=="build-server" exit /b 0
                set "APP_DIR=%CD%\src\Mcg.AgentOrchestrator.App\bin\Debug\net10.0"
                mkdir "%APP_DIR%" >nul 2>nul
                echo rebuilt>"%APP_DIR%\Mcg.AgentOrchestrator.App.dll"
                exit /b 0
                """);

            var startInfo = new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                WorkingDirectory = workDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-InputFormat");
            startInfo.ArgumentList.Add("None");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-GoalPrefix");
            startInfo.ArgumentList.Add("feedbeef");
            startInfo.ArgumentList.Add("-AttemptName");
            startInfo.ArgumentList.Add("Marker Test");
            startInfo.ArgumentList.Add("test");
            startInfo.ArgumentList.Add("Fake.Tests.csproj");
            startInfo.ArgumentList.Add("--no-restore");
            startInfo.EnvironmentVariables["PATH"] = shimDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.EnvironmentVariables[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = Path.Combine(root, "isolated-dotnet");
            startInfo.EnvironmentVariables.Remove(WorkerSandboxOptions.DispatchWorkerVariable);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start PowerShell.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10000), "Invoke-IsolatedDotnet.ps1 did not exit within 10 seconds.");
            Assert.True(
                process.ExitCode == 0,
                $"Invoke-IsolatedDotnet.ps1 exited {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");

            var markerPath = Path.Combine(
                workDirectory,
                "src",
                "Mcg.AgentOrchestrator.App",
                "bin",
                "Debug",
                "net10.0",
                "Mcg.AgentOrchestrator.App.dll.git-head");
            Assert.Equal(expectedHead, File.ReadAllText(markerPath).Trim());
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_does_not_update_stale_AppDll_marker_after_non_App_success")]
    public void InvokeIsolatedDotnetDoesNotUpdateStaleAppDllMarkerAfterNonAppSuccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = ResolveRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "Invoke-IsolatedDotnet.ps1");
        var root = CreateTempDirectory();
        var shimDirectory = Path.Combine(root, "shim");
        var workDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(shimDirectory);
        Directory.CreateDirectory(workDirectory);
        try
        {
            RunCommand("git", workDirectory, "init", "--initial-branch=main");
            RunCommand("git", workDirectory, "config", "user.email", "test@example.invalid");
            RunCommand("git", workDirectory, "config", "user.name", "Isolated Dotnet Test");
            File.WriteAllText(Path.Combine(workDirectory, "README.md"), "base");
            RunCommand("git", workDirectory, "add", "README.md");
            RunCommand("git", workDirectory, "commit", "-m", "base");
            var currentHead = RunCommand("git", workDirectory, "rev-parse", "HEAD").Trim();
            Assert.NotEqual("stale-test-head", currentHead);

            var appOutputPath = Path.Combine(
                workDirectory,
                "src",
                "Mcg.AgentOrchestrator.App",
                "bin",
                "Debug",
                "net10.0");
            Directory.CreateDirectory(appOutputPath);
            var appDllPath = Path.Combine(appOutputPath, "Mcg.AgentOrchestrator.App.dll");
            var markerPath = appDllPath + ".git-head";
            File.WriteAllText(appDllPath, "stale app host");
            File.WriteAllText(markerPath, "stale-test-head");
            var appDllLastWriteTime = File.GetLastWriteTimeUtc(appDllPath);

            var shimPath = Path.Combine(shimDirectory, "dotnet.cmd");
            File.WriteAllText(
                shimPath,
                """
                @echo off
                exit /b 0
                """);

            var startInfo = new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                WorkingDirectory = workDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-InputFormat");
            startInfo.ArgumentList.Add("None");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-GoalPrefix");
            startInfo.ArgumentList.Add("feedbeef");
            startInfo.ArgumentList.Add("-AttemptName");
            startInfo.ArgumentList.Add("Non App Marker Test");
            startInfo.ArgumentList.Add("test");
            startInfo.ArgumentList.Add("Fake.Tests.csproj");
            startInfo.ArgumentList.Add("--no-restore");
            startInfo.EnvironmentVariables["PATH"] = shimDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.EnvironmentVariables[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = Path.Combine(root, "isolated-dotnet");
            startInfo.EnvironmentVariables.Remove(WorkerSandboxOptions.DispatchWorkerVariable);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start PowerShell.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10000), "Invoke-IsolatedDotnet.ps1 did not exit within 10 seconds.");
            Assert.True(
                process.ExitCode == 0,
                $"Invoke-IsolatedDotnet.ps1 exited {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");

            Assert.Equal("stale-test-head", File.ReadAllText(markerPath).Trim());
            Assert.Equal(appDllLastWriteTime, File.GetLastWriteTimeUtc(appDllPath));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_blocks_worker_dispatch_before_dotnet_launch")]
    public void InvokeIsolatedDotnetBlocksWorkerDispatchBeforeDotnetLaunch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = ResolveRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "Invoke-IsolatedDotnet.ps1");
        var root = CreateTempDirectory();
        var shimDirectory = Path.Combine(root, "shim");
        var workDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(shimDirectory);
        Directory.CreateDirectory(workDirectory);
        try
        {
            var logPath = Path.Combine(root, "dotnet.log");
            var shimPath = Path.Combine(shimDirectory, "dotnet.cmd");
            File.WriteAllText(
                shimPath,
                """
                @echo off
                >> "%DOTNET_SHIM_LOG%" echo args=%*
                exit /b 0
                """);

            var startInfo = new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                WorkingDirectory = workDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-InputFormat");
            startInfo.ArgumentList.Add("None");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-GoalPrefix");
            startInfo.ArgumentList.Add("feedbeef");
            startInfo.ArgumentList.Add("test");
            startInfo.ArgumentList.Add("Fake.Tests.csproj");
            startInfo.Environment["PATH"] = shimDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment["DOTNET_SHIM_LOG"] = logPath;
            startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = Path.Combine(root, "isolated-dotnet");
            startInfo.Environment[WorkerSandboxOptions.DispatchWorkerVariable] = "1";

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start PowerShell.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10000), "Invoke-IsolatedDotnet.ps1 did not exit within 10 seconds.");

            Assert.NotEqual(0, process.ExitCode);
            Assert.True(!File.Exists(logPath), "dotnet shim should not be invoked for worker-side self-verification.");
            Assert.True(stderr.Contains("Worker-side .NET self-verification is disabled", StringComparison.Ordinal), stderr);
            Assert.True(string.IsNullOrWhiteSpace(stdout), stdout);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private static string ResolveRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (IsRepositoryRoot(directory.FullName))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        var environmentRoot = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT");
        if (IsRepositoryRoot(environmentRoot))
        {
            return Path.GetFullPath(environmentRoot!);
        }

        throw new InvalidOperationException("Could not resolve repository root for Invoke-IsolatedDotnet.ps1.");
    }

    private static bool IsRepositoryRoot(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            File.Exists(Path.Combine(path, "scripts", "Invoke-IsolatedDotnet.ps1"));
    }

    private static string RunCommand(string fileName, string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10000), $"{fileName} did not exit within 10 seconds.");
        Assert.True(
            process.ExitCode == 0,
            $"{fileName} {string.Join(' ', arguments)} exited {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        return stdout;
    }

    private static string EncodePowerShell(string script)
    {
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    private static void WriteForeignOwnerMarker(string artifactsPath)
    {
        File.WriteAllText(
            Path.Combine(artifactsPath, ".mcg-artifacts-owner.json"),
            JsonSerializer.Serialize(new
            {
                version = 1,
                ownerToken = "foreign-owner",
                ownerProcessId = 123456789,
                machineName = Environment.MachineName,
                lastAcquiredAt = DateTimeOffset.UtcNow
            }));
    }

    private static void WriteAttemptMetadata(
        string path,
        string attemptId,
        string outcome,
        int ownerProcessId)
    {
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                attemptId,
                outcome,
                ownerProcessId
            }));
    }

    private static (string FixtureRoot, string LockedPath) CreateLandingFixtureLockPath()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "mcg-landing-tests", Guid.NewGuid().ToString("N"));
        return (fixtureRoot, CreateLandingFixtureLockPath(fixtureRoot));
    }

    private static string CreateLandingFixtureLockPath(string fixtureRoot)
    {
        var lockedPath = Path.Combine(
            fixtureRoot,
            ".orchestrator-worktrees",
            "28f428da",
            "src",
            "Mcg.AgentOrchestrator.Core",
            "bin",
            "Debug",
            "net10.0",
            "Mcg.AgentOrchestrator.Core.dll");
        return lockedPath;
    }

    private static Process StartFileHolder(string lockedPath, string readyPath, string releasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockedPath)!);
        var script = string.Join(
            Environment.NewLine,
            [
                "$ErrorActionPreference = 'Stop'",
                $"$stream = [System.IO.File]::Open('{EscapePowerShell(lockedPath)}', [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)",
                $"[System.IO.File]::WriteAllText('{EscapePowerShell(readyPath)}', 'ready')",
                "try {",
                $"  while (-not [System.IO.File]::Exists('{EscapePowerShell(releasePath)}')) {{ Start-Sleep -Milliseconds 100 }}",
                "} finally {",
                "  $stream.Dispose()",
                "}"
            ]);
        return Process.Start(new ProcessStartInfo
        {
            FileName = ResolvePowerShell(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {EncodePowerShell(script)}"
        }) ?? throw new InvalidOperationException("Failed to start fixture holder process.");
    }

    private static string ResolvePowerShell()
    {
        foreach (var name in new[] { "pwsh", "powershell" })
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = name,
                    Arguments = "-NoProfile -NonInteractive -Command \"$PSVersionTable.PSVersion.Major\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                if (process is not null && process.WaitForExit(5000) && process.ExitCode == 0)
                {
                    return ResolveExecutablePath(name) ?? name;
                }
            }
            catch
            {
            }
        }

        return "powershell";
    }

    private static string? ResolveHandleExecutableForTests()
    {
        var explicitPath = Environment.GetEnvironmentVariable("MCG_HANDLE64");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        foreach (var directory in HandleProbeSearchDirectories())
        {
            foreach (var name in new[] { "handle64.exe", "handle.exe" })
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string FormatAttributionDiagnostic(
        string handleExecutable,
        string lockedPath,
        int expectedHolderPid,
        TimeSpan elapsed,
        BuildLockAttribution attribution)
    {
        var holders = attribution.Holders.Count == 0
            ? "none"
            : string.Join(
                " | ",
                attribution.Holders.Select(holder =>
                    $"pid={holder.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
                    $"name={holder.ProcessName ?? "unknown"} owned={holder.IsOrchestratorOwned} commandLine={holder.CommandLine ?? string.Empty}"));
        return
            $"handle={handleExecutable}; lockedPath={lockedPath}; expectedPid={expectedHolderPid}; " +
            $"elapsed={elapsed}; source={attribution.Source}; holders={holders}";
    }

    private static IEnumerable<string> HandleProbeSearchDirectories()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory.Trim()))
            {
                yield return directory.Trim();
            }
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var directory in new[]
        {
            Path.Combine(userProfile, "Downloads"),
            Path.Combine(userProfile, "Downloads", "Handle"),
            Path.Combine(userProfile, "Downloads", "SysinternalsSuite"),
            Path.Combine(userProfile, "Desktop"),
            Path.GetTempPath(),
            @"C:\Sysinternals",
            @"C:\SysinternalsSuite",
            @"C:\Tools",
            @"C:\Tools\Sysinternals",
            Path.Combine(Environment.GetEnvironmentVariable("ChocolateyInstall") ?? string.Empty, "bin"),
            @"C:\ProgramData\chocolatey\bin",
            Path.Combine(userProfile, "scoop", "shims")
        })
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                yield return directory;
            }
        }
    }

    private static string? ResolveExecutablePath(string name)
    {
        var extensions = Path.HasExtension(name)
            ? [string.Empty]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim(), name + extension.ToLowerInvariant());
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool CanStartRestartManagerForTests()
    {
        uint session = 0;
        var result = RmStartSessionForTests(out session, 0, Guid.NewGuid().ToString("N"));
        if (result != 0)
        {
            return false;
        }

        _ = RmEndSessionForTests(session);
        return true;
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Xunit.Fact(DisplayName = "ProcessSpawnGuard_clears_inheritable_state_db_file_handles")]
    public void ProcessSpawnGuardClearsInheritableStateDbFileHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var directory = Path.Combine(Path.GetTempPath(), $"mcg-state-handle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.db");
        try
        {
            using var handle = CreateInheritableFileHandle(path);
            Assert.True(ProcessSpawnGuard.IsHandleInheritable(handle.DangerousGetHandle()));

            var cleared = ProcessSpawnGuard.ClearInheritableFileHandles("state.db");

            Assert.True(cleared >= 1);
            Assert.False(ProcessSpawnGuard.IsHandleInheritable(handle.DangerousGetHandle()));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static string ArgumentValue(IReadOnlyList<string> arguments, string name)
    {
        var index = -1;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Equals(name, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        Assert.True(index >= 0 && index <= arguments.Count - 2);
        return arguments[index + 1];
    }

    private static string MaxCpuCountArgument(IReadOnlyList<string> arguments)
    {
        var argument = arguments.SingleOrDefault(argument => argument.StartsWith("-maxcpucount:", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(argument));
        return argument!;
    }

    private static void WriteProjectArtifacts(string artifactsPath, string project, string content)
    {
        var projectName = Path.GetFileNameWithoutExtension(project);
        var binPath = Path.Combine(artifactsPath, "bin", projectName, "debug_net10.0");
        var objPath = Path.Combine(artifactsPath, "obj", projectName, "debug_net10.0");
        Directory.CreateDirectory(binPath);
        Directory.CreateDirectory(objPath);
        File.WriteAllText(Path.Combine(binPath, "cache.txt"), content);
        File.WriteAllText(Path.Combine(objPath, "cache.obj"), content);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static Process StartSleepProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-InputFormat");
        startInfo.ArgumentList.Add("None");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start sleep process.");
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
        }
    }

    private static int SlotIndexFromPath(string path)
    {
        var slotName = Path.GetFileName(Path.GetDirectoryName(path));
        Assert.StartsWith("slot-", slotName, StringComparison.Ordinal);
        return int.Parse(slotName["slot-".Length..], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SafeFileHandle CreateInheritableFileHandle(string path)
    {
        var securityAttributes = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true
        };
        var handle = CreateFile(
            path,
            0x40000000,
            0x00000001 | 0x00000002,
            ref securityAttributes,
            2,
            0x80,
            IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SecurityAttributes securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, EntryPoint = "RmStartSession")]
    private static extern int RmStartSessionForTests(out uint sessionHandle, int sessionFlags, string sessionKey);

    [DllImport("rstrtmgr.dll", EntryPoint = "RmEndSession")]
    private static extern int RmEndSessionForTests(uint sessionHandle);

    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;
        private readonly string? _root;

        private EnvVarScope(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            _root = value;
            Environment.SetEnvironmentVariable(name, value);
        }

        public static EnvVarScope ForIsolatedDotnetRoot()
        {
            return new EnvVarScope(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
                Path.Combine(Path.GetTempPath(), $"{DotnetBuildEnvironmentManager.RootDirectoryName}-test-{Guid.NewGuid():N}"));
        }

        public static EnvVarScope ForVariable(string name, string? value)
        {
            return new EnvVarScope(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _originalValue);
            if (string.IsNullOrWhiteSpace(_root))
            {
                return;
            }

            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // Best effort; a failed test may leave a stream open for failure inspection.
            }
        }
    }
}
