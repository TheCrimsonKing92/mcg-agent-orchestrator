using Mcg.AgentOrchestrator.Core;

public sealed class RoundValueReviewReRoundTests
{
    [Theory]
    [InlineData("finding evidence-on-demand: receipt", null, RoundValueClass.ExpectedOverhead, null)]
    [InlineData("Dispatch hit a recoverable subscription usage limit", null, RoundValueClass.ExpectedOverhead, null)]
    [InlineData("free text", OperatorActorKind.Human, RoundValueClass.ExpectedOverhead, null)]
    [InlineData("free text", OperatorActorKind.Agent, RoundValueClass.ExpectedOverhead, null)]
    [InlineData("Invalidated upstream", null, RoundValueClass.Wasted, "unchanged-commit-review")]
    [InlineData("free text", null, RoundValueClass.Wasted, "unchanged-commit-review")]
    public void SameCommitReviewUsesItsOwnRetryCause(string message, OperatorActorKind? actor,
        RoundValueClass expectedValue, string? expectedCause)
    {
        var round = ClassifySecondRound(AgentRole.Reviewer, "c1", message, actor);

        Assert.Equal(expectedValue, round.ValueClass);
        Assert.Equal(expectedCause, round.WasteCause);
    }

    [Theory]
    [InlineData(AgentRole.Developer, "c1")]
    [InlineData(AgentRole.Reviewer, "c2")]
    public void HumanRetryOutsideSameCommitReviewerRemainsManualRoute(AgentRole role, string secondCommit)
    {
        var round = ClassifySecondRound(role, secondCommit, "free text", OperatorActorKind.Human);

        Assert.Equal(RoundValueClass.Wasted, round.ValueClass);
        Assert.Equal("manual-route", round.WasteCause);
    }

    [Theory]
    [InlineData(GoalStatus.Cancelled, true, "abandoned-goal")]
    [InlineData(GoalStatus.Completed, false, "orphaned-dispatch")]
    public void LostGoalAndUnknownRoundTakePrecedenceOverExpectedReviewRerun(
        GoalStatus status, bool completed, string expectedCause)
    {
        var round = ClassifySecondRound(AgentRole.Reviewer, "c1", "free text", OperatorActorKind.Human,
            status, completed);

        Assert.Equal(RoundValueClass.Wasted, round.ValueClass);
        Assert.Equal(expectedCause, round.WasteCause);
    }

    private static RoundValueRecord ClassifySecondRound(AgentRole role, string secondCommit,
        string message, OperatorActorKind? actor, GoalStatus status = GoalStatus.Completed, bool completed = true)
    {
        var retryAt = RoundValueFixture.At("2026-09-24T02:00:00Z");
        var events = new List<ProgressEventSnapshot>
        {
            new("goal", "rev", ProgressKind.TaskCompleted, "done", RoundValueFixture.At("2026-09-24T01:30:00Z")),
            new("goal", "rev", ProgressKind.TaskRetried, message, retryAt)
        };
        if (completed)
            events.Add(new("goal", "rev", ProgressKind.TaskCompleted, "done",
                RoundValueFixture.At("2026-09-24T03:30:00Z")));
        var goal = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
        [
            new GoalSnapshot("goal", "Review rerun", status,
            [
                RoundValueFixture.Task("rev", role, WorkTaskStatus.Completed,
                    RoundValueFixture.Dispatch("2026-09-24T01:00:00Z", "c1"),
                    RoundValueFixture.Dispatch("2026-09-24T03:00:00Z", secondCommit))
            ], events)
        ], [])).Goals.Single();
        AppliedRetryIntent[] intents = actor is { } kind ? [new("rev", retryAt, kind)] : [];

        return Assert.Single(RoundValueClassifier.Classify([goal], intents).Where(r => r.Round.RoundIndex == 2));
    }
}
