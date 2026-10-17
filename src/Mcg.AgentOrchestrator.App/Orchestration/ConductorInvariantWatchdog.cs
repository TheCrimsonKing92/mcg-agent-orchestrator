namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns only the rules. Persistence and transition side effects belong to program health.
internal static class ConductorInvariantWatchdog
{
    internal static (IReadOnlyList<ConductorInvariantBreach> Raise,
        IReadOnlyList<ConductorInvariantBreach> Clear,
        IReadOnlyDictionary<string, DateTimeOffset> Active) Evaluate(
        ConductorInvariantObservation observation, IReadOnlyDictionary<string, DateTimeOffset> active)
    {
        var current = new Dictionary<string, ConductorInvariantBreach>(StringComparer.Ordinal);
        var oldest = observation.GateReadyGoals.OrderBy(goal => goal.ReadySince)
            .ThenBy(goal => goal.GoalId, StringComparer.Ordinal).FirstOrDefault();
        if (oldest is not null)
        {
            var since = observation.LastGateProgressAt is { } progress && progress > oldest.ReadySince
                ? progress : oldest.ReadySince;
            if (observation.Now - since > ConductorInvariantThresholds.GateIdleWhileReady)
                Add(new(ConductorInvariantThresholds.GateIdleWhileReadyKind, oldest.GoalId, since,
                    $"ready:{observation.GateReadyGoals.Count},idleSeconds:{(observation.Now - since).TotalSeconds:F0}"));
        }

        var tail = observation.Refusals.TakeLast(ConductorInvariantThresholds.RepeatedRefusalCount).ToArray();
        if (tail.Length == ConductorInvariantThresholds.RepeatedRefusalCount &&
            tail.All(item => item.RecordId == tail[0].RecordId && item.Reason == tail[0].Reason) &&
            (observation.LastGateProgressAt is null || observation.LastGateProgressAt <= tail[0].At))
        {
            var first = observation.Refusals.Reverse()
                .TakeWhile(item => item.RecordId == tail[0].RecordId && item.Reason == tail[0].Reason &&
                    (observation.LastGateProgressAt is null || item.At >= observation.LastGateProgressAt)).Last();
            Add(new(ConductorInvariantThresholds.RepeatedRefusalKind, first.RecordId, first.At,
                $"refusals:{tail.Length},reason:{tail[0].Reason.Replace(' ', '_')}"));
        }

        var raise = current.Values.Where(breach => !active.ContainsKey(breach.Key)).ToArray();
        var clear = active.Where(pair => !current.ContainsKey(pair.Key)).Select(pair =>
        {
            var parts = pair.Key.Split('|', 2);
            return new ConductorInvariantBreach(parts[0], parts[1], pair.Value, "recovered");
        }).ToArray();
        return (raise, clear, current.ToDictionary(pair => pair.Key,
            pair => active.TryGetValue(pair.Key, out var since) ? since : pair.Value.Since, StringComparer.Ordinal));

        void Add(ConductorInvariantBreach breach) => current.Add(breach.Key, breach);
    }
}

internal sealed record ConductorInvariantBreach(string Kind, string Record, DateTimeOffset Since, string Evidence)
{
    internal string Key => Kind + "|" + Record;
    internal string CorrelationKey => "invariant-breach:" + Kind + ":" + Record;
}
