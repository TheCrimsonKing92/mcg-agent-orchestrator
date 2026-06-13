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

public static class DotnetBuildEnvironmentManager
{
    public const string RootDirectoryName = "mcg-dotnet-isolated";
    private const string LeaseDirectoryName = "lease";
    private const string LeaseMetadataFileName = "lease.json";
    private const string LeaseLockFileName = "lease.lock";
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

        var root = Path.Combine(Path.GetTempPath(), RootDirectoryName, "runs");
        var runName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var artifactsPath = Path.Combine(root, "attempts", $"{Sanitize(attemptName)}-{runName}");
        Directory.CreateDirectory(artifactsPath);

        return new DotnetBuildEnvironment(
            $"run-{runName}",
            root,
            artifactsPath,
            Path.Combine(artifactsPath, "lease.execution.lock"),
            BuildArguments(artifactsPath));
    }

    public static string GoalRoot(GoalId goalId)
    {
        return Path.Combine(Path.GetTempPath(), RootDirectoryName, "goals", Prefix(goalId));
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
        var artifactsPath = Path.Combine(leaseDirectory, "artifacts");
        var metadataPath = Path.Combine(leaseDirectory, LeaseMetadataFileName);
        var rootExists = Directory.Exists(root);
        var artifactsExist = Directory.Exists(artifactsPath);
        var metadataExists = File.Exists(metadataPath);
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

    public static FileStream AcquireLeaseExecutionLock(
        DotnetBuildEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(environment.ExecutionLockPath)!);
        var timeoutAt = DateTimeOffset.UtcNow.AddMinutes(5);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = new FileStream(
                environment.ExecutionLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);
            try
            {
                stream.Lock(0, 1);
                return stream;
            }
            catch (IOException)
            {
                stream.Dispose();
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
        var artifactsPath = Path.Combine(leaseDirectory, "artifacts");
        var executionLockPath = Path.Combine(leaseDirectory, "lease.execution.lock");
        var metadataPath = Path.Combine(leaseDirectory, LeaseMetadataFileName);
        var lockPath = Path.Combine(leaseDirectory, LeaseLockFileName);
        var reused = Directory.Exists(leaseDirectory);
        Directory.CreateDirectory(artifactsPath);
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
            metadataPath,
            ReusedGoalLease: reused,
            StaleLockCleared: staleLockCleared);
    }

    private static IReadOnlyList<string> BuildArguments(string artifactsPath) =>
    [
        "--artifacts-path",
        artifactsPath,
        "-maxcpucount:1",
        "-p:UseSharedCompilation=false"
    ];

    private static string LeaseDirectory(GoalId goalId)
    {
        return Path.Combine(GoalRoot(goalId), LeaseDirectoryName);
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
}
