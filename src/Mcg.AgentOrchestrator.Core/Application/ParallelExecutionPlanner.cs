namespace Mcg.AgentOrchestrator.Core;

public static class ParallelExecutionPlanner
{
    public static ParallelExecutionPlan Build(
        IReadOnlyList<ParallelExecutionIntent> intents,
        IReadOnlyList<ParallelExecutionProviderQuota>? providerQuotas = null,
        bool approveHighRiskOwnership = false)
    {
        var quotas = providerQuotas?.ToDictionary(quota => quota.ProviderKey, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ParallelExecutionProviderQuota>(StringComparer.OrdinalIgnoreCase);
        var decisions = new List<ParallelExecutionDecision>();
        var batches = new List<ParallelExecutionBatch>();
        var remaining = new List<ParallelExecutionIntent>();
        var ownershipAutoApproved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var intent in intents)
        {
            var guard = RepositoryOwnershipMap.GuardWriteSet(intent.TargetPaths);
            var guardedIntent = ApplyWriteSetGuard(intent, guard);
            // An explicit intent-level approval flag is never auto-granted, and generated/noisy paths
            // (bin, obj, .scratch, worktrees, ...) always need operator cleanup first. A purely
            // HIGH-RISK ownership block (scripts, shared infra, build system, config, ...) is
            // auto-granted only when the autonomy policy opts in via approveHighRiskOwnership —
            // recorded for audit on the batch decision.
            var hasGeneratedPath = guard.Paths.Any(path => path.IsGeneratedOrNoisy);
            var ownershipAutoApprovable = approveHighRiskOwnership && !hasGeneratedPath;
            if (intent.RequiresOperatorApproval || (guard.RequiresOperatorApproval && !ownershipAutoApprovable))
            {
                decisions.Add(new ParallelExecutionDecision(
                    intent.Id,
                    ParallelExecutionDisposition.RequiresOperatorApproval,
                    null,
                    BuildApprovalReasons(intent, guard)));
                continue;
            }

            if (guard.RequiresOperatorApproval)
            {
                ownershipAutoApproved.Add(intent.Id);
            }

            remaining.Add(guardedIntent);
        }

        var scheduled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (remaining.Count > 0)
        {
            var batchItems = new List<ParallelExecutionIntent>();
            var deferred = new List<ParallelExecutionIntent>();
            var providerUse = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var intent in remaining)
            {
                if (intent.DependsOn.Any(dependency => !scheduled.Contains(dependency)))
                {
                    deferred.Add(intent);
                    continue;
                }

                if (TryFindBatchConflict(intent, batchItems, providerUse, quotas, out _))
                {
                    deferred.Add(intent);
                    continue;
                }

                batchItems.Add(intent);
                if (!string.IsNullOrWhiteSpace(intent.ProviderKey))
                {
                    providerUse[intent.ProviderKey] = CurrentProviderUse(providerUse, intent.ProviderKey) + intent.ProviderSlots;
                }
            }

            if (batchItems.Count == 0)
            {
                var blocked = deferred[0];
                decisions.Add(new ParallelExecutionDecision(
                    blocked.Id,
                    ParallelExecutionDisposition.RequiresOperatorApproval,
                    null,
                    ["dependency could not be scheduled"]));
                remaining.RemoveAt(0);
                continue;
            }

            var batchNumber = batches.Count + 1;
            batches.Add(new ParallelExecutionBatch(batchNumber, batchItems.Select(intent => intent.Id).ToArray()));
            foreach (var intent in batchItems)
            {
                scheduled.Add(intent.Id);
                var reasons = batchNumber == 1
                    ? new List<string> { "safe to run in this batch" }
                    : BuildSerializedReasons(intent);
                if (ownershipAutoApproved.Contains(intent.Id))
                {
                    reasons.Add("high-risk ownership auto-approved by autonomy policy");
                }

                decisions.Add(new ParallelExecutionDecision(
                    intent.Id,
                    batchNumber == 1 ? ParallelExecutionDisposition.Concurrent : ParallelExecutionDisposition.Serialized,
                    batchNumber,
                    reasons));
            }

            remaining = deferred;
        }

