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
        foreach (var root in Directory.EnumerateDirectories(goalsFolder).OrderBy(path => path, StringComparer.Ordinal))
        {
            if (reclaimed.Count >= maxReclaims)
                break;
            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (!storageRoot.ContainsPath(canonical) ||
                storedPrefixes.Contains(Path.GetFileName(canonical)) ||
                IsReparsePointOrMissing(canonical) ||
                ownedRoots.ReadRegisteredPaths([canonical]).Contains(canonical) ||
                !TryReadLease(canonical, out var goalId, out var ownerProcessId) ||
                storedGoalIds.Contains(goalId) || _isOwnerRunning(ownerProcessId) ||
                !TryReadLease(canonical, out var confirmedId, out var confirmedOwner) ||
                !string.Equals(goalId, confirmedId, StringComparison.OrdinalIgnoreCase) ||
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

    private static bool TryReadLease(string root, out string goalId, out int ownerProcessId)
    {
        goalId = string.Empty;
        ownerProcessId = 0;
        var path = Path.Combine(root, "lease", "lease.json");
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var lease = document.RootElement;
            if (!lease.TryGetProperty("goalId", out var id) || id.ValueKind != JsonValueKind.String ||
                !lease.TryGetProperty("ownerProcessId", out var pid) || !pid.TryGetInt32(out ownerProcessId) ||
                !lease.TryGetProperty("machineName", out var machine) ||
                !string.Equals(machine.GetString(), Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                return false;
            goalId = id.GetString() ?? string.Empty;
            return goalId.Length == 32 && goalId.All(Uri.IsHexDigit) && ownerProcessId > 0 &&
                string.Equals(goalId[..8], Path.GetFileName(root), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
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
