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
    bool StaleLockCleared = false);

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

public sealed record DotnetTesthostFirewallPath(
    int SlotIndex,
    string Project,
    string Configuration,
    string Path);

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

    // Supported escape hatch for tests that need lease-root isolation. Production and acceptance
    // runs use the default stable slots so their testhost.exe paths stay firewall-covered.
    public const string IsolatedRootOverrideVariable = "MCG_DOTNET_ISOLATED_ROOT";
    public const int StableSlotCount = 4;
    public static readonly TimeSpan DefaultSlotBusyPollTimeout = TimeSpan.FromSeconds(20);
    private const string LeaseDirectoryName = "lease";
    private const string LeaseMetadataFileName = "lease.json";
    private const string LeaseLockFileName = "lease.lock";
    private const string ArtifactsOwnerFileName = ".mcg-artifacts-owner.json";
    private const string LandingTestsRootDirectoryName = "mcg-landing-tests";
    public const string BuildMaxCpuCountVariable = "MCG_BUILD_MAXCPUCOUNT";
    private static readonly object CurrentLandingFixtureRootsGate = new();
    private static readonly HashSet<string> CurrentLandingFixtureRoots = new(StringComparer.OrdinalIgnoreCase);
    private static int s_nextStableSlotScanStart = -1;
    internal static Action<DotnetBuildEnvironment>? PrepareArtifactsDirectoryForTests { get; set; }
    internal static Action? ShutdownBuildServersForTests { get; set; }
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static DotnetBuildEnvironment CreateAttempt(GoalId? goalId, string attemptName)
    {
        if (goalId is not null)
        {
            return CreateGoalLease(goalId, attemptName);
        }

        var root = StableSlotRoot("manual");
        var artifactsPath = StableSlotArtifactsPath("manual");
        var executionLockPath = StableSlotExecutionLockPath("manual");
        Directory.CreateDirectory(artifactsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(executionLockPath)!);

        return new DotnetBuildEnvironment(
            "run-slot-manual",
            root,
            artifactsPath,
            executionLockPath,
            BuildArguments(artifactsPath),
            "manual");
    }

    public static string GoalRoot(GoalId goalId)
    {
        return Path.Combine(IsolatedRootBase(), "goals", Prefix(goalId));
    }

    public static string GoalArtifactsPath(GoalId goalId)
    {
        return TryReadArtifactsPath(Path.Combine(LeaseDirectory(goalId), LeaseMetadataFileName)) ??
            StableSlotArtifactsPath(StableSlotName(goalId));
    }

    public static IReadOnlyList<DotnetTesthostFirewallPath> StableSlotTesthostFirewallPaths()
    {
        DotnetTesthostFirewallPath[] paths = new DotnetTesthostFirewallPath[StableSlotCount * 4];
        var index = 0;
        for (var slot = 0; slot < StableSlotCount; slot++)
        {
            foreach (var project in TesthostFirewallProjects)
            {
                foreach (var configuration in TesthostFirewallConfigurations)
                {
                    paths[index++] = new DotnetTesthostFirewallPath(
                        slot,
                        project.RuleProject,
                        configuration,
                        Path.Combine(
                            StableSlotArtifactsPath($"slot-{slot}"),
                            "bin",
                            project.ArtifactProject,
                            $"{configuration.ToLowerInvariant()}_net10.0",
                            "testhost.exe"));
                }
            }
        }

        return paths;
    }

    public static string StableSlotArtifactsPath(int slotIndex)
    {
        ValidateStableSlotIndex(slotIndex);
        return StableSlotArtifactsPath($"slot-{slotIndex}");
    }

    public static IReadOnlyList<string> StableSlotBuildArguments(int slotIndex)
    {
        return BuildArguments(StableSlotArtifactsPath(slotIndex));
    }

    public static DotnetBuildEnvironment CreateStableSlotAttempt(int slotIndex)
    {
        return CreateStableSlotEnvironment(slotIndex);
    }

    public static DotnetBuildEnvironmentLease AcquireFirstAvailableStableSlotExecutionLock(
        TimeSpan? timeout = null,
        Action<DotnetBuildStableSlotWait>? onWait = null,
        CancellationToken cancellationToken = default)
    {
        return TryAcquireFirstAvailableStableSlotExecutionLock(timeout, onWait, cancellationToken) switch
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
        CancellationToken cancellationToken = default)
    {
        var waitTimeout = timeout ?? DefaultSlotBusyPollTimeout;
        var timeoutAt = DateTimeOffset.UtcNow.Add(waitTimeout);
        var waitingReported = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scanStart = NextStableSlotScanStart();
            for (var offset = 0; offset < StableSlotCount; offset++)
            {
                var slot = (scanStart + offset) % StableSlotCount;
                var environment = CreateStableSlotEnvironment(slot);
                if (TryOpenLeaseExecutionLock(environment, out var stream, out var blockedAttribution))
                {
                    return new DotnetBuildLeaseAcquisition.Acquired(new DotnetBuildEnvironmentLease(environment, stream));
                }
                if (blockedAttribution is not null)
                {
                    return EmitBuildLockBlocked(environment.LeaseId, blockedAttribution);
                }
            }

            if (DateTimeOffset.UtcNow >= timeoutAt)
            {
                return EmitSlotsBusy("first-available-stable-slot");
            }

            var leastRecentlyLeased = FindLeastRecentlyLeasedStableSlot();
            if (!waitingReported)
            {
                onWait?.Invoke(new DotnetBuildStableSlotWait(leastRecentlyLeased.SlotIndex, leastRecentlyLeased.OwnerProcessId));
                waitingReported = true;
            }

            var target = CreateStableSlotEnvironment(leastRecentlyLeased.SlotIndex);
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
        CancellationToken cancellationToken = default) =>
        AcquireLeaseExecutionLock(environment, timeout: null, cancellationToken);

    public static FileStream AcquireLeaseExecutionLock(
        DotnetBuildEnvironment environment,
        TimeSpan? timeout,
        CancellationToken cancellationToken = default)
    {
        return TryAcquireLeaseExecutionLock(environment, timeout, cancellationToken) switch
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
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(environment.ExecutionLockPath)!);
        var timeoutAt = DateTimeOffset.UtcNow.Add(timeout ?? DefaultSlotBusyPollTimeout);
        var attemptedCompilerLockRemediation = false;
        var attemptedOwnedProcessRemediation = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LeaseFileStream stream;
            var reclaimed = TryReclaimStaleExecutionLease(environment);
            try
            {
                stream = OpenExecutionLeaseStream(environment);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTimeOffset.UtcNow >= timeoutAt)
                {
                    return EmitSlotsBusy(environment.LeaseId);
                }

                Thread.Sleep(100);
                continue;
            }

            try
            {
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
                if (TryRemediateArtifactPrepLock(
                    environment,
                    ex,
                    ref attemptedCompilerLockRemediation,
                    ref attemptedOwnedProcessRemediation,
                    out var blockedAttribution))
                {
                    continue;
                }

                if (DateTimeOffset.UtcNow >= timeoutAt)
                {
                    return EmitBuildLockBlocked(environment.LeaseId, blockedAttribution);
                }

                Thread.Sleep(100);
            }
        }
    }

    private static (int SlotIndex, int? OwnerProcessId, DateTimeOffset LastAcquiredAt) FindLeastRecentlyLeasedStableSlot()
    {
        var oldest = (SlotIndex: 0, OwnerProcessId: (int?)null, LastAcquiredAt: DateTimeOffset.MaxValue);
        for (var slot = 0; slot < StableSlotCount; slot++)
        {
            var marker = TryReadStableSlotOwnerMarker(slot);
            var acquiredAt = marker?.LastAcquiredAt ?? DateTimeOffset.MinValue;
            if (acquiredAt < oldest.LastAcquiredAt)
            {
                oldest = (slot, marker?.OwnerProcessId, acquiredAt);
            }
        }

        return oldest;
    }

    private static int NextStableSlotScanStart()
    {
        return (int)((uint)Interlocked.Increment(ref s_nextStableSlotScanStart) % StableSlotCount);
    }

    private static DotnetBuildEnvironment CreateGoalLease(GoalId goalId, string attemptName)
    {
        var root = GoalRoot(goalId);
        var leaseId = $"goal-{Prefix(goalId)}";
        var leaseDirectory = LeaseDirectory(goalId);
        var metadataPath = Path.Combine(leaseDirectory, LeaseMetadataFileName);
        var lockPath = Path.Combine(leaseDirectory, LeaseLockFileName);
        var reused = Directory.Exists(leaseDirectory);
        var slotName = SelectStableSlotNameForGoal(metadataPath, reused);
        var artifactsPath = StableSlotArtifactsPath(slotName);
        var executionLockPath = StableSlotExecutionLockPath(slotName);
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
            StaleLockCleared: staleLockCleared);
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

        return Math.Max(2, Environment.ProcessorCount / StableSlotCount);
    }

    private static string LeaseDirectory(GoalId goalId)
    {
        return Path.Combine(GoalRoot(goalId), LeaseDirectoryName);
    }

    private static string StableSlotName(GoalId goalId)
    {
        var hash = 0;
        foreach (var ch in Prefix(goalId))
        {
            hash = unchecked((hash * 31) + char.ToLowerInvariant(ch));
        }

        return $"slot-{Math.Abs(hash % StableSlotCount)}";
    }

    private static string SelectStableSlotNameForGoal(string metadataPath, bool reused)
    {
        if (reused &&
            TryReadGoalLeaseSlotName(metadataPath) is { } existingSlotName &&
            IsStableSlotExecutionLockAvailable(existingSlotName))
        {
            return existingSlotName;
        }

        var scanStart = NextStableSlotScanStart();
        for (var offset = 0; offset < StableSlotCount; offset++)
        {
            var slotName = $"slot-{(scanStart + offset) % StableSlotCount}";
            if (IsStableSlotExecutionLockAvailable(slotName))
            {
                return slotName;
            }
        }

        var leastRecentlyLeased = FindLeastRecentlyLeasedStableSlot();
        return $"slot-{leastRecentlyLeased.SlotIndex}";
    }

    private static string? TryReadGoalLeaseSlotName(string metadataPath)
    {
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            if (!document.RootElement.TryGetProperty("artifactsPath", out var artifactsPath) ||
                artifactsPath.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return TryStableSlotNameFromArtifactsPath(artifactsPath.GetString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? TryStableSlotNameFromArtifactsPath(string? artifactsPath)
    {
        if (string.IsNullOrWhiteSpace(artifactsPath))
        {
            return null;
        }

        var trimmed = artifactsPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var slotName = Path.GetFileName(Path.GetDirectoryName(trimmed));
        return IsStableSlotName(slotName) ? slotName : null;
    }

    private static bool IsStableSlotExecutionLockAvailable(string slotName)
    {
        var executionLockPath = StableSlotExecutionLockPath(slotName);
        Directory.CreateDirectory(Path.GetDirectoryName(executionLockPath)!);
        var createdByProbe = !File.Exists(executionLockPath);
        try
        {
            using (new FileStream(executionLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
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

    private static bool IsStableSlotName(string? slotName)
    {
        if (slotName is null ||
            !slotName.StartsWith("slot-", StringComparison.Ordinal) ||
            !int.TryParse(slotName["slot-".Length..], out var slotIndex))
        {
            return false;
        }

        return slotIndex >= 0 && slotIndex < StableSlotCount;
    }

    private static DotnetBuildEnvironment CreateStableSlotEnvironment(int slotIndex)
    {
        ValidateStableSlotIndex(slotIndex);
        var slotName = $"slot-{slotIndex}";
        var root = StableSlotRoot(slotName);
        var artifactsPath = StableSlotArtifactsPath(slotName);
        var executionLockPath = StableSlotExecutionLockPath(slotName);
        Directory.CreateDirectory(artifactsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(executionLockPath)!);

        return new DotnetBuildEnvironment(
            $"run-{slotName}",
            root,
            artifactsPath,
            executionLockPath,
            BuildArguments(artifactsPath),
            slotName);
    }

    private static void ValidateStableSlotIndex(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= StableSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex, $"Stable slot index must be 0 through {StableSlotCount - 1}.");
        }
    }

    private static string IsolatedRootBase()
    {
        var overridden = Environment.GetEnvironmentVariable(IsolatedRootOverrideVariable);
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Path.GetTempPath(), RootDirectoryName)
            : overridden;
    }

    private static string StableSlotRoot(string slotName)
    {
        return Path.Combine(IsolatedRootBase(), "slots", slotName);
    }

    private static string StableSlotArtifactsPath(string slotName)
    {
        return Path.Combine(StableSlotRoot(slotName), "artifacts");
    }

    private static string StableSlotExecutionLockPath(string slotName)
    {
        return Path.Combine(StableSlotRoot(slotName), "lease.execution.lock");
    }

    private static DotnetBuildLeaseAcquisition.SlotsBusy EmitSlotsBusy(string wantedBy)
    {
        var busySlots = BuildBusySlotSnapshot();
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

    private static IReadOnlyList<DotnetBuildStableSlotWait> BuildBusySlotSnapshot()
    {
        var waits = new DotnetBuildStableSlotWait[StableSlotCount];
        for (var slot = 0; slot < StableSlotCount; slot++)
        {
            waits[slot] = new DotnetBuildStableSlotWait(slot, TryReadStableSlotExecutionOwner(slot));
        }

        return waits;
    }

    private static int? TryReadStableSlotExecutionOwner(int slotIndex)
    {
        var metadata = TryReadExecutionLeaseMetadata(StableSlotExecutionLockPath($"slot-{slotIndex}"));
        return metadata?.OwnerProcessId ?? TryReadStableSlotOwnerMarker(slotIndex)?.OwnerProcessId;
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
                if (TryRemediateArtifactPrepLock(
                    environment,
                    ex,
                    ref attemptedCompilerLockRemediation,
                    ref attemptedOwnedProcessRemediation,
                    out blockedAttribution))
                {
                    continue;
                }

                stream = null!;
                return false;
            }
        }
    }

    private static bool TryRemediateArtifactPrepLock(
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
            return false;
        }

        attribution = LockAttribution.Attribute(lockedPath, environment.ArtifactsPath);
        if (IsCurrentLeaseSelfHeldArtifactLock(environment, attribution, currentProcessOwnsExecutionLease: true))
        {
            return true;
        }

        if (!attemptedCompilerLockRemediation && IsCompilerLock(attribution))
        {
            attemptedCompilerLockRemediation = true;
            ShutdownBuildServersBestEffort();
            return true;
        }

        if (attemptedOwnedProcessRemediation)
        {
            return false;
        }

        var killed = false;
        foreach (var holder in attribution.Holders
            .Where(holder => holder.IsOrchestratorOwned && holder.ProcessId.HasValue)
            .DistinctBy(holder => holder.ProcessId!.Value))
        {
            killed |= WorkerProcessJobs.TryKillOrFallbackAndWait(holder.ProcessId!.Value, TimeSpan.FromSeconds(5));
        }

        attemptedOwnedProcessRemediation = killed;
        return killed;
    }

    private static bool IsCompilerLock(BuildLockAttribution attribution) =>
        attribution.Holders.Any(holder =>
            ContainsCompilerLockSignal(holder.ProcessName) ||
            ContainsCompilerLockSignal(holder.CommandLine));

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
        // Holding the file open exclusively IS the lease lock. A process crash closes the OS
        // handle, so the durable file is advisory metadata rather than ownership.
        return new LeaseFileStream(environment);
    }

    private static readonly (string RuleProject, string ArtifactProject)[] TesthostFirewallProjects =
    [
        ("Core", "Mcg.AgentOrchestrator.Core.Tests"),
        ("Infrastructure", "Mcg.AgentOrchestrator.Infrastructure.Tests")
    ];

    private static readonly string[] TesthostFirewallConfigurations = ["Debug", "Release"];

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
                var attribution = LockAttribution.Attribute(lockedPath, environment.ArtifactsPath);
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

    private static ArtifactsOwnerMarker? TryReadStableSlotOwnerMarker(int slotIndex)
    {
        var ownerPath = Path.Combine(StableSlotArtifactsPath(slotIndex), ArtifactsOwnerFileName);
        if (!File.Exists(ownerPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ArtifactsOwnerMarker>(File.ReadAllText(ownerPath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
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
        if (!PathIsUnderDirectory(attribution.Path, environment.ArtifactsPath) &&
            !PathIsUnderCurrentGateLandingTestFixtureRoot(attribution.Path))
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

        if (currentProcessOwnsExecutionLease)
        {
            return true;
        }

        var metadata = TryReadExecutionLeaseMetadata(environment.ExecutionLockPath);
        return metadata?.OwnerProcessId == Environment.ProcessId;
    }

    internal static void RegisterCurrentLandingTestFixtureRootForTests(string path)
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

    private static bool PathIsUnderCurrentGateLandingTestFixtureRoot(string path)
    {
        if (!TryGetLandingTestFixtureRoot(path, out var fixtureRoot))
        {
            return false;
        }

        lock (CurrentLandingFixtureRootsGate)
        {
            if (CurrentLandingFixtureRoots.Contains(fixtureRoot))
            {
                return true;
            }
        }

        if (!Directory.Exists(fixtureRoot) ||
            TryGetCurrentProcessStartTimeUtc() is not { } processStartTime)
        {
            return false;
        }

        try
        {
            var createdAt = new DateTimeOffset(Directory.GetCreationTimeUtc(fixtureRoot), TimeSpan.Zero);
            return createdAt >= processStartTime.AddSeconds(-5);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

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

    private static DateTimeOffset? TryGetCurrentProcessStartTimeUtc()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return null;
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
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(path).Trim();
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
        stream.SetLength(0);
        stream.Position = 0;
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.Write(JsonSerializer.Serialize(metadata, JsonOptions));
        writer.WriteLine();
        writer.Flush();
        stream.Flush(flushToDisk: true);
        stream.Position = 0;
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
        var slotName = Path.GetFileName(Path.GetDirectoryName(environment.ExecutionLockPath));
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

    private sealed class LeaseFileStream : FileStream
    {
        private readonly DotnetBuildEnvironment _environment;
        private int _released;

        internal LeaseFileStream(DotnetBuildEnvironment environment)
            : base(environment.ExecutionLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
        {
            _environment = environment;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _released, 1) == 0)
            {
                EmitLeaseReceipt("LEASE_RELEASE", _environment);
            }

            base.Dispose(disposing);
        }
    }
}

public sealed class DotnetBuildEnvironmentLease : IDisposable
{
    private readonly FileStream _stream;
    private bool _detached;

    internal DotnetBuildEnvironmentLease(DotnetBuildEnvironment environment, FileStream stream)
    {
        Environment = environment;
        _stream = stream;
    }

    public DotnetBuildEnvironment Environment { get; }

    internal FileStream DetachStreamForLegacyCaller()
    {
        _detached = true;
        return _stream;
    }

    public void Dispose()
    {
        if (_detached)
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
