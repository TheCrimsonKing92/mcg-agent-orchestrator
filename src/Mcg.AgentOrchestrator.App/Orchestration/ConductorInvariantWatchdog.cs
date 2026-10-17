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

        foreach (var attempt in observation.FinishedAttempts ?? [])
            if (observation.Now - attempt.FinishedAt > ConductorInvariantThresholds.FinishedUnreconciled)
                Add(new(ConductorInvariantThresholds.FinishedUnreconciledKind, attempt.AttemptId, attempt.FinishedAt,
                    $"goal:{Short(attempt.GoalId)},kind:{attempt.Kind},finishedAt:{attempt.FinishedAt:O},unreconciledFor:{(observation.Now - attempt.FinishedAt).TotalMinutes:F0}m"));

        if (observation.Relaunch is { ScheduledAt: { } scheduled } relaunch)
        {
            var since = relaunch.LastLiveGateAt is { } live && live > scheduled ? live : scheduled;
            if (relaunch.FailureOutcomeAt is null)
            {
                var silenceSince = relaunch.HandoffAt is { } handoff && handoff > since ? handoff : since;
                var ceilingExceeded = relaunch.HandoffAt is { } started &&
                    observation.Now - started > ConductorInvariantThresholds.ProbeWindowCeiling;
                var breachSince = ceilingExceeded
                    ? relaunch.HandoffAt!.Value + AcceptanceAdmissionPathSet.CompletionTimeout : silenceSince;
                if (ceilingExceeded || observation.Now - silenceSince > ConductorInvariantThresholds.RelaunchWithoutActivation)
                    Add(new(ConductorInvariantThresholds.RelaunchWithoutActivationKind, relaunch.GoalId ?? "unknown", breachSince,
                        $"scheduledAt:{scheduled:O},lastLiveGateAt:{relaunch.LastLiveGateAt?.ToString("O") ?? "none"},silentFor:{(observation.Now - breachSince).TotalMinutes:F0}m" +
                        (relaunch.HandoffAt is { } at ? $",handoffAt:{at:O}" : "")));
            }
            else if (observation.Now - since > ConductorInvariantThresholds.GenerationBehindMain &&
                observation.MainSha?.Trim() is { Length: > 0 } main &&
                relaunch.RunningBuildCommit?.Trim() is { Length: > 0 } running &&
                !main.StartsWith(running, StringComparison.OrdinalIgnoreCase) &&
                !running.StartsWith(main, StringComparison.OrdinalIgnoreCase))
                Add(new(ConductorInvariantThresholds.GenerationBehindMainKind, Short(main), since,
                    $"running:{Short(running)},main:{Short(main)},failureAt:{relaunch.FailureOutcomeAt:O}"));
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
        static string Short(string value) => value[..Math.Min(12, value.Length)];
    }
}

internal sealed record ConductorInvariantBreach(string Kind, string Record, DateTimeOffset Since, string Evidence)
{
    internal string Key => Kind + "|" + Record;
    internal string CorrelationKey => "invariant-breach:" + Kind + ":" + Record;
}
