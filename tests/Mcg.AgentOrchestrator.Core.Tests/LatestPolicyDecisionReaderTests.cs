using Mcg.AgentOrchestrator.Core;

// Parallel-safe: pure projections and a per-test kernel with an injected clock.
public sealed class LatestPolicyDecisionReaderTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-24T00:00:00Z");

    [Fact]
    public void Read_UnorderedEvents_SelectsMostRecentDecisionByEventTime()
    {
        var latest = LatestPolicyDecisionReader.Read(
        [
            Tick(3, "Executed", Decision("Landing", "Execute", 3, "candidate verified")),
            Tick(4, "Held", null),
            Tick(1, "Held", Decision("DispatchStart", "Hold", 1, "capacity pending")),
            Tick(2, "Escalated", Decision("Verified", "Escalate", 2, "gate failed"))
        ]);
        Assert.Equal(new LatestPolicyDecision("Landing", "Execute", 3, "candidate verified",
            Start.AddHours(3)), latest);
    }

    [Fact]
    public void Read_EqualEventTimes_LaterSequencePositionWins()
    {
        var latest = LatestPolicyDecisionReader.Read(
        [
            Tick(2, "Held", Decision("Verifying", "Hold", 1, "pending")),
            Tick(2, "Executed", Decision("Landing", "Execute", 3, "ready"))
        ]);
        Assert.Equal("Landing", latest?.Stage);
        Assert.Equal("ready", latest?.DiscriminatingEvidence);
    }

    [Fact]
    public void Read_NoDecision_ReturnsNull()
    {
        Assert.Null(LatestPolicyDecisionReader.Read([]));
        Assert.Null(LatestPolicyDecisionReader.Read(
        [
            Tick(1, "Held", null),
            new(new GoalId("goal"), null, ProgressKind.GoalPolicyDecision,
                "legacy narrative only", Start.AddHours(2))
        ]));
    }

    [Fact]
    public void Read_RecordedTickReplay_LaterUndecidedTickKeepsPriorDecision()
    {
        var clock = new MutableClock(Start);
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal(new GoalId("replay"), "Replay real tick timeline");
        void Record(int hour, string outcome, PolicyDecisionRecord? decision)
        {
            clock.UtcNow = Start.AddHours(hour);
            kernel.RecordGoalPolicyDecision(goal.Id, "tick",
                new ConductorTickOutcomePayload(outcome, "Verified",
                    outcome == "Escalated" ? "AcceptanceVerificationFailed" : null, decision));
        }
        Record(1, "Held", Decision("Verifying", "Hold", 1, "pending"));
        Record(2, "Escalated", Decision("Verified", "Escalate", 2, "gate failed"));
        Record(3, "Executed", Decision("Landing", "Execute", 3, "candidate verified"));
        Record(4, "Held", null);
        Assert.Equal(new[] { "Held", "Escalated", "Executed", "Held" },
            goal.Timeline.Where(e => e.TickOutcome is not null).Select(e => e.TickOutcome!.OutcomeKind));
        var latest = LatestPolicyDecisionReader.Read(goal.Timeline);
        Assert.Equal(new LatestPolicyDecision("Landing", "Execute", 3, "candidate verified",
            Start.AddHours(3)), latest);
    }

    private static ProgressEvent Tick(int hour, string outcome, PolicyDecisionRecord? decision) =>
        new(new GoalId("goal"), null, ProgressKind.GoalPolicyDecision, "tick",
            Start.AddHours(hour), TickOutcome: new(outcome, "Verified", null, decision));

    private static PolicyDecisionRecord Decision(string stage, string action, int rung, string evidence) =>
        new(stage, action, rung, evidence, "Policy reason", []);

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
