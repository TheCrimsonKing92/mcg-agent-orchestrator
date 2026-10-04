using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalLifecycleOutboxProjectionFailureTests : CliGoalLifecycleOutboxTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("unpark")]
    [Xunit.InlineData("supersede")]
    [Xunit.InlineData("abandon")]
    [Xunit.InlineData("stop-abandon")]
    public async Task ProjectionFailure_CommitsPendingDelivery_AnotherGoalsWriterRetries(string command)
    {
        using var seed = await Seed.Create(command);
        Directory.CreateDirectory(seed.EventPath);

        var result = Transition(seed, command);

        var error = Xunit.Assert.IsType<CliCommandHandlers.GoalLifecycleProjectionException>(result.Error);
        Xunit.Assert.Equal(
            $"Goal 'aaaaaaaa' is committed {CommittedStatus(command)}, but lifecycle event projection failed: " +
            error.InnerException!.Message + " " + PendingSentence, error.Message);
        Xunit.Assert.Equal(CommittedStatus(command), (await seed.Repository.LoadGoalAsync(seed.GoalId))!.Status);
        var message = Xunit.Assert.Single(await seed.Repository.ListOutboxMessagesAsync(GoalLifecycleEventOutbox.Kind));
        Xunit.Assert.NotEqual(OrchestratorStateOutboxStatus.Quarantined,
            (await seed.Repository.GetOutboxStateAsync(message.Id))!.Status);

        Directory.Delete(seed.EventPath);
        var retry = ParkOther(seed);

        Xunit.Assert.Null(retry.Error);
        Xunit.Assert.True(retry.Changed);
        AssertOneLine(seed, EventType(command), message.Id);
        Xunit.Assert.Null(await seed.Repository.GetOutboxStateAsync(message.Id));
        Xunit.Assert.Equal(0L, await CountOutboxRows(seed));
    }
}
