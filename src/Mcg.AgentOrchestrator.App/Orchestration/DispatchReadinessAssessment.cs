using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the plan and verdict for one goal at one instant; callers own the hold scope.
internal static class DispatchReadinessAssessment
{
    public static DispatchReadinessAssessmentResult Evaluate(
        Goal goal,
        IReadOnlyCollection<Goal>? providerHoldScope,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        DateTimeOffset now)
    {
        var plan = SubscriptionPlanBuilder.Build(
            goal, agents, profiles, now: now, providerHoldScope: providerHoldScope);
        return new(plan, DispatchReadinessEvaluator.EvaluateDispatchReadiness(goal, plan, now));
    }
}

internal sealed record DispatchReadinessAssessmentResult(
    SubscriptionPlan Plan,
    DispatchReadinessVerdict Verdict);
