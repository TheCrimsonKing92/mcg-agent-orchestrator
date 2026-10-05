using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalEffectOutboxPreCommitFaultTests : CliGoalEffectOutboxTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("park")]
    [Xunit.InlineData("stop-park")]
    [Xunit.InlineData("abandon")]
    [Xunit.InlineData("stop-abandon")]
    public async Task FaultBeforeCommit_RollsBackStateAndBothEffectKinds(string command)
    {
        using var seed = await Seed.Create();
        await Raise(seed);
        var before = await Version(seed);
        var fault = new InvalidOperationException("fault after goal and outbox writes");
        var repository = new SqliteOrchestratorStateRepository(seed.Workspace.SqliteStatePath)
        {
            BeforeGoalStateOutboxCommit = () => throw fault
        };

        var result = RunCommand(EffectParts(seed, command), repository, seed.Workspace);

        Xunit.Assert.Same(fault, result.Error);
        Xunit.Assert.Equal(string.Empty, result.Output);
        Xunit.Assert.Equal(GoalStatus.Active, (await seed.Repository.LoadGoalAsync(seed.GoalId))!.Status);
        Xunit.Assert.Equal(before, await Version(seed));
        Xunit.Assert.Empty(await seed.Repository.ListOutboxMessagesAsync(ParkKind));
        Xunit.Assert.Empty(await seed.Repository.ListOutboxMessagesAsync(AbandonKind));
        Xunit.Assert.Equal(0L, await CountOutboxRows(seed));
        Xunit.Assert.Equal(CollaborationItemStatus.Raised, (await Item(seed)).Status);
        Xunit.Assert.False(GoalOperationJournal.HasRetiredTerminalDisposition(Journal(seed)));
        Xunit.Assert.False(File.Exists(seed.EventPath));
    }
}
