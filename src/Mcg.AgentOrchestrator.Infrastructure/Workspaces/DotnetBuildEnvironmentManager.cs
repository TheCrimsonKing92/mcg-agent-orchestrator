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

public static class DotnetBuildEnvironmentManager
{
    public const string RootDirectoryName = "mcg-dotnet-isolated";

    // Supported escape hatch for tests that need lease-root isolation. Production and acceptance
    // runs normally use the default stable slots so their testhost.exe paths stay firewall-covered.
    public const string IsolatedRootOverrideVariable = "MCG_DOTNET_ISOLATED_ROOT";
    public const int StableSlotCount = 4;
    private const string LeaseDirectoryName = "lease";
    private const string LeaseMetadataFileName = "lease.json";
    private const string LeaseLockFileName = "lease.lock";
    private const string ArtifactsOwnerFileName = ".mcg-artifacts-owner.json";
    public const string BuildMaxCpuCountVariable = "MCG_BUILD_MAXCPUCOUNT";
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
        return StableSlotArtifactsPath(StableSlotName(goalId));
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

    public static DotnetBuildEnvironmentLease AcquireFirstAvailableStableSlotExecutionLock(
        CancellationToken cancellationToken = default)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddMinutes(5);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var slot = 0; slot < StableSlotCount; slot++)
            {
                var environment = CreateStableSlotEnvironment(slot);
                if (TryAcquireLeaseExecutionLock(environment, out var stream))
                {
                    return new DotnetBuildEnvironmentLease(environment, stream);
                }
            }

            if (DateTimeOffset.UtcNow >= timeoutAt)
            {
                throw new IOException("Timed out waiting for an available stable dotnet build slot.");
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
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(environment.ExecutionLockPath)!);
        var timeoutAt = DateTimeOffset.UtcNow.AddMinutes(5);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Holding the file open exclusively IS the lease lock — cross-platform, unlike
                // FileStream.Lock (unsupported on macOS, CA1416). A competing holder fails the
                // exclusive open with IOException, so we retry until the timeout.
                var stream = new FileStream(
                    environment.ExecutionLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                try
                {
                    PrepareArtifactsDirectory(environment);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }

                return stream;
            }
            catch (IOException)
            {
                if (DateTimeOffset.UtcNow >= timeoutAt)
                {
                    throw new IOException($"Timed out waiting for build lease execution lock: {environment.ExecutionLockPath}");
                }

                Thread.Sleep(100);
            }
        }
    }

    private static DotnetBuildEnvironment CreateGoalLease(GoalId goalId, string attemptName)
    {
        var root = GoalRoot(goalId);
        var leaseId = $"goal-{Prefix(goalId)}";
        var leaseDirectory = LeaseDirectory(goalId);
        var slotName = StableSlotName(goalId);
        var artifactsPath = StableSlotArtifactsPath(slotName);
        var executionLockPath = StableSlotExecutionLockPath(slotName);
        var metadataPath = Path.Combine(leaseDirectory, LeaseMetadataFileName);
        var lockPath = Path.Combine(leaseDirectory, LeaseLockFileName);
        var reused = Directory.Exists(leaseDirectory);
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
        $"-maxcpucount:{ResolveMaxCpuCount()}"
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

    private static bool TryAcquireLeaseExecutionLock(DotnetBuildEnvironment environment, out FileStream stream)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(environment.ExecutionLockPath)!);
        try
        {
            stream = new FileStream(
                environment.ExecutionLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            try
            {
                PrepareArtifactsDirectory(environment);
            }
            catch
            {
                stream.Dispose();
                throw;
            }

            return true;
        }
        catch (IOException)
        {
            stream = null!;
            return false;
        }
    }

    private static readonly (string RuleProject, string ArtifactProject)[] TesthostFirewallProjects =
    [
        ("Core", "Mcg.AgentOrchestrator.Core.Tests"),
        ("Infrastructure", "Mcg.AgentOrchestrator.Infrastructure.Tests")
    ];

    private static readonly string[] TesthostFirewallConfigurations = ["Debug", "Release"];

    private static void PrepareArtifactsDirectory(DotnetBuildEnvironment environment)
    {
        var clean = environment.StaleLockCleared;
        var ownerPath = Path.Combine(environment.ArtifactsPath, ArtifactsOwnerFileName);
        if (Directory.Exists(environment.ArtifactsPath) && Directory.EnumerateFileSystemEntries(environment.ArtifactsPath).Any())
        {
            clean |= !OwnerMarkerMatches(ownerPath, environment.SlotOwnerToken);
        }

        if (clean && Directory.Exists(environment.ArtifactsPath))
        {
            Directory.Delete(environment.ArtifactsPath, recursive: true);
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
}

public sealed class DotnetBuildEnvironmentLease : IDisposable
{
    private readonly FileStream _stream;

    internal DotnetBuildEnvironmentLease(DotnetBuildEnvironment environment, FileStream stream)
    {
        Environment = environment;
        _stream = stream;
    }

    public DotnetBuildEnvironment Environment { get; }

    public void Dispose()
    {
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
