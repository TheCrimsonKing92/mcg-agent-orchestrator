namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum SpecRefinementLaunchDecisionKind
{
    Launch,
    DeferCadence,
    Escalated
}

internal sealed record SpecRefinementLaunchDecision(
    SpecRefinementLaunchDecisionKind Kind,
    int ConsecutiveFailedClaims);

internal static class SpecRefinementLaunchPolicy
{
    public static SpecRefinementLaunchDecision Decide(
        SpecRefinementLaunchAttempt? attempt,
        DateTimeOffset now,
        TimeSpan cadence,
        int failureLimit)
    {
        if (attempt?.EscalatedAt is not null)
        {
            return new SpecRefinementLaunchDecision(
                SpecRefinementLaunchDecisionKind.Escalated,
                attempt.ConsecutiveFailedClaims);
        }

        if (attempt?.LastLaunchAt is not { } lastLaunch)
            return new SpecRefinementLaunchDecision(SpecRefinementLaunchDecisionKind.Launch, 0);

        var elapsed = now - lastLaunch;
        if (elapsed < TimeSpan.Zero || elapsed < cadence)
        {
            return new SpecRefinementLaunchDecision(
                SpecRefinementLaunchDecisionKind.DeferCadence,
                attempt.ConsecutiveFailedClaims);
        }

        var failures = checked(attempt.ConsecutiveFailedClaims + 1);
        return new SpecRefinementLaunchDecision(
            failures >= failureLimit
                ? SpecRefinementLaunchDecisionKind.Escalated
                : SpecRefinementLaunchDecisionKind.Launch,
            failures);
    }
}
