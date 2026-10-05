using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each test owns its in-memory snapshot and fixed timestamps.
public sealed class WorkerRoundLedgerDuplicateDispatchTests
{
    [Fact]
    public void FromGoal_DuplicateDispatch_CountsDistinctRoundsWithCorrectEndings()
    {
        var goal = RoundValueFixture.Create().Goals.Single(g => g.Objective == "A");
        Assert.Equal(3, goal.Tasks.Single(t => t.Id.Value == "a-test").DispatchHistory.Count);

        var rounds = WorkerRoundLedger.FromGoal(goal).Where(r => r.TaskId == "a-test").ToArray();

        Assert.Equal(2, rounds.Length);
        Assert.Equal([1, 2], rounds.Select(r => r.RoundIndex));
        Assert.Equal([WorkerRoundStopCause.Failed, WorkerRoundStopCause.Completed], rounds.Select(r => r.StopCause));
        Assert.Equal([ReworkCauseFamily.FirstPass, ReworkCauseFamily.FlakeOrApparatus], rounds.Select(r => r.ReworkCause));
    }

    [Fact]
    public void FromGoal_DuplicateWithDifferentUsage_KeepsFirstEntry()
    {
        var first = RoundValueFixture.Dispatch("2026-09-24T01:00:00Z", "first", 10, 2, 3);
        var duplicate = RoundValueFixture.Dispatch("2026-09-24T01:00:00Z", "duplicate", 99, 99, 99);
        var task = RoundValueFixture.Task("dev", AgentRole.Developer, WorkTaskStatus.Completed, first, duplicate);
        var goal = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot("goal", "work", GoalStatus.Completed, [task],
                [new ProgressEventSnapshot("goal", "dev", ProgressKind.TaskCompleted, "done", first.DispatchedAt.AddMinutes(1))])], [])).Goals.Single();

        var round = Assert.Single(WorkerRoundLedger.FromGoal(goal));

        Assert.Equal(1, round.RoundIndex);
        Assert.Equal(WorkerRoundStopCause.Completed, round.StopCause);
        Assert.Equal(10, round.InputTokens);
        Assert.Equal(2, round.CachedInputTokens);
        Assert.Equal(3, round.OutputTokens);
    }
}
