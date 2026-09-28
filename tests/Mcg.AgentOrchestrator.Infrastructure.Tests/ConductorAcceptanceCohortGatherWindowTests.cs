using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorAcceptanceCohortGatherWindowTests
{
    [Xunit.Fact]
    public void ExactBoundaryReleasesAndDoesNotRearm()
    {
        var window = new ConductorAcceptanceCohortGatherWindow();
        var first = DateTimeOffset.Parse("2026-09-28T14:00:00Z");
        window.ObserveReadyGoals(["ready"]);
        Assert.Equal(480, window.Evaluate("ready", ["partner"], first, 480).RemainingSeconds);
        Assert.Equal(1, window.Evaluate("ready", ["partner"], first.AddMilliseconds(479001), 480).RemainingSeconds);
        Assert.False(window.Evaluate("ready", ["partner"], first.AddSeconds(480), 480).Hold);
        Assert.False(window.Evaluate("ready", ["partner"], first.AddSeconds(481), 480).Hold);
    }

    [Xunit.Fact]
    public void ReadyStreakEndAllowsNewWindow()
    {
        var window = new ConductorAcceptanceCohortGatherWindow();
        var first = DateTimeOffset.Parse("2026-09-28T14:00:00Z");
        window.ObserveReadyGoals(["ready"]);
        Assert.False(window.Evaluate("ready", [], first, 480).Hold);
        window.ObserveReadyGoals([]);
        window.ObserveReadyGoals(["ready"]);
        Assert.True(window.Evaluate("ready", ["partner"], first.AddSeconds(600), 480).Hold);
    }
}
