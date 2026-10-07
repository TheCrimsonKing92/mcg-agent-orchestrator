namespace Mcg.AgentOrchestrator.Core;

public sealed record LaneReuseShadowRecord(string GoalId, string AttemptId, DateTimeOffset RecordedAt,
    IReadOnlyList<LaneReuseShadowLaneRow> Lanes);

public sealed record LaneReuseShadowLaneRow(string Lane, string Decision, string? Reason, bool Executed,
    long? DurationMs, bool ShadowMiss = false, string? MissReason = null, string? ReferenceSource = null,
    bool FlakeConfirmed = false, string? FailedPredicate = null, IReadOnlyList<string>? FailingClasses = null,
    string? Verdict = null, string? RuleV2Decision = null, string? RuleV2Reason = null);

public sealed record LaneReuseShadowRuleSummary(string Rule, int PairedLaneRows, int WouldReuseRows,
    double WouldReuseShare, double SavedLaneSeconds, int Misses);

public sealed record LaneReuseShadowReasonCount(string Family, int Lanes);

public sealed record LaneReuseShadowMissRow(DateTimeOffset RecordedAt, string GoalId, string AttemptId,
    string Lane, string? Reason, string? MissReason, string? ReferenceSource, bool FlakeConfirmed,
    string? FailedPredicate, IReadOnlyList<string> FailingClasses);

/// <summary>Observed shadow decisions and hypothetical serial savings; never authorizes lane reuse.</summary>
public sealed record LaneReuseShadowReport(DateTimeOffset Since, DateTimeOffset Until, int Gates,
    int LaneRows, int WouldReuseRows, double WouldReuseShare, double ExecutedLaneSeconds,
    double SavedLaneSeconds, double SavedShare, IReadOnlyList<LaneReuseShadowReasonCount> MustRunReasons,
    IReadOnlyList<LaneReuseShadowMissRow> Misses, int FlakeConfirmedMisses, int UntimedRecords,
    int UnreadableRecords)
{
    public IReadOnlyList<LaneReuseShadowRuleSummary> RuleSummaries { get; init; } = [];
    public int RecordsWithoutRuleV2 { get; init; }

    public static LaneReuseShadowReport Build(IEnumerable<LaneReuseShadowRecord> records,
        DateTimeOffset since, DateTimeOffset until, int untimedRecords = 0, int unreadableRecords = 0)
    {
        if (since >= until) throw new ArgumentException("The report window must have since < until.");
        var gates = records.Where(r => r.RecordedAt >= since && r.RecordedAt < until).ToArray();
        var lanes = gates.SelectMany(r => r.Lanes).ToArray();
        var reuse = lanes.Where(r => r.Decision == "would-reuse").ToArray();
        var executedSeconds = Seconds(lanes);
        var savedSeconds = Seconds(reuse);
        var reasons = lanes.Where(r => r.Decision == "must-run")
            .GroupBy(r => ReasonFamily(r.Reason), StringComparer.Ordinal)
            .Select(g => new LaneReuseShadowReasonCount(g.Key, g.Count()))
            .OrderByDescending(r => r.Lanes).ThenBy(r => r.Family, StringComparer.Ordinal).ToArray();
        var misses = gates.SelectMany(g => g.Lanes.Where(r => r.ShadowMiss).Select(r =>
                new LaneReuseShadowMissRow(g.RecordedAt.ToUniversalTime(), g.GoalId, g.AttemptId,
                    r.Lane, r.Reason, r.MissReason, r.ReferenceSource, r.FlakeConfirmed,
                    r.FailedPredicate, r.FailingClasses?.ToArray() ?? [])))
            .OrderBy(r => r.RecordedAt).ThenBy(r => r.GoalId, StringComparer.Ordinal)
            .ThenBy(r => r.AttemptId, StringComparer.Ordinal).ThenBy(r => r.Lane, StringComparer.Ordinal)
            .ToArray();
        var paired = lanes.Where(r => r.RuleV2Decision is not null && !string.IsNullOrEmpty(r.Decision)).ToArray();
        LaneReuseShadowRuleSummary Summarize(string rule, Func<LaneReuseShadowLaneRow, string?> decision)
        {
            var wouldReuse = paired.Where(r => decision(r) == "would-reuse").ToArray();
            return new(rule, paired.Length, wouldReuse.Length, Share(wouldReuse.Length, paired.Length),
                Seconds(wouldReuse), wouldReuse.Count(r => r.Executed && r.Verdict == "RED"));
        }
        return new(since.ToUniversalTime(), until.ToUniversalTime(), gates.Length, lanes.Length,
            reuse.Length, Share(reuse.Length, lanes.Length), executedSeconds, savedSeconds,
            Share(savedSeconds, executedSeconds), reasons, misses, misses.Count(r => r.FlakeConfirmed),
            untimedRecords, unreadableRecords)
        {
            RuleSummaries = [Summarize("marker-v1", r => r.Decision), Summarize("launch-contract-v2", r => r.RuleV2Decision)],
            RecordsWithoutRuleV2 = gates.Count(g => g.Lanes.Count == 0 || g.Lanes.Any(r => r.RuleV2Decision is null))
        };
    }

    private static double Seconds(IEnumerable<LaneReuseShadowLaneRow> rows) =>
        rows.Where(r => r.Executed && r.DurationMs.HasValue).Sum(r => (double)r.DurationMs!.Value) / 1000;

    private static double Share(double numerator, double denominator) => denominator == 0
        ? 0 : Math.Round(numerator / denominator, 4, MidpointRounding.AwayFromZero);

    private static string ReasonFamily(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return "unspecified";
        var colon = reason.IndexOf(':');
        if (colon < 0) return reason;
        if (reason[..colon] != "always-affected") return reason[..colon];
        var secondColon = reason.IndexOf(':', colon + 1);
        return secondColon < 0 ? reason : reason[..secondColon];
    }
}
