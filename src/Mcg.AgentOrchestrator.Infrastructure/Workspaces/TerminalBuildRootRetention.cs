using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A root becomes disposable only after the stored goal and its lease both prove eligibility.
internal sealed class TerminalBuildRootRetention(
    DotnetBuildStorageRoot storageRoot,
    IOwnedRunRootStore ownedRoots,
    Func<int, bool> isOwnerRunning,
    TimeProvider timeProvider)
{
    public TerminalBuildRootRetentionResult Run(
        IReadOnlyList<StoredGoalRetentionFact> goals, int retentionDays, int maxReclaims)
    {
        var skipped = new Dictionary<TerminalBuildRootSkipReason, int>();
        var reclaimed = new List<string>();
        long bytes = 0;
        if (retentionDays == 0)
            return new(0, 0, skipped, reclaimed, string.Empty);
        ArgumentOutOfRangeException.ThrowIfNegative(retentionDays);
        ArgumentOutOfRangeException.ThrowIfNegative(maxReclaims);
        ArgumentNullException.ThrowIfNull(isOwnerRunning);
        var now = timeProvider.GetUtcNow();
        var cutoff = now.AddDays(-retentionDays);
        var reclaimFolder = Path.Combine(storageRoot.RootPath, "goal-root-reclaim");
        var attempts = 0;
        var byPrefix = goals.Where(goal => goal.GoalId.Length >= 8)
            .GroupBy(goal => goal.GoalId[..8], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        void Skip(TerminalBuildRootSkipReason reason) => skipped[reason] = skipped.GetValueOrDefault(reason) + 1;

        void Remove(string renamed, string original)
        {
            try
            {
                // Never traverse links when measuring the detached tree.
                var size = Directory.EnumerateFiles(renamed, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint,
                    IgnoreInaccessible = false
                }).Sum(path => new FileInfo(path).Length);
                Directory.Delete(renamed, recursive: true);
                bytes += size;
                reclaimed.Add(original);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Skip(TerminalBuildRootSkipReason.RemovalFailed);
            }
        }

        string[] Children(string folder)
        {
            try
            {
                return Directory.Exists(folder)
                    ? Directory.GetDirectories(folder).OrderBy(path => path, StringComparer.Ordinal).ToArray()
                    : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Skip(TerminalBuildRootSkipReason.NotContained);
                return [];
            }
        }

        foreach (var leftover in Children(reclaimFolder))
        {
            if (!IsContainedDirectory(leftover))
                Skip(TerminalBuildRootSkipReason.NotContained);
            else if (attempts >= maxReclaims)
                Skip(TerminalBuildRootSkipReason.BudgetExhausted);
            else
            {
                attempts++;
                Remove(leftover, leftover);
            }
        }

        foreach (var root in Children(Path.Combine(storageRoot.RootPath, "goals")))
        {
            var prefix = Path.GetFileName(root);
            // The first three decisions intentionally inspect only stored facts, not root contents.
            if (!byPrefix.TryGetValue(prefix, out var facts))
                Skip(TerminalBuildRootSkipReason.GoalNotStored);
            else if (facts.Select(fact => fact.GoalId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                Skip(TerminalBuildRootSkipReason.GoalPrefixAmbiguous);
            else if (facts.Any(fact => fact.Status is not (GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded)))
                Skip(TerminalBuildRootSkipReason.GoalNotTerminal);
            else if (!IsContainedDirectory(root) ||
                (Directory.Exists(reclaimFolder) && !IsContainedDirectory(reclaimFolder)))
                Skip(TerminalBuildRootSkipReason.NotContained);
            else if (!TryReadLease(root, facts[0].GoalId, out var owner, out var lastUsed))
                Skip(TerminalBuildRootSkipReason.LeaseUnreadable);
            else if (ownedRoots.ReadRegisteredPaths([root]).Contains(root))
                Skip(TerminalBuildRootSkipReason.RegisteredOwnedRoot);
            else if (lastUsed > cutoff || facts.Any(fact => fact.UpdatedAt is null || fact.UpdatedAt > cutoff))
                Skip(TerminalBuildRootSkipReason.WithinWindow);
            else if (isOwnerRunning(owner))
                Skip(TerminalBuildRootSkipReason.OwnerLive);
            else if (attempts >= maxReclaims)
                Skip(TerminalBuildRootSkipReason.BudgetExhausted);
            else
            {
                attempts++;
                var renamed = Path.Combine(reclaimFolder, $"{prefix}-{timeProvider.GetUtcNow().UtcTicks}");
                try
                {
                    Directory.CreateDirectory(reclaimFolder);
                    Directory.Move(root, renamed);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Skip(TerminalBuildRootSkipReason.Held);
                    continue;
                }
                Remove(renamed, root);
            }
        }

        var reasons = string.Join(",", Enum.GetValues<TerminalBuildRootSkipReason>()
            .Where(reason => skipped.GetValueOrDefault(reason) > 0)
            .Select(reason => $"{Regex.Replace(reason.ToString(), "([a-z])([A-Z])", "$1-$2").ToLowerInvariant()}:{skipped[reason]}"));
        var summary = FormattableString.Invariant(
            $"SWEEP_BUILD_ROOT_RETENTION window-days={retentionDays} reclaimed={reclaimed.Count} bytes={bytes} skipped={skipped.Values.Sum()} reasons={(reasons.Length == 0 ? "none" : reasons)}");
        return new(reclaimed.Count, bytes, skipped, reclaimed, summary);
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

    private bool TryReadLease(string root, string expectedGoalId, out int owner, out DateTimeOffset lastUsed)
    {
        owner = 0;
        lastUsed = default;
        try
        {
            var leaseFolder = Path.Combine(root, "lease");
            var leasePath = Path.Combine(leaseFolder, "lease.json");
            if (!IsContainedDirectory(leaseFolder) ||
                (File.GetAttributes(leasePath) & FileAttributes.ReparsePoint) != 0) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(leasePath));
            var lease = document.RootElement;
            return lease.TryGetProperty("goalId", out var id) && id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString(), expectedGoalId, StringComparison.OrdinalIgnoreCase) &&
                lease.TryGetProperty("ownerProcessId", out var pid) && pid.TryGetInt32(out owner) && owner > 0 &&
                lease.TryGetProperty("lastUsedAt", out var used) && used.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(used.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out lastUsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool IsOwnerProcessRunning(int pid)
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
