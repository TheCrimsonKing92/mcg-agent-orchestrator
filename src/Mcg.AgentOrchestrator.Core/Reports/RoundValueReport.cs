namespace Mcg.AgentOrchestrator.Core;

public sealed record RoundValueTotals(int LandedGoals, int LostGoals, int Rounds,
    int Productive, int ExpectedOverhead, int Wasted, double? RoundsPerLanding, double WasteShare,
    long InputTokens, long CachedInputTokens, long OutputTokens, int UsageUnreported);

public sealed record RoundValueDay(DateOnly Day, RoundValueTotals Totals);
public sealed record RoundValueCauseShare(string Cause, int Rounds, double Share);
public sealed record RoundValueCauseDelta(string Cause, int CurrentRounds, double CurrentShare,
    int BaselineRounds, double BaselineShare, int RoundsDelta, double ShareDelta);
public sealed record RoundValueBaselineComparison(DateTimeOffset Since, DateTimeOffset Until,
    double? CurrentRoundsPerLanding, double? BaselineRoundsPerLanding,
    IReadOnlyList<RoundValueCauseDelta> Causes);
public sealed record RoundValueCascadeRoute(string Decision, int Rounds, int Productive, int Overhead, int Wasted);

/// <summary>Attributes every terminal goal's full round history to its final dispatch date.</summary>
public sealed record RoundValueReport(DateTimeOffset Since, DateTimeOffset Until,
    IReadOnlyList<RoundValueDay> Days, RoundValueTotals Window,
    IReadOnlyList<RoundValueCauseShare> WasteByCause, int PendingGoals, int PendingRounds)
{
    public IReadOnlyList<RoundValueCascadeRoute> CascadeRoutes { get; init; } = [];
    public RoundValueBaselineComparison? Baseline { get; init; }

    public static RoundValueReport Build(IEnumerable<Goal> goals, DateTimeOffset since, DateTimeOffset until) =>
        Build(goals, since, until, []);

    public static RoundValueReport Build(IEnumerable<Goal> goals, DateTimeOffset since, DateTimeOffset until,
        DateTimeOffset baselineSince, DateTimeOffset baselineUntil) =>
        Build(goals, since, until, baselineSince, baselineUntil, []);

    public static RoundValueReport Build(IEnumerable<Goal> goals, DateTimeOffset since, DateTimeOffset until,
        DateTimeOffset baselineSince, DateTimeOffset baselineUntil, IReadOnlyCollection<AppliedRetryIntent> intents)
    {
        var materializedGoals = goals.ToArray();
        var current = Build(materializedGoals, since, until, intents);
        var baseline = Build(materializedGoals, baselineSince, baselineUntil, intents);
        var currentCauses = current.WasteByCause.ToDictionary(c => c.Cause, StringComparer.Ordinal);
        var baselineCauses = baseline.WasteByCause.ToDictionary(c => c.Cause, StringComparer.Ordinal);
        var causes = currentCauses.Keys.Union(baselineCauses.Keys, StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal).Select(cause =>
            {
                currentCauses.TryGetValue(cause, out var now);
                baselineCauses.TryGetValue(cause, out var before);
                var currentRounds = now?.Rounds ?? 0;
                var baselineRounds = before?.Rounds ?? 0;
                var currentShare = now?.Share ?? 0;
                var baselineShare = before?.Share ?? 0;
                return new RoundValueCauseDelta(cause, currentRounds, currentShare, baselineRounds, baselineShare,
                    currentRounds - baselineRounds, currentShare - baselineShare);
            }).ToArray();
        return current with
        {
            Baseline = new(baselineSince, baselineUntil, current.Window.RoundsPerLanding,
                baseline.Window.RoundsPerLanding, causes)
        };
    }

    public static RoundValueReport Build(IEnumerable<Goal> goals, DateTimeOffset since, DateTimeOffset until,
        IReadOnlyCollection<AppliedRetryIntent> intents)
    {
        if (since >= until) throw new ArgumentException("The round-value window must end after it starts.");
        var materializedGoals = goals.ToArray();
        var groups = RoundValueClassifier.Classify(materializedGoals, intents).GroupBy(r => r.Round.GoalId).ToArray();
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
            pending.Select(r => r.Round.GoalId).Distinct(StringComparer.Ordinal).Count(), pending.Length)
        {
            CascadeRoutes = CascadeTotals(materializedGoals, rounds)
        };
    }

    private static IReadOnlyList<RoundValueCascadeRoute> CascadeTotals(IReadOnlyList<Goal> goals,
        IReadOnlyList<RoundValueRecord> rounds)
    {
        var dispatches = goals.SelectMany(g => g.Tasks.SelectMany(t => t.DispatchHistory.Select(d =>
            (Key: (g.Id.Value, t.Id.Value, d.DispatchedAt), Dispatch: d))))
            .GroupBy(d => d.Key).ToDictionary(g => g.Key, g => g.Last().Dispatch);
        var routed = rounds.Select(r => (Round: r, Decision:
            dispatches.TryGetValue((r.Round.GoalId, r.Round.TaskId, r.Round.DispatchedAt), out var dispatch) &&
            CascadeRouteMarker.TryParse(dispatch.ModelSelectionReason, out var marker) ? marker.Decision : null)).ToArray();
        return new[] { CascadeRouteMarker.Cheap, CascadeRouteMarker.Escalated, CascadeRouteMarker.Primary }
            .Select(decision => (Decision: decision, Rounds: routed.Where(r => r.Decision == decision).ToArray()))
            .Where(g => g.Rounds.Length > 0).Select(g => new RoundValueCascadeRoute(g.Decision, g.Rounds.Length,
                g.Rounds.Count(r => r.Round.ValueClass == RoundValueClass.Productive),
                g.Rounds.Count(r => r.Round.ValueClass == RoundValueClass.ExpectedOverhead),
                g.Rounds.Count(r => r.Round.ValueClass == RoundValueClass.Wasted))).ToArray();
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