        return new ParallelExecutionPlan(batches, decisions);
    }

    private static bool TryFindBatchConflict(
        ParallelExecutionIntent intent,
        IReadOnlyList<ParallelExecutionIntent> batchItems,
        IDictionary<string, int> providerUse,
        Dictionary<string, ParallelExecutionProviderQuota> providerQuotas,
        out string reason)
    {
        reason = string.Empty;
        if (!string.IsNullOrWhiteSpace(intent.ProviderKey) &&
            providerQuotas.TryGetValue(intent.ProviderKey, out var quota) &&
            CurrentProviderUse(providerUse, intent.ProviderKey) + intent.ProviderSlots > quota.AvailableSlots)
        {
            reason = $"provider quota exceeded: {intent.ProviderKey}";
            return true;
        }

        foreach (var other in batchItems)
        {
            if (RequiresGlobalSerialization(intent) || RequiresGlobalSerialization(other))
            {
                reason = "global state mutation requires serialization";
                return true;
            }

            if (SharesGoal(intent, other) && (TouchesWorkspace(intent) || TouchesWorkspace(other)))
            {
                reason = "shared goal workspace requires serialization";
                return true;
            }

            if (Overlaps(intent.RequiredResources, other.RequiredResources))
            {
                reason = "required resource overlap";
                return true;
            }

            if (OverlapsPaths(intent.TargetPaths, other.TargetPaths))
            {
                reason = "target path overlap";
                return true;
            }
        }

        return false;
    }

    private static List<string> BuildSerializedReasons(ParallelExecutionIntent intent)
    {
        var reasons = new List<string> { "serialized after earlier batch" };
        if (RequiresGlobalSerialization(intent))
        {
            reasons.Add("mutates shared goal state or lifecycle");
        }

        if (intent.DependsOn.Count > 0)
        {
            reasons.Add("waits for dependency completion");
        }

        return reasons;
    }

    private static ParallelExecutionIntent ApplyWriteSetGuard(
        ParallelExecutionIntent intent,
        RepositoryWriteSetGuardReport guard)
    {
        if (guard.RequiredResources.Count == 0)
        {
            return intent;
        }

        return new ParallelExecutionIntent(
            intent.Id,
            intent.GoalKey,
            intent.TargetPaths,
            intent.RequiredResources.Concat(guard.RequiredResources)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            intent.WritesGoalState,
            intent.MutatesWorkspaceLifecycle,
            intent.RunsAcceptance,
            intent.RequiresOperatorApproval,
            intent.ProviderKey,
            intent.ProviderSlots,
            intent.DependsOn);
    }

    private static List<string> BuildApprovalReasons(
        ParallelExecutionIntent intent,
        RepositoryWriteSetGuardReport guard)
    {
        var reasons = new List<string>();
        if (intent.RequiresOperatorApproval)
        {
            reasons.Add("operator approval required");
        }

        reasons.AddRange(guard.Reasons);
        return reasons.Count == 0 ? ["operator approval required"] : reasons;
    }

    private static int CurrentProviderUse(IDictionary<string, int> providerUse, string providerKey)
    {
        return providerUse.TryGetValue(providerKey, out var current) ? current : 0;
    }

    private static bool RequiresGlobalSerialization(ParallelExecutionIntent intent)
    {
        return intent.WritesGoalState || intent.MutatesWorkspaceLifecycle || intent.RunsAcceptance;
    }

    private static bool TouchesWorkspace(ParallelExecutionIntent intent)
    {
        return intent.TargetPaths.Count > 0 || intent.MutatesWorkspaceLifecycle || intent.RunsAcceptance;
    }

    private static bool SharesGoal(ParallelExecutionIntent left, ParallelExecutionIntent right)
    {
        return !string.IsNullOrWhiteSpace(left.GoalKey) &&
            left.GoalKey.Equals(right.GoalKey, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Overlaps(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        return left.Any(value => right.Contains(value, StringComparer.OrdinalIgnoreCase));
    }

    private static bool OverlapsPaths(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        return left.Any(l => right.Any(r => PathsOverlap(l, r)));
    }

    private static bool PathsOverlap(string left, string right)
    {
        var a = NormalizePath(left);
        var b = NormalizePath(right);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').Trim('/').Trim();
    }
}

public sealed record ParallelExecutionIntent(
    string Id,
    string? GoalKey = null,
    IReadOnlyList<string>? TargetPaths = null,
    IReadOnlyList<string>? RequiredResources = null,
    bool WritesGoalState = false,
    bool MutatesWorkspaceLifecycle = false,
    bool RunsAcceptance = false,
    bool RequiresOperatorApproval = false,
    string? ProviderKey = null,
    int ProviderSlots = 1,
    IReadOnlyList<string>? DependsOn = null)
{
    public IReadOnlyList<string> TargetPaths { get; } = TargetPaths ?? [];
    public IReadOnlyList<string> RequiredResources { get; } = RequiredResources ?? [];
    public IReadOnlyList<string> DependsOn { get; } = DependsOn ?? [];
}

public sealed record ParallelExecutionProviderQuota(string ProviderKey, int AvailableSlots);

public sealed record ParallelExecutionPlan(
    IReadOnlyList<ParallelExecutionBatch> Batches,
    IReadOnlyList<ParallelExecutionDecision> Decisions);

public sealed record ParallelExecutionBatch(int Number, IReadOnlyList<string> IntentIds);

public sealed record ParallelExecutionDecision(
    string IntentId,
    ParallelExecutionDisposition Disposition,
    int? BatchNumber,
    IReadOnlyList<string> Reasons);

public enum ParallelExecutionDisposition
{
    Concurrent,
    Serialized,
    RequiresOperatorApproval
}
