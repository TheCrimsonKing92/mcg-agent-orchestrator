using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Observations belong to one invocation, never to cache identity or publication authority.
internal sealed class BaseBuildCachePhaseTiming
{
    internal enum Phase { IdentityResolution, IdentityRevalidation, RunBuildCommands, CacheRestoreCopy, CacheCapturePublish }

    private static readonly string[] Names =
        ["identity_resolution", "identity_revalidation", "run_build_commands", "cache_restore_copy", "cache_capture_publish"];
    private readonly TimeProvider _clock;
    private readonly long _start;
    private readonly List<Interval> _intervals = [];
    private long? _end;

    internal BaseBuildCachePhaseTiming(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _start = _clock.GetTimestamp();
    }

    internal IDisposable Measure(Phase phase)
    {
        var interval = new Interval(phase, _clock.GetTimestamp());
        _intervals.Add(interval);
        return new Measurement(this, interval);
    }

    internal void Stop() => _end ??= _clock.GetTimestamp();
    internal long ElapsedMilliseconds => Milliseconds((_end ?? _clock.GetTimestamp()) - _start);

    internal string Format()
    {
        var end = _end ?? _clock.GetTimestamp();
        var total = end - _start;
        var unknown = new bool[Names.Length];
        var durations = new long[Names.Length];
        var invalid = total < 0;
        foreach (var interval in _intervals)
        {
            var index = (int)interval.Phase;
            if (interval.End is not { } closed)
                unknown[index] = true;
            else if (interval.Start < _start || closed < interval.Start || closed > end)
                invalid = unknown[index] = true;
            else
                durations[index] += closed - interval.Start;
        }

        // Only a restore completely contained by a managed invocation may overlap.
        // Exclude that exact raw interval from managed time before unit conversion.
        var hasNestedRestore = false;
        for (var i = 0; i < _intervals.Count; i++)
        {
            var left = _intervals[i];
            for (var j = i + 1; j < _intervals.Count; j++)
            {
                var right = _intervals[j];
                if (left.Start >= (right.End ?? end) || right.Start >= (left.End ?? end)) continue;
                var build = left.Phase == Phase.RunBuildCommands ? left : right;
                var restore = ReferenceEquals(build, left) ? right : left;
                if (build.Phase == Phase.RunBuildCommands && restore.Phase == Phase.CacheRestoreCopy &&
                    build.End is { } buildEnd && restore.End is { } restoreEnd &&
                    build.Start <= restore.Start && buildEnd >= restoreEnd)
                {
                    hasNestedRestore = true;
                    durations[(int)Phase.RunBuildCommands] -= restoreEnd - restore.Start;
                }
                else
                {
                    unknown[(int)left.Phase] = unknown[(int)right.Phase] = true;
                    invalid = true;
                }
            }
        }
        if (hasNestedRestore && unknown[(int)Phase.CacheRestoreCopy])
            unknown[(int)Phase.RunBuildCommands] = true;

        var fields = new List<string>();
        long assigned = 0;
        for (var index = 0; index < Names.Length; index++)
        {
            if (durations[index] < 0) invalid = unknown[index] = true;
            var count = _intervals.Count(interval => (int)interval.Phase == index);
            var status = unknown[index] ? "unknown" : count == 0 ? "not_run" : "measured";
            fields.Add($"{Names[index]}_ms={(unknown[index] ? "unknown" : Number(Milliseconds(durations[index])))}");
            fields.Add($"{Names[index]}_count={Number(count)}");
            fields.Add($"{Names[index]}_status={status}");
            if (!unknown[index]) assigned += durations[index];
        }
        var residual = total - assigned;
        invalid |= residual < 0;
        // Residual is unassigned elapsed time, including observation overhead and unavailable
        // categories. Independently truncated millisecond fields need not sum to the total.
        fields.Add($"build_phase_residual_ms={(invalid ? "unknown" : Number(Milliseconds(residual)))}");
        fields.Add($"build_phase_accounting_status={(invalid || unknown.Any(value => value) ? "partial" : "complete")}");
        fields.Add("run_build_commands_scope=managed_check_exclusive_of_cache");
        return string.Join(" ", fields);
    }

    private long Milliseconds(long ticks) => (long)_clock.GetElapsedTime(0, ticks).TotalMilliseconds;
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private sealed class Interval(Phase phase, long start)
    {
        internal Phase Phase { get; } = phase;
        internal long Start { get; } = start;
        internal long? End { get; set; }
    }
    private sealed class Measurement(BaseBuildCachePhaseTiming owner, Interval interval) : IDisposable
    {
        public void Dispose() => interval.End ??= owner._clock.GetTimestamp();
    }
}
