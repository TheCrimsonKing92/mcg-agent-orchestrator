using Mcg.AgentOrchestrator.Core;

// Parallel-safe: all fixtures are in-memory snapshots with fixed timestamps.
public sealed class ReworkCauseUnclassifiedPrefixTests
{
    private static readonly string[] RetryMessages =
    [
        """
        worker-build-check-failed automatic recovery 1/2:
        checkpoint commit 2c09fd5e69aa4237935bd57c59f0c0a192aec0d2
        Fix only what the build reports below, and run scripts/Invoke-WorkerBuildCheck.ps1 after the last edit.
        worker build errors:
        """,
        "Auto-retry real worker/command failure for task d0cdca6d (attempt 1/2); Failed command: codex exec --json --skip-git-repo-check --model 'gpt-6.1-sol'; Failure evidence: Developer declared the assigned implementation scope incomplete.",
        "developer-completion structural pre-check failed: src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs has 2607 lines, exceeding the recorded ceiling of 2605. Extract behavior to a collaborator and lower the ceiling for this entry, or raise the recorded ceiling for this entry deliberately with justification in the same change by editing SourceSizeRatchet.SeededCeilings."
    ];

    [Fact]
    public void VerbatimRetryMessagesProduceCandidateRedSecondRounds()
    {
        var goal = GoalWithRetries(RetryMessages);

        var rounds = WorkerRoundLedger.FromGoal(goal);

        Assert.Equal(3, goal.Tasks.Count);
        Assert.Equal(6, rounds.Count);
        foreach (var task in goal.Tasks)
        {
            var second = Assert.Single(rounds.Where(r => r.TaskId == task.Id.Value && r.RoundIndex == 2));
            Assert.Equal(AgentRole.Developer, second.Role);
            Assert.Equal(ReworkCauseFamily.CandidateRed, second.ReworkCause);
        }
    }

    [Fact]
    public void VerbatimRetryMessagesProduceProductiveSecondRoundsWithoutWaste()
    {
        var goal = GoalWithRetries(RetryMessages);

        var values = RoundValueClassifier.Classify([goal]);

        Assert.Equal(3, goal.Tasks.Count);
        Assert.Equal(6, values.Count);
        foreach (var task in goal.Tasks)
        {
            var second = Assert.Single(values.Where(v => v.Round.TaskId == task.Id.Value && v.Round.RoundIndex == 2));
            Assert.Equal(ReworkCauseFamily.CandidateRed, second.Round.ReworkCause);
            Assert.Equal(RoundValueClass.Productive, second.ValueClass);
            Assert.Null(second.WasteCause);
        }
    }

    [Theory]
    [InlineData("worker-build-check-failed automatic recoveryX")]
    [InlineData("free text")]
    public void NonMatchingRetryMessagesRemainUnclassified(string message)
    {
        var goal = GoalWithRetries([message]);

        var rounds = WorkerRoundLedger.FromGoal(goal);

        Assert.Equal(2, rounds.Count);
        var second = Assert.Single(rounds.Where(r => r.RoundIndex == 2));
        Assert.Equal(ReworkCauseFamily.Unclassified, second.ReworkCause);
    }

    private static Goal GoalWithRetries(IReadOnlyList<string> messages)
    {
        var tasks = messages.Select((_, index) => RoundValueFixture.Task(
            $"developer-{index}", AgentRole.Developer, WorkTaskStatus.Completed,
            RoundValueFixture.Dispatch("2026-09-24T01:00:00Z"),
            RoundValueFixture.Dispatch("2026-09-24T03:00:00Z"))).ToArray();
        var events = messages.SelectMany((message, index) => new[]
        {
            new ProgressEventSnapshot("goal", tasks[index].Id, ProgressKind.TaskRetried, message,
                RoundValueFixture.At("2026-09-24T02:00:00Z")),
            new ProgressEventSnapshot("goal", tasks[index].Id, ProgressKind.TaskCompleted, "done",
                RoundValueFixture.At("2026-09-24T04:00:00Z"))
        }).ToArray();

        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot("goal", "Developer candidate repairs", GoalStatus.Completed, tasks, events)],
            [])).Goals.Single();
    }
}
