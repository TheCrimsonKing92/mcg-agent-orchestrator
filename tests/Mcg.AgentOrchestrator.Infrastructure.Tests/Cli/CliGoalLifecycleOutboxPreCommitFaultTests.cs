using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalLifecycleOutboxPreCommitFaultTests : CliGoalLifecycleOutboxTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("unpark")]
    [Xunit.InlineData("supersede")]
    [Xunit.InlineData("abandon")]
    [Xunit.InlineData("stop-abandon")]
    public async Task FaultBeforeCommit_RollsBackGoalVersionAndOutbox(string command)
    {
        using var seed = await Seed.Create(command);
        var beforeVersion = await Version(seed);
        var fault = new InvalidOperationException("fault after goal and outbox writes");
        var repository = new SqliteOrchestratorStateRepository(seed.Workspace.SqliteStatePath)
        {
            BeforeGoalStateOutboxCommit = () => throw fault
        };

        var result = RunCommand(Parts(seed, command), repository, seed.Workspace);

        Xunit.Assert.Same(fault, result.Error);
        Xunit.Assert.Equal(string.Empty, result.Output);
        Xunit.Assert.Equal(PriorStatus(command), (await seed.Repository.LoadGoalAsync(seed.GoalId))!.Status);
        Xunit.Assert.Equal(beforeVersion, await Version(seed));
        Xunit.Assert.Equal(0L, await CountOutboxRows(seed));
        Xunit.Assert.False(File.Exists(seed.EventPath));
    }
}
