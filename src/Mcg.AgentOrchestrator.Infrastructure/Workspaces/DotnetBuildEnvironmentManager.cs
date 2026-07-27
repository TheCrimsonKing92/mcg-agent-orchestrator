using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record DotnetBuildEnvironment(
    string LeaseId,
    string RootPath,
    string ArtifactsPath,
    string ExecutionLockPath,
    IReadOnlyList<string> Arguments,
    string SlotOwnerToken,
    string? LeaseMetadataPath = null,
    bool ReusedGoalLease = false,
    bool StaleLockCleared = false,
    int? BuildPermitIndex = null);

public sealed record DotnetBuildLeaseStatus(
    string LeaseId,
    string RootPath,
    string ArtifactsPath,
    string LeaseMetadataPath,
    bool RootExists,
    bool ArtifactsPathExists,
    bool LeaseMetadataExists,
    int? OwnerProcessId,
    bool OwnerProcessAlive,
    bool CanCleanup,
    string Detail);

public sealed record DotnetBuildStableSlotWait(
    int SlotIndex,
    int? OwnerProcessId);

public abstract record DotnetBuildLeaseAcquisition
{
    public sealed record Acquired(DotnetBuildEnvironmentLease Lease) : DotnetBuildLeaseAcquisition;

    public sealed record SlotsBusy(
        string WantedBy,
        IReadOnlyList<DotnetBuildStableSlotWait> BusySlots) : DotnetBuildLeaseAcquisition;

    public sealed record BuildLockBlocked(
        string WantedBy,
        BuildLockAttribution Attribution) : DotnetBuildLeaseAcquisition;
}

public sealed class DotnetBuildSlotsBusyException : IOException
{
    public DotnetBuildSlotsBusyException(DotnetBuildLeaseAcquisition.SlotsBusy slotsBusy)
        : base($"Stable dotnet build slots busy for {slotsBusy.WantedBy}.")
    {
        SlotsBusy = slotsBusy;
    }

    public DotnetBuildLeaseAcquisition.SlotsBusy SlotsBusy { get; }
}

public static class DotnetBuildEnvironmentManager
{
    public const string RootDirectoryName = "mcg-dotnet-isolated";

    // Supported escape hatch for tests that need lease-root isolation.
    public const string IsolatedRootOverrideVariable = "MCG_DOTNET_ISOLATED_ROOT";
    public const int BuildConcurrencySlotCount = 2;
    // Compatibility name for callers migrating from the former artifact-slot grid.
    public const int StableSlotCount = BuildConcurrencySlotCount;
    public static readonly TimeSpan DefaultSlotBusyPollTimeout = TimeSpan.FromSeconds(20);
    private const string LeaseDirectoryName = "lease";
    private const string LeaseMetadataFileName = "lease.json";
    private const string LeaseLockFileName = "lease.lock";
    private const string ArtifactsOwnerFileName = ".mcg-artifacts-owner.json";
    private const string LandingTestsRootDirectoryName = "mcg-landing-tests";
    private const string LandingTestFixtureMarkerFileName = ".mcg-landing-fixture.json";
    private static readonly TimeSpan LandingFixtureMarkerStaleAge = TimeSpan.FromHours(2);
    public const string BuildMaxCpuCountVariable = "MCG_BUILD_MAXCPUCOUNT";
    private const int ArtifactPrepBusyRetryLimit = 3;
    private static readonly TimeSpan ArtifactPrepBusyRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SlotBusyPollDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeProvider DefaultLeaseTimeProvider = TimeProvider.System;
    private static readonly Action<TimeSpan> DefaultLeaseSleep = Thread.Sleep;
    private static readonly object CurrentLandingFixtureRootsGate = new();
    private static readonly HashSet<string> CurrentLandingFixtureRoots = new(StringComparer.OrdinalIgnoreCase);
    private static int s_nextStableSlotScanStart = -1;
    internal static Action<DotnetBuildEnvironment>? PrepareArtifactsDirectoryForTests { get; set; }
    internal static Action? ShutdownBuildServersForTests { get; set; }
    internal static Func<ProcessCommandLineSnapshot>? ProcessCommandLineSnapshotForTests { get; set; }
    internal static TimeProvider DefaultLeaseTimeProviderForTests => DefaultLeaseTimeProvider;
    internal static Action<TimeSpan> DefaultLeaseSleepForTests => DefaultLeaseSleep;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static DotnetBuildEnvironment CreateAttempt(
        GoalId? goalId,
        string attemptName,
        int slotCount = StableSlotCount)
    {
        ValidateRequestedSlotCount(slotCount);
        if (goalId is not null)
        {
            return CreateGoalLease(goalId, attemptName, slotCount);
        }

        var owner = $"{Environment.ProcessId}-{Sanitize(attemptName)}-{Guid.NewGuid():N}";
        var root = Path.Combine(IsolatedRootBase(), "runs", owner);
        var artifactsPath = Path.Combine(root, "artifacts");
        var executionLockPath = BuildSlotExecutionLockPath(BuildSlotIndex(owner));
        Directory.CreateDirectory(artifactsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(executionLockPath)!);

        return new DotnetBuildEnvironment(
            $"run-{owner}",
            root,
            artifactsPath,
            executionLockPath,
            BuildArguments(artifactsPath),
            owner);
    }

    public static string GoalRoot(GoalId goalId)
    {
        return Path.Combine(IsolatedRootBase(), "goals", Prefix(goalId));
    }

    public static string GoalArtifactsPath(GoalId goalId)
    {
        return TryReadArtifactsPath(Path.Combine(LeaseDirectory(goalId), LeaseMetadataFileName)) ??
            Path.Combine(GoalRoot(goalId), "artifacts");
    }

    public static string BaseBuildCacheRoot()
    {
        return DotnetBaseBuildCache.DefaultRootPath(IsolatedRootBase());
    }

