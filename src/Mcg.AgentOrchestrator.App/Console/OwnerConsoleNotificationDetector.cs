namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Session state records consumed crossings even while a tier is muted.
internal sealed class OwnerConsoleNotificationDetector(OwnerConsoleNotificationSettings settings)
{
    private readonly Dictionary<string, (bool Eligible, bool Consumed)> _decisions = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastCalm;
    private DateTimeOffset? _restartAt;
    private DateTimeOffset? _tickReference;
    private bool _tickConsumed;
    private bool _initialized;

    internal IReadOnlyList<OwnerConsoleNotificationTier> Detect(OwnerConsoleViewModel? previous,
        OwnerConsoleViewModel current, DateTimeOffset now, OwnerConsoleBellTiers enabled)
    {
        if (previous is null || Unready(previous) || Unready(current))
        {
            Baseline(current, now);
            return [];
        }
        if (!_initialized) Baseline(previous, now);

        var started = !previous.Status.ConductorRunning && current.Status.ConductorRunning;
        if (started) _restartAt = now;
        var calm = current.Status.LandedToday > previous.Status.LandedToday || started;
        var attention = ObserveDecisions(current, now, baseline: false);
        var alarm = previous.Status.ConductorRunning && !current.Status.ConductorRunning;
        alarm |= ObserveTick(current, now, baseline: false);

        var result = new List<OwnerConsoleNotificationTier>(3);
        if (calm && enabled.Calm && (_lastCalm is null || now - _lastCalm >= settings.CalmCoalesce))
        {
            result.Add(OwnerConsoleNotificationTier.Calm);
            _lastCalm = now;
        }
        if (attention && enabled.Attention) result.Add(OwnerConsoleNotificationTier.Attention);
        if (alarm && enabled.Alarm) result.Add(OwnerConsoleNotificationTier.Alarm);
        return result;
    }

    private void Baseline(OwnerConsoleViewModel model, DateTimeOffset now)
    {
        _initialized = true;
        _decisions.Clear();
        ObserveDecisions(model, now, baseline: true);
        ObserveTick(model, now, baseline: true);
    }

    private bool ObserveDecisions(OwnerConsoleViewModel model, DateTimeOffset now, bool baseline)
    {
        var live = model.Decisions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _decisions.Keys.Where(id => !live.Contains(id)).ToArray()) _decisions.Remove(id);
        var ring = false;
        foreach (var decision in model.Decisions)
        {
            var fresh = decision.RaisedAt is { } raised && now - raised < settings.AttentionThreshold;
            if (!_decisions.TryGetValue(decision.Id, out var state))
            {
                _decisions[decision.Id] = (fresh, !fresh);
                continue;
            }
            if (state.Eligible && !state.Consumed && decision.RaisedAt is { } at &&
                now - at >= settings.AttentionThreshold)
            {
                _decisions[decision.Id] = (true, true);
                ring = !baseline;
            }
        }
        return ring;
    }

    private bool ObserveTick(OwnerConsoleViewModel model, DateTimeOffset now, bool baseline)
    {
        if (!model.Status.ConductorRunning || model.Status.LatestTickAt is not { } tick) return false;
        var reference = _restartAt is { } restart && restart > tick ? restart : tick;
        var stale = now - reference > settings.StaleTickThreshold;
        // Older or temporarily unavailable ticks cannot re-arm a consumed reference.
        if (_tickReference is null || reference > _tickReference)
        {
            _tickReference = reference;
            _tickConsumed = stale;
            return false;
        }
        if (baseline)
        {
            _tickConsumed |= stale;
            return false;
        }
        if (reference != _tickReference || _tickConsumed || !stale) return false;
        _tickConsumed = true;
        return true;
    }

    private static bool Unready(OwnerConsoleViewModel model) =>
        model.DecisionsState is { Loading: true } or { Error: not null } ||
        model.ActivityState is { Loading: true } or { Error: not null };
}
