namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class RetryLoopPolicy
{
    internal static readonly TimeSpan InitialWriteDelay = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan MaximumWriteDelay = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(30);

    internal static TimeSpan ClampInterval(TimeSpan computed, TimeSpan floor) =>
        computed < floor ? floor : computed;

    internal static TimeSpan GetWriteDelay(int retryNumber, double jitter)
    {
        if (retryNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(retryNumber));
        if (!double.IsFinite(jitter))
            throw new ArgumentOutOfRangeException(nameof(jitter));

        // Add at most 25% jitter. Since the base doubles, adjacent waits remain monotonic
        // for every jitter sequence while independently-running conductors de-synchronize.
        var boundedJitter = Math.Clamp(jitter, 0d, 1d) * 0.25d;
        var baseMilliseconds = Math.Min(
            InitialWriteDelay.TotalMilliseconds * Math.Pow(2, retryNumber - 1),
            MaximumWriteDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Min(
            MaximumWriteDelay.TotalMilliseconds,
            baseMilliseconds * (1d + boundedJitter)));
    }
}

internal readonly record struct RetryDiagnosticKey(string EventName, string Goal, string Condition);

internal sealed class RetryDiagnosticCoalescer
{
    private const int VerbatimLimit = 3;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Dictionary<RetryDiagnosticKey, State> _states = [];

    internal RetryDiagnosticCoalescer(Func<DateTimeOffset> utcNow) => _utcNow = utcNow;

    internal string? Observe(RetryDiagnosticKey key, string verbatim)
    {
        var now = _utcNow();
        if (!_states.TryGetValue(key, out var state))
        {
            state = new State(now, now, 0);
        }

        state = state with { Attempts = state.Attempts + 1 };
        string? line = null;
        if (state.Attempts <= VerbatimLimit)
        {
            line = verbatim;
        }
        else if (now - state.LastSummaryAt >= RetryLoopPolicy.SummaryInterval)
        {
            state = state with { LastSummaryAt = now };
            line = $"RETRY_STILL_PENDING event={key.EventName} goal={key.Goal} condition={key.Condition} attempts={state.Attempts} elapsed_ms={(long)(now - state.StartedAt).TotalMilliseconds}";
        }

        _states[key] = state;
        return line;
    }

    internal string? Complete(RetryDiagnosticKey key)
    {
        if (!_states.Remove(key, out var state))
            return null;

        var elapsed = _utcNow() - state.StartedAt;
        return $"RETRY_TERMINAL_SUMMARY event={key.EventName} goal={key.Goal} condition={key.Condition} attempts={state.Attempts} elapsed_ms={(long)elapsed.TotalMilliseconds}";
    }

    internal IReadOnlyList<string> CompleteAll() =>
        _states.Keys.ToArray().Select(Complete).Where(line => line is not null).Select(line => line!).ToArray();

    private sealed record State(DateTimeOffset StartedAt, DateTimeOffset LastSummaryAt, int Attempts);
}
