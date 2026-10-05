using Mcg.AgentOrchestrator.Core;

public sealed class WorkerRoundLedgerHumanInputTests
{
    [Fact]
    public void HumanInputRequestEndsRoundAsClarificationWithoutWaste()
    {
        var goal = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
        [
            new GoalSnapshot("goal", "Clarification", GoalStatus.Active,
            [
                RoundValueFixture.Task("dev", AgentRole.Developer, WorkTaskStatus.Completed,
                    RoundValueFixture.Dispatch("2026-09-24T01:00:00Z"),
                    RoundValueFixture.Dispatch("2026-09-24T03:00:00Z"))
            ],
            [
                new("goal", "dev", ProgressKind.HumanInputRequested, "question",
                    RoundValueFixture.At("2026-09-24T01:30:00Z")),
                new("goal", "dev", ProgressKind.HumanInputReceived, "answered",
                    RoundValueFixture.At("2026-09-24T02:30:00Z")),
                new("goal", "dev", ProgressKind.TaskCompleted, "done",
                    RoundValueFixture.At("2026-09-24T03:30:00Z"))
            ])
        ], [])).Goals.Single();

        var rounds = WorkerRoundLedger.FromGoal(goal);

        Assert.Equal([WorkerRoundStopCause.Clarification, WorkerRoundStopCause.Completed],
            rounds.Select(round => round.StopCause));
        var first = Assert.Single(RoundValueClassifier.Classify([goal]).Where(r => r.Round.RoundIndex == 1));
        Assert.NotEqual(RoundValueClass.Wasted, first.ValueClass);
        Assert.Null(first.WasteCause);
    }
}
