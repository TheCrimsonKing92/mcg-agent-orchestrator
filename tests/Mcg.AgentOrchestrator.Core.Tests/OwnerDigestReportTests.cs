using Mcg.AgentOrchestrator.Core;

public sealed class OwnerDigestReportTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-24T00:00:00Z");
    private static readonly DateTimeOffset End = Start.AddDays(1);

    [Fact]
    public void TwoLandedGoalsHaveExactPerGoalAndTotalMetrics()
    {
        var goals = new[]
        {
            new OwnerDigestGoalInput("a", Start.AddHours(8), "aaaa",
            [
                Tick("a", 2, "Escalated", "AwaitingHumanInput"),
                Intent("a", 3.5, "h1", OperatorActorKind.Human),
                Tick("a", 4, "Executed", "Verified"),
                Tick("a", 5, "Escalated", "Verified"),
                Intent("a", 6, "a1", OperatorActorKind.Agent),
                Intent("a", 7, "h2", OperatorActorKind.Human)
            ]),
            new OwnerDigestGoalInput("b", Start.AddHours(12), "bbbb",
                [Tick("b", 10, "Executed", "Verified")]),
            new OwnerDigestGoalInput("c", null, null,
                [Intent("c", 14, "h3", OperatorActorKind.Human)]),
            new OwnerDigestGoalInput("d", Start.AddHours(-4), "dddd", [])
        };
        var receipts = new[]
        {
            new OwnerDigestCanaryReceipt("aaaa", Start.AddHours(9), true),
            new OwnerDigestCanaryReceipt("bbbb", Start.AddHours(13), false)
        };

        var result = OwnerDigestReport.Build(goals, receipts, new FixedClock(End), Start, End);

        Assert.Equal(2, result.Goals.Count);
        var a = result.Goals.Single(r => r.GoalId == "a");
        Assert.Equal(new OwnerDigestActorTotals(2, 1, 0), a.Interventions);
        Assert.Equal("correct", a.LandingStatus);
        Assert.Equal(4, a.TailHours);
        Assert.Equal(new OwnerDigestHours(1.5, 1, 0), a.MechanicalHours);
        var b = result.Goals.Single(r => r.GoalId == "b");
        Assert.Equal("escape", b.LandingStatus);
        Assert.Equal(2, b.TailHours);
        Assert.Equal(0, b.Interventions.Total);
        Assert.Equal(2, result.Totals.LandedGoals);
        Assert.Equal(new OwnerDigestActorTotals(2, 1, 0), result.Totals.Interventions);
        Assert.Equal(1.5, result.Totals.MeanInterventionsPerLanding);
        Assert.Equal(1, result.Totals.CorrectLandings);
        Assert.Equal(1, result.Totals.Escapes);
        Assert.Equal(0.5, result.Totals.CorrectLandingRate);
        Assert.Equal(2, result.Totals.TailMedianHours);
        Assert.Equal(4, result.Totals.TailP90Hours);
        Assert.Equal(new OwnerDigestHours(1.5, 1, 0), result.Totals.MechanicalHours);
        Assert.Equal(1, result.NonLandedGoalsWithInterventions);
    }

    [Fact]
    public void MissingCanaryAndVerifiedStayPendingAndUnknown()
    {
        var goal = new OwnerDigestGoalInput("a", Start.AddHours(3), "aaaa",
            [Tick("a", 1, "Escalated", "Failed"),
             Intent("a", 2, "other", (OperatorActorKind)0)]);
        var result = OwnerDigestReport.Build([goal], [], new FixedClock(End), Start, End);
        var row = Assert.Single(result.Goals);
        Assert.Equal("pending", row.LandingStatus);
        Assert.Null(row.TailHours);
        Assert.Equal(1, row.Interventions.Other);
        Assert.Equal(1, row.MechanicalHours.Other);
        Assert.Equal(1, result.Totals.UnknownTailCount);
        Assert.Null(result.Totals.CorrectLandingRate);
    }

    [Fact]
    public void OpenEscalationIsSeparateAndWindowIsHalfOpen()
    {
        var goals = new[]
        {
            new OwnerDigestGoalInput("start", Start, "a", []),
            new OwnerDigestGoalInput("inside", Start.AddHours(3), "b",
                [Tick("inside", 1, "Escalated", "AcceptanceFailed")]),
            new OwnerDigestGoalInput("end", End, "c", [])
        };
        var result = OwnerDigestReport.Build(goals, [], new FixedClock(End), Start, End);
        Assert.Equal(2, result.Totals.LandedGoals);
        Assert.Equal(2, result.Goals.Single(r => r.GoalId == "inside").UnresolvedHoldHours);
        Assert.Equal(0, result.Totals.MechanicalHours.Total);
        Assert.Throws<ArgumentException>(() => OwnerDigestReport.Build(goals, [], new FixedClock(End), End, Start));
        Assert.Equal(Start, OwnerDigestReport.Build(goals, [], new FixedClock(End)).Since);
    }

    private static ProgressEvent Tick(string id, double hour, string outcome, string state) =>
        new(new GoalId(id), null, ProgressKind.GoalPolicyDecision, "tick", Start.AddHours(hour),
            TickOutcome: new ConductorTickOutcomePayload(outcome, state, null));

    private static ProgressEvent Intent(string id, double hour, string intentId, OperatorActorKind kind) =>
        new(new GoalId(id), null, ProgressKind.GoalPolicyDecision, "intent", Start.AddHours(hour),
            OperatorIntentApplied: new OperatorIntentAppliedPayload(intentId, "retry", null,
                "operator", "cli", null, kind));

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;
}
