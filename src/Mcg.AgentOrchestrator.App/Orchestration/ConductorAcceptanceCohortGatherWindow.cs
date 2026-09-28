namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorAcceptanceCohortGatherDecision(
    bool Hold,
    int RemainingSeconds);

internal sealed class ConductorAcceptanceCohortGatherWindow
{
    private readonly Dictionary<string, DateTimeOffset> _firstObserved = new(StringComparer.Ordinal);
    private readonly HashSet<string> _released = new(StringComparer.Ordinal);

    internal void ObserveReadyGoals(IReadOnlyCollection<string> readyGoalIds)
    {
        var current = readyGoalIds.ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _firstObserved.Keys.Where(id => !current.Contains(id)).ToArray())
            _firstObserved.Remove(stale);
        _released.RemoveWhere(id => !current.Contains(id));
        if (current.Count > 1)
            _released.UnionWith(current);
    }

    internal ConductorAcceptanceCohortGatherDecision Evaluate(
        string readyGoalId,
        IReadOnlyList<string> compatiblePartners,
        DateTimeOffset now,
        int windowSeconds)
    {
        if (_released.Contains(readyGoalId))
            return new(false, 0);

        _firstObserved.TryAdd(readyGoalId, now);
        var decision = Decide(_firstObserved[readyGoalId], now, windowSeconds, compatiblePartners.Count);
        if (!decision.Hold)
            _released.Add(readyGoalId);
        return decision;
    }

    internal static ConductorAcceptanceCohortGatherDecision Decide(
        DateTimeOffset firstObserved,
        DateTimeOffset now,
        int windowSeconds,
        int compatiblePartnerCount)
    {
        if (windowSeconds <= 0 || compatiblePartnerCount == 0)
            return new(false, 0);

        var elapsed = now - firstObserved;
        if (elapsed >= TimeSpan.FromSeconds(windowSeconds))
            return new(false, 0);

        var remaining = TimeSpan.FromSeconds(windowSeconds) - (elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed);
        return new(true, (int)Math.Ceiling(remaining.TotalSeconds));
    }
}
