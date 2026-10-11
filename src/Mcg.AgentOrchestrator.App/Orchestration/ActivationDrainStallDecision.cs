namespace Mcg.AgentOrchestrator.App.Orchestration;

// A drain can hold activation on one healthy tick, but cannot renew that tick's total budget.
internal static class ActivationDrainStallDecision
{
    internal static TimeSpan? Evaluate(
        DateTimeOffset now,
        DateTimeOffset? lastHealthyTickAt,
        DateTimeOffset? lastDrainLineAt,
        TimeSpan silenceWindow,
        TimeSpan tickStallBudget)
    {
        if (lastHealthyTickAt is not { } tickAt || lastDrainLineAt is not { } drainAt ||
            drainAt < tickAt)
        {
            return null;
        }

        var silenceRemaining = silenceWindow - (now - drainAt);
        var budgetRemaining = tickStallBudget - (now - tickAt);
        if (silenceRemaining <= TimeSpan.Zero || budgetRemaining <= TimeSpan.Zero)
        {
            return null;
        }

        return silenceRemaining < budgetRemaining ? silenceRemaining : budgetRemaining;
    }
}
