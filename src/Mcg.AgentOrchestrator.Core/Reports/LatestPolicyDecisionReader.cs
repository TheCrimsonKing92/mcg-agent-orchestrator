namespace Mcg.AgentOrchestrator.Core;

public sealed record LatestPolicyDecision(
    string Stage, string Action, int Rung, string DiscriminatingEvidence,
    DateTimeOffset OccurredAt);

public static class LatestPolicyDecisionReader
{
    public static LatestPolicyDecision? Read(IReadOnlyList<ProgressEvent> timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        LatestPolicyDecision? latest = null;
        foreach (var evt in timeline)
        {
            if (evt.TickOutcome?.Decision is not { } decision ||
                latest is not null && evt.OccurredAt < latest.OccurredAt)
                continue;
            // Later sequence positions win when event times tie.
            latest = new(decision.Stage, decision.Action, decision.Rung,
                decision.DiscriminatingEvidence, evt.OccurredAt);
        }
        return latest;
    }
}
