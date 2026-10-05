using Mcg.AgentOrchestrator.Core;

public sealed class WorkerRoundLedgerPreparedDispatchTests
{
    [Fact]
    public void PreparedDispatchWithoutStartIsSkippedAndOriginalIndexIsPreserved()
    {
        var goal = CreateGoal("2026-09-24T01:00:06Z");

        var round = Assert.Single(WorkerRoundLedger.FromGoal(goal));

        Assert.Equal(2, round.RoundIndex);
        Assert.Equal(WorkerRoundStopCause.Completed, round.StopCause);
        var value = Assert.Single(RoundValueClassifier.Classify([goal]));
        Assert.Equal(2, value.Round.RoundIndex);
        Assert.NotEqual("orphaned-dispatch", value.WasteCause);
    }

    [Fact]
    public void HistoryWithoutAnyStartEvidenceKeepsBothDispatchRounds()
    {
        var rounds = WorkerRoundLedger.FromGoal(CreateGoal(null));

        Assert.Equal([1, 2], rounds.Select(round => round.RoundIndex));
        Assert.Equal([WorkerRoundStopCause.Unknown, WorkerRoundStopCause.Completed],
            rounds.Select(round => round.StopCause));
    }

    [Theory]
    [InlineData("2026-09-24T01:00:00Z", 1)]
    [InlineData("2026-09-24T01:00:04Z", 1)]
    [InlineData("2026-09-24T01:00:05Z", 2)]
    public void StartBelongsToInclusiveLowerExclusiveUpperDispatchWindow(string startedAt, int index)
    {
        var round = Assert.Single(WorkerRoundLedger.FromGoal(CreateGoal(startedAt)));

        Assert.Equal(index, round.RoundIndex);
    }

    private static Goal CreateGoal(string? startedAt)
    {
        var events = new List<ProgressEventSnapshot>();
        if (startedAt is not null)
            events.Add(new("goal", "dev", ProgressKind.TaskProcessStarted, "Started process 1: cmd",
                RoundValueFixture.At(startedAt)));
        events.Add(new("goal", "dev", ProgressKind.TaskCompleted, "done",
            RoundValueFixture.At("2026-09-24T01:30:00Z")));
        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
        [
            new GoalSnapshot("goal", "Prepared dispatch", GoalStatus.Completed,
            [
                RoundValueFixture.Task("dev", AgentRole.Developer, WorkTaskStatus.Completed,
                    RoundValueFixture.Dispatch("2026-09-24T01:00:00Z"),
                    RoundValueFixture.Dispatch("2026-09-24T01:00:05Z"))
            ], events)
        ], [])).Goals.Single();
    }
}
