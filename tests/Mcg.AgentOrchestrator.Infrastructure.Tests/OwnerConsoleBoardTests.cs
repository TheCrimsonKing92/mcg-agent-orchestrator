using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

public sealed class OwnerConsoleBoardTests
{
    [Fact]
    public async Task WatchTransitionPrintsHeaderAndBothActiveGoals()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111111111111111111111111111", "Build search", AgentRole.Developer);
        harness.AddGoal("22222222222222222222222222222222", "Review search", AgentRole.Reviewer);
        var session = harness.Session();

        await session.StartAsync(null, CancellationToken.None);
        await session.HandleEventAsync(new OwnerConductEvent(
            harness.Clock.GetUtcNow(), "watch-transition", "11111111", "running"), CancellationToken.None);

        Assert.Contains("console", CliArgumentParser.RecognizedCommands);
        Assert.Contains("active goals: 2", harness.Output.Text);
        Assert.Contains("11111111 | Build search | Active | Developer", harness.Output.Text);
        Assert.Contains("22222222 | Review search | Active | Reviewer", harness.Output.Text);
    }
}
