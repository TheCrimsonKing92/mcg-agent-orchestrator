using Mcg.AgentOrchestrator.Core;

public sealed class KernelIngestNewGoalsTests
{
    [Xunit.Fact(DisplayName = "IngestNewGoals_adds_unknown_goals_and_leaves_in-flight_goals_untouched")]
    public void IngestNewGoalsAddsUnknownAndPreservesExisting()
    {
        var clock = new FakeClock();

        // Live kernel (e.g. the daemon loop's): goal A is activated and carries in-flight task state.
        var live = new AgentOrchestratorKernel(clock);
        var goalA = live.CreateGoal("Goal A");
        live.ActivateGoal(goalA.Id, DefaultAgents());
        var aTaskCountBefore = live.Goals.First(g => g.Id == goalA.Id).Tasks.Count;
        Xunit.Assert.True(aTaskCountBefore > 0);

        // Store snapshot: a STALE copy of A (never activated, no tasks) plus a brand-new goal B.
        var store = new AgentOrchestratorKernel(clock);
        store.CreateGoal(goalA.Id, "Goal A");
        var goalB = store.CreateGoal("Goal B");
        var snapshot = store.ExportSnapshot();

        var ingested = live.IngestNewGoals(snapshot);

        Xunit.Assert.Equal(1, ingested);
        Xunit.Assert.Contains(live.Goals, g => g.Id == goalB.Id);
        // A is left exactly as the live kernel had it — NOT overwritten by the stale snapshot copy.
        Xunit.Assert.Equal(aTaskCountBefore, live.Goals.First(g => g.Id == goalA.Id).Tasks.Count);
    }

    [Xunit.Fact(DisplayName = "IngestNewGoals_returns_zero_when_no_new_goals")]
    public void IngestNewGoalsReturnsZeroWhenNothingNew()
    {
        var clock = new FakeClock();
        var live = new AgentOrchestratorKernel(clock);
        var goalA = live.CreateGoal("Goal A");

        var store = new AgentOrchestratorKernel(clock);
        store.CreateGoal(goalA.Id, "Goal A");

        Xunit.Assert.Equal(0, live.IngestNewGoals(store.ExportSnapshot()));
        Xunit.Assert.Single(live.Goals);
    }
}
