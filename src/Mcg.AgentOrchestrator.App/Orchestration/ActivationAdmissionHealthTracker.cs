using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ActivationAdmissionSample(
    int Tick, bool CensusFailed, int WaitingVerified, int Occupied, int LiveGates, int EvidenceHeldPermits = 0)
{
    internal const string Prefix = "ACTIVATION_ADMISSION_HEALTH ";

    internal string Format() => FormattableString.Invariant(
        $"{Prefix}tick={Tick} census={(CensusFailed ? "failed" : "ok")} waiting={WaitingVerified} occupied={Occupied} live={LiveGates} evidenceHeld={EvidenceHeldPermits}");

    internal static bool TryParse(string line, out ActivationAdmissionSample sample)
    {
        sample = null!;
        if (!line.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var parts = line[Prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || !ReadCount(parts[0], "tick=", out var tick) ||
            parts[1] is not ("census=ok" or "census=failed") ||
            !ReadCount(parts[2], "waiting=", out var waiting) ||
            !ReadCount(parts[3], "occupied=", out var occupied) ||
            !ReadCount(parts[4], "live=", out var live)) return false;
        var evidenceHeld = 0;
        if (parts.Length > 5 && parts[5].StartsWith("evidenceHeld=", StringComparison.Ordinal) &&
            !ReadCount(parts[5], "evidenceHeld=", out evidenceHeld)) return false;
        sample = new(tick, parts[1] == "census=failed", waiting, occupied, live, evidenceHeld);
        return true;
    }

    private static bool ReadCount(string token, string key, out int value)
    {
        value = 0;
        return token.StartsWith(key, StringComparison.Ordinal) &&
            int.TryParse(token.AsSpan(key.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}

internal enum ActivationAdmissionHealthState { Healthy, StreakOpen, Tripped }

internal sealed record ActivationAdmissionHealthVerdict(
    ActivationAdmissionHealthState State,
    ConductorActivationRevertReason? Reason = null,
    string Detail = "");

// One activation window owns this tracker; its monitor serializes observations.
internal sealed class ActivationAdmissionHealthTracker
{
    private bool _failureStreak;
    private int _count;
    private int _firstTick;
    private DateTimeOffset _startedAt;
    internal ActivationAdmissionHealthVerdict Verdict { get; private set; } =
        new(ActivationAdmissionHealthState.Healthy);

    internal ActivationAdmissionHealthVerdict Observe(ActivationAdmissionSample sample, DateTimeOffset now)
    {
        if (Verdict.State == ActivationAdmissionHealthState.Tripped) return Verdict;
        var failure = sample.CensusFailed;
        var occupied = !failure && sample.WaitingVerified >= 1 && sample.Occupied >= 1 && sample.LiveGates == 0 &&
            sample.EvidenceHeldPermits < DotnetBuildEnvironmentManager.BuildConcurrencySlotCount;
        if (!failure && !occupied)
        {
            _count = 0;
            return Verdict = new(ActivationAdmissionHealthState.Healthy);
        }

        if (_count == 0 || _failureStreak != failure)
        {
            _count = 0;
            _firstTick = sample.Tick;
            _startedAt = now;
        }
        _failureStreak = failure;
        _count++;
        var elapsed = now - _startedAt;
        var elapsedCap = elapsed >= ActivationAdmissionHealthPolicy.StreakElapsedCap;
        var limit = failure ? ActivationAdmissionHealthPolicy.CensusFailureTicks :
            ActivationAdmissionHealthPolicy.OccupiedWithoutLiveGateTicks;
        if (_count < limit && !elapsedCap)
            return Verdict = new(ActivationAdmissionHealthState.StreakOpen);

        var detail = failure
            ? FormattableString.Invariant($"censusFailedTicks={_count} firstTick={_firstTick} lastTick={sample.Tick}")
            : FormattableString.Invariant($"occupiedNoLiveGateTicks={_count} waiting={sample.WaitingVerified} occupied={sample.Occupied} live={sample.LiveGates}");
        if (elapsedCap) detail += FormattableString.Invariant($" elapsedSeconds={elapsed.TotalSeconds:0.###}");
        return Verdict = new(ActivationAdmissionHealthState.Tripped,
            failure ? ConductorActivationRevertReason.AdmissionCensusFailing :
                ConductorActivationRevertReason.AdmissionStalled, detail);
    }
}
