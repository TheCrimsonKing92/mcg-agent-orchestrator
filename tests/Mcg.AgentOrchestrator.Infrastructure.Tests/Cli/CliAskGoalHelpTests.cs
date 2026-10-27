using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private fixture roots and AsyncLocal console capture.
public sealed class CliAskGoalHelpTests : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task HelpWithoutCurrentGoalSkipsStateAndCleanup(string flag)
    {
        using var fixture = new CliAskGoalFixture();
        var result = await fixture.RunAsync(["ask-goal", flag], poisonCleanup: true);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(CliCommandHelp.AskGoalUsage, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.False(Directory.Exists(fixture.Workspace.OrchestratorDirectory));
    }

    [Theory]
    [InlineData("--help", false)]
    [InlineData("-h", false)]
    [InlineData("--help", true)]
    [InlineData("-h", true)]
    public async Task HelpPreservesEveryPersistedRow(string flag, bool mixedArguments)
    {
        using var fixture = new CliAskGoalFixture();
        await fixture.SeedAsync();
        var before = fixture.StateRows();
        var args = mixedArguments
            ? new[] { "ask-goal", "--goal", "unknown", "--unsupported", flag }
            : new[] { "ask-goal", flag };
        var result = await fixture.RunAsync(args, poisonCleanup: true);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(CliCommandHelp.AskGoalUsage, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Equal(before, fixture.StateRows());
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void PersistentHelpNeverLoadsOrMutatesKernel(string flag)
    {
        using var fixture = new CliAskGoalFixture();
        var kernel = new AgentOrchestratorKernel();
        var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        var changed = true;
        var output = CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["ask-goal", flag], repository, fixture.Workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref current));
        Assert.False(changed);
        Assert.Contains(CliCommandHelp.AskGoalUsage, output);
        Assert.Null(current);
        Assert.Equal(0, repository.FullLoadAttempts);
        Assert.Equal(0, repository.MutationAttempts);
        Assert.Equal(0, repository.ListOutboxMessagesCount);
        Assert.Equal(0, repository.OutboxClaimAttempts);
        Assert.Equal(0, repository.SaveAttempts);
        Assert.Equal(0, repository.MergeSaveAttempts);
    }
}
