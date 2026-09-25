namespace Mcg.AgentOrchestrator.Core;

public sealed record OwnerDigestGoalInput(
    string GoalId, DateTimeOffset? LandedAt, string? LandingSha,
    IReadOnlyList<ProgressEvent> Timeline);

public sealed record OwnerDigestCanaryReceipt(
    string? LandingSha, DateTimeOffset OccurredAt, bool Passed);

public sealed record OwnerDigestActorTotals(int Human, int Agent, int Other)
{
    public int Total => Human + Agent + Other;
}

public sealed record OwnerDigestHours(double Human, double Agent, double Other)
{
    public double Total => Human + Agent + Other;
}

public sealed record OwnerDigestGoalRow(
    string GoalId, DateTimeOffset LandedAt, string? LandingSha,
    OwnerDigestActorTotals Interventions, string LandingStatus,
    double? TailHours, OwnerDigestHours MechanicalHours,
    double UnresolvedHoldHours, double ObservedAfterLandingHours);

public sealed record OwnerDigestTotals(
    int LandedGoals, OwnerDigestActorTotals Interventions,
    double? MeanInterventionsPerLanding, int CorrectLandings, int Escapes,
    int Pending, double? CorrectLandingRate, double? TailMedianHours,
    double? TailP90Hours, int KnownTailCount, int UnknownTailCount,
    OwnerDigestHours MechanicalHours, double UnresolvedHoldHours);

public sealed record OwnerDigestResult(
    DateTimeOffset Since, DateTimeOffset Until, string Reverts,
    IReadOnlyList<OwnerDigestGoalRow> Goals, OwnerDigestTotals Totals,
    int NonLandedGoalsWithInterventions, int MalformedLifecycleLines = 0);

public static class OwnerDigestReport
{
    public static OwnerDigestResult Build(
        IReadOnlyList<OwnerDigestGoalInput> goals,
        IReadOnlyList<OwnerDigestCanaryReceipt> receipts,
        IClock clock, DateTimeOffset? since = null, DateTimeOffset? until = null,
        int malformedLifecycleLines = 0)
    {
        ArgumentNullException.ThrowIfNull(goals);
        ArgumentNullException.ThrowIfNull(receipts);
        ArgumentNullException.ThrowIfNull(clock);
        var end = (until ?? clock.UtcNow).ToUniversalTime();
        var start = (since ?? end.AddHours(-24)).ToUniversalTime();
        if (start >= end)
            throw new ArgumentException("--since must precede --until.");

        var landed = goals.Where(g => g.LandedAt is { } at && at >= start && at < end)
            .OrderBy(g => g.LandedAt).ThenBy(g => g.GoalId, StringComparer.Ordinal).ToArray();
        var rows = landed.Select(g => BuildRow(g, receipts, end)).ToArray();
        var nonLanded = goals.Count(g => !landed.Contains(g) &&
            g.Timeline.Any(e => e.OperatorIntentApplied is not null && e.OccurredAt >= start && e.OccurredAt < end));
        var interventionTotals = new OwnerDigestActorTotals(
            rows.Sum(r => r.Interventions.Human), rows.Sum(r => r.Interventions.Agent),
            rows.Sum(r => r.Interventions.Other));
        var hourTotals = new OwnerDigestHours(
            rows.Sum(r => r.MechanicalHours.Human), rows.Sum(r => r.MechanicalHours.Agent),
            rows.Sum(r => r.MechanicalHours.Other));
        var tails = rows.Where(r => r.TailHours.HasValue).Select(r => r.TailHours!.Value)
            .Order().ToArray();
        var correct = rows.Count(r => r.LandingStatus == "correct");
        var escapes = rows.Count(r => r.LandingStatus == "escape");
        var totals = new OwnerDigestTotals(rows.Length, interventionTotals,
            rows.Length == 0 ? null : (double)interventionTotals.Total / rows.Length,
            correct, escapes, rows.Length - correct - escapes,
            correct + escapes == 0 ? null : (double)correct / (correct + escapes),
            Percentile(tails, .5), Percentile(tails, .9), tails.Length,
            rows.Length - tails.Length, hourTotals, rows.Sum(r => r.UnresolvedHoldHours));
        return new OwnerDigestResult(start, end, "not tracked", rows, totals,
            nonLanded, malformedLifecycleLines);
    }

    private static OwnerDigestGoalRow BuildRow(
        OwnerDigestGoalInput goal, IReadOnlyList<OwnerDigestCanaryReceipt> receipts,
        DateTimeOffset end)
    {
        var landedAt = goal.LandedAt!.Value;
        var events = goal.Timeline.Where(e => e.OccurredAt <= landedAt)
            .OrderBy(e => e.OccurredAt).ToArray();
        var intents = events.Where(e => e.OperatorIntentApplied is not null)
            .DistinctBy(e => e.OperatorIntentApplied!.IntentId).ToArray();
        var interventions = new OwnerDigestActorTotals(
            intents.Count(e => e.OperatorIntentApplied!.ActorKind == OperatorActorKind.Human),
            intents.Count(e => e.OperatorIntentApplied!.ActorKind == OperatorActorKind.Agent),
            intents.Count(e => e.OperatorIntentApplied!.ActorKind is not OperatorActorKind.Human and not OperatorActorKind.Agent));
        var verified = events.FirstOrDefault(e =>
            string.Equals(e.TickOutcome?.LifecycleState, "Verified", StringComparison.OrdinalIgnoreCase));
        var tail = verified is null ? (double?)null : (landedAt - verified.OccurredAt).TotalHours;

        DateTimeOffset? open = null;
        double human = 0, agent = 0, other = 0;
        foreach (var evt in events)
        {
            if (open is null && IsOperatorEscalation(evt.TickOutcome))
                open = evt.OccurredAt;
            if (open is null || evt.OperatorIntentApplied is not { } intent)
                continue;
            var hours = (evt.OccurredAt - open.Value).TotalHours;
            switch (intent.ActorKind)
            {
                case OperatorActorKind.Human: human += hours; break;
                case OperatorActorKind.Agent: agent += hours; break;
                default: other += hours; break;
            }
            open = null;
        }

        var eligible = receipts.Where(r => r.OccurredAt >= landedAt && r.OccurredAt < end).ToArray();
        var matching = eligible.Where(r => !string.IsNullOrWhiteSpace(goal.LandingSha) &&
            string.Equals(r.LandingSha, goal.LandingSha, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.OccurredAt).ToArray();
        // A coalesced canary may be recorded for a later SHA; use the first later receipt when no exact SHA exists.
        if (matching.Length == 0)
            matching = eligible.OrderBy(r => r.OccurredAt).Take(1).ToArray();
        var status = matching.Length == 0 ? "pending" :
            matching.Any(r => !r.Passed) ? "escape" : "correct";
        return new OwnerDigestGoalRow(goal.GoalId, landedAt, goal.LandingSha,
            interventions, status, tail, new OwnerDigestHours(human, agent, other),
            open is null ? 0 : (landedAt - open.Value).TotalHours,
            (end - landedAt).TotalHours);
    }

    private static bool IsOperatorEscalation(ConductorTickOutcomePayload? tick) =>
        tick is not null &&
        string.Equals(tick.OutcomeKind, "Escalated", StringComparison.OrdinalIgnoreCase) &&
        tick.LifecycleState is "Failed" or "AwaitingHumanInput" or "AwaitingClarification" or
            "Verified" or "AcceptanceFailed";

    private static double? Percentile(double[] values, double proportion) =>
        values.Length == 0 ? null : values[Math.Max(0, (int)Math.Ceiling(values.Length * proportion) - 1)];
}
