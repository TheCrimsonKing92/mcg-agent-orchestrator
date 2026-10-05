namespace Mcg.AgentOrchestrator.Core;

public sealed record RoundValueTotals(int LandedGoals, int LostGoals, int Rounds,
    int Productive, int ExpectedOverhead, int Wasted, double? RoundsPerLanding, double WasteShare,
    long InputTokens, long CachedInputTokens, long OutputTokens, int UsageUnreported);

public sealed record RoundValueDay(DateOnly Day, RoundValueTotals Totals);
public sealed record RoundValueCauseShare(string Cause, int Rounds, double Share);

/// <summary>Attributes every terminal goal's full round history to its final dispatch date.</summary>
public sealed record RoundValueReport(DateTimeOffset Since, DateTimeOffset Until,
    IReadOnlyList<RoundValueDay> Days, RoundValueTotals Window,
    IReadOnlyList<RoundValueCauseShare> WasteByCause, int PendingGoals, int PendingRounds)
{
    public static RoundValueReport Build(IEnumerable<Goal> goals, DateTimeOffset since, DateTimeOffset until) =>
        Build(goals, since, until, []);

    public static RoundValueReport Build(IEnumerable<Goal> goals, DateTimeOffset since, DateTimeOffset until,
        IReadOnlyCollection<AppliedRetryIntent> intents)
    {
        if (since >= until) throw new ArgumentException("The round-value window must end after it starts.");
        var groups = RoundValueClassifier.Classify(goals, intents).GroupBy(r => r.Round.GoalId).ToArray();
        bool InWindow(DateTimeOffset at) => at >= since && at < until;
        var cohort = groups.Where(g => g.First().Outcome != RoundGoalOutcome.Pending &&
            InWindow(g.Max(r => r.Round.DispatchedAt))).ToArray();
        var rounds = cohort.SelectMany(g => g).ToArray();
        var days = cohort.GroupBy(g => DateOnly.FromDateTime(g.Max(r => r.Round.DispatchedAt).UtcDateTime))
            .OrderBy(g => g.Key).Select(g => new RoundValueDay(g.Key, Total(g.SelectMany(r => r).ToArray())))
            .ToArray();
        var causes = rounds.Where(r => r.ValueClass == RoundValueClass.Wasted).GroupBy(r => r.WasteCause!)
            .Select(g => new RoundValueCauseShare(g.Key, g.Count(), (double)g.Count() / rounds.Length))
            .OrderByDescending(g => g.Rounds).ThenBy(g => g.Cause, StringComparer.Ordinal).ToArray();
        var pending = groups.Where(g => g.First().Outcome == RoundGoalOutcome.Pending)
            .SelectMany(g => g.Where(r => InWindow(r.Round.DispatchedAt))).ToArray();
        return new(since, until, days, Total(rounds), causes,
            pending.Select(r => r.Round.GoalId).Distinct(StringComparer.Ordinal).Count(), pending.Length);
    }

    private static RoundValueTotals Total(IReadOnlyList<RoundValueRecord> rounds)
    {
        var landed = rounds.Where(r => r.Outcome == RoundGoalOutcome.Landed)
            .Select(r => r.Round.GoalId).Distinct(StringComparer.Ordinal).Count();
        var lost = rounds.Where(r => r.Outcome == RoundGoalOutcome.Lost)
            .Select(r => r.Round.GoalId).Distinct(StringComparer.Ordinal).Count();
        var wasted = rounds.Count(r => r.ValueClass == RoundValueClass.Wasted);
        return new(landed, lost, rounds.Count,
            rounds.Count(r => r.ValueClass == RoundValueClass.Productive),
            rounds.Count(r => r.ValueClass == RoundValueClass.ExpectedOverhead), wasted,
            landed == 0 ? null : (double)rounds.Count / landed,
            rounds.Count == 0 ? 0 : (double)wasted / rounds.Count,
            rounds.Sum(r => r.Round.InputTokens ?? 0), rounds.Sum(r => r.Round.CachedInputTokens ?? 0),
            rounds.Sum(r => r.Round.OutputTokens ?? 0), rounds.Count(r => !r.Round.UsageReported));
    }
}
