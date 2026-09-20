using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal abstract record DispatchReadinessVerdict;

internal sealed record DispatchReadinessReady : DispatchReadinessVerdict;

internal sealed record DispatchReadinessDeferred(
    DateTimeOffset RetryAfter,
    string Reason) : DispatchReadinessVerdict;

// HasCandidates=false means no assigned dispatch candidates exist (→ escalate in conductor).
// HasCandidates=true means candidates are assigned but all routes are blocked without a retry window
// (e.g. missing profile) — operator action needed but conductor should hold, not escalate.
internal sealed record DispatchReadinessBlocked(
    string Reason,
    bool HasCandidates = false) : DispatchReadinessVerdict;

internal static class DispatchReadinessEvaluator
{
    public static DispatchReadinessVerdict EvaluateDispatchReadiness(
        Goal goal,
        SubscriptionPlan plan,
        DateTimeOffset now)
    {
        if (!DispatchReadinessRules.HasAssignedDispatchCandidates(goal))
        {
            return new DispatchReadinessBlocked("No assigned dispatch candidates", HasCandidates: false);
        }

        var assignedItems = plan.Items
            .Where(item => item.TaskStatus == WorkTaskStatus.Assigned)
            .ToList();

        if (assignedItems.Any(item => item.CanPrepare))
        {
            return new DispatchReadinessReady();
        }

        // All assigned tasks have CanPrepare=false; check for provider cooldown (deferred with retry-after).
        // Surface MIN RetryAfter: earliest moment any task becomes dispatchable for partial dispatch.
        var deferredRetryAfters = assignedItems
            .Where(item => item.RetryAfter is { } ra && ra > now)
            .Select(item => item.RetryAfter!.Value)
            .ToList();

        if (deferredRetryAfters.Count > 0)
        {
            var minRetryAfter = deferredRetryAfters.Min();
            var providerName = assignedItems
                .Where(item => item.RetryAfter == minRetryAfter && !string.IsNullOrWhiteSpace(item.ProviderName))
                .Select(item => item.ProviderName!)
                .FirstOrDefault();
            var reason = providerName is not null
                ? $"Provider '{providerName}' is cooling down; retry after {minRetryAfter:u}"
                : $"All assigned tasks are deferred; retry after {minRetryAfter:u}";
            return new DispatchReadinessDeferred(minRetryAfter, reason);
        }

        var blockReason = assignedItems.Count > 0
            ? assignedItems[0].Detail
            : "No assigned tasks can be prepared for dispatch";
        return new DispatchReadinessBlocked(blockReason, HasCandidates: true);
    }
}
