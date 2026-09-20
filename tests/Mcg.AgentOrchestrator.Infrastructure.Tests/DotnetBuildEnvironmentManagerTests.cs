using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;

[Xunit.Collection(TestCollections.DotnetBuildEnvironmentManagerStaticHooks)]
public sealed partial class DotnetBuildEnvironmentManagerTests : DotnetBuildEnvironmentManagerRootedTestBase
{
    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_lease_defaults_use_system_time_and_thread_sleep")]
    public void DotnetBuildEnvironmentManagerLeaseDefaultsUseSystemTimeAndThreadSleep()
    {
        Assert.Same(TimeProvider.System, DotnetBuildEnvironmentManager.DefaultLeaseTimeProviderForTests);
        Assert.Null(DotnetBuildEnvironmentManager.DefaultLeaseSleepForTests.Target);
        Assert.Equal(typeof(Thread), DotnetBuildEnvironmentManager.DefaultLeaseSleepForTests.Method.DeclaringType);
        Assert.Equal(nameof(Thread.Sleep), DotnetBuildEnvironmentManager.DefaultLeaseSleepForTests.Method.Name);
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_nested_hermetic_profile_preserves_shared_LocalLow_grid")]
    public void DotnetBuildEnvironmentManagerNestedHermeticProfilePreservesSharedLocalLowGrid()
    {
        var realLocalAppData = Path.Combine(Path.GetTempPath(), "real-profile", "AppData", "Local");
        var redirectedKnownFolder = Path.Combine(Path.GetTempPath(), "mcg-hvp", "AppData", "Local");

        var resolved = DotnetBuildEnvironmentManager.ResolveIsolatedRootBase(
            overridden: null,
            localAppDataVariable: realLocalAppData,
            localAppDataKnownFolder: redirectedKnownFolder,
            tempPath: Path.GetTempPath(),
            isWindows: true);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                realLocalAppData,
                "..",
                "LocalLow",
                DotnetBuildEnvironmentManager.RootDirectoryName)),
            resolved);
        Assert.DoesNotContain(redirectedKnownFolder, resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_reuses_goal_lease_with_metadata_and_cleanup")]
    public void DotnetBuildEnvironmentManagerReusesGoalLeaseWithMetadataAndCleanup()
    {
        var goalId = new GoalId("feedbeeffeedbeeffeedbeeffeedbeef");
        try
        {
            var first = RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, goalId, "Acceptance");
            var second = RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, goalId, "Acceptance");

            Assert.Equal(RootedDotnetBuildEnvironmentManager.GoalRoot(StorageRoot, goalId), first.RootPath);
            Assert.Equal("goal-feedbeef", first.LeaseId);
            Assert.Equal(first.LeaseId, second.LeaseId);
            Assert.Equal(first.RootPath, second.RootPath);
            Assert.Equal(first.ArtifactsPath, second.ArtifactsPath);
            Assert.True(second.ReusedGoalLease);
            Assert.Equal(Path.Combine(first.RootPath, "artifacts"), first.ArtifactsPath);
            Assert.Contains(Path.Combine("build-slots", "build-"), first.ExecutionLockPath, StringComparison.OrdinalIgnoreCase);
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
            var other = RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, otherGoalId, "Acceptance");
            Assert.Equal(Path.Combine(other.RootPath, "artifacts"), other.ArtifactsPath);
            Assert.NotEqual(first.ArtifactsPath, other.ArtifactsPath);
            Assert.True(RootedDotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(StorageRoot, otherGoalId));
            using var metadata = JsonDocument.Parse(File.ReadAllText(second.LeaseMetadataPath!));
            Assert.Equal(goalId.Value, metadata.RootElement.GetProperty("goalId").GetString());
            Assert.Equal(first.LeaseId, metadata.RootElement.GetProperty("leaseId").GetString());
            Assert.Equal(second.ArtifactsPath, metadata.RootElement.GetProperty("artifactsPath").GetString());

            Assert.True(RootedDotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(StorageRoot, goalId));
            Assert.False(Directory.Exists(first.RootPath));
        }
        finally
        {
            RootedDotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(StorageRoot, goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_concurrent_goal_resolution_is_read_only")]
    public async Task DotnetBuildEnvironmentManagerConcurrentGoalResolutionIsReadOnly()
    {
        var goalId = new GoalId("1234567890abcdef1234567890abcdef");
        var initial = RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, goalId, "acceptance");
        var metadataPath = initial.LeaseMetadataPath
            ?? throw new InvalidOperationException("Expected goal lease metadata.");
        try
        {
            var metadataBefore = await File.ReadAllBytesAsync(metadataPath);

            var resolved = await Task.WhenAll(
                Enumerable.Range(0, 32)
                    .Select(_ => Task.Run(() =>
                        RootedDotnetBuildEnvironmentManager.ResolveGoalEnvironment(StorageRoot, goalId))));

            var metadataAfter = await File.ReadAllBytesAsync(metadataPath);
            Assert.Equal(metadataBefore, metadataAfter);
            Assert.All(resolved, environment =>
            {
                Assert.Equal(initial.RootPath, environment.RootPath);
                Assert.Equal(initial.ArtifactsPath, environment.ArtifactsPath);
                Assert.Equal(initial.ExecutionLockPath, environment.ExecutionLockPath);
                Assert.Equal(initial.LeaseMetadataPath, environment.LeaseMetadataPath);
            });
        }
        finally
        {
            RootedDotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(StorageRoot, goalId);
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_manual_attempts_use_invocation_local_artifacts")]
    public void DotnetBuildEnvironmentManagerManualAttemptsUseInvocationLocalArtifacts()
    {
        var first = RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, null, "Acceptance");
        var second = RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, null, "Retry");

        Assert.StartsWith("run-", first.LeaseId, StringComparison.Ordinal);
        Assert.NotEqual(first.RootPath, second.RootPath);
        Assert.NotEqual(first.ArtifactsPath, second.ArtifactsPath);
        Assert.Contains(Path.Combine("runs", Environment.ProcessId.ToString()), first.ArtifactsPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine("build-slots", "build-"), first.ExecutionLockPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(first.Arguments.Contains(first.ArtifactsPath));
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_candidate_slot_count_limits_goal_and_stable_attempts")]
    public void DotnetBuildEnvironmentManagerCandidateSlotCountLimitsGoalAndStableAttempts()
    {
        var goalId = new GoalId("facefeedfacefeedfacefeedfacefeed");
        try
        {
            var environment = RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, goalId, "Acceptance", slotCount: 1);

            Assert.Equal(Path.Combine(RootedDotnetBuildEnvironmentManager.GoalRoot(StorageRoot, goalId), "artifacts"), environment.ArtifactsPath);
            var error = Assert.Throws<ArgumentOutOfRangeException>(
                () => RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 1, slotCount: 1));
            Assert.Contains("requested slot count", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            RootedDotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(StorageRoot, goalId);
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
        var cacheRoot = Path.Combine(CreateTempDirectory(), "cache");
        var cache = new DotnetBaseBuildCache(cacheRoot);
        var sourceArtifacts = Path.Combine(Path.GetTempPath(), "mcg-cache-source", Guid.NewGuid().ToString("N"));
        const string project = "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj";
        try
        {
            WriteProjectArtifacts(sourceArtifacts, project, "slot");
            cache.Publish("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sourceArtifacts, [project]);
            var slot = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);

            var restore = cache.Restore("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", slot.ArtifactsPath, [project]);

            Assert.True(restore.AllHit);
            Assert.Contains(Path.Combine("runs", $"p{Environment.ProcessId}-build-0", "artifacts"), slot.ArtifactsPath, StringComparison.OrdinalIgnoreCase);
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
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
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
            DotnetBuildEnvironmentManager.ShutdownBuildServersForTests =
                AssemblyBuildServerShutdownIsolation.SafeDefault;
            LockAttribution.AttributeForTests = null;
        }
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_live_attempt_custody_refuses_foreign_owner_takeover")]
    public void DotnetBuildEnvironmentManagerLiveAttemptCustodyRefusesForeignOwnerTakeover()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var attemptId = "live-attempt-123";
        var metadataPath = Path.Combine(environment.RootPath, $"{attemptId}.attempt.json");
        var evidencePath = Path.Combine(environment.ArtifactsPath, "TestResults", "completed-lane.trx");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        File.WriteAllText(evidencePath, "receipt");
        WriteForeignOwnerMarker(environment.ArtifactsPath);
        WriteAttemptMetadata(metadataPath, attemptId, 0, Environment.ProcessId);
        AcceptanceAttemptArtifactCustody.Write(
            environment.ArtifactsPath,
            attemptId,
            metadataPath,
            Environment.ProcessId);

        var exception = Assert.Throws<AcceptanceAttemptArtifactCustodyException>(
            () => AcceptanceAttemptArtifactCustody.ThrowIfLiveCustodianBlocksTakeover(
                environment.ArtifactsPath));
        var slotsBusy = Assert.Throws<DotnetBuildSlotsBusyException>(
            () => DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment, TimeSpan.Zero));

        Assert.Equal(attemptId, exception.AttemptId);
        Assert.Contains(attemptId, exception.Message, StringComparison.Ordinal);
        Assert.Contains(environment.ArtifactsPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(environment.LeaseId, slotsBusy.SlotsBusy.WantedBy);
        Assert.True(File.Exists(evidencePath));
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_terminal_attempt_custody_allows_foreign_owner_takeover")]
    public void DotnetBuildEnvironmentManagerTerminalAttemptCustodyAllowsForeignOwnerTakeover()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var attemptId = "failed-attempt-123";
        var metadataPath = Path.Combine(environment.RootPath, $"{attemptId}.attempt.json");
        var evidencePath = Path.Combine(environment.ArtifactsPath, "TestResults", "stale.trx");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        File.WriteAllText(evidencePath, "stale");
        WriteForeignOwnerMarker(environment.ArtifactsPath);
        WriteAttemptMetadata(metadataPath, attemptId, 2, Environment.ProcessId);
        AcceptanceAttemptArtifactCustody.Write(
            environment.ArtifactsPath,
            attemptId,
            metadataPath,
            Environment.ProcessId);

        using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment, TimeSpan.Zero);

        Assert.False(File.Exists(evidencePath));
        Assert.False(File.Exists(AcceptanceAttemptArtifactCustody.MarkerPath(environment.ArtifactsPath)));
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_stale_lease_metadata_does_not_override_live_attempt_custody")]
    public void DotnetBuildEnvironmentManagerStaleLeaseMetadataDoesNotOverrideLiveAttemptCustody()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var attemptId = "stale-custody-attempt-123";
        var metadataPath = Path.Combine(environment.RootPath, $"{attemptId}.attempt.json");
        var evidencePath = Path.Combine(environment.ArtifactsPath, "TestResults", "stale.trx");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        File.WriteAllText(evidencePath, "stale");
        File.WriteAllText(environment.ExecutionLockPath, "999999");
        WriteForeignOwnerMarker(environment.ArtifactsPath);
        WriteAttemptMetadata(metadataPath, attemptId, 0, Environment.ProcessId);
        AcceptanceAttemptArtifactCustody.Write(
            environment.ArtifactsPath,
            attemptId,
            metadataPath,
            Environment.ProcessId);

        var blocked = Assert.Throws<DotnetBuildSlotsBusyException>(
            () => DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment, TimeSpan.Zero));

        Assert.Equal(environment.LeaseId, blocked.SlotsBusy.WantedBy);
        Assert.True(File.Exists(evidencePath));
        Assert.True(File.Exists(AcceptanceAttemptArtifactCustody.MarkerPath(environment.ArtifactsPath)));
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_stale_remote_custody_marker_allows_foreign_owner_takeover")]
    public void DotnetBuildEnvironmentManagerStaleRemoteCustodyMarkerAllowsForeignOwnerTakeover()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var evidencePath = Path.Combine(environment.ArtifactsPath, "stale.txt");
        File.WriteAllText(evidencePath, "stale");
        WriteForeignOwnerMarker(environment.ArtifactsPath);
        File.WriteAllText(
            AcceptanceAttemptArtifactCustody.MarkerPath(environment.ArtifactsPath),
            JsonSerializer.Serialize(new
            {
                version = 1,
                attemptId = "orphaned-remote-attempt",
                livenessCheckHint = Path.Combine(environment.RootPath, "missing.attempt.json"),
                ownerProcessId = Environment.ProcessId,
                machineName = "other-machine",
                acquiredAt = DateTimeOffset.UtcNow.AddHours(-7)
            }));

        using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment, TimeSpan.Zero);

        Assert.False(File.Exists(evidencePath));
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_dead_local_custodian_marker_is_cleared_during_lease_reclaim")]
    public void DotnetBuildEnvironmentManagerDeadLocalCustodianMarkerIsClearedDuringLeaseReclaim()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var evidencePath = Path.Combine(environment.ArtifactsPath, "stale.txt");
        File.WriteAllText(evidencePath, "stale");
        File.WriteAllText(environment.ExecutionLockPath, "999999");
        WriteForeignOwnerMarker(environment.ArtifactsPath);
        AcceptanceAttemptArtifactCustody.Write(
            environment.ArtifactsPath,
            "dead-local-attempt",
            Path.Combine(environment.RootPath, "missing.attempt.json"),
            999999);

        using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment, TimeSpan.Zero);

        Assert.False(File.Exists(evidencePath));
        Assert.False(File.Exists(AcceptanceAttemptArtifactCustody.MarkerPath(environment.ArtifactsPath)));
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_missing_custody_marker_preserves_foreign_owner_takeover")]
    public void DotnetBuildEnvironmentManagerMissingCustodyMarkerPreservesForeignOwnerTakeover()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var evidencePath = Path.Combine(environment.ArtifactsPath, "stale.txt");
        File.WriteAllText(evidencePath, "stale");
        WriteForeignOwnerMarker(environment.ArtifactsPath);

        using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment, TimeSpan.Zero);

        Assert.False(File.Exists(evidencePath));
    }

    [Xunit.Fact(DisplayName = "DotnetBuildEnvironmentManager_terminal_cleanup_releases_exact_run_custody")]
    public void DotnetBuildEnvironmentManagerTerminalCleanupReleasesExactRunCustody()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, null, "coverage-discovery");
        var attemptId = "manual-slot-attempt";
        var metadataPath = Path.Combine(environment.RootPath, $"{attemptId}.attempt.json");
        WriteAttemptMetadata(metadataPath, attemptId, 0, Environment.ProcessId);
        AcceptanceAttemptArtifactCustody.Write(
            environment.ArtifactsPath,
            attemptId,
            metadataPath,
            Environment.ProcessId);

        AcceptanceAttemptArtifactCustody.Release(environment.ArtifactsPath, attemptId);

        Assert.False(File.Exists(AcceptanceAttemptArtifactCustody.MarkerPath(environment.ArtifactsPath)));
    }

    [Xunit.Fact(DisplayName = "InvokeWorkerBuildCheck_preserves_per_goal_root_and_shared_build_slot_grid")]
    public void InvokeWorkerBuildCheckPreservesPerGoalRootAndSharedBuildSlotGrid()
    {
        var source = File.ReadAllText(Path.Combine(ResolveRepositoryRoot(), "scripts", "Invoke-WorkerBuildCheck.ps1"));
        var isolatedDotnetSource = File.ReadAllText(Path.Combine(ResolveRepositoryRoot(), "scripts", "Invoke-IsolatedDotnet.ps1"));
        var rootResolverStart = source.IndexOf("function Get-IsolatedRootBase", StringComparison.Ordinal);
        var rootResolverEnd = source.IndexOf("function Get-HostTempBase", rootResolverStart, StringComparison.Ordinal);
        var isolatedRootResolverStart = isolatedDotnetSource.IndexOf("function Get-IsolatedRootBase", StringComparison.Ordinal);
        var isolatedRootResolverEnd = isolatedDotnetSource.IndexOf("function Get-HostTempBase", isolatedRootResolverStart, StringComparison.Ordinal);

        Assert.Contains(@"goals\$safeGoalPrefix""", source, StringComparison.Ordinal);
        Assert.Contains(@"build-slots\$buildSlotName.lock""", source, StringComparison.Ordinal);
        Assert.True(rootResolverStart >= 0 && rootResolverEnd > rootResolverStart);
        Assert.True(isolatedRootResolverStart >= 0 && isolatedRootResolverEnd > isolatedRootResolverStart);
        Assert.Equal(
            isolatedDotnetSource[isolatedRootResolverStart..isolatedRootResolverEnd],
            source[rootResolverStart..rootResolverEnd]);
        Assert.DoesNotContain(@"slots\operator-build", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"operators\worker-build", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-StableSlotName", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"Join-Path $leaseRoot ""lease.lock""", source, StringComparison.Ordinal);
        Assert.Contains("Assert-CustodyAllowsTakeover -ArtifactsPath $Path", source, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "MtpTestRunner_preserves_absolute_targets_and_selects_manifest_projects_from_solution")]
    public void MtpTestRunnerPreservesAbsoluteTargetsAndSelectsManifestProjectsFromSolution()
    {
        var source = File.ReadAllText(Path.Combine(ResolveRepositoryRoot(), "scripts", "MtpTestRunner.psm1"));

        Assert.Contains("[System.IO.Path]::IsPathRooted($Target)", source, StringComparison.Ordinal);
        Assert.Contains("[System.IO.Path]::GetFullPath($Target)", source, StringComparison.Ordinal);
        Assert.Contains("$solutionText.IndexOf($project", source, StringComparison.Ordinal);
    }

    internal static void PrepareFocusedArtifacts(
        string workDirectory,
        string projectFile,
        string projectName,
        string goalPrefix,
        string isolatedRoot)
    {
        var slot = goalPrefix.Aggregate(
            0,
            (hash, character) => (hash + char.ToLowerInvariant(character)) % DotnetBuildEnvironmentManager.BuildConcurrencySlotCount);
        var artifactsPath = Path.Combine(
            isolatedRoot,
            "goals",
            goalPrefix,
            "focused-artifacts",
            $"build-{slot}");
        var artifactOutput = Path.Combine(artifactsPath, "bin", projectName, "debug");
        var testOutput = Path.GetDirectoryName(typeof(DotnetBuildEnvironmentManagerTests).Assembly.Location)!;
        foreach (var sourcePath in Directory.GetFiles(testOutput, "*", SearchOption.AllDirectories))
        {
            var targetPath = Path.Combine(artifactOutput, Path.GetRelativePath(testOutput, sourcePath));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath);
        }
        File.WriteAllText(
            Path.Combine(artifactsPath, ".mcg-artifacts-owner.json"),
            JsonSerializer.Serialize(new { ownerToken = $"focused-{goalPrefix}-build-{slot}" }));
        var commit = RunCommand("git", workDirectory, "rev-parse", "HEAD").Trim();
        var cleanDigest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();
        var fingerprintInput = string.Join(
            '\n',
            commit,
            cleanDigest,
            Path.GetFullPath(Path.Combine(workDirectory, projectFile)),
            "Debug",
            string.Empty);
        var fingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput))).ToLowerInvariant();
        File.WriteAllText(
            Path.Combine(artifactsPath, ".mcg-focused-build-state.json"),
            JsonSerializer.Serialize(new { fingerprint }));
    }

    internal static string BuildFocusedRunnerFailureDiagnostics(
        (int ExitCode, string Stdout, string Stderr) result,
        string receiptPath,
        string isolatedRoot)
    {
        var diagnostics = new StringBuilder();
        var exitClassification = result.ExitCode switch
        {
            2 => "exit-2-budget-exceeded",
            3 => "exit-3-distinct-runner-failure",
            5 => "exit-5-harness-setup-or-no-tests",
            _ => "unexpected-exit"
        };
        diagnostics.Append("focusedRunnerExitCode=").Append(result.ExitCode)
            .Append("; exitClassification=").AppendLine(exitClassification);

        if (!File.Exists(receiptPath))
        {
            diagnostics.AppendLine("receiptStatus=missing; receiptOutcome=<unavailable>; receiptReason=<unavailable>; receiptExitCode=<unavailable>");
            diagnostics.AppendLine("leaseState=unknown; actualLeaseState=unknown; ownedProcessState=unknown");
        }
        else
        {
            try
            {
                using var receipt = JsonDocument.Parse(File.ReadAllText(receiptPath));
                var root = receipt.RootElement;
                diagnostics.Append("receiptStatus=parsed; receiptOutcome=").Append(JsonDiagnosticValue(root, "outcome"))
                    .Append("; receiptReason=").Append(JsonDiagnosticValue(root, "reason"))
                    .Append("; receiptExitCode=").Append(JsonDiagnosticValue(root, "exitCode"))
                    .Append("; leaseReleased=").Append(JsonDiagnosticValue(root, "leaseReleased"))
                    .Append("; leaseState=").AppendLine(JsonDiagnosticValue(root, "leaseState"));

                var slotId = JsonDiagnosticValue(root, "slotId");
                if (slotId is "<null>" or "<missing>")
                {
                    diagnostics.AppendLine("actualLeaseState=not-acquired");
                }
                else
                {
                    try
                    {
                        var slotPath = Path.Combine(isolatedRoot, "build-slots", slotId + ".lock");
                        diagnostics.Append("actualLeaseState=")
                            .AppendLine(IsByteRangeLocked(slotPath) ? "locked" : "released");
                    }
                    catch (Exception exception)
                    {
                        diagnostics.Append("actualLeaseState=inspection-failed:").AppendLine(exception.GetType().Name);
                    }
                }

                if (!root.TryGetProperty("childProcess", out var childProcess) ||
                    childProcess.ValueKind != JsonValueKind.Object)
                {
                    diagnostics.AppendLine("childProcessStatus=missing; ownedProcessState=unknown; liveDescendantProcessIds=<unavailable>");
                }
                else
                {
                    diagnostics.Append("childProcessId=").Append(JsonDiagnosticValue(childProcess, "processId"))
                        .Append("; childStartedAt=").Append(JsonDiagnosticValue(childProcess, "startedAt"))
                        .Append("; childExecutable=").Append(JsonDiagnosticValue(childProcess, "executable"))
                        .Append("; childIdentityStatus=").Append(JsonDiagnosticValue(childProcess, "identityStatus"))
                        .Append("; childIdentityError=").Append(JsonDiagnosticValue(childProcess, "identityError"))
                        .Append("; terminationRequested=").Append(JsonDiagnosticValue(childProcess, "terminationRequested"))
                        .Append("; terminationSucceeded=").Append(JsonDiagnosticValue(childProcess, "terminationSucceeded"))
                        .Append("; terminationError=").Append(JsonDiagnosticValue(childProcess, "terminationError"))
                        .Append("; childStateAfter=").AppendLine(JsonDiagnosticValue(childProcess, "stateAfter"));

                    var ownedProcessState = GetFocusedChildIdentityState(childProcess);
                    var descendants = TryGetFocusedLiveDescendantProcessIds(
                        childProcess,
                        out var descendantInspectionStatus);
                    diagnostics.Append("ownedProcessState=").Append(ownedProcessState)
                        .Append("; liveDescendantInspectionStatus=").Append(descendantInspectionStatus)
                        .Append("; liveDescendantProcessIds=[")
                        .Append(string.Join(",", descendants))
                        .AppendLine("]");
                }
            }
            catch (JsonException exception)
            {
                diagnostics.Append("receiptStatus=malformed; receiptParseError=").AppendLine(exception.Message);
                diagnostics.AppendLine("receiptOutcome=<unavailable>; receiptReason=<unavailable>; receiptExitCode=<unavailable>");
                diagnostics.AppendLine("leaseState=unknown; actualLeaseState=unknown; ownedProcessState=unknown");
            }
            catch (Exception exception)
            {
                diagnostics.Append("receiptStatus=setup-failure; receiptReadError=").AppendLine(exception.Message);
                diagnostics.AppendLine("receiptOutcome=<unavailable>; receiptReason=<unavailable>; receiptExitCode=<unavailable>");
                diagnostics.AppendLine("leaseState=unknown; actualLeaseState=unknown; ownedProcessState=unknown");
            }
        }

        diagnostics.Append("stdout=").AppendLine(result.Stdout)
            .Append("stderr=").AppendLine(result.Stderr);
        return diagnostics.ToString();
    }

    internal static string JsonDiagnosticValue(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return "<missing>";
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => "<null>",
            JsonValueKind.String => value.GetString() ?? "<null>",
            _ => value.GetRawText()
        };
    }

    internal static bool TryReadJsonDocument(string path, out JsonDocument? document)
    {
        document = null;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<int> TryGetFocusedLiveDescendantProcessIds(
        JsonElement childProcess,
        out string inspectionStatus,
        Func<int, IReadOnlyList<int>>? inspectDescendants = null)
    {
        if (!childProcess.TryGetProperty("processId", out var processIdElement) ||
            processIdElement.ValueKind != JsonValueKind.Number ||
            !processIdElement.TryGetInt32(out var processId) ||
            processId <= 0)
        {
            inspectionStatus = "not-started";
            return [];
        }

        var identityStatus = JsonDiagnosticValue(childProcess, "identityStatus");
        if (!string.Equals(identityStatus, "confirmed", StringComparison.Ordinal))
        {
            inspectionStatus = "identity-unconfirmed:" + identityStatus;
            return [];
        }

        try
        {
            var descendants = (inspectDescendants ?? WorkerProcessJobs.ListLiveDescendantProcessIds)(processId);
            inspectionStatus = "confirmed";
            return descendants;
        }
        catch (Exception exception)
        {
            inspectionStatus = "inspection-failed:" + exception.GetType().Name;
            return [];
        }
    }

    internal static string GetFocusedChildIdentityState(JsonElement childProcess)
    {
        if (!childProcess.TryGetProperty("processId", out var processIdElement) ||
            processIdElement.ValueKind != JsonValueKind.Number ||
            !processIdElement.TryGetInt32(out var processId) ||
            processId <= 0)
        {
            return "not-started";
        }

        var identityStatus = JsonDiagnosticValue(childProcess, "identityStatus");
        if (!string.Equals(identityStatus, "confirmed", StringComparison.Ordinal))
        {
            return "identity-unconfirmed:" + identityStatus;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var expectedStartedAt = JsonDiagnosticValue(childProcess, "startedAt");
            var expectedExecutable = JsonDiagnosticValue(childProcess, "executable");
            if (!DateTimeOffset.TryParse(
                    expectedStartedAt,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var startedAt))
            {
                return "running-identity-unavailable";
            }

            var startMatches = Math.Abs((process.StartTime.ToUniversalTime() - startedAt.UtcDateTime).TotalSeconds) < 1;
            string? actualExecutable;
            try
            {
                actualExecutable = process.MainModule?.FileName;
            }
            catch
            {
                actualExecutable = null;
            }
            var executableMatches = !string.IsNullOrWhiteSpace(actualExecutable) &&
                !string.IsNullOrWhiteSpace(expectedExecutable) &&
                string.Equals(
                    Path.GetFullPath(actualExecutable),
                    Path.GetFullPath(expectedExecutable),
                    StringComparison.OrdinalIgnoreCase);
            return startMatches && executableMatches
                ? "running-same-identity"
                : "pid-reused-or-identity-mismatch";
        }
        catch (ArgumentException)
        {
            return "exited";
        }
        catch (InvalidOperationException)
        {
            return "exited";
        }
        catch (Exception exception)
        {
            return "identity-inspection-failed:" + exception.GetType().Name;
        }
    }

    internal static bool IsByteRangeLocked(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            stream.Lock(0, 1);
            stream.Unlock(0, 1);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    internal static string ReadIsolatedDotnetScript() =>
        File.ReadAllText(Path.Combine(ResolveRepositoryRoot(), "scripts", "Invoke-IsolatedDotnet.ps1"));

    internal static string FocusedModeSource(string source)
    {
        var start = source.IndexOf("function Invoke-FocusedTestMode", StringComparison.Ordinal);
        var end = source.IndexOf("if ($FocusedTest) {", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    internal static string ResolveSharedLocalAppData()
    {
        var inherited = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        return string.IsNullOrWhiteSpace(inherited)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : inherited;
    }

    internal static string ResolveRepositoryRoot()
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

    internal static bool IsRepositoryRoot(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            File.Exists(Path.Combine(path, "scripts", "Invoke-IsolatedDotnet.ps1"));
    }

    internal static string RunCommand(string fileName, string workingDirectory, params string[] arguments)
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

    internal static string EncodePowerShell(string script)
    {
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    internal static void WriteForeignOwnerMarker(string artifactsPath)
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

    internal static void WriteAttemptMetadata(
        string path,
        string attemptId,
        int outcome,
        int ownerProcessId)
    {
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                attemptId,
                outcome,
                ownerProcessId,
                lastHeartbeatAt = DateTimeOffset.UtcNow
            }));
    }

    internal static string StableSlotNameForScript(string value)
    {
        long hash = 0;
        foreach (var character in value.ToLowerInvariant())
        {
            hash = ((hash * 31) + character) % int.MaxValue;
        }

        return $"slot-{Math.Abs(hash % DotnetBuildEnvironmentManager.StableSlotCount)}";
    }

    internal static string LeaseJournalPath(DotnetBuildEnvironment environment) =>
        Path.Combine(Path.GetDirectoryName(environment.ExecutionLockPath)!, "lease.journal.jsonl");

    internal static string CreateLeaseJournalEntry(DateTimeOffset recordedAt, string reason) =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            recordedAt,
            @event = "LEASE_RECLAIM",
            processId = Environment.ProcessId,
            slot = "slot-0",
            leaseId = "manual",
            reclaimedProcessId = 999999,
            decision = "preserved",
            reason,
            triggerPath = "test"
        });

    internal static void WriteValidPeFile(string path)
    {
        File.Copy(typeof(DotnetBuildEnvironmentManagerTests).Assembly.Location, path, overwrite: true);
    }

    internal static JsonDocument ReadLastLeaseJournalEntry(DotnetBuildEnvironment environment)
    {
        var lines = File.ReadAllLines(LeaseJournalPath(environment));
        Assert.NotEmpty(lines);
        return JsonDocument.Parse(lines[^1]);
    }

    internal static (string FixtureRoot, string LockedPath) CreateLandingFixtureLockPath()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "mcg-landing-tests", Guid.NewGuid().ToString("N"));
        return (fixtureRoot, CreateLandingFixtureLockPath(fixtureRoot));
    }

    internal static string CreateLandingFixtureLockPath(string fixtureRoot)
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

    internal static Process StartFileHolder(string lockedPath, string readyPath, string releasePath)
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

    internal static string ResolvePowerShell()
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

    internal static void ConfigureTimeoutProbeForTests()
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
    }

    internal static void ClearLockAttributionTestHooks()
    {
        LockAttribution.AttributeForTests = null;
        LockAttribution.HandleExecutableForTests = null;
        LockAttribution.HandleProbeTimeoutForTests = null;
        LockAttribution.ConfigureHandleProbeForTests = null;
        LockAttribution.DisableRestartManagerForTests = false;
    }

    internal static string? ResolveHandleExecutableForTests()
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

    internal static string FormatAttributionDiagnostic(
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

    internal static IEnumerable<string> HandleProbeSearchDirectories()
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

    internal static string? ResolveExecutablePath(string name)
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

    internal static bool CanStartRestartManagerForTests()
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

    internal static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    internal static void TryDeleteDirectory(string path)
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

    internal static string ArgumentValue(IReadOnlyList<string> arguments, string name)
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

    internal static string MaxCpuCountArgument(IReadOnlyList<string> arguments)
    {
        var argument = arguments.SingleOrDefault(argument => argument.StartsWith("-maxcpucount:", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(argument));
        return argument!;
    }

    internal static void WriteProjectArtifacts(string artifactsPath, string project, string content)
    {
        var projectName = Path.GetFileNameWithoutExtension(project);
        var binPath = Path.Combine(artifactsPath, "bin", projectName, "debug_net10.0");
        var objPath = Path.Combine(artifactsPath, "obj", projectName, "debug_net10.0");
        Directory.CreateDirectory(binPath);
        Directory.CreateDirectory(objPath);
        File.WriteAllText(Path.Combine(binPath, "cache.txt"), content);
        File.WriteAllText(Path.Combine(objPath, "cache.obj"), content);
    }

    internal static int CountOccurrences(string text, string value)
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

    internal static Process StartSleepProcess()
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

    internal static void StopProcess(Process process)
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

    internal static void StopFocusedProcessIdentity(string identityPath)
    {
        if (!File.Exists(identityPath))
        {
            return;
        }

        try
        {
            using var identity = JsonDocument.Parse(File.ReadAllText(identityPath));
            var root = identity.RootElement;
            if (!string.Equals(GetFocusedChildIdentityState(root), "running-same-identity", StringComparison.Ordinal))
            {
                return;
            }

            using var process = Process.GetProcessById(root.GetProperty("processId").GetInt32());
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch
        {
            // Exact-identity cleanup is best effort after the behavioral assertion has captured the failure.
        }
    }

    internal static SafeFileHandle CreateInheritableFileHandle(string path)
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
    internal struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SecurityAttributes securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, EntryPoint = "RmStartSession")]
    internal static extern int RmStartSessionForTests(out uint sessionHandle, int sessionFlags, string sessionKey);

    [DllImport("rstrtmgr.dll", EntryPoint = "RmEndSession")]
    internal static extern int RmEndSessionForTests(uint sessionHandle);

    internal sealed class EnvVarScope : IDisposable
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
