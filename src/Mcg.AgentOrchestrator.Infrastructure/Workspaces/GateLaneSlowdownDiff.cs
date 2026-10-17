using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GateLaneSlowdownDiff
{
    internal const string FileName = "gate-lane-slowdowns.jsonl";
    internal sealed record GrownTest(string Test, double CurrentMs, double MedianMs);
    internal sealed record NewTest(string Test, double CurrentMs);
    internal sealed record GateLaneSlowdownResult(string Status, int HistoryRuns, double WallMs,
        double? Ratio = null, double? MedianWallMs = null, int TestsCompared = 0, int TestsGrown = 0,
        IReadOnlyList<GrownTest>? Grown = null, int TestsNewAboveFloor = 0,
        IReadOnlyList<NewTest>? NewAboveFloor = null, string? UnavailableReason = null);

    internal static GateLaneSlowdownResult Compute(double wallMs, LaneTestDurationSnapshot current, IReadOnlyList<GateLaneBaselineStore.Run> history)
    {
        if (history.Count < 3) return new("insufficient-history", history.Count, wallMs);
        var median = Median(history.Select(run => run.WallMs));
        var ratio = wallMs / median;
        if (ratio <= 1.5) return new("not-slow", history.Count, wallMs);
        if (current.Tests.Count == 0 || current.UnavailableReason is not null)
            return new("no-test-data", history.Count, wallMs, ratio, median,
                UnavailableReason: current.UnavailableReason ?? "no-passed-tests-above-floor");
        var grown = new List<GrownTest>();
        var fresh = new List<NewTest>();
        var compared = 0;
        foreach (var (test, milliseconds) in current.Tests)
        {
            var samples = history.Where(run => run.Tests.ContainsKey(test)).Select(run => run.Tests[test]).ToArray();
            if (samples.Length == 0) fresh.Add(new(test, milliseconds));
            if (samples.Length < 2) continue;
            compared++;
            var testMedian = Median(samples);
            if (milliseconds >= 2 * testMedian && milliseconds - testMedian >= 500)
                grown.Add(new(test, milliseconds, testMedian));
        }
        return new("slow", history.Count, wallMs, ratio, median, compared, grown.Count,
            grown.OrderByDescending(test => test.CurrentMs - test.MedianMs).ThenBy(test => test.Test, StringComparer.Ordinal).Take(10).ToArray(),
            fresh.Count, fresh.OrderByDescending(test => test.CurrentMs).ThenBy(test => test.Test, StringComparer.Ordinal).Take(10).ToArray());
    }

    internal static object ToRow(GateLaneSlowdownResult result, string? goalId, string lane, int runOrdinal, DateTimeOffset now) => new
    {
        version = 1, goal_id = goalId, lane, run_ordinal = runOrdinal,
        ratio = Math.Round(result.Ratio!.Value, 2), median_wall_ms = result.MedianWallMs, wall_ms = result.WallMs,
        history_runs = result.HistoryRuns, tests_compared = result.TestsCompared, tests_grown = result.TestsGrown,
        grown = result.Grown!.Select(test => new { test = test.Test, current_ms = test.CurrentMs, median_ms = test.MedianMs }),
        tests_new_above_floor = result.TestsNewAboveFloor,
        new_above_floor = result.NewAboveFloor!.Select(test => new { test = test.Test, current_ms = test.CurrentMs }), recorded_at = now
    };

    internal static void TryEmitProgress(GateLaneSlowdownResult? result, string? goalId, string lane, TimeSpan elapsed, Action<AcceptanceGateProgress> sink)
    {
        try
        {
            if (result?.Status != "slow") return;
            // The formatter already prints target and elapsed. Keep this producer's other fields small.
            var now = DateTimeOffset.UtcNow;
            sink(new(goalId, "lane-slowdown", lane.Length <= 80 ? lane : lane[..77] + "...", null, null, null,
                now - elapsed, now, now, elapsed, 0, "none") { Slowdown = result });
        }
        catch (Exception) { /* A diagnostic sink cannot change a lane verdict. */ }
    }

    internal static string FormatProgressSuffix(GateLaneSlowdownResult? result, string lane)
    {
        if (result?.Status != "slow") return string.Empty;
        var prefix = FormattableString.Invariant($" ratio={result.Ratio:F2} median_wall_ms={result.MedianWallMs:0} grown={result.TestsGrown} top=");
        var suffix = FormattableString.Invariant($" new_above_floor={result.TestsNewAboveFloor}");
        var names = (result.Grown ?? []).Take(3).Select(test => string.Join('.', test.Test.Split('.').TakeLast(2))).ToArray();
        // Reserve target space as well as the two-member cohort formatter prefix.
        var tokenBudget = Math.Min(150, 195 - lane.Length);
        var budget = Math.Max(0, tokenBudget - prefix.Length - suffix.Length);
        var top = names.Length == 0 ? "none" : string.Join(',', names);
        if (top.Length > budget)
        {
            var kept = new List<string>();
            foreach (var name in names)
            {
                if (string.Join(',', kept.Append(name)).Length + 3 > budget) break;
                kept.Add(name);
            }
            top = string.Join(',', kept.Append("+" + (names.Length - kept.Count).ToString(CultureInfo.InvariantCulture)));
        }
        return prefix + top + suffix;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : sorted[sorted.Length / 2 - 1] / 2 + sorted[sorted.Length / 2] / 2;
    }
}