    public static bool TryCleanupSuccessfulRun(DotnetBuildEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var runsRoot = Path.GetFullPath(Path.Combine(IsolatedRootBase(), "runs"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(environment.RootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!root.StartsWith(runsRoot, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(root))
        {
            return false;
        }

        try
        {
            Directory.Delete(root, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string BuildSlotHeartbeatPath(int slotIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotIndex);
        return Path.Combine(IsolatedRootBase(), "build-slots", $"activity-{slotIndex}.heartbeat.json");
    }

    public static DotnetBuildEnvironment CreateStableSlotAttempt(
        int slotIndex,
        int slotCount = StableSlotCount)
    {
        ValidateRequestedSlotCount(slotCount);
        if (slotIndex >= slotCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slotIndex),
                slotIndex,
                $"Stable slot index must be 0 through {slotCount - 1} for the requested slot count.");
        }

        return CreateStableSlotEnvironment(slotIndex);
    }

    public static DotnetBuildLeaseAcquisition TryAcquireStableSlotExecutionLock(
        int slotIndex,
        TimeSpan? timeout,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        Action<TimeSpan>? sleep = null)
    {
        var environment = CreateStableSlotEnvironment(slotIndex, createArtifactsDirectory: false);
        return TryAcquireLeaseExecutionLock(environment, timeout, cancellationToken, timeProvider, sleep);
    }

    public static bool IsStableSlotExecutionLeaseAvailable(int slotIndex)
    {
        var environment = CreateStableSlotEnvironment(slotIndex);
        var executionLockPath = environment.ExecutionLockPath;
        Directory.CreateDirectory(Path.GetDirectoryName(executionLockPath)!);
        var createdByProbe = !File.Exists(executionLockPath);
        try
        {
            using (var stream = new FileStream(executionLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                stream.Lock(0, 1);
                stream.Unlock(0, 1);
            }

            if (createdByProbe && IsEmptyFile(executionLockPath))
            {
                File.Delete(executionLockPath);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static int? GetStableSlotExecutionLeaseOwner(int slotIndex)
    {
        ValidateStableSlotIndex(slotIndex);
        return TryReadStableSlotExecutionOwner(slotIndex);
    }

    public static DotnetBuildEnvironmentLease AcquireFirstAvailableStableSlotExecutionLock(
        TimeSpan? timeout = null,
        Action<DotnetBuildStableSlotWait>? onWait = null,
        CancellationToken cancellationToken = default,
        int slotCount = StableSlotCount)
    {
        return TryAcquireFirstAvailableStableSlotExecutionLock(timeout, onWait, cancellationToken, slotCount) switch
        {
            DotnetBuildLeaseAcquisition.Acquired acquired => acquired.Lease,
            DotnetBuildLeaseAcquisition.SlotsBusy busy => throw new DotnetBuildSlotsBusyException(busy),
            DotnetBuildLeaseAcquisition.BuildLockBlocked blocked => throw new BuildLockBlockedException(blocked.Attribution),
            _ => throw new InvalidOperationException("Unknown dotnet build lease acquisition result.")
        };
    }

    public static DotnetBuildLeaseAcquisition TryAcquireFirstAvailableStableSlotExecutionLock(
        TimeSpan? timeout = null,
        Action<DotnetBuildStableSlotWait>? onWait = null,
        CancellationToken cancellationToken = default,
        int slotCount = StableSlotCount)
    {
        ValidateRequestedSlotCount(slotCount);
        var waitTimeout = timeout ?? DefaultSlotBusyPollTimeout;
        var timeoutAt = DateTimeOffset.UtcNow.Add(waitTimeout);
        var waitingReported = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scanStart = NextStableSlotScanStart(slotCount);
            for (var offset = 0; offset < slotCount; offset++)
            {
                var slot = (scanStart + offset) % slotCount;
                var environment = CreateStableSlotEnvironment(slot);
                if (IsSlotArtifactsBusy(environment))
                {
                    continue;
                }

                if (TryOpenLeaseExecutionLock(environment, out var stream, out var blockedAttribution))
                {
                    return new DotnetBuildLeaseAcquisition.Acquired(new DotnetBuildEnvironmentLease(environment, stream));
                }
                if (blockedAttribution is not null)
                {
                    return EmitBuildLockBlocked(environment.LeaseId, blockedAttribution);
                }
            }

            var leastRecentlyLeased = FindLeastRecentlyLeasedStableSlot(slotCount);
            if (!waitingReported)
            {
                onWait?.Invoke(new DotnetBuildStableSlotWait(leastRecentlyLeased.SlotIndex, leastRecentlyLeased.OwnerProcessId));
                waitingReported = true;
            }

            if (DateTimeOffset.UtcNow >= timeoutAt)
            {
                return EmitSlotsBusy("first-available-stable-slot", slotCount);
            }

            var target = CreateStableSlotEnvironment(leastRecentlyLeased.SlotIndex);
            if (IsSlotArtifactsBusy(target))
            {
                Thread.Sleep(100);
                continue;
            }

            if (TryOpenLeaseExecutionLock(target, out var targetStream, out var targetBlockedAttribution))
            {
                return new DotnetBuildLeaseAcquisition.Acquired(new DotnetBuildEnvironmentLease(target, targetStream));
            }
            if (targetBlockedAttribution is not null)
            {
                return EmitBuildLockBlocked(target.LeaseId, targetBlockedAttribution);
            }

            Thread.Sleep(100);
        }
    }

    public static bool TryRotateGoalLease(GoalId goalId, string reason)
    {
        var leaseDirectory = LeaseDirectory(goalId);
        if (!Directory.Exists(leaseDirectory))
        {
            return true;
        }

        var rotatedRoot = Path.Combine(GoalRoot(goalId), "rotated-leases");
        Directory.CreateDirectory(rotatedRoot);
        var target = Path.Combine(rotatedRoot, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Sanitize(reason)}");
        try
        {
            Directory.Move(leaseDirectory, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool TryDeleteGoalArtifacts(GoalId goalId)
    {
        var root = GoalRoot(goalId);
        if (!Directory.Exists(root))
        {
            return true;
        }

        try
        {
            Directory.Delete(root, recursive: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static DotnetBuildLeaseStatus InspectGoalLease(GoalId goalId)
    {
        var root = GoalRoot(goalId);
        var leaseId = $"goal-{Prefix(goalId)}";
        var leaseDirectory = LeaseDirectory(goalId);
        var metadataPath = Path.Combine(leaseDirectory, LeaseMetadataFileName);
        var rootExists = Directory.Exists(root);
        var metadataExists = File.Exists(metadataPath);
        var artifactsPath = TryReadArtifactsPath(metadataPath) ?? GoalArtifactsPath(goalId);
        var artifactsExist = Directory.Exists(artifactsPath);
        var ownerProcessId = metadataExists ? TryReadOwnerProcessId(metadataPath) : null;
        var ownerAlive = ownerProcessId is not null && IsProcessRunning(ownerProcessId.Value);
        var canCleanup = rootExists && !ownerAlive;
        var detail = !rootExists
            ? "goal build lease is missing"
            : ownerAlive
                ? $"goal build lease is active; owner pid={ownerProcessId}"
                : metadataExists
                    ? $"goal build lease is orphaned; recorded owner pid={ownerProcessId?.ToString() ?? "unknown"} is not alive"
                    : "goal build lease has no metadata; no owner process can be confirmed";

        return new DotnetBuildLeaseStatus(
            leaseId,
            root,
            artifactsPath,
            metadataPath,
            rootExists,
            artifactsExist,
            metadataExists,
            ownerProcessId,
            ownerAlive,
            canCleanup,
            detail);
    }

    public static bool TryCleanupOrphanedGoalLease(GoalId goalId, out DotnetBuildLeaseStatus status, out string detail)
    {
        status = InspectGoalLease(goalId);
        if (!status.CanCleanup)
        {
            detail = status.OwnerProcessAlive
                ? $"Refusing to delete active build lease {status.LeaseId}; owner pid={status.OwnerProcessId} is alive."
                : $"No orphaned build lease to clean for {status.LeaseId}.";
            return false;
        }

        var deleted = TryDeleteGoalArtifacts(goalId);
        detail = deleted
            ? $"Deleted orphaned build lease {status.LeaseId} at {status.RootPath}."
            : $"Could not delete orphaned build lease {status.LeaseId}; inspect file locks under {status.RootPath}.";
        return deleted;
    }

    public static void ShutdownBuildServersBestEffort()
    {
        if (ShutdownBuildServersForTests is { } shutdownForTests)
        {
            shutdownForTests();
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "build-server", "shutdown" }
            });

            process?.WaitForExit(10_000);
        }
        catch
        {
            // Best effort cleanup only; build/test result handling owns the real verdict.
        }
    }

    public static FileStream AcquireLeaseExecutionLock(
        DotnetBuildEnvironment environment,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        Action<TimeSpan>? sleep = null) =>
        AcquireLeaseExecutionLock(environment, null, cancellationToken, timeProvider, sleep);

    public static FileStream AcquireLeaseExecutionLock(
        DotnetBuildEnvironment environment,
        TimeSpan? timeout,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        Action<TimeSpan>? sleep = null)
    {
        return TryAcquireLeaseExecutionLock(environment, timeout, cancellationToken, timeProvider, sleep) switch
        {
            DotnetBuildLeaseAcquisition.Acquired acquired => acquired.Lease.DetachStreamForLegacyCaller(),
            DotnetBuildLeaseAcquisition.SlotsBusy busy => throw new DotnetBuildSlotsBusyException(busy),
            DotnetBuildLeaseAcquisition.BuildLockBlocked blocked => throw new BuildLockBlockedException(blocked.Attribution),
            _ => throw new InvalidOperationException("Unknown dotnet build lease acquisition result.")
        };
    }

    public static DotnetBuildLeaseAcquisition TryAcquireLeaseExecutionLock(
        DotnetBuildEnvironment environment,
        TimeSpan? timeout,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        Action<TimeSpan>? sleep = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(environment.ExecutionLockPath)!);
        var clock = timeProvider ?? DefaultLeaseTimeProvider;
        var delay = sleep ?? DefaultLeaseSleep;
        var timeoutAt = clock.GetUtcNow().Add(timeout ?? DefaultSlotBusyPollTimeout);
        var attemptedCompilerLockRemediation = false;
        var attemptedOwnedProcessRemediation = false;
        var artifactPrepBusyAttempts = 0;
        BuildLockAttribution? selfHeldLandingFixtureAttribution = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LeaseFileStream stream;
            var reclaimed = TryReclaimStaleExecutionLease(environment);
            try
            {
                if (selfHeldLandingFixtureAttribution is null &&
                    IsSlotArtifactsBusy(environment))
                {
                    if (clock.GetUtcNow() >= timeoutAt)
                    {
                        return EmitSlotsBusy(environment.LeaseId);
                    }

                    delay(SlotBusyPollDelay);
                    continue;
                }

                stream = OpenExecutionLeaseStream(environment);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (clock.GetUtcNow() >= timeoutAt)
                {
                    return EmitSlotsBusy(environment.LeaseId);
                }

                delay(SlotBusyPollDelay);
                continue;
            }

            try
            {
                if (selfHeldLandingFixtureAttribution is not null)
                {
                    try
                    {
                        PrepareArtifactsDirectoryForTests?.Invoke(environment);
                    }
                    catch
                    {
                        stream.Dispose();
                        throw;
                    }

                    stream.Dispose();
                    LockAttribution.EmitReceipt(selfHeldLandingFixtureAttribution);
                    artifactPrepBusyAttempts++;
                    if (artifactPrepBusyAttempts >= ArtifactPrepBusyRetryLimit ||
                        clock.GetUtcNow() >= timeoutAt)
                    {
                        return EmitSlotsBusy(environment.LeaseId);
                    }

                    delay(ArtifactPrepBusyRetryDelay);
                    continue;
                }

                try
                {
                    WriteExecutionLeaseMetadata(stream, environment);
                    PrepareArtifactsDirectory(environment, forceClean: reclaimed, currentProcessOwnsExecutionLease: true);
                    EmitLeaseReceipt("LEASE_ACQUIRE", environment);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }

                return new DotnetBuildLeaseAcquisition.Acquired(new DotnetBuildEnvironmentLease(environment, stream));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var remediation = TryRemediateArtifactPrepLock(
                    environment,
                    ex,
                    ref attemptedCompilerLockRemediation,
                    ref attemptedOwnedProcessRemediation,
                    out var blockedAttribution);
                if (remediation is ArtifactPrepLockRemediation.RetryImmediately)
                {
                    continue;
                }

                if (remediation is ArtifactPrepLockRemediation.SlotBusy)
                {
                    artifactPrepBusyAttempts++;
                    if (IsCurrentProcessLandingFixtureAttribution(blockedAttribution))
                    {
                        selfHeldLandingFixtureAttribution = blockedAttribution;
                    }

                    if (artifactPrepBusyAttempts >= ArtifactPrepBusyRetryLimit ||
                        clock.GetUtcNow() >= timeoutAt)
                    {
                        return EmitSlotsBusy(environment.LeaseId);
                    }

                    delay(ArtifactPrepBusyRetryDelay);
                    continue;
                }

                if (clock.GetUtcNow() >= timeoutAt)
                {
                    return EmitBuildLockBlocked(environment.LeaseId, blockedAttribution);
                }

                delay(SlotBusyPollDelay);
            }
        }
    }

    private static (int SlotIndex, int? OwnerProcessId, DateTimeOffset LastAcquiredAt) FindLeastRecentlyLeasedStableSlot(
        int slotCount = StableSlotCount)
    {
        var oldest = (SlotIndex: 0, OwnerProcessId: (int?)null, LastAcquiredAt: DateTimeOffset.MaxValue);
        for (var slot = 0; slot < slotCount; slot++)
        {
            var environment = CreateStableSlotEnvironment(slot, createArtifactsDirectory: false);
            var marker = TryReadExecutionLeaseMetadata(environment.ExecutionLockPath);
            var acquiredAt = marker?.AcquiredAt ?? DateTimeOffset.MinValue;
            if (acquiredAt < oldest.LastAcquiredAt)
            {
                oldest = (slot, marker?.OwnerProcessId, acquiredAt);
            }
        }

        return oldest;
    }

    private static int NextStableSlotScanStart(int slotCount = StableSlotCount)
    {
        return (int)((uint)Interlocked.Increment(ref s_nextStableSlotScanStart) % slotCount);
    }

    private static DotnetBuildEnvironment CreateGoalLease(
        GoalId goalId,
        string attemptName,
        int slotCount)
    {
        var root = GoalRoot(goalId);
        var leaseId = $"goal-{Prefix(goalId)}";
        var leaseDirectory = LeaseDirectory(goalId);
        var metadataPath = Path.Combine(leaseDirectory, LeaseMetadataFileName);
        var lockPath = Path.Combine(leaseDirectory, LeaseLockFileName);
        var reused = Directory.Exists(leaseDirectory);
        var artifactsPath = Path.Combine(root, "artifacts");
        var buildPermitIndex = BuildSlotIndex(Prefix(goalId));
        var executionLockPath = BuildSlotExecutionLockPath(buildPermitIndex);
        Directory.CreateDirectory(leaseDirectory);
        Directory.CreateDirectory(artifactsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(executionLockPath)!);
        var staleLockCleared = TryClearStaleLock(lockPath);
        File.WriteAllText(lockPath, Environment.ProcessId.ToString());
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(
            new GoalBuildEnvironmentLeaseMetadata(
                1,
                goalId.Value,
                Prefix(goalId),
                leaseId,
                root,
                artifactsPath,
                Environment.ProcessId,
                Environment.MachineName,
                DateTimeOffset.UtcNow,
                attemptName,
                staleLockCleared),
            JsonOptions));

        return new DotnetBuildEnvironment(
            leaseId,
            root,
            artifactsPath,
            executionLockPath,
            BuildArguments(artifactsPath),
            leaseId,
            metadataPath,
            ReusedGoalLease: reused,
            StaleLockCleared: staleLockCleared,
            BuildPermitIndex: buildPermitIndex);
    }

    private static IReadOnlyList<string> BuildArguments(string artifactsPath) =>
    [
        "--artifacts-path",
        artifactsPath,
        $"-maxcpucount:{ResolveMaxCpuCount()}",
        "-p:BuildInParallel=false"
    ];

    private static int ResolveMaxCpuCount()
    {
        var configured = Environment.GetEnvironmentVariable(BuildMaxCpuCountVariable);
        if (int.TryParse(configured, out var value) && value > 1)
        {
            return value;
        }

        return Math.Max(2, Environment.ProcessorCount / BuildConcurrencySlotCount);
    }

    private static string LeaseDirectory(GoalId goalId)
    {
        return Path.Combine(GoalRoot(goalId), LeaseDirectoryName);
    }

    private static bool IsEmptyFile(string path)
    {
        try
        {
            return new FileInfo(path).Length == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static DotnetBuildEnvironment CreateStableSlotEnvironment(
        int slotIndex,
        bool createArtifactsDirectory = true)
    {
        ValidateStableSlotIndex(slotIndex);
        var owner = $"p{Environment.ProcessId}-build-{slotIndex}";
        var root = Path.Combine(IsolatedRootBase(), "runs", owner);
        var artifactsPath = Path.Combine(root, "artifacts");
        var executionLockPath = BuildSlotExecutionLockPath(slotIndex % BuildConcurrencySlotCount);
        if (createArtifactsDirectory)
        {
            Directory.CreateDirectory(artifactsPath);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(executionLockPath)!);

        return new DotnetBuildEnvironment(
            $"run-build-{slotIndex}",
            root,
            artifactsPath,
            executionLockPath,
            BuildArguments(artifactsPath),
            $"build-{slotIndex % BuildConcurrencySlotCount}",
            BuildPermitIndex: slotIndex % BuildConcurrencySlotCount);
    }

    private static void ValidateStableSlotIndex(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= StableSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex, $"Stable slot index must be 0 through {StableSlotCount - 1}.");
        }
    }

    private static void ValidateRequestedSlotCount(int slotCount)
    {
        if (slotCount is < 1 or > StableSlotCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slotCount),
                slotCount,
                $"Requested stable slot count must be 1 through {StableSlotCount}.");
        }
    }

    private static string IsolatedRootBase()
    {
        var overridden = Environment.GetEnvironmentVariable(IsolatedRootOverrideVariable);
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Path.GetTempPath(), RootDirectoryName)
            : overridden;
    }

    private static string BuildSlotExecutionLockPath(int slotIndex)
    {
        return Path.Combine(IsolatedRootBase(), "build-slots", $"build-{slotIndex}.lock");
    }

    private static int BuildSlotIndex(string owner)
    {
        return owner
            .Select(char.ToLowerInvariant)
            .Sum(ch => (int)ch) %
            BuildConcurrencySlotCount;
    }

    private static DotnetBuildLeaseAcquisition.SlotsBusy EmitSlotsBusy(
        string wantedBy,
        int slotCount = StableSlotCount)
    {
        var busySlots = BuildBusySlotSnapshot(slotCount);
        Console.WriteLine(
            $"SLOTS_BUSY wantedBy={wantedBy} busySlots={FormatBusySlots(busySlots)} pid={Environment.ProcessId}");
        return new DotnetBuildLeaseAcquisition.SlotsBusy(wantedBy, busySlots);
    }

    private static DotnetBuildLeaseAcquisition.BuildLockBlocked EmitBuildLockBlocked(
        string wantedBy,
        BuildLockAttribution attribution)
    {
        Console.WriteLine(
            $"BUILD_LOCK_BLOCKED wantedBy={wantedBy} path=\"{attribution.Path}\" holder={FormatBuildLockHolder(attribution.Holders.FirstOrDefault())} source={attribution.Source} pid={Environment.ProcessId}");
        return new DotnetBuildLeaseAcquisition.BuildLockBlocked(wantedBy, attribution);
    }

    private static IReadOnlyList<DotnetBuildStableSlotWait> BuildBusySlotSnapshot(
        int slotCount = StableSlotCount)
    {
        var waits = new DotnetBuildStableSlotWait[slotCount];
        for (var slot = 0; slot < slotCount; slot++)
        {
            waits[slot] = new DotnetBuildStableSlotWait(slot, TryReadStableSlotExecutionOwner(slot));
        }

        return waits;
    }

    private static int? TryReadStableSlotExecutionOwner(int slotIndex)
    {
        var environment = CreateStableSlotEnvironment(slotIndex, createArtifactsDirectory: false);
        var metadata = TryReadExecutionLeaseMetadata(environment.ExecutionLockPath);
        return TryFindActiveSlotArtifactConsumer(environment)?.ProcessId ??
            metadata?.OwnerProcessId;
    }

    private static string FormatBusySlots(IReadOnlyList<DotnetBuildStableSlotWait> busySlots) =>
        string.Join(
            "|",
            busySlots.Select(slot =>
                $"slot-{slot.SlotIndex}:pid-{slot.OwnerProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}"));

    private static bool TryOpenLeaseExecutionLock(
        DotnetBuildEnvironment environment,
        out FileStream stream,
        out BuildLockAttribution? blockedAttribution)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(environment.ExecutionLockPath)!);
        var attemptedCompilerLockRemediation = false;
        var attemptedOwnedProcessRemediation = false;
        blockedAttribution = null;
        while (true)
        {
            try
            {
                var reclaimed = TryReclaimStaleExecutionLease(environment);
                try
                {
                    stream = OpenExecutionLeaseStream(environment);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    stream = null!;
                    blockedAttribution = null;
                    return false;
                }

                try
                {
                    WriteExecutionLeaseMetadata(stream, environment);
                    PrepareArtifactsDirectory(environment, forceClean: reclaimed);
                    EmitLeaseReceipt("LEASE_ACQUIRE", environment);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var remediation = TryRemediateArtifactPrepLock(
                    environment,
                    ex,
                    ref attemptedCompilerLockRemediation,
                    ref attemptedOwnedProcessRemediation,
                    out blockedAttribution);
                if (remediation is ArtifactPrepLockRemediation.RetryImmediately)
                {
                    continue;
                }

                if (remediation is ArtifactPrepLockRemediation.SlotBusy)
                {
                    blockedAttribution = null;
                    stream = null!;
                    return false;
                }

                stream = null!;
                return false;
            }
        }
    }

    private static ArtifactPrepLockRemediation TryRemediateArtifactPrepLock(
        DotnetBuildEnvironment environment,
        Exception exception,
        ref bool attemptedCompilerLockRemediation,
        ref bool attemptedOwnedProcessRemediation,
        out BuildLockAttribution attribution)
    {
        var lockedPath = LockAttribution.TryExtractLockedPath(exception.ToString()) ?? environment.ArtifactsPath;
        if (LockAttribution.IsLeaseLockPath(lockedPath))
        {
            attribution = new BuildLockAttribution(lockedPath, [], "lease-lock-refused");
            return ArtifactPrepLockRemediation.Blocked;
        }

        if (TryCreateCurrentLandingFixtureAttribution(lockedPath, out attribution))
        {
            LockAttribution.EmitReceipt(attribution);
            return attribution.Source is "landing-fixture-marker"
                ? ArtifactPrepLockRemediation.SlotBusy
                : ArtifactPrepLockRemediation.RetryImmediately;
        }

        if (TryFindActiveSlotArtifactConsumer(environment) is { } activeSlotArtifactConsumer)
        {
            attribution = new BuildLockAttribution(
                lockedPath,
                [activeSlotArtifactConsumer],
                "slot-artifact-consumer",
                "artifact-prep",
                "prepare-artifacts");
            LockAttribution.EmitReceipt(attribution);
            return ArtifactPrepLockRemediation.SlotBusy;
        }

        attribution = LockAttribution.Attribute(
            lockedPath,
            environment.ArtifactsPath,
            "artifact-prep",
            "prepare-artifacts");
        if (IsCurrentLeaseSelfHeldArtifactLock(environment, attribution, currentProcessOwnsExecutionLease: true))
        {
            return ArtifactPrepLockRemediation.RetryImmediately;
        }

        if (!attemptedCompilerLockRemediation && IsCompilerLock(attribution))
        {
            attemptedCompilerLockRemediation = true;
            ShutdownBuildServersBestEffort();
            return ArtifactPrepLockRemediation.RetryImmediately;
        }

        if (IsTransientNoHolderSlotArtifactLock(environment, attribution))
        {
            return ArtifactPrepLockRemediation.SlotBusy;
        }

        if (attemptedOwnedProcessRemediation)
        {
            return ArtifactPrepLockRemediation.Blocked;
        }

        var killed = false;
        foreach (var holder in attribution.Holders
            .Where(holder => holder.IsOrchestratorOwned && holder.ProcessId.HasValue)
            .DistinctBy(holder => holder.ProcessId!.Value))
        {
            killed |= WorkerProcessJobs.TryKillOrFallbackAndWait(holder.ProcessId!.Value, TimeSpan.FromSeconds(5));
        }

        attemptedOwnedProcessRemediation = killed;
        return killed ? ArtifactPrepLockRemediation.RetryImmediately : ArtifactPrepLockRemediation.Blocked;
    }

    private static bool IsCurrentProcessLandingFixtureAttribution(BuildLockAttribution attribution) =>
        attribution.Source is "landing-fixture-marker" or "landing-fixture-registration" &&
        attribution.Holders.Any(holder => holder.ProcessId == Environment.ProcessId);

    private static bool IsCompilerLock(BuildLockAttribution attribution) =>
        attribution.Holders.Any(holder =>
            ContainsCompilerLockSignal(holder.ProcessName) ||
            ContainsCompilerLockSignal(holder.CommandLine));

    private static bool IsTransientNoHolderSlotArtifactLock(
        DotnetBuildEnvironment environment,
        BuildLockAttribution attribution) =>
        PathIsUnderDirectory(attribution.Path, environment.ArtifactsPath) &&
        HasNoActionableHolder(attribution);

    private static bool HasNoActionableHolder(BuildLockAttribution attribution) =>
        attribution.Holders.Count == 0 ||
        attribution.Holders.All(holder =>
            holder.ProcessId is null &&
            (string.IsNullOrWhiteSpace(holder.ProcessName) ||
                holder.ProcessName.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
                holder.ProcessName.Equals("unknown-probe-timeout", StringComparison.OrdinalIgnoreCase)));

    private static bool IsSlotArtifactsBusy(DotnetBuildEnvironment environment) =>
        TryFindActiveSlotArtifactConsumer(environment) is not null;

    internal static BuildLockHolder? TryFindActiveSlotArtifactConsumer(DotnetBuildEnvironment environment)
    {
        var artifactsPath = NormalizeForCommandLineMatch(environment.ArtifactsPath);
        if (string.IsNullOrWhiteSpace(artifactsPath))
        {
            return null;
        }

        var snapshot = ProcessCommandLineSnapshotForTests?.Invoke() ?? ProcessCommandLines.Snapshot();
        foreach (var pair in snapshot.Read(Process.GetProcesses().Select(process => process.Id)))
        {
            if (pair.Key == Environment.ProcessId ||
                !IsProcessRunning(pair.Key) ||
                !CommandLineUsesSlotArtifacts(pair.Value, artifactsPath))
            {
                continue;
            }

            return CreateProcessHolder(pair.Key, pair.Value, isOrchestratorOwned: true);
        }

        return null;
    }

    private static bool CommandLineUsesSlotArtifacts(string commandLine, string artifactsPath)
    {
        if (string.IsNullOrWhiteSpace(commandLine) ||
            !NormalizeForCommandLineMatch(commandLine).Contains(artifactsPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return commandLine.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
            commandLine.Contains("vstest.console", StringComparison.OrdinalIgnoreCase) ||
            commandLine.Contains("datacollector", StringComparison.OrdinalIgnoreCase) ||
            (commandLine.Contains("dotnet", StringComparison.OrdinalIgnoreCase) &&
                commandLine.Contains(" test ", StringComparison.OrdinalIgnoreCase)) ||
            commandLine.Contains("--artifacts-path", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeForCommandLineMatch(string value) =>
        value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static bool ContainsCompilerLockSignal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("VBCSCompiler", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("MSBuild", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("csc", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("build-server", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBuildLockHolder(BuildLockHolder? holder)
    {
        if (holder is null)
        {
            return "unknown";
        }

        var pid = holder.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        var name = string.IsNullOrWhiteSpace(holder.ProcessName) ? "unknown" : holder.ProcessName;
        return $"pid-{pid}:name-{name}";
    }

    private static LeaseFileStream OpenExecutionLeaseStream(DotnetBuildEnvironment environment)
    {
        // All slot lease participants use the same protocol: the file is share-opened and
        // byte 0 is the exclusion primitive. A process crash closes the OS handle, so the
        // durable file is advisory metadata rather than ownership.
        return new LeaseFileStream(environment);
    }

    private static void PrepareArtifactsDirectory(
        DotnetBuildEnvironment environment,
        bool forceClean = false,
        bool currentProcessOwnsExecutionLease = false)
    {
        PrepareArtifactsDirectoryForTests?.Invoke(environment);
        var clean = forceClean || environment.StaleLockCleared;
        var ownerPath = Path.Combine(environment.ArtifactsPath, ArtifactsOwnerFileName);
        if (Directory.Exists(environment.ArtifactsPath) && Directory.EnumerateFileSystemEntries(environment.ArtifactsPath).Any())
        {
            clean |= !OwnerMarkerMatches(ownerPath, environment.SlotOwnerToken);
        }

        if (clean && Directory.Exists(environment.ArtifactsPath))
        {
            try
            {
                Directory.Delete(environment.ArtifactsPath, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var lockedPath = LockAttribution.TryExtractLockedPath(ex.ToString()) ?? environment.ArtifactsPath;
                var attribution = LockAttribution.Attribute(
                    lockedPath,
                    environment.ArtifactsPath,
                    "artifact-clean",
                    "delete-artifacts");
                if (!IsCurrentLeaseSelfHeldArtifactLock(
                    environment,
                    attribution,
                    currentProcessOwnsExecutionLease))
                {
                    throw;
                }
            }
        }

        Directory.CreateDirectory(environment.ArtifactsPath);
        WriteOwnerMarker(ownerPath, environment.SlotOwnerToken);
    }

    private static bool OwnerMarkerMatches(string ownerPath, string expectedToken)
    {
        if (!File.Exists(ownerPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(ownerPath));
            return document.RootElement.TryGetProperty("ownerToken", out var ownerToken) &&
                ownerToken.ValueKind == JsonValueKind.String &&
                string.Equals(ownerToken.GetString(), expectedToken, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static void WriteOwnerMarker(string ownerPath, string ownerToken)
    {
        var marker = new ArtifactsOwnerMarker(1, ownerToken, Environment.ProcessId, Environment.MachineName, DateTimeOffset.UtcNow);
        File.WriteAllText(ownerPath, JsonSerializer.Serialize(marker, JsonOptions));
    }

    private static bool IsCurrentLeaseSelfHeldArtifactLock(
        DotnetBuildEnvironment environment,
        BuildLockAttribution attribution,
        bool currentProcessOwnsExecutionLease)
    {
        var landingFixtureDisposition = GetLandingFixtureDisposition(attribution.Path);
        if (!PathIsUnderDirectory(attribution.Path, environment.ArtifactsPath) &&
            landingFixtureDisposition is not LandingFixtureLockDisposition.CurrentTransient and not LandingFixtureLockDisposition.StaleDebris)
        {
            return false;
        }

        if (attribution.Holders.Any(holder => !holder.IsOrchestratorOwned))
        {
            return false;
        }

        if (attribution.Holders.Any(holder => holder.ProcessId is { } processId && processId != Environment.ProcessId))
        {
            return false;
        }

        if (landingFixtureDisposition is LandingFixtureLockDisposition.StaleDebris)
        {
            return true;
        }

        if (currentProcessOwnsExecutionLease)
        {
            return true;
        }

        var metadata = TryReadExecutionLeaseMetadata(environment.ExecutionLockPath);
        return metadata?.OwnerProcessId == Environment.ProcessId;
    }

    private static bool TryCreateCurrentLandingFixtureAttribution(
        string path,
        out BuildLockAttribution attribution)
    {
        attribution = null!;
        if (!TryGetLandingTestFixtureRoot(path, out var fixtureRoot))
        {
            return false;
        }

        if (TryReadLandingTestFixtureMarker(fixtureRoot) is { } marker)
        {
            if (IsLandingFixtureMarkerStale(marker))
            {
                return false;
            }

            attribution = new BuildLockAttribution(
                path,
                [CreateLandingFixtureMarkerHolder(marker)],
                "landing-fixture-marker",
                "artifact-prep",
                "prepare-artifacts");
            return true;
        }

        lock (CurrentLandingFixtureRootsGate)
        {
            if (!CurrentLandingFixtureRoots.Contains(fixtureRoot))
            {
                return false;
            }
        }

        attribution = new BuildLockAttribution(
            path,
            [CreateProcessHolder(Environment.ProcessId, "current landing fixture root", false)],
            "landing-fixture-registration",
            "artifact-prep",
            "prepare-artifacts");
        return true;
    }

    private static BuildLockHolder CreateLandingFixtureMarkerHolder(LandingTestFixtureMarker marker) =>
        new(
            marker.CreatorProcessId,
            "landing-test-fixture",
            $"landing fixture purpose={marker.Purpose} machine={marker.MachineName} createdAt={marker.CreatedAt:O}",
            false);

    private static BuildLockHolder CreateProcessHolder(int processId, string commandLine, bool isOrchestratorOwned)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new BuildLockHolder(
                processId,
                process.ProcessName,
                commandLine,
                isOrchestratorOwned,
                TryGetProcessStartTime(process));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new BuildLockHolder(processId, "unknown", commandLine, isOrchestratorOwned);
        }
    }

    private static DateTimeOffset? TryGetProcessStartTime(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static void RegisterCurrentLandingTestFixtureRoot(string path)
    {
        if (!TryGetLandingTestFixtureRoot(path, out var fixtureRoot))
        {
            return;
        }

        lock (CurrentLandingFixtureRootsGate)
        {
            CurrentLandingFixtureRoots.Add(fixtureRoot);
        }
    }

    internal static void ClearCurrentLandingTestFixtureRootsForTests()
    {
        lock (CurrentLandingFixtureRootsGate)
        {
            CurrentLandingFixtureRoots.Clear();
        }
    }

    private static LandingFixtureLockDisposition GetLandingFixtureDisposition(string path)
    {
        if (!TryGetLandingTestFixtureRoot(path, out var fixtureRoot))
        {
            return LandingFixtureLockDisposition.None;
        }

        if (TryReadLandingTestFixtureMarker(fixtureRoot) is { } marker)
        {
            if (IsLandingFixtureMarkerStale(marker))
            {
                return LandingFixtureLockDisposition.StaleDebris;
            }

            return LandingFixtureLockDisposition.CurrentTransient;
        }

        lock (CurrentLandingFixtureRootsGate)
        {
            return CurrentLandingFixtureRoots.Contains(fixtureRoot)
                ? LandingFixtureLockDisposition.CurrentTransient
                : LandingFixtureLockDisposition.None;
        }
    }

    internal static void WriteLandingTestFixtureMarkerForTests(string fixtureRoot, int? ownerProcessId = null, DateTimeOffset? createdAt = null)
    {
        WriteLandingTestFixtureMarker(fixtureRoot, "landing-test-fixture", ownerProcessId, createdAt);
    }

    private static void WriteLandingTestFixtureMarker(
        string fixtureRoot,
        string purpose,
        int? ownerProcessId = null,
        DateTimeOffset? createdAt = null)
    {
        try
        {
            Directory.CreateDirectory(fixtureRoot);
            var marker = new LandingTestFixtureMarker(
                1,
                purpose,
                ownerProcessId ?? Environment.ProcessId,
                Environment.MachineName,
                createdAt ?? DateTimeOffset.UtcNow);
            File.WriteAllText(
                Path.Combine(fixtureRoot, LandingTestFixtureMarkerFileName),
                JsonSerializer.Serialize(marker, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static LandingTestFixtureMarker? TryReadLandingTestFixtureMarker(string fixtureRoot)
    {
        try
        {
            var markerPath = Path.Combine(fixtureRoot, LandingTestFixtureMarkerFileName);
            return File.Exists(markerPath)
                ? JsonSerializer.Deserialize<LandingTestFixtureMarker>(File.ReadAllText(markerPath), JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool IsLandingFixtureMarkerStale(LandingTestFixtureMarker marker) =>
        DateTimeOffset.UtcNow - marker.CreatedAt >= LandingFixtureMarkerStaleAge &&
        !IsProcessRunning(marker.CreatorProcessId);

    private static bool TryGetLandingTestFixtureRoot(string path, out string fixtureRoot)
    {
        fixtureRoot = string.Empty;
        try
        {
            var landingTestsRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), LandingTestsRootDirectoryName))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(path);
            if (!PathIsUnderDirectory(fullPath, landingTestsRoot))
            {
                return false;
            }

            var relative = Path.GetRelativePath(landingTestsRoot, fullPath);
            if (relative.StartsWith("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative) ||
                string.IsNullOrWhiteSpace(relative) ||
                relative.Equals(".", StringComparison.Ordinal))
            {
                return false;
            }

            var firstSeparator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            var fixtureName = firstSeparator < 0 ? relative : relative[..firstSeparator];
            if (string.IsNullOrWhiteSpace(fixtureName))
            {
                return false;
            }

            fixtureRoot = Path.Combine(landingTestsRoot, fixtureName);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool PathIsUnderDirectory(string path, string directory)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(fullDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryReclaimStaleExecutionLease(DotnetBuildEnvironment environment)
    {
        var metadata = TryReadExecutionLeaseMetadata(environment.ExecutionLockPath);
        if (metadata?.OwnerProcessId is not { } processId || IsProcessRunning(processId))
        {
            return false;
        }

        try
        {
            File.Delete(environment.ExecutionLockPath);
            TryDeleteExecutionLeaseMetadata(environment.ExecutionLockPath);
            EmitLeaseReceipt("LEASE_RECLAIM", environment, processId);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ExecutionLeaseMetadata? TryReadExecutionLeaseMetadata(string path)
    {
        var metadataPath = ExecutionLeaseMetadataPath(path);
        var sourcePath = File.Exists(metadataPath) ? metadataPath : path;
        if (!File.Exists(sourcePath))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(sourcePath).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (int.TryParse(text, out var legacyProcessId))
            {
                return new ExecutionLeaseMetadata(
                    0,
                    "legacy",
                    null,
                    null,
                    legacyProcessId,
                    null,
                    DateTimeOffset.MinValue);
            }

            return JsonSerializer.Deserialize<ExecutionLeaseMetadata>(text, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteExecutionLeaseMetadata(FileStream stream, DotnetBuildEnvironment environment)
    {
        var metadata = new ExecutionLeaseMetadata(
            1,
            environment.LeaseId,
            environment.SlotOwnerToken,
            environment.ArtifactsPath,
            Environment.ProcessId,
            Environment.MachineName,
            DateTimeOffset.UtcNow);
        var serialized = JsonSerializer.Serialize(metadata, JsonOptions);
        stream.SetLength(0);
        stream.Position = 0;
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.Write(serialized);
        writer.WriteLine();
        writer.Flush();
        stream.Flush(flushToDisk: true);
        stream.Position = 0;
        File.WriteAllText(ExecutionLeaseMetadataPath(environment.ExecutionLockPath), serialized);
    }

    private static string ExecutionLeaseMetadataPath(string executionLockPath) =>
        $"{executionLockPath}.owner.json";

    private static void TryDeleteExecutionLeaseMetadata(string executionLockPath)
    {
        try
        {
            File.Delete(ExecutionLeaseMetadataPath(executionLockPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The byte-range lock remains authoritative; owner metadata is diagnostic only.
        }
    }

    private static void EmitLeaseReceipt(string receipt, DotnetBuildEnvironment environment, int? reclaimedProcessId = null)
    {
        var detail = reclaimedProcessId is null
            ? string.Empty
            : $" reclaimedPid={reclaimedProcessId}";
        Console.WriteLine($"{receipt} pid={Environment.ProcessId} slot={ReceiptSlotName(environment)} lease={environment.LeaseId}{detail} path={environment.ExecutionLockPath}");
    }

    private static string ReceiptSlotName(DotnetBuildEnvironment environment)
    {
        var slotName = Path.GetFileNameWithoutExtension(environment.ExecutionLockPath);
        return string.IsNullOrWhiteSpace(slotName) ? environment.SlotOwnerToken : slotName;
    }

    private static bool TryClearStaleLock(string lockPath)
    {
        if (!File.Exists(lockPath))
        {
            return false;
        }

        var text = File.ReadAllText(lockPath).Trim();
        if (int.TryParse(text, out var processId) && IsProcessRunning(processId))
        {
            return false;
        }

        try
        {
            File.Delete(lockPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static int? TryReadOwnerProcessId(string metadataPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            if (document.RootElement.TryGetProperty("ownerProcessId", out var owner) &&
                owner.ValueKind == JsonValueKind.Number &&
                owner.TryGetInt32(out var processId))
            {
                return processId;
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? TryReadArtifactsPath(string metadataPath)
    {
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            return document.RootElement.TryGetProperty("artifactsPath", out var artifactsPath) &&
                artifactsPath.ValueKind == JsonValueKind.String
                    ? artifactsPath.GetString()
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string Prefix(GoalId goalId)
    {
        var value = goalId.Value;
        return (value.Length <= 8 ? value : value[..8]).ToLowerInvariant();
    }

    private static string Sanitize(string value)
    {
        var sanitized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "dotnet" : sanitized;
    }

    private sealed record GoalBuildEnvironmentLeaseMetadata(
        int Version,
        string GoalId,
        string GoalPrefix,
        string LeaseId,
        string RootPath,
        string ArtifactsPath,
        int OwnerProcessId,
        string MachineName,
        DateTimeOffset LastUsedAt,
        string LastAttemptName,
        bool StaleLockCleared);

    private sealed record ArtifactsOwnerMarker(
        int Version,
        string OwnerToken,
        int OwnerProcessId,
        string MachineName,
        DateTimeOffset LastAcquiredAt);

    private sealed record ExecutionLeaseMetadata(
        int Version,
        string LeaseId,
        string? SlotOwnerToken,
        string? ArtifactsPath,
        int OwnerProcessId,
        string? MachineName,
        DateTimeOffset AcquiredAt);

    private sealed record LandingTestFixtureMarker(
        int Version,
        string Purpose,
        int CreatorProcessId,
        string MachineName,
        DateTimeOffset CreatedAt);

    private enum LandingFixtureLockDisposition
    {
        None,
        CurrentTransient,
        StaleDebris
    }

    private enum ArtifactPrepLockRemediation
    {
        Blocked,
        RetryImmediately,
        SlotBusy
    }

    private sealed class LeaseFileStream : FileStream
    {
        private readonly DotnetBuildEnvironment _environment;
        private bool _rangeLocked;
        private int _released;

        internal LeaseFileStream(DotnetBuildEnvironment environment)
            : base(environment.ExecutionLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)
        {
            _environment = environment;
            try
            {
                Lock(0, 1);
                _rangeLocked = true;
            }
            catch
            {
                base.Dispose(true);
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing && Interlocked.Exchange(ref _released, 1) == 0)
                {
                    if (_rangeLocked)
                    {
                        TryDeleteExecutionLeaseMetadata(_environment.ExecutionLockPath);
                        Unlock(0, 1);
                        _rangeLocked = false;
                    }

                    EmitLeaseReceipt("LEASE_RELEASE", _environment);
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }
}

public sealed class DotnetBuildEnvironmentLease : IDisposable
{
    private readonly FileStream _stream;
    private int _state;

    internal DotnetBuildEnvironmentLease(DotnetBuildEnvironment environment, FileStream stream)
    {
        Environment = environment;
        _stream = stream;
    }

    public DotnetBuildEnvironment Environment { get; }

    internal FileStream DetachStreamForLegacyCaller()
    {
        Interlocked.Exchange(ref _state, 1);
        return _stream;
    }

    internal void ReleaseExecutionLock()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            return;
        }

        try
        {
            DotnetBuildEnvironmentManager.ShutdownBuildServersBestEffort();
        }
        finally
        {
            _stream.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _stream.Dispose();
        }
        finally
        {
            DotnetBuildEnvironmentManager.ShutdownBuildServersBestEffort();
        }
    }
}
