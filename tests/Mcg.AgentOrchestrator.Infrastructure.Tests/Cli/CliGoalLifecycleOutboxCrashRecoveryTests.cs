using Mcg.AgentOrchestrator.App.Cli;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalLifecycleOutboxCrashRecoveryTests : CliGoalLifecycleOutboxTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("supersede")]
    [Xunit.InlineData("unpark")]
    public async Task CrashAfterAppendBeforeMark_WriterRetiresWithoutDuplicateLine(string command)
    {
        using var seed = await Seed.Create(command);
        Directory.CreateDirectory(seed.EventPath);
        Xunit.Assert.IsType<CliCommandHandlers.GoalLifecycleProjectionException>(Transition(seed, command).Error);
        var message = Xunit.Assert.Single(await seed.Repository.ListOutboxMessagesAsync(GoalLifecycleEventOutbox.Kind));
        Directory.Delete(seed.EventPath);

        // External projection completes, but the process exits before finalizing the outbox row.
        GoalLifecycleEventOutbox.AppendForMessage(seed.Workspace, message);
        AssertOneLine(seed, EventType(command), message.Id);
        Xunit.Assert.NotNull(await seed.Repository.GetOutboxStateAsync(message.Id));

        Xunit.Assert.Null(ParkOther(seed).Error);

        AssertOneLine(seed, EventType(command), message.Id);
        Xunit.Assert.Null(await seed.Repository.GetOutboxStateAsync(message.Id));
        Xunit.Assert.Empty(await seed.Repository.ListOutboxMessagesAsync(GoalLifecycleEventOutbox.Kind));
    }
}
