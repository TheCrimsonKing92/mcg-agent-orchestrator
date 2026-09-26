using System.Diagnostics;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record GoalBuildRootReclaimResult(
    IReadOnlyList<string> ReclaimedRoots,
    IReadOnlyList<string> Failures);

/// <summary>Reclaims only locally owned goal leases that no goal in the state store references.</summary>
internal sealed class GoalBuildRootReclaimer(
    DotnetBuildStorageRoot storageRoot,
    IOwnedRunRootStore ownedRoots,
    string owningRepositoryRoot,
    Func<int, bool>? isOwnerRunning = null)
{
    private readonly Func<int, bool> _isOwnerRunning = isOwnerRunning ?? IsProcessRunning;

    public GoalBuildRootReclaimResult Reclaim(IReadOnlySet<string> storedGoalIds, int maxReclaims)
    {
        ArgumentNullException.ThrowIfNull(storedGoalIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxReclaims, 1);
        var goalsFolder = Path.Combine(storageRoot.RootPath, "goals");
        if (!Directory.Exists(goalsFolder))
            return new GoalBuildRootReclaimResult([], []);

        var reclaimed = new List<string>();
        var failures = new List<string>();
        var storedPrefixes = storedGoalIds
            .Where(id => id.Length >= 8)
            .Select(id => id[..8])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] roots;
        try { roots = Directory.GetDirectories(goalsFolder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new GoalBuildRootReclaimResult([], [$"{goalsFolder}: {ex.Message}"]);
        }
        foreach (var root in roots.OrderBy(path => path, StringComparer.Ordinal))
        {
            if (reclaimed.Count >= maxReclaims)
                break;
            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (!storageRoot.ContainsPath(canonical) ||
                storedPrefixes.Contains(Path.GetFileName(canonical)) ||
                IsReparsePointOrMissing(canonical) ||
                !TryReadLease(canonical, out var goalId, out var ownerProcessId, out var repositoryRoot) ||
                !SamePath(repositoryRoot, owningRepositoryRoot) ||
                storedGoalIds.Contains(goalId) ||
                ownedRoots.ReadRegisteredPaths([canonical]).Contains(canonical) ||
                _isOwnerRunning(ownerProcessId) ||
                !TryReadLease(canonical, out var confirmedId, out var confirmedOwner, out var confirmedRepositoryRoot) ||
                !string.Equals(goalId, confirmedId, StringComparison.OrdinalIgnoreCase) ||
                !SamePath(confirmedRepositoryRoot, owningRepositoryRoot) ||
                ownerProcessId != confirmedOwner)
                continue;

            try
            {
                Directory.Delete(canonical, recursive: true);
                reclaimed.Add(canonical);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{canonical}: {ex.Message}");
            }
        }

        return new GoalBuildRootReclaimResult(reclaimed, failures);
    }

    private static bool TryReadLease(string root, out string goalId, out int ownerProcessId,
        out string? repositoryRoot)
    {
        goalId = string.Empty;
        ownerProcessId = 0;
        repositoryRoot = null;
        var path = Path.Combine(root, "lease", "lease.json");
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var lease = document.RootElement;
            if (!lease.TryGetProperty("goalId", out var id) || id.ValueKind != JsonValueKind.String ||
                !lease.TryGetProperty("ownerProcessId", out var pid) || !pid.TryGetInt32(out ownerProcessId))
                return false;
            goalId = id.GetString() ?? string.Empty;
            if (lease.TryGetProperty("repositoryRoot", out var repo) && repo.ValueKind == JsonValueKind.String)
                repositoryRoot = repo.GetString();
            return goalId.Length == 32 && goalId.All(Uri.IsHexDigit) && ownerProcessId > 0 &&
                string.Equals(goalId[..8], Path.GetFileName(root), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool SamePath(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsReparsePointOrMissing(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }
}
