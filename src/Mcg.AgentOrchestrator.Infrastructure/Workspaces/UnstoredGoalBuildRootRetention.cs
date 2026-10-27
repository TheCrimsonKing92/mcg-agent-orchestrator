using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Absence from every discovered store permits reclamation only after all ambiguity checks pass.
internal sealed class UnstoredGoalBuildRootRetention(
    DotnetBuildStorageRoot storageRoot,
    IOwnedRunRootStore ownedRoots,
    Func<int, bool> isOwnerRunning,
    TimeProvider timeProvider)
{
    public UnstoredGoalBuildRootRetentionResult Run(
        IReadOnlyCollection<string> storedGoalIds, IReadOnlyCollection<string> discoveredStorePaths,
        int retentionDays, int maxInspections, long maxReclaimBytes, string? cursor)
    {
        var kept = new Dictionary<UnstoredGoalBuildRootKeepReason, int>();
        var reclaimed = new List<string>();
        if (retentionDays == 0) return new(0, 0, 0, kept, reclaimed, null, string.Empty);
        ArgumentOutOfRangeException.ThrowIfNegative(retentionDays);
        ArgumentOutOfRangeException.ThrowIfNegative(maxInspections);
        ArgumentOutOfRangeException.ThrowIfNegative(maxReclaimBytes);
        var cutoff = timeProvider.GetUtcNow().AddDays(-retentionDays);
        var reclaimFolder = Path.Combine(storageRoot.RootPath, "goal-root-reclaim");
        var prefixes = storedGoalIds.Where(id => id.Length >= 8)
            .Select(id => id[..8]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var stores = discoveredStorePaths.Select(Path.GetFullPath).ToHashSet(pathComparer);
        var inspected = 0;
        long bytes = 0;
        string? nextCursor = null;
        void Keep(UnstoredGoalBuildRootKeepReason reason) => kept[reason] = kept.GetValueOrDefault(reason) + 1;

        var goalsFolder = Path.Combine(storageRoot.RootPath, "goals");
        string[] roots;
        try
        {
            roots = Directory.Exists(goalsFolder)
                ? Directory.GetDirectories(goalsFolder).OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Keep(UnstoredGoalBuildRootKeepReason.NotContained);
            roots = [];
        }
        var start = cursor is null ? 0 : Array.FindIndex(roots,
            root => StringComparer.Ordinal.Compare(Path.GetFileName(root), cursor) > 0);
        if (start < 0) start = 0;
        for (var offset = 0; offset < roots.Length; offset++)
        {
            var root = roots[(start + offset) % roots.Length];
            var name = Path.GetFileName(root);
            if (name.Length != 8 || !name.All(Uri.IsHexDigit))
                Keep(UnstoredGoalBuildRootKeepReason.NotGoalName);
            else if (prefixes.Contains(name))
                Keep(UnstoredGoalBuildRootKeepReason.GoalStored);
            else if (!IsContainedDirectory(root) ||
                (Directory.Exists(reclaimFolder) && !IsContainedDirectory(reclaimFolder)))
                Keep(UnstoredGoalBuildRootKeepReason.NotContained);
            else if (inspected >= maxInspections)
                Keep(UnstoredGoalBuildRootKeepReason.InspectionDeferred);
            else
            {
                inspected++;
                nextCursor = name;
                if (ownedRoots.ReadRegisteredPaths([root]).Contains(root))
                {
                    Keep(UnstoredGoalBuildRootKeepReason.RegisteredOwnedRoot);
                    continue;
                }
                if (Directory.Exists(Path.Combine(root, "lease")))
                {
                    if (!TryReadLease(root, name, out var owner, out var lastUsed, out var repository))
                    {
                        Keep(UnstoredGoalBuildRootKeepReason.LeaseUnreadable);
                        continue;
                    }
                    if (lastUsed > cutoff)
                    {
                        Keep(UnstoredGoalBuildRootKeepReason.WithinWindow);
                        continue;
                    }
                    var store = Path.GetFullPath(Path.Combine(repository, ".orchestrator", "state.db"));
                    if (File.Exists(store) && !stores.Contains(store))
                    {
                        Keep(UnstoredGoalBuildRootKeepReason.ForeignStore);
                        continue;
                    }
                    if (isOwnerRunning(owner))
                    {
                        Keep(UnstoredGoalBuildRootKeepReason.OwnerLive);
                        continue;
                    }
                }
                try
                {
                    var directory = new DirectoryInfo(root);
                    if (directory.LastWriteTimeUtc > cutoff.UtcDateTime ||
                        directory.EnumerateFileSystemInfos("*", new EnumerationOptions
                        {
                            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint,
                            IgnoreInaccessible = false
                        }).Any(entry => entry.LastWriteTimeUtc > cutoff.UtcDateTime))
                    {
                        Keep(UnstoredGoalBuildRootKeepReason.WithinWindow);
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Keep(UnstoredGoalBuildRootKeepReason.TreeUnreadable);
                    continue;
                }
                if (bytes >= maxReclaimBytes)
                {
                    Keep(UnstoredGoalBuildRootKeepReason.BudgetExhausted);
                    continue;
                }
                var renamed = Path.Combine(reclaimFolder, $"{name}-unstored-{timeProvider.GetUtcNow().UtcTicks}");
                try
                {
                    Directory.CreateDirectory(reclaimFolder);
                    Directory.Move(root, renamed);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Keep(UnstoredGoalBuildRootKeepReason.Held);
                    continue;
                }
                try
                {
                    var size = Directory.EnumerateFiles(renamed, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint,
                        IgnoreInaccessible = false
                    }).Sum(path => new FileInfo(path).Length);
                    Directory.Delete(renamed, recursive: true);
                    bytes += size;
                    reclaimed.Add(root);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Keep(UnstoredGoalBuildRootKeepReason.RemovalFailed);
                }
            }
        }
        var reasons = string.Join(",", Enum.GetValues<UnstoredGoalBuildRootKeepReason>()
            .Where(reason => kept.GetValueOrDefault(reason) > 0)
            .Select(reason => $"{Regex.Replace(reason.ToString(), "([a-z])([A-Z])", "$1-$2").ToLowerInvariant()}:{kept[reason]}"));
        var summary = FormattableString.Invariant(
            $"SWEEP_UNSTORED_BUILD_ROOT_RETENTION window-days={retentionDays} inspected={inspected} reclaimed={reclaimed.Count} bytes={bytes} kept={kept.Values.Sum()} reasons={(reasons.Length == 0 ? "none" : reasons)}");
        return new(inspected, reclaimed.Count, bytes, kept, reclaimed, nextCursor, summary);
    }

    private bool IsContainedDirectory(string path)
    {
        try
        {
            if (!storageRoot.ContainsPath(path)) return false;
            for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (string.Equals(current.FullName, storageRoot.RootPath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    private bool TryReadLease(string root, string prefix, out int owner,
        out DateTimeOffset lastUsed, out string repository)
    {
        owner = 0;
        lastUsed = default;
        repository = string.Empty;
        try
        {
            var folder = Path.Combine(root, "lease");
            var path = Path.Combine(folder, "lease.json");
            if (!IsContainedDirectory(folder) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var lease = document.RootElement;
            if (!lease.TryGetProperty("goalId", out var id) || id.ValueKind != JsonValueKind.String ||
                id.GetString() is not { Length: 32 } goal || !goal.All(Uri.IsHexDigit) ||
                !goal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !lease.TryGetProperty("ownerProcessId", out var pid) || !pid.TryGetInt32(out owner) || owner <= 0 ||
                !lease.TryGetProperty("lastUsedAt", out var used) || used.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(used.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out lastUsed) ||
                !lease.TryGetProperty("repositoryRoot", out var repo) || repo.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(repo.GetString())) return false;
            repository = Path.GetFullPath(repo.GetString()!);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
