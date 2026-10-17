using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ActivationAdmissionProbeLine(int Tick, string Phase, string Attempt, int Waiting)
{
    internal const string Prefix = "ACTIVATION_ADMISSION_PROBE ";

    internal string Format() => FormattableString.Invariant(
        $"{Prefix}tick={Tick} phase={Phase} attempt={Attempt} waiting={Waiting}");

    internal static bool TryParse(string line, out ActivationAdmissionProbeLine sample)
    {
        sample = null!;
        if (!line.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var fields = line[Prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 4 || !fields[0].StartsWith("tick=", StringComparison.Ordinal) ||
            !fields[1].StartsWith("phase=", StringComparison.Ordinal) ||
            !fields[2].StartsWith("attempt=", StringComparison.Ordinal) ||
            !fields[3].StartsWith("waiting=", StringComparison.Ordinal) ||
            !int.TryParse(fields[0][5..], NumberStyles.None, CultureInfo.InvariantCulture, out var tick) ||
            !int.TryParse(fields[3][8..], NumberStyles.None, CultureInfo.InvariantCulture, out var waiting)) return false;
        var phase = fields[1][6..];
        var attempt = fields[2][8..];
        if (phase is not ("none" or "admitted" or "terminal" or "released") || attempt.Length == 0 ||
            (phase == "none") != (attempt == "none")) return false;
        sample = new(tick, phase, attempt, waiting);
        return true;
    }
}

internal enum ActivationAdmissionProbeState { Pending, Satisfied, Skipped, Tripped }

internal sealed record ActivationAdmissionProbeVerdict(
    ActivationAdmissionProbeState State,
    ConductorActivationRevertReason? Reason = null,
    string Detail = "",
    string? SkipReason = null);

internal sealed class ActivationAdmissionProbeTracker(int healthyTicks = 3)
{
    private sealed class Attempt(DateTimeOffset admittedAt)
    {
        internal DateTimeOffset AdmittedAt { get; } = admittedAt;
        internal bool Terminal { get; set; }
    }

    private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);
    private ActivationAdmissionProbeVerdict _verdict = new(ActivationAdmissionProbeState.Pending);
    private ActivationAdmissionProbeLine? _latest;
    private DateTimeOffset? _waitingSince;
    private string? _open;
    private int _ticks;

    internal ActivationAdmissionProbeVerdict Observe(string line, DateTimeOffset now)
    {
        if (_verdict.State != ActivationAdmissionProbeState.Pending) return _verdict;
        if (line.StartsWith("TICK_END ", StringComparison.Ordinal) &&
            line.Contains(" activation=true", StringComparison.Ordinal)) _ticks++;
        if (ActivationAdmissionProbeLine.TryParse(line, out var sample))
        {
            _latest = sample;
            // A different id (or none) means the successor abandoned the prior holder.
            if (_open != sample.Attempt) _open = null;
            if (sample.Phase == "admitted")
            {
                _attempts.TryAdd(sample.Attempt, new(now));
                _open = sample.Attempt;
            }
            if (_open is { } id && _attempts.TryGetValue(id, out var attempt))
            {
                if (sample.Phase == "terminal") attempt.Terminal = true;
                if (sample.Phase == "released")
                {
                    _open = null;
                    if (attempt.Terminal) _verdict = new(ActivationAdmissionProbeState.Satisfied);
                }
            }
            if (sample.Waiting == 0 || sample.Phase != "none") _waitingSince = null;
            else _waitingSince ??= now;
        }
        return Evaluate(now);
    }

    internal DateTimeOffset? NextDeadline => _open is { } id
        ? _attempts[id].AdmittedAt + AcceptanceAdmissionPathSet.CompletionTimeout
        : _waitingSince + AcceptanceAdmissionPathSet.AdmissionTimeout;

    internal ActivationAdmissionProbeVerdict Evaluate(DateTimeOffset now)
    {
        if (_verdict.State != ActivationAdmissionProbeState.Pending) return _verdict;
        if (_open is { } id && now - _attempts[id].AdmittedAt >= AcceptanceAdmissionPathSet.CompletionTimeout)
            _verdict = new(ActivationAdmissionProbeState.Tripped, ConductorActivationRevertReason.ProbeNotCompleted,
                FormattableString.Invariant($"attempt={id} phase={_latest?.Phase} admittedSeconds={(now - _attempts[id].AdmittedAt).TotalSeconds:0}"));
        else if (_waitingSince is { } since && now - since >= AcceptanceAdmissionPathSet.AdmissionTimeout)
            _verdict = new(ActivationAdmissionProbeState.Tripped, ConductorActivationRevertReason.ProbeNotAdmitted,
                FormattableString.Invariant($"waitingSeconds={(now - since).TotalSeconds:0} waiting={_latest?.Waiting} lastTick={_latest?.Tick}"));
        else if (_ticks >= healthyTicks && _open is null && (_latest is null || _latest.Waiting == 0))
            _verdict = new(ActivationAdmissionProbeState.Skipped,
                SkipReason: _latest is null ? "probe-line-absent" : "no-verified-goal-waiting");
        return _verdict;
    }
}
