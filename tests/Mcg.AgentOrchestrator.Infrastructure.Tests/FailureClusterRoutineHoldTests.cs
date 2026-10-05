using Mcg.AgentOrchestrator.Core;
using Xunit;

public sealed class FailureClusterRoutineHoldTests
{
    [Fact]
    public void LiveRoutineHoldsLeaveOnlyTheEscalation()
    {
        var rows = FailureClusterReport.Build(
            FailureClusterFixtureData.ReadGoals("goal-events-routine-holds.jsonl"), [], [],
            DateTimeOffset.Parse("2026-10-01T00:00:00Z"), DateTimeOffset.Parse("2026-10-02T00:00:00Z"));

        var escalation = Assert.Single(rows);
        Assert.Equal("GoalEscalated", escalation.EventKind);
        Assert.Equal("escalated at WorkspaceReady — dirty worktree", escalation.MessageFamily);
        Assert.Equal(1, escalation.RootEvents);
    }

    [Theory]
    [InlineData("GoalEscalated")]
    [InlineData("GoalLifecycleDecision")]
    public void EscalationsWithRoutineReasonPrefixesStillRank(string kind)
    {
        var at = FailureClusterTestData.Since;
        string[] reasons = ["acceptance verification still running in background", "Worker process running",
            "Acceptance cohort gate is running in the background", "Acceptance cohort gate owns this member"];
        var rows = FailureClusterReport.Build(reasons.Select(reason => new FailureClusterGoalEvent(
            kind, "escalated at Verified — " + reason, "goal", null, at)), [], [], at, at.AddDays(1));

        Assert.Equal(reasons.Length, rows.Count);
        Assert.All(rows, row => Assert.StartsWith("escalated at Verified — ", row.MessageFamily));
    }
}
