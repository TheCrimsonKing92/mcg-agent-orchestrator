using System.Diagnostics;
using System.Globalization;
using System.Reflection.PortableExecutable;
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
    int? BuildPermitIndex = null)
{
    internal DotnetBuildEnvironment DeriveArtifactsPath(string artifactsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsPath);
        var arguments = Arguments.ToArray();
        var artifactSwitches = arguments
            .Select((argument, index) => (argument, index))
            .Where(item => item.argument.Equals("--artifacts-path", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .ToArray();
        if (artifactSwitches.Length != 1 || artifactSwitches[0] + 1 >= arguments.Length)
        {
            throw new InvalidOperationException(
                "A dotnet build environment must contain exactly one --artifacts-path argument with a value.");
        }

        arguments[artifactSwitches[0] + 1] = artifactsPath;
        return this with
        {
            ArtifactsPath = artifactsPath,
            Arguments = arguments
        };
    }
}

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
    int? OwnerProcessId,
    int? UnavailableProcessId = null,
    string? UnavailableProcessName = null,
    ProcessInspectionStatus? UnavailableStatus = null,
    int? NativeError = null,
    string? FailureOperation = null);

internal sealed record DotnetBuildServerShutdownOutcome(
    bool Exited,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    string? Failure = null);

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
    public const string ForceCleanStaleLeaseArtifactsVariable = "MCG_DOTNET_FORCE_CLEAN_STALE_LEASE_ARTIFACTS";
    public const int BuildConcurrencySlotCount = 2;
    // Compatibility name for callers migrating from the former artifact-slot grid.
    public const int StableSlotCount = BuildConcurrencySlotCount;
    public static readonly TimeSpan DefaultSlotBusyPollTimeout = TimeSpan.FromSeconds(20);
    private const string LeaseDirectoryName = "lease";
    private const string LeaseMetadataFileName = "lease.json";
    private const string LeaseLockFileName = "lease.lock";
    private const string GoalLeaseReclaimPendingFileName = "goal-lease-reclaim.pending.json";
    private const string ArtifactsOwnerFileName = ".mcg-artifacts-owner.json";
    private const string LeaseJournalFileName = "lease.journal.jsonl";
    private const long LeaseJournalMaxBytes = 1_048_576;
    private const int StaleLeaseIntegrityProbeAttempts = 3;
    private const string LandingTestsRootDirectoryName = "mcg-landing-tests";
    private const string LandingTestFixtureMarkerFileName = ".mcg-landing-fixture.json";
    private static readonly TimeSpan LandingFixtureMarkerStaleAge = TimeSpan.FromHours(2);
    public const string BuildMaxCpuCountVariable = "MCG_BUILD_MAXCPUCOUNT";
    public const string GateBuildMaxCpuCountVariable = "MCG_GATE_BUILD_MAXCPUCOUNT";
    private const int ArtifactPrepBusyRetryLimit = 3;
    private static readonly TimeSpan ArtifactPrepBusyRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SlotBusyPollDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeProvider DefaultLeaseTimeProvider = TimeProvider.System;
    private static readonly Action<TimeSpan> DefaultLeaseSleep = Thread.Sleep;
    private static readonly object CurrentLandingFixtureRootsGate = new();
    private static readonly object LeaseJournalDrainGate = new();
    private static readonly HashSet<string> CurrentLandingFixtureRoots = new(StringComparer.OrdinalIgnoreCase);
    private static int s_nextStableSlotScanStart = -1;
    private static int s_heldExecutionLeaseCount;
    private static int s_compilerLockRecoveryRequested;
    private static readonly object s_buildServerRecoverySync = new();
    private static DotnetBuildServerShutdownOutcome? s_lastBuildServerShutdownOutcome;
    internal static Action<DotnetBuildEnvironment>? PrepareArtifactsDirectoryForTests { get; set; }
    internal static Action<int>? BeforeStaleLeaseIntegrityProbeForTests { get; set; }
    internal static Action? ShutdownBuildServersForTests { get; set; }
    internal static DotnetBuildServerShutdownOutcome? LastBuildServerShutdownOutcome =>
        Volatile.Read(ref s_lastBuildServerShutdownOutcome);
    internal static Func<ProcessCommandLineSnapshot>? ProcessCommandLineSnapshotForTests { get; set; }
    internal static TimeProvider DefaultLeaseTimeProviderForTests => DefaultLeaseTimeProvider;
    internal static Action<TimeSpan> DefaultLeaseSleepForTests => DefaultLeaseSleep;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions LeaseJournalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static DotnetBuildEnvironment CreateAttempt(
        GoalId? goalId,
        string attemptName,
        int slotCount = StableSlotCount,
        DotnetBuildStorageRoot? storageRoot = null)
    {
        storageRoot ??= CaptureStorageRoot();
        ValidateRequestedSlotCount(slotCount);
        if (goalId is not null)
        {
            return CreateGoalLease(goalId, attemptName, slotCount, storageRoot);
        }

        var owner = $"{Environment.ProcessId}-{Sanitize(attemptName)}-{Guid.NewGuid():N}";
        var root = Path.Combine(storageRoot.RootPath, "runs", owner);
        var artifactsPath = Path.Combine(root, "artifacts");
        var executionLockPath = BuildSlotExecutionLockPath(BuildSlotIndex(owner), storageRoot);
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

    internal static DotnetBuildEnvironment ResolveGoalEnvironment(GoalId goalId, DotnetBuildStorageRoot? storageRoot = null)
    {
        storageRoot ??= CaptureStorageRoot();
        ArgumentNullException.ThrowIfNull(goalId);
        var root = GoalRoot(goalId, storageRoot);
        var leaseId = $"goal-{Prefix(goalId)}";
        var leaseDirectory = LeaseDirectory(goalId, storageRoot);
        var metadataPath = Path.Combine(leaseDirectory, LeaseMetadataFileName);
        var artifactsPath = Path.Combine(root, "artifacts");
        var buildPermitIndex = BuildSlotIndex(Prefix(goalId));
        var executionLockPath = BuildSlotExecutionLockPath(buildPermitIndex, storageRoot);
        Directory.CreateDirectory(leaseDirectory);
        Directory.CreateDirectory(artifactsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(executionLockPath)!);

        return new DotnetBuildEnvironment(
            leaseId,
            root,
            artifactsPath,
            executionLockPath,
            BuildArguments(artifactsPath),
            leaseId,
            metadataPath,
            ReusedGoalLease: File.Exists(metadataPath),
            BuildPermitIndex: buildPermitIndex);
    }

    public static string GoalRoot(GoalId goalId, DotnetBuildStorageRoot? storageRoot = null)
    {
        return Path.Combine((storageRoot ?? CaptureStorageRoot()).RootPath, "goals", Prefix(goalId));
    }

    public static string GoalArtifactsPath(GoalId goalId, DotnetBuildStorageRoot? storageRoot = null)
    {
        storageRoot ??= CaptureStorageRoot();
        return TryReadArtifactsPath(Path.Combine(LeaseDirectory(goalId, storageRoot), LeaseMetadataFileName)) ??
            Path.Combine(GoalRoot(goalId, storageRoot), "artifacts");
    }

    public static string BaseBuildCacheRoot(DotnetBuildStorageRoot? storageRoot = null)
    {
        return DotnetBaseBuildCache.DefaultRootPath((storageRoot ?? CaptureStorageRoot()).RootPath);
    }

    public static bool TryCleanupSuccessfulRun(DotnetBuildEnvironment environment, DotnetBuildStorageRoot? storageRoot = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var runsRoot = Path.GetFullPath(Path.Combine((storageRoot ?? CaptureStorageRoot()).RootPath, "runs"))
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

    public static string BuildSlotHeartbeatPath(int slotIndex, DotnetBuildStorageRoot? storageRoot = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotIndex);
        return Path.Combine((storageRoot ?? CaptureStorageRoot()).RootPath, "build-slots", $"activity-{slotIndex}.heartbeat.json");
    }

    public static DotnetBuildEnvironment CreateStableSlotAttempt(
        int slotIndex,
        int slotCount = StableSlotCount,
        DotnetBuildStorageRoot? storageRoot = null)
    {
        ValidateRequestedSlotCount(slotCount);
        if (slotIndex >= slotCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slotIndex),
                slotIndex,
                $"Stable slot index must be 0 through {slotCount - 1} for the requested slot count.");
        }

        return CreateStableSlotEnvironment(slotIndex, storageRoot ?? CaptureStorageRoot());
    }

    public static DotnetBuildLeaseAcquisition TryAcquireStableSlotExecutionLock(
        int slotIndex,
        TimeSpan? timeout,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        Action<TimeSpan>? sleep = null,
        DotnetBuildStorageRoot? storageRoot = null)
    {
        var environment = CreateStableSlotEnvironment(slotIndex, storageRoot ?? CaptureStorageRoot(), createArtifactsDirectory: false);
        return TryAcquireLeaseExecutionLock(environment, timeout, cancellationToken, timeProvider, sleep);
    }

    public static bool IsStableSlotExecutionLeaseAvailable(int slotIndex, DotnetBuildStorageRoot? storageRoot = null)
    {
        var environment = CreateStableSlotEnvironment(slotIndex, storageRoot ?? CaptureStorageRoot());
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

    public static int? GetStableSlotExecutionLeaseOwner(int slotIndex, DotnetBuildStorageRoot? storageRoot = null)
    {
        ValidateStableSlotIndex(slotIndex);
        return TryReadStableSlotExecutionWait(slotIndex,
            CreateStableSlotEnvironment(slotIndex, storageRoot ?? CaptureStorageRoot(), createArtifactsDirectory: false)).OwnerProcessId;
    }

    public static DotnetBuildEnvironmentLease AcquireFirstAvailableStableSlotExecutionLock(
        TimeSpan? timeout = null,
        Action<DotnetBuildStableSlotWait>? onWait = null,
        CancellationToken cancellationToken = default,
        int slotCount = StableSlotCount,
        DotnetBuildStorageRoot? storageRoot = null)
    {
        return TryAcquireFirstAvailableStableSlotExecutionLock(timeout, onWait, cancellationToken, slotCount, storageRoot: storageRoot) switch
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
        int slotCount = StableSlotCount,
        TimeProvider? timeProvider = null,
        Action<TimeSpan>? sleep = null,
        DotnetBuildStorageRoot? storageRoot = null)
    {
        storageRoot ??= CaptureStorageRoot();
        ValidateRequestedSlotCount(slotCount);
        var clock = timeProvider ?? DefaultLeaseTimeProvider;
        var delay = sleep ?? DefaultLeaseSleep;
        var waitTimeout = timeout ?? DefaultSlotBusyPollTimeout;
        var timeoutAt = clock.GetUtcNow().Add(waitTimeout);
        DateTimeOffset? waitStartedAt = null;
        var waitingReported = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processSnapshot = CreateSlotCandidateProcessSnapshot();
            var scanStart = NextStableSlotScanStart(slotCount);
            for (var offset = 0; offset < slotCount; offset++)
            {
                var slot = (scanStart + offset) % slotCount;
                var environment = CreateStableSlotEnvironment(slot, storageRoot);
                if (IsSlotArtifactsBusy(environment, processSnapshot))
                {
                    continue;
                }

                if (TryOpenLeaseExecutionLock(environment, out var stream, out var blockedAttribution))
                {
                    return new DotnetBuildLeaseAcquisition.Acquired(new DotnetBuildEnvironmentLease(
                        environment, stream, waitStartedAt is null ? TimeSpan.Zero : clock.GetUtcNow() - waitStartedAt.Value));
                }
                if (blockedAttribution is not null)
                {
                    return EmitBuildLockBlocked(environment.LeaseId, blockedAttribution);
                }
            }

            var leastRecentlyLeased = FindLeastRecentlyLeasedStableSlot(storageRoot, slotCount);
            if (!waitingReported)
            {
                waitStartedAt = clock.GetUtcNow();
                onWait?.Invoke(new DotnetBuildStableSlotWait(leastRecentlyLeased.SlotIndex, leastRecentlyLeased.OwnerProcessId));
                waitingReported = true;
            }

            var now = clock.GetUtcNow();
            if (now >= timeoutAt)
            {
                return EmitSlotsBusy("first-available-stable-slot",
                    slot => CreateStableSlotEnvironment(slot, storageRoot, createArtifactsDirectory: false),
                    slotCount, processSnapshot);
            }

            var remaining = timeoutAt - now;
            var pollDelay = remaining < SlotBusyPollDelay ? remaining : SlotBusyPollDelay;

            var target = CreateStableSlotEnvironment(leastRecentlyLeased.SlotIndex, storageRoot);
            if (IsSlotArtifactsBusy(target, processSnapshot))
            {
                delay(pollDelay);
                continue;
            }

            if (TryOpenLeaseExecutionLock(target, out var targetStream, out var targetBlockedAttribution))
            {
                return new DotnetBuildLeaseAcquisition.Acquired(new DotnetBuildEnvironmentLease(
                    target, targetStream, waitStartedAt is null ? TimeSpan.Zero : clock.GetUtcNow() - waitStartedAt.Value));
            }
            if (targetBlockedAttribution is not null)
            {
                return EmitBuildLockBlocked(target.LeaseId, targetBlockedAttribution);
            }

            delay(pollDelay);
        }
    }

    public static bool TryRotateGoalLease(GoalId goalId, string reason, DotnetBuildStorageRoot? storageRoot = null)
    {
        storageRoot ??= CaptureStorageRoot();
        var leaseDirectory = LeaseDirectory(goalId, storageRoot);
        if (!Directory.Exists(leaseDirectory))
        {
            return true;
        }

        // The rotated lease stays inside the goal root of the same storage root the lease came from:
        // resolving it ambiently would write another root's lease into the current-directory namespace
        // (and can fail outright across volumes).
        var rotatedRoot = Path.Combine(GoalRoot(goalId, storageRoot), "rotated-leases");
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

    public static bool TryDeleteGoalArtifacts(GoalId goalId, DotnetBuildStorageRoot? storageRoot = null)
    {
        var root = GoalRoot(goalId, storageRoot ?? CaptureStorageRoot());
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

    public static DotnetBuildLeaseStatus InspectGoalLease(GoalId goalId, DotnetBuildStorageRoot? storageRoot = null)
    {
        storageRoot ??= CaptureStorageRoot();
        var root = GoalRoot(goalId, storageRoot);
        var leaseId = $"goal-{Prefix(goalId)}";
        var leaseDirectory = LeaseDirectory(goalId, storageRoot);
        var metadataPath = Path.Combine(leaseDirectory, LeaseMetadataFileName);
        var rootExists = Directory.Exists(root);
        var metadataExists = File.Exists(metadataPath);
        // The no-metadata fallback stays inside the inspected storage root: resolving it ambiently would
        // report another root's artifacts for a lease this call already located under storageRoot.
        var artifactsPath = TryReadArtifactsPath(metadataPath) ?? GoalArtifactsPath(goalId, storageRoot);
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

    public static bool TryCleanupOrphanedGoalLease(GoalId goalId, out DotnetBuildLeaseStatus status, out string detail,
        DotnetBuildStorageRoot? storageRoot = null)
    {
        storageRoot ??= CaptureStorageRoot();
        status = InspectGoalLease(goalId, storageRoot);
        if (!status.CanCleanup)
        {
            detail = status.OwnerProcessAlive
                ? $"Refusing to delete active build lease {status.LeaseId}; owner pid={status.OwnerProcessId} is alive."
                : $"No orphaned build lease to clean for {status.LeaseId}.";
            return false;
        }

        var deleted = TryDeleteGoalArtifacts(goalId, storageRoot);
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
            RunBuildServerShutdownProcess(new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "build-server", "shutdown" }
            });
        }
        catch (Exception exception)
        {
            // Best effort cleanup only; build/test result handling owns the real verdict.
            Volatile.Write(
                ref s_lastBuildServerShutdownOutcome,
                new DotnetBuildServerShutdownOutcome(false, null, string.Empty, string.Empty, exception.Message));
        }
    }

    internal static DotnetBuildServerShutdownOutcome RunBuildServerShutdownProcess(
        ProcessStartInfo startInfo,
        int timeoutMilliseconds = 10_000)
    {
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            var notStarted = new DotnetBuildServerShutdownOutcome(
                false, null, string.Empty, string.Empty, "Process.Start returned null.");
            Volatile.Write(ref s_lastBuildServerShutdownOutcome, notStarted);
            return notStarted;
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(timeoutMilliseconds);
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2_000);
            }
            catch
            {
                // Best effort cleanup only; the retained outcome reports that the shutdown did not exit.
            }
        }

        Task.WaitAll([standardOutput, standardError], 2_000);
        var outcome = new DotnetBuildServerShutdownOutcome(
            exited,
            exited ? process.ExitCode : null,
            standardOutput.IsCompletedSuccessfully ? standardOutput.Result : string.Empty,
            standardError.IsCompletedSuccessfully ? standardError.Result : string.Empty);
        Volatile.Write(ref s_lastBuildServerShutdownOutcome, outcome);
        return outcome;
    }

    private static bool ShutdownBuildServersForCompilerLockRecovery(bool requestRecovery = true)
    {
        lock (s_buildServerRecoverySync)
        {
            if (requestRecovery)
                s_compilerLockRecoveryRequested = 1;
            if (s_heldExecutionLeaseCount != 0 || s_compilerLockRecoveryRequested == 0)
                return false;

            s_compilerLockRecoveryRequested = 0;
            ShutdownBuildServersBestEffort();
            return true;
        }
    }

    internal static void RegisterExecutionLease()
    {
        lock (s_buildServerRecoverySync)
            s_heldExecutionLeaseCount++;
    }

    internal static void RequestCompilerLockRecovery()
    {
        lock (s_buildServerRecoverySync)
            s_compilerLockRecoveryRequested = 1;
    }

    internal static void ReleaseExecutionLease(bool attemptPendingRecovery)
    {
        lock (s_buildServerRecoverySync)
        {
            s_heldExecutionLeaseCount--;
            if (s_heldExecutionLeaseCount < 0)
            {
                s_heldExecutionLeaseCount = 0;
                throw new InvalidOperationException("The held dotnet build execution lease count became negative.");
            }

            if (attemptPendingRecovery && s_heldExecutionLeaseCount == 0)
                ShutdownBuildServersForCompilerLockRecovery(requestRecovery: false);
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

    internal static DotnetBuildEnvironmentLease AcquireLeaseExecutionPermit(
        DotnetBuildEnvironment environment,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        Action<TimeSpan>? sleep = null,
        AcceptanceAttemptArtifactCustodyContext? artifactCustody = null)
    {
        return TryAcquireLeaseExecutionLockCore(
            environment,
            timeout: null,
            cancellationToken,
            timeProvider,
            sleep,
            acceptancePriorityHeldByCaller: false,
            emitSlotsBusyReceipt: true,
            artifactCustody: artifactCustody) switch
        {
            DotnetBuildLeaseAcquisition.Acquired acquired => acquired.Lease,
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
        Action<TimeSpan>? sleep = null) =>
        TryAcquireLeaseExecutionLockCore(
            environment,
            timeout,
            cancellationToken,
            timeProvider,
            sleep,
            acceptancePriorityHeldByCaller: false,
            emitSlotsBusyReceipt: true,
            artifactCustody: null);

    private static DotnetBuildLeaseAcquisition TryAcquireLeaseExecutionLockCore(
        DotnetBuildEnvironment environment,
        TimeSpan? timeout,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider,
        Action<TimeSpan>? sleep,
        bool acceptancePriorityHeldByCaller,
        bool emitSlotsBusyReceipt,
        AcceptanceAttemptArtifactCustodyContext? artifactCustody)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(environment.ExecutionLockPath)!);
        var clock = timeProvider ?? DefaultLeaseTimeProvider;
        var delay = sleep ?? DefaultLeaseSleep;
        var timeoutAt = clock.GetUtcNow().Add(timeout ?? DefaultSlotBusyPollTimeout);
        var attemptedCompilerLockRemediation = false;
        var attemptedOwnedProcessRemediation = false;
        var artifactPrepBusyAttempts = 0;
        BuildLockAttribution? selfHeldLandingFixtureAttribution = null;
        var forceCleanArtifacts = false;
        var pendingStaleExecutionLeaseReclaim = StaleExecutionLeaseReclaim.None;
        FileStream? acceptancePriorityStream = null;
        var acceptancePriorityHeld = acceptancePriorityHeldByCaller;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!acceptancePriorityHeld)
                {
                    try
                    {
                        acceptancePriorityStream = new FileStream(
                            environment.ExecutionLockPath + ".acceptance-priority.lock",
                            FileMode.OpenOrCreate,
                            FileAccess.ReadWrite,
                            FileShare.ReadWrite);
                        acceptancePriorityStream.Lock(0, 1);
                        acceptancePriorityHeld = true;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        acceptancePriorityStream?.Dispose();
                        acceptancePriorityStream = null;
                        if (clock.GetUtcNow() >= timeoutAt)
                        {
                            return CreateSlotsBusy(environment, emitSlotsBusyReceipt);
                        }

                        delay(SlotBusyPollDelay);
                        continue;
                    }
                }

            var reclaim = TryReclaimStaleExecutionLease(environment);
            if (reclaim.Reclaimed)
            {
                pendingStaleExecutionLeaseReclaim = reclaim;
            }

            forceCleanArtifacts |= reclaim.ForceCleanArtifacts;
            LeaseFileStream stream;
            try
            {
                if (selfHeldLandingFixtureAttribution is null &&
                    IsSlotArtifactsBusy(environment))
                {
                    if (clock.GetUtcNow() >= timeoutAt)
                    {
                        return CreateSlotsBusy(environment, emitSlotsBusyReceipt);
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
                    return CreateSlotsBusy(environment, emitSlotsBusyReceipt);
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
                        return CreateSlotsBusy(environment, emitSlotsBusyReceipt);
                    }

                    delay(ArtifactPrepBusyRetryDelay);
                    continue;
                }

                try
                {
                    WriteExecutionLeaseMetadata(stream, environment);
                    PrepareArtifactsDirectory(
                        environment,
                        forceClean: forceCleanArtifacts,
                        currentProcessOwnsExecutionLease: true,
                        ownerMarkerValidated: reclaim.Reclaimed,
                        staleExecutionLeaseReclaim: pendingStaleExecutionLeaseReclaim,
                        artifactCustody: artifactCustody);
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
                        return CreateSlotsBusy(environment, emitSlotsBusyReceipt);
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
        finally
        {
            if (acceptancePriorityStream is not null && acceptancePriorityHeld)
            {
                acceptancePriorityStream.Unlock(0, 1);
            }

            acceptancePriorityStream?.Dispose();
        }
    }

    public static DotnetBuildLeaseAcquisition TryAcquireFirstAvailableBuildPermit(
        DotnetBuildEnvironment environment,
        TimeSpan? timeout,
        CancellationToken cancellationToken = default,
        Action? onWait = null,
        TimeProvider? timeProvider = null,
        Action<TimeSpan>? sleep = null) =>
        TryAcquireFirstAvailableBuildPermitCore(
            environment,
            timeout,
            cancellationToken,
            onWait,
            timeProvider,
            sleep,
            artifactCustody: null);

    internal static DotnetBuildLeaseAcquisition TryAcquireFirstAvailableBuildPermitOwned(
        DotnetBuildEnvironment environment,
        AcceptanceAttemptArtifactCustodyContext artifactCustody,
        TimeSpan? timeout,
        CancellationToken cancellationToken = default,
        Action? onWait = null,
        TimeProvider? timeProvider = null,
        Action<TimeSpan>? sleep = null) =>
        TryAcquireFirstAvailableBuildPermitCore(
            environment,
            timeout,
            cancellationToken,
            onWait,
            timeProvider,
            sleep,
            artifactCustody);

    private static DotnetBuildLeaseAcquisition TryAcquireFirstAvailableBuildPermitCore(
        DotnetBuildEnvironment environment,
        TimeSpan? timeout,
        CancellationToken cancellationToken,
        Action? onWait,
        TimeProvider? timeProvider,
        Action<TimeSpan>? sleep,
        AcceptanceAttemptArtifactCustodyContext? artifactCustody)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var preferredPermit = environment.BuildPermitIndex ?? BuildSlotIndex(environment.SlotOwnerToken);
        var clock = timeProvider ?? DefaultLeaseTimeProvider;
        var delay = sleep ?? DefaultLeaseSleep;
        var waitTimeout = timeout ?? DefaultSlotBusyPollTimeout;
        var timeoutAt = clock.GetUtcNow().Add(waitTimeout);
        var waitingReported = false;
        var priorityReservations = new FileStream?[BuildConcurrencySlotCount];
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var offset = 0; offset < BuildConcurrencySlotCount; offset++)
                {
                    var permit = (preferredPermit + offset) % BuildConcurrencySlotCount;
                    var candidate = WithBuildPermit(environment, permit);
                    var priorityPath = candidate.ExecutionLockPath + ".acceptance-priority.lock";
                    if (priorityReservations[permit] is null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(priorityPath)!);
                        FileStream? reservation = null;
                        try
                        {
                            reservation = new FileStream(
                                priorityPath,
                                FileMode.OpenOrCreate,
                                FileAccess.ReadWrite,
                                FileShare.ReadWrite);
                            reservation.Lock(0, 1);
                            priorityReservations[permit] = reservation;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            reservation?.Dispose();
                            continue;
                        }
                    }
                    var acquisition = TryAcquireLeaseExecutionLockCore(
                        candidate,
                        TimeSpan.Zero,
                        cancellationToken,
                        clock,
                        delay,
                        acceptancePriorityHeldByCaller: true,
                        emitSlotsBusyReceipt: false,
                        artifactCustody: artifactCustody);
                    if (acquisition is not DotnetBuildLeaseAcquisition.SlotsBusy)
                    {
                        return acquisition;
                    }
                }

                if (!waitingReported)
                {
                    onWait?.Invoke();
                    waitingReported = true;
                }

                if (clock.GetUtcNow() >= timeoutAt)
                {
                    return EmitSlotsBusy(environment.LeaseId,
                        permit => CreatePermitDiagnosticEnvironment(environment, permit), BuildConcurrencySlotCount);
                }

                delay(SlotBusyPollDelay);
            }
        }
        finally
        {
            ReleaseAcceptancePriorityReservations(priorityReservations);
        }
    }

    private static (int SlotIndex, int? OwnerProcessId, DateTimeOffset LastAcquiredAt) FindLeastRecentlyLeasedStableSlot(
        DotnetBuildStorageRoot storageRoot,
        int slotCount = StableSlotCount)
    {
        var oldest = (SlotIndex: 0, OwnerProcessId: (int?)null, LastAcquiredAt: DateTimeOffset.MaxValue);
        for (var slot = 0; slot < slotCount; slot++)
        {
            var environment = CreateStableSlotEnvironment(slot, storageRoot, createArtifactsDirectory: false);
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
        int slotCount,
        DotnetBuildStorageRoot storageRoot)
    {
        var root = GoalRoot(goalId, storageRoot);
        var leaseId = $"goal-{Prefix(goalId)}";
        var leaseDirectory = LeaseDirectory(goalId, storageRoot);
        var metadataPath = Path.Combine(leaseDirectory, LeaseMetadataFileName);
        var lockPath = Path.Combine(leaseDirectory, LeaseLockFileName);
        var reused = Directory.Exists(leaseDirectory);
        var artifactsPath = Path.Combine(root, "artifacts");
        var buildPermitIndex = BuildSlotIndex(Prefix(goalId));
        var executionLockPath = BuildSlotExecutionLockPath(buildPermitIndex, storageRoot);
        Directory.CreateDirectory(leaseDirectory);
        Directory.CreateDirectory(artifactsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(executionLockPath)!);
        var staleLockCleared = TryClearStaleLock(lockPath, out var reclaimedProcessId);
        if (staleLockCleared)
        {
            File.WriteAllText(
                Path.Combine(leaseDirectory, GoalLeaseReclaimPendingFileName),
                JsonSerializer.Serialize(
                    new GoalLeaseReclaim(reclaimedProcessId, lockPath),
                    JsonOptions));
        }

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
                staleLockCleared,
                staleLockCleared ? reclaimedProcessId : null),
            JsonOptions));

        var environment = new DotnetBuildEnvironment(
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
        return environment;
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
        var configured = Environment.GetEnvironmentVariable(GateBuildMaxCpuCountVariable);
        if (int.TryParse(configured, out var value) && value > 1)
        {
            return value;
        }

        configured = Environment.GetEnvironmentVariable(BuildMaxCpuCountVariable);
        if (int.TryParse(configured, out value) && value > 1)
        {
            return value;
        }

        return Math.Max(2, Environment.ProcessorCount / BuildConcurrencySlotCount);
    }

    private static string LeaseDirectory(GoalId goalId, DotnetBuildStorageRoot storageRoot)
    {
        return Path.Combine(GoalRoot(goalId, storageRoot), LeaseDirectoryName);
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
        DotnetBuildStorageRoot storageRoot,
        bool createArtifactsDirectory = true)
    {
        ValidateStableSlotIndex(slotIndex);
        var owner = $"p{Environment.ProcessId}-build-{slotIndex}";
        var root = Path.Combine(storageRoot.RootPath, "runs", owner);
        var artifactsPath = Path.Combine(root, "artifacts");
        var executionLockPath = BuildSlotExecutionLockPath(slotIndex % BuildConcurrencySlotCount, storageRoot);
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

    public static DotnetBuildStorageRoot CaptureStorageRoot()
    {
        return new DotnetBuildStorageRoot(Path.GetFullPath(ResolveIsolatedRootBase(
            Environment.GetEnvironmentVariable(IsolatedRootOverrideVariable),
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath(),
            OperatingSystem.IsWindows())));
    }

    internal static string ResolveIsolatedRootBase(
        string? overridden,
        string? localAppDataVariable,
        string? localAppDataKnownFolder,
        string tempPath,
        bool isWindows)
    {
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        if (isWindows)
        {
            // Nested hermetic acceptance processes redirect USERPROFILE, so GetFolderPath can resolve
            // beneath mcg-hvp even though the parent explicitly preserved the real LOCALAPPDATA. Prefer
            // that inherited value so C# callers share the same machine-user Low-integrity slot grid as
            // Invoke-IsolatedDotnet.ps1 and Invoke-WorkerBuildCheck.ps1.
            var localAppData = string.IsNullOrWhiteSpace(localAppDataVariable)
                ? localAppDataKnownFolder
                : localAppDataVariable;
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.GetFullPath(Path.Combine(localAppData, "..", "LocalLow", RootDirectoryName));
            }
        }

        return Path.Combine(tempPath, RootDirectoryName);
    }

    private static string BuildSlotExecutionLockPath(int slotIndex, DotnetBuildStorageRoot storageRoot)
    {
        return Path.Combine(storageRoot.RootPath, "build-slots", $"build-{slotIndex}.lock");
    }

    private static DotnetBuildEnvironment WithBuildPermit(DotnetBuildEnvironment environment, int permitIndex)
    {
        var permitDirectory = Path.GetDirectoryName(Path.GetFullPath(environment.ExecutionLockPath))!;
        return environment with
        {
            ExecutionLockPath = Path.Combine(permitDirectory, $"build-{permitIndex}.lock"),
            BuildPermitIndex = permitIndex
        };
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
        Func<int, DotnetBuildEnvironment> environmentForSlot,
        int slotCount = StableSlotCount,
        ProcessCommandLineSnapshot? processSnapshot = null)
    {
        var busySlots = BuildBusySlotSnapshot(environmentForSlot, slotCount, processSnapshot);
        Console.WriteLine(
            $"SLOTS_BUSY wantedBy={wantedBy} busySlots={FormatBusySlots(busySlots)} pid={Environment.ProcessId}");
        return new DotnetBuildLeaseAcquisition.SlotsBusy(wantedBy, busySlots);
    }

    private static DotnetBuildLeaseAcquisition.SlotsBusy CreateSlotsBusy(
        DotnetBuildEnvironment environment,
        bool emitReceipt)
    {
        if (emitReceipt)
        {
            return EmitSlotsBusy(environment.LeaseId,
                permit => CreatePermitDiagnosticEnvironment(environment, permit), BuildConcurrencySlotCount);
        }

        var busySlots = environment.BuildPermitIndex is { } permitIndex
            ? [new DotnetBuildStableSlotWait(permitIndex, null)]
            : Array.Empty<DotnetBuildStableSlotWait>();
        return new DotnetBuildLeaseAcquisition.SlotsBusy(environment.LeaseId, busySlots);
    }

    private static DotnetBuildEnvironment CreatePermitDiagnosticEnvironment(
        DotnetBuildEnvironment requestingEnvironment,
        int permitIndex)
    {
        // A permit belongs to the shared build-slots namespace. Its diagnostic
        // must not attribute every permit to a consumer of the requesting goal's artifacts.
        var permitDirectory = Path.GetDirectoryName(Path.GetFullPath(requestingEnvironment.ExecutionLockPath))!;
        var storageRoot = new DotnetBuildStorageRoot(Path.GetDirectoryName(permitDirectory)!);
        return CreateStableSlotEnvironment(permitIndex, storageRoot, createArtifactsDirectory: false);
    }

    private static void ReleaseAcceptancePriorityReservations(FileStream?[] reservations)
    {
        for (var index = reservations.Length - 1; index >= 0; index--)
        {
            var reservation = reservations[index];
            if (reservation is null)
            {
                continue;
            }

            try
            {
                reservation.Unlock(0, 1);
            }
            finally
            {
                reservation.Dispose();
            }
        }
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
        Func<int, DotnetBuildEnvironment> environmentForSlot,
        int slotCount = StableSlotCount,
        ProcessCommandLineSnapshot? processSnapshot = null)
    {
        processSnapshot ??= CreateSlotCandidateProcessSnapshot();
        var waits = new DotnetBuildStableSlotWait[slotCount];
        for (var slot = 0; slot < slotCount; slot++)
        {
            waits[slot] = TryReadStableSlotExecutionWait(slot, environmentForSlot(slot), processSnapshot);
        }

        return waits;
    }

    private static DotnetBuildStableSlotWait TryReadStableSlotExecutionWait(
        int slotIndex,
        DotnetBuildEnvironment environment,
        ProcessCommandLineSnapshot? processSnapshot = null)
    {
        var metadata = TryReadExecutionLeaseMetadata(environment.ExecutionLockPath);
        var snapshot = processSnapshot ?? CreateSlotCandidateProcessSnapshot();
        var ownerProcessId = TryFindActiveSlotArtifactConsumer(environment, snapshot)?.ProcessId ??
            metadata?.OwnerProcessId;
        var unavailable = FindUnavailableSlotCandidate(environment, snapshot, metadata);
        return new DotnetBuildStableSlotWait(
            slotIndex,
            ownerProcessId,
            unavailable?.ProcessId,
            unavailable?.Name,
            unavailable?.Status ?? snapshot.Failure?.Status,
            snapshot.Failure?.NativeError,
            snapshot.Failure?.Operation);
    }

    private static string FormatBusySlots(IReadOnlyList<DotnetBuildStableSlotWait> busySlots) =>
        string.Join(
            "|",
            busySlots.Select(FormatBusySlot));

    private static string FormatBusySlot(DotnetBuildStableSlotWait slot)
    {
        var value =
            $"slot-{slot.SlotIndex}:pid-{slot.OwnerProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}";
        if (slot.UnavailableProcessId.HasValue)
        {
            value += $":unavailable-pid-{slot.UnavailableProcessId.Value}:name-{slot.UnavailableProcessName ?? "unknown"}";
        }

        if (slot.UnavailableStatus.HasValue)
        {
            value += $":status-{slot.UnavailableStatus.Value}";
        }

        if (slot.NativeError.HasValue)
        {
            value += $":native-error-{slot.NativeError.Value}";
        }

        if (!string.IsNullOrWhiteSpace(slot.FailureOperation))
        {
            value += $":operation-{slot.FailureOperation}";
        }

        return value;
    }

    private static bool TryOpenLeaseExecutionLock(
        DotnetBuildEnvironment environment,
        out FileStream stream,
        out BuildLockAttribution? blockedAttribution)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(environment.ExecutionLockPath)!);
        var attemptedCompilerLockRemediation = false;
        var attemptedOwnedProcessRemediation = false;
        blockedAttribution = null;
        var forceCleanArtifacts = false;
        var pendingStaleExecutionLeaseReclaim = StaleExecutionLeaseReclaim.None;
        while (true)
        {
            var reclaim = TryReclaimStaleExecutionLease(environment);
            if (reclaim.Reclaimed)
            {
                pendingStaleExecutionLeaseReclaim = reclaim;
            }

            forceCleanArtifacts |= reclaim.ForceCleanArtifacts;
            try
            {
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
                    PrepareArtifactsDirectory(
                        environment,
                        forceClean: forceCleanArtifacts,
                        ownerMarkerValidated: reclaim.Reclaimed,
                        staleExecutionLeaseReclaim: pendingStaleExecutionLeaseReclaim);
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
        if (exception is AcceptanceAttemptArtifactCustodyException custody)
        {
            attribution = new BuildLockAttribution(
                custody.ArtifactsPath,
                [],
                "acceptance-attempt-custody",
                "artifact-prep",
                "prepare-artifacts");
            LockAttribution.EmitReceipt(attribution);
            return ArtifactPrepLockRemediation.SlotBusy;
        }

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
            ShutdownBuildServersForCompilerLockRecovery();
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
            .Where(holder =>
                holder.IsOrchestratorOwned &&
                holder.ProcessId.HasValue &&
                holder.ProcessId.Value != Environment.ProcessId)
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

    private static bool IsSlotArtifactsBusy(
        DotnetBuildEnvironment environment,
        ProcessCommandLineSnapshot? processSnapshot = null)
    {
        var snapshot = processSnapshot ?? CreateSlotCandidateProcessSnapshot();
        return TryFindActiveSlotArtifactConsumer(environment, snapshot) is not null ||
            FindUnavailableSlotCandidate(
                environment,
                snapshot,
                TryReadExecutionLeaseMetadata(environment.ExecutionLockPath)) is not null ||
            snapshot.Failure is not null;
    }

    internal static BuildLockHolder? TryFindActiveSlotArtifactConsumer(
        DotnetBuildEnvironment environment,
        ProcessCommandLineSnapshot? processSnapshot = null,
        Func<int, bool>? queryProcessLiveness = null)
    {
        var artifactsPath = NormalizeForCommandLineMatch(environment.ArtifactsPath);
        if (string.IsNullOrWhiteSpace(artifactsPath))
        {
            return null;
        }

        var snapshot = processSnapshot ?? CreateSlotCandidateProcessSnapshot();
        foreach (var pair in snapshot.Records)
        {
            var record = pair.Value;
            if (pair.Key == Environment.ProcessId ||
                record.Status is ProcessInspectionStatus.Exited or ProcessInspectionStatus.DeadOrRecycled)
            {
                continue;
            }

            if (record.Status != ProcessInspectionStatus.Available ||
                string.IsNullOrWhiteSpace(record.CommandLine))
            {
                continue;
            }

            if (!CommandLineUsesSlotArtifacts(record.CommandLine, artifactsPath) ||
                !IsProcessRunning(pair.Key, queryProcessLiveness))
            {
                continue;
            }

            return CreateProcessHolder(pair.Key, record.CommandLine, isOrchestratorOwned: true);
        }

        return null;
    }

    private static ProcessInspectionRecord? FindUnavailableSlotCandidate(
        DotnetBuildEnvironment environment,
        ProcessCommandLineSnapshot snapshot,
        ExecutionLeaseMetadata? metadata)
    {
        if (metadata is null ||
            metadata.OwnerProcessId == Environment.ProcessId ||
            string.IsNullOrWhiteSpace(metadata.SlotOwnerToken) ||
            !metadata.SlotOwnerToken.Equals(
                environment.SlotOwnerToken,
                StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(metadata.MachineName) &&
                !metadata.MachineName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) ||
            !snapshot.Records.TryGetValue(metadata.OwnerProcessId, out var record) ||
            !IsProcessRunning(metadata.OwnerProcessId))
        {
            return null;
        }

        var unavailable =
            record.Status is not ProcessInspectionStatus.Exited and not ProcessInspectionStatus.DeadOrRecycled &&
            (record.Status != ProcessInspectionStatus.Available ||
                string.IsNullOrWhiteSpace(record.CommandLine));
        return unavailable ? record : null;
    }

    private static ProcessCommandLineSnapshot CreateSlotCandidateProcessSnapshot()
    {
        if (ProcessCommandLineSnapshotForTests is not null)
        {
            return ProcessCommandLineSnapshotForTests();
        }

        return ProcessCommandLines.SnapshotByNames(["testhost", "vstest.console", "datacollector", "dotnet"]);
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
        bool currentProcessOwnsExecutionLease = false,
        bool ownerMarkerValidated = false,
        StaleExecutionLeaseReclaim? staleExecutionLeaseReclaim = null,
        AcceptanceAttemptArtifactCustodyContext? artifactCustody = null)
    {
        PrepareArtifactsDirectoryForTests?.Invoke(environment);
        var clean = forceClean;
        var ownerPath = Path.Combine(environment.ArtifactsPath, ArtifactsOwnerFileName);
        var ownerMarkerMismatch = false;
        var goalLeaseReclaim = TryReadPendingGoalLeaseReclaim(environment);
        if (!ownerMarkerValidated &&
            Directory.Exists(environment.ArtifactsPath) &&
            Directory.EnumerateFileSystemEntries(environment.ArtifactsPath).Any())
        {
            ownerMarkerMismatch = !OwnerMarkerMatches(ownerPath, environment.SlotOwnerToken);
            clean |= ownerMarkerMismatch;
        }

        if (clean)
        {
            AcceptanceAttemptArtifactCustody.ThrowIfLiveCustodianBlocksTakeover(
                environment.ArtifactsPath,
                artifactCustody?.AttemptId);
        }

        var decision = "preserved";
        StaleLeaseArtifactIntegrity? outcomeOverride = null;
        if (clean && Directory.Exists(environment.ArtifactsPath))
        {
            try
            {
                Directory.Delete(environment.ArtifactsPath, recursive: true);
                decision = "wiped";
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
                    var failedIntegrity = new StaleLeaseArtifactIntegrity(
                        true,
                        "wipe-failed",
                        lockedPath);
                    EmitArtifactPreparationReceipts(
                        environment,
                        staleExecutionLeaseReclaim,
                        goalLeaseReclaim,
                        ownerMarkerMismatch,
                        failedIntegrity,
                        "wipe-failed");
                    throw;
                }

                outcomeOverride = new StaleLeaseArtifactIntegrity(
                    false,
                    "wipe-skipped-self-held-lock",
                    lockedPath);
            }
        }

        Directory.CreateDirectory(environment.ArtifactsPath);
        if (outcomeOverride is null)
        {
            WriteOwnerMarker(ownerPath, environment.SlotOwnerToken);
        }

        if (artifactCustody is not null)
        {
            AcceptanceAttemptArtifactCustody.Write(
                environment.ArtifactsPath,
                artifactCustody.AttemptId,
                artifactCustody.LivenessCheckHint,
                artifactCustody.OwnerProcessId);
        }

        var outcomeIntegrity = outcomeOverride ??
            (ownerMarkerMismatch
                ? new StaleLeaseArtifactIntegrity(true, "owner-marker-mismatch", ownerPath)
                : null);
        EmitArtifactPreparationReceipts(
            environment,
            staleExecutionLeaseReclaim,
            goalLeaseReclaim,
            ownerMarkerMismatch,
            outcomeIntegrity,
            decision);
        if (goalLeaseReclaim is not null)
        {
            MarkGoalLeaseReclaimReceiptEmitted(environment);
        }
    }

    private static void EmitArtifactPreparationReceipts(
        DotnetBuildEnvironment environment,
        StaleExecutionLeaseReclaim? staleExecutionLeaseReclaim,
        GoalLeaseReclaim? goalLeaseReclaim,
        bool ownerMarkerMismatch,
        StaleLeaseArtifactIntegrity? outcomeOverride,
        string decision)
    {
        if (staleExecutionLeaseReclaim is { Reclaimed: true, Integrity: { } integrity })
        {
            EmitLeaseReclaimReceipt(
                environment,
                staleExecutionLeaseReclaim.ReclaimedProcessId,
                outcomeOverride ?? integrity,
                decision: decision);
        }

        if (ownerMarkerMismatch)
        {
            EmitLeaseReclaimReceipt(
                environment,
                reclaimedProcessId: null,
                integrity: outcomeOverride ??
                    new StaleLeaseArtifactIntegrity(true, "owner-marker-mismatch", Path.Combine(environment.ArtifactsPath, ArtifactsOwnerFileName)),
                receipt: "ARTIFACT_PREP",
                decision: decision);
        }

        if (goalLeaseReclaim is not null)
        {
            EmitLeaseReclaimReceipt(
                environment,
                goalLeaseReclaim.ReclaimedProcessId,
                integrity: outcomeOverride ??
                    new StaleLeaseArtifactIntegrity(false, "goal-lease-dead-holder", goalLeaseReclaim.TriggerPath),
                receipt: "GOAL_LEASE_RECLAIM",
                decision: decision);
        }
    }

    private static GoalLeaseReclaim? TryReadPendingGoalLeaseReclaim(DotnetBuildEnvironment environment)
    {
        var pendingPath = Path.Combine(
            environment.RootPath,
            LeaseDirectoryName,
            GoalLeaseReclaimPendingFileName);
        if (!File.Exists(pendingPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GoalLeaseReclaim>(
                File.ReadAllText(pendingPath),
                JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void MarkGoalLeaseReclaimReceiptEmitted(DotnetBuildEnvironment environment)
    {
        var pendingPath = Path.Combine(
            environment.RootPath,
            LeaseDirectoryName,
            GoalLeaseReclaimPendingFileName);
        try
        {
            File.Delete(pendingPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine(
                $"GOAL_LEASE_RECLAIM_STATE_PENDING pid={Environment.ProcessId} pending=\"{pendingPath}\" error={ex.GetType().Name}");
        }
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

    private static StaleExecutionLeaseReclaim TryReclaimStaleExecutionLease(DotnetBuildEnvironment environment)
    {
        var metadata = TryReadExecutionLeaseMetadata(environment.ExecutionLockPath);
        if (metadata?.OwnerProcessId is not { } processId || IsProcessRunning(processId))
        {
            return StaleExecutionLeaseReclaim.None;
        }

        var integrity = ProbeStaleLeaseArtifacts(environment, metadata.AcquiredAt);
        try
        {
            File.Delete(environment.ExecutionLockPath);
            AcceptanceAttemptArtifactCustody.ClearIfStale(environment.ArtifactsPath);
            EmitLeaseReceipt("LEASE_RECLAIM", environment, processId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StaleExecutionLeaseReclaim.None;
        }

        return new StaleExecutionLeaseReclaim(true, processId, integrity);
    }

    private static StaleLeaseArtifactIntegrity ProbeStaleLeaseArtifacts(
        DotnetBuildEnvironment environment,
        DateTimeOffset leaseAcquiredAt)
    {
        if (IsEnabled(Environment.GetEnvironmentVariable(ForceCleanStaleLeaseArtifactsVariable)))
        {
            return new StaleLeaseArtifactIntegrity(
                true,
                "operator-forced",
                $"env:{ForceCleanStaleLeaseArtifactsVariable}");
        }

        for (var attempt = 1; attempt <= StaleLeaseIntegrityProbeAttempts; attempt++)
        {
            try
            {
                BeforeStaleLeaseIntegrityProbeForTests?.Invoke(attempt);
                return ProbeStaleLeaseArtifactsOnce(environment, leaseAcquiredAt);
            }
            catch (Exception ex) when (
                (ex is IOException or UnauthorizedAccessException) &&
                attempt < StaleLeaseIntegrityProbeAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(10));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new StaleLeaseArtifactIntegrity(true, "integrity-probe-failed", environment.ArtifactsPath);
            }
        }

        throw new InvalidOperationException("Stale lease integrity probe exhausted without a verdict.");
    }

    private static StaleLeaseArtifactIntegrity ProbeStaleLeaseArtifactsOnce(
        DotnetBuildEnvironment environment,
        DateTimeOffset leaseAcquiredAt)
    {
        var ownerPath = Path.Combine(environment.ArtifactsPath, ArtifactsOwnerFileName);
        if (!Directory.Exists(environment.ArtifactsPath) ||
            !Directory.EnumerateFileSystemEntries(environment.ArtifactsPath).Any())
        {
            return new StaleLeaseArtifactIntegrity(false, "artifacts-empty", environment.ArtifactsPath);
        }

        if (!File.Exists(ownerPath))
        {
            return new StaleLeaseArtifactIntegrity(true, "missing-owner-marker", ownerPath);
        }

        try
        {
            var marker = JsonSerializer.Deserialize<ArtifactsOwnerMarker>(File.ReadAllText(ownerPath), JsonOptions);
            if (marker is null ||
                string.IsNullOrWhiteSpace(marker.OwnerToken) ||
                !string.Equals(marker.OwnerToken, environment.SlotOwnerToken, StringComparison.Ordinal))
            {
                return new StaleLeaseArtifactIntegrity(true, "invalid-owner-marker", ownerPath);
            }
        }
        catch (JsonException)
        {
            return new StaleLeaseArtifactIntegrity(true, "invalid-owner-marker", ownerPath);
        }

        var probeAfterUtc = leaseAcquiredAt == DateTimeOffset.MinValue
            ? DateTime.MinValue
            : leaseAcquiredAt.UtcDateTime.AddSeconds(-2);
        foreach (var dll in new DirectoryInfo(environment.ArtifactsPath)
            .EnumerateFiles("*.dll", SearchOption.AllDirectories)
            .Where(file => file.LastWriteTimeUtc >= probeAfterUtc))
        {
            if (dll.Length == 0)
            {
                return new StaleLeaseArtifactIntegrity(true, "zero-length-dll", dll.FullName);
            }

            if (!HasValidPortableExecutableHeader(dll))
            {
                return new StaleLeaseArtifactIntegrity(true, "invalid-pe-dll", dll.FullName);
            }
        }

        return new StaleLeaseArtifactIntegrity(false, "integrity-ok", ownerPath);
    }

    private static bool HasValidPortableExecutableHeader(FileInfo file)
    {
        using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        try
        {
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var headers = peReader.PEHeaders;
            if (headers.PEHeader is null ||
                headers.PEHeader.SizeOfHeaders > stream.Length)
            {
                return false;
            }

            return headers.SectionHeaders.All(section =>
                section.PointerToRawData >= 0 &&
                section.SizeOfRawData >= 0 &&
                (long)section.PointerToRawData + section.SizeOfRawData <= stream.Length);
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static bool IsEnabled(string? value) =>
        value is not null &&
        (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static void EmitLeaseReclaimReceipt(
        DotnetBuildEnvironment environment,
        int? reclaimedProcessId,
        StaleLeaseArtifactIntegrity integrity,
        string receipt = "LEASE_RECLAIM",
        string? decision = null)
    {
        var reclaimDecision = decision ?? (integrity.ForceCleanArtifacts ? "wiped" : "preserved");
        var journalPath = Path.Combine(
            Path.GetDirectoryName(environment.ExecutionLockPath)!,
            LeaseJournalFileName);
        var entry = new LeaseJournalEntry(
            1,
            DateTimeOffset.UtcNow,
            receipt,
            Environment.ProcessId,
            ReceiptSlotName(environment),
            environment.LeaseId,
            reclaimedProcessId,
            reclaimDecision,
            integrity.Reason,
            integrity.TriggerPath);
        var journalWritten = TryAppendLeaseJournal(
            journalPath,
            JsonSerializer.Serialize(entry, LeaseJournalJsonOptions) + Environment.NewLine);

        Console.WriteLine(
            $"{receipt} pid={Environment.ProcessId} slot={ReceiptSlotName(environment)} lease={environment.LeaseId} reclaimedPid={reclaimedProcessId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} decision={reclaimDecision} reason={integrity.Reason} triggerPath=\"{integrity.TriggerPath}\" journal=\"{journalPath}\" journalStatus={(journalWritten ? "written" : "pending")} path={environment.ExecutionLockPath}");
    }

    private static bool TryAppendLeaseJournal(string journalPath, string serializedEntry)
    {
        string? pendingPath = null;
        try
        {
            var directory = Path.GetDirectoryName(journalPath)!;
            Directory.CreateDirectory(directory);
            pendingPath = Path.Combine(
                directory,
                $"{Path.GetFileName(journalPath)}.pending-{Guid.NewGuid():N}.jsonl");
            File.WriteAllText(pendingPath, serializedEntry);
            DrainPendingLeaseJournalEntries(journalPath);
            return !File.Exists(pendingPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine(
                $"LEASE_RECLAIM_JOURNAL_PENDING pid={Environment.ProcessId} journal=\"{journalPath}\" pending=\"{pendingPath ?? "unavailable"}\" error={ex.GetType().Name}");
            return false;
        }
    }

    private static void DrainPendingLeaseJournalEntries(string journalPath)
    {
        lock (LeaseJournalDrainGate)
        {
            using var drainLock = new FileStream(
                journalPath + ".drain.lock",
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            var directory = Path.GetDirectoryName(journalPath)!;
            var pattern = $"{Path.GetFileName(journalPath)}.pending-*.jsonl";
            foreach (var pendingPath in Directory.GetFiles(directory, pattern)
                .OrderBy(ReadPendingLeaseJournalRecordedAt)
                .ThenBy(path => path, StringComparer.Ordinal))
            {
                RotateLeaseJournalIfNeeded(journalPath);
                File.AppendAllText(journalPath, File.ReadAllText(pendingPath));
                File.Delete(pendingPath);
            }
        }
    }

    private static DateTimeOffset ReadPendingLeaseJournalRecordedAt(string pendingPath)
    {
        try
        {
            return JsonSerializer.Deserialize<LeaseJournalEntry>(
                    File.ReadAllText(pendingPath),
                    LeaseJournalJsonOptions)
                ?.RecordedAt ?? DateTimeOffset.MaxValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return DateTimeOffset.MaxValue;
        }
    }

    private static void RotateLeaseJournalIfNeeded(string journalPath)
    {
        if (!File.Exists(journalPath) || new FileInfo(journalPath).Length < LeaseJournalMaxBytes)
        {
            return;
        }

        var directory = Path.GetDirectoryName(journalPath)!;
        var fileName = Path.GetFileNameWithoutExtension(journalPath);
        var extension = Path.GetExtension(journalPath);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var rotatedPath = Path.Combine(directory, $"{fileName}-{stamp}{extension}");
        for (var index = 1; File.Exists(rotatedPath); index++)
        {
            rotatedPath = Path.Combine(directory, $"{fileName}-{stamp}-{index}{extension}");
        }

        File.Move(journalPath, rotatedPath);
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

    private static bool TryClearStaleLock(string lockPath, out int? reclaimedProcessId)
    {
        reclaimedProcessId = null;
        if (!File.Exists(lockPath))
        {
            return false;
        }

        string text;
        try
        {
            text = File.ReadAllText(lockPath).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (int.TryParse(text, out var processId))
        {
            reclaimedProcessId = processId;
            if (IsProcessRunning(processId))
            {
                return false;
            }
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

    private static bool IsProcessRunning(
        int processId,
        Func<int, bool>? queryProcessLiveness = null)
    {
        try
        {
            if (queryProcessLiveness is not null)
            {
                return queryProcessLiveness(processId);
            }

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
        catch (System.ComponentModel.Win32Exception)
        {
            // A lower-integrity caller may be unable to query a live owner. Preserve the lease
            // until the byte-range lock and process identity can be observed authoritatively.
            return true;
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
        bool StaleLockCleared,
        int? ReclaimedProcessId = null);

    private sealed record ArtifactsOwnerMarker(
        int Version,
        string OwnerToken,
        int OwnerProcessId,
        string MachineName,
        DateTimeOffset LastAcquiredAt);

    private sealed record StaleExecutionLeaseReclaim(
        bool Reclaimed,
        int? ReclaimedProcessId,
        StaleLeaseArtifactIntegrity? Integrity)
    {
        public bool ForceCleanArtifacts => Integrity?.ForceCleanArtifacts == true;

        public static StaleExecutionLeaseReclaim None { get; } = new(false, null, null);
    }

    private sealed record GoalLeaseReclaim(int? ReclaimedProcessId, string TriggerPath);

    private sealed record StaleLeaseArtifactIntegrity(
        bool ForceCleanArtifacts,
        string Reason,
        string TriggerPath);

    private sealed record LeaseJournalEntry(
        int Version,
        DateTimeOffset RecordedAt,
        string Event,
        int ProcessId,
        string Slot,
        string LeaseId,
        int? ReclaimedProcessId,
        string Decision,
        string Reason,
        string TriggerPath);

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
    private readonly object _releaseObserverGate = new();
    private Action? _executionLockReleaseObserver;
    private int _state;

    internal DotnetBuildEnvironmentLease(DotnetBuildEnvironment environment, FileStream stream, TimeSpan? slotWaitDuration = null)
    {
        Environment = environment;
        _stream = stream;
        SlotWaitDuration = slotWaitDuration;
        DotnetBuildEnvironmentManager.RegisterExecutionLease();
    }

    public DotnetBuildEnvironment Environment { get; }
    public TimeSpan? SlotWaitDuration { get; }

    internal bool IsExecutionLockHeld => Volatile.Read(ref _state) == 0;

    internal FileStream DetachStreamForLegacyCaller()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            throw new InvalidOperationException("The execution lock has already left lease custody.");
        }

        DotnetBuildEnvironmentManager.ReleaseExecutionLease(attemptPendingRecovery: false);
        return _stream;
    }

    internal void MarkCompilerLockRemediationRequired() =>
        DotnetBuildEnvironmentManager.RequestCompilerLockRecovery();

    internal void ReleaseExecutionLock()
    {
        ReleaseExecutionLockCore();
    }

    internal void RegisterExecutionLockReleaseObserver(Action observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_releaseObserverGate)
        {
            if (_state != 0)
            {
                throw new InvalidOperationException("The execution lock has already been released.");
            }

            if (_executionLockReleaseObserver is not null)
            {
                throw new InvalidOperationException("An execution lock release observer is already registered.");
            }

            _executionLockReleaseObserver = observer;
        }
    }

    public void Dispose()
    {
        ReleaseExecutionLockCore();
    }

    private void ReleaseExecutionLockCore()
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
            DotnetBuildEnvironmentManager.ReleaseExecutionLease(attemptPendingRecovery: true);
        }

        Action? observer;
        lock (_releaseObserverGate)
        {
            observer = _executionLockReleaseObserver;
            _executionLockReleaseObserver = null;
        }

        try
        {
            observer?.Invoke();
        }
        catch
        {
            // Permit-release telemetry is advisory and cannot alter gate disposition.
        }
    }
}
