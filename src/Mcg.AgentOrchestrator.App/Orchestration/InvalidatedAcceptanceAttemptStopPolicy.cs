using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One grace deadline and at most one stop dispatch per invalidated owner identity, per loop.
internal sealed class InvalidatedAcceptanceAttemptStopPolicy(Func<DateTimeOffset> utcNow)
{
    internal static readonly TimeSpan GraceWindow = TimeSpan.FromSeconds(120);
    private readonly object _sync = new();
    // Retain the last identity per goal across unreadable metadata or a transient liveness gap.
    // A later attempt replaces it; an observation gap must not authorize a duplicate stop.
    private readonly Dictionary<string, (ConductorParallelAcceptanceAttempt Attempt, DateTimeOffset InvalidatedAt, bool StopIssued)> _observed = new(StringComparer.Ordinal);
    private readonly InvalidatedAcceptanceAttemptProcessStop _processStop = new();

    internal bool TryHold(
        ConductorParallelAcceptanceAttemptCoordinator coordinator,
        string goalId,
        Func<ConductorParallelAcceptanceAttempt, InvalidatedAcceptanceAttemptStopResult>? stop,
        Action<string> report,
        out string holdReason)
    {
        holdReason = string.Empty;
        if (!coordinator.TryGetLiveInvalidatedAttempt(goalId, out var attempt)) return false;
        holdReason = $"invalidated acceptance attempt {attempt.AttemptId} process {attempt.OwnerProcessId} is still exiting";
        try
        {
            if (Observe(coordinator, attempt, stop) is { } stopEvent) report(stopEvent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            report($"INVALIDATED_ATTEMPT_STOP_FAILED goal={goalId} attempt={attempt.AttemptId} error={ConductorBatchLoop.SanitizeReason(ex.Message)}");
        }
        // Even a successful stop holds this tick. The next tick must confirm owner exit.
        return true;
    }

    internal string? Observe(
        ConductorParallelAcceptanceAttemptCoordinator coordinator,
        ConductorParallelAcceptanceAttempt observed,
        Func<ConductorParallelAcceptanceAttempt, InvalidatedAcceptanceAttemptStopResult>? stop = null)
    {
        lock (_sync)
        {
            string? line = null;
            coordinator.WithLiveInvalidatedOwner(observed, current =>
            {
                var now = utcNow();
                if (!_observed.TryGetValue(current.GoalId, out var state) || !SameOwner(state.Attempt, current))
                {
                    state = (current, current.InvalidatedAt ??
                        (current.Outcome == ConductorParallelAcceptanceAttemptOutcome.StaleCandidate ? current.ReconciledAt : null) ?? now, false);
                    _observed[current.GoalId] = state;
                }
                var waited = now - state.InvalidatedAt;
                if (state.StopIssued || waited <= GraceWindow) return;

                // Reserve before invoking the process seam, including refusals and exceptions.
                _observed[current.GoalId] = (current, state.InvalidatedAt, true);
                InvalidatedAcceptanceAttemptStopResult result;
                try { result = (stop ?? _processStop.Stop)(current); }
                catch (Exception ex) { result = new("failed", [], ex.Message); }
                line = $"INVALIDATED_ATTEMPT_STOP goal={current.GoalId} attempt={current.AttemptId} " +
                    $"ownerPid={current.OwnerProcessId} descendantPids={(result.DescendantPids.Count == 0 ? "none" : string.Join(',', result.DescendantPids))} " +
                    $"invalidatedAt={state.InvalidatedAt:O} waitedSeconds={waited.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"graceSeconds=120 outcome={result.Outcome} detail={Token(result.Detail)}";
            });
            return line;
        }
    }

    private static bool SameOwner(ConductorParallelAcceptanceAttempt left, ConductorParallelAcceptanceAttempt right) =>
        left.AttemptId == right.AttemptId && left.OwnerProcessId == right.OwnerProcessId &&
        left.OwnerProcessStartedAt == right.OwnerProcessStartedAt &&
        string.Equals(left.OwnerExecutablePath, right.OwnerExecutablePath, StringComparison.OrdinalIgnoreCase);

    private static string Token(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "none" : string.Concat(value.Select(character => char.IsWhiteSpace(character) ? '_' : character));
}
