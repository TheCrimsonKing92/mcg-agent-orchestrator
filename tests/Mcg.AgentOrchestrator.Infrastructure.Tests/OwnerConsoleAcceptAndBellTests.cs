using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

public sealed class OwnerConsoleAcceptAndBellTests
{
    [Fact]
    public async Task AcceptUsesDefaultOnlyAndBellCanBeDisabled()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("11111111111111111111111111111111", "Build search", AgentRole.Developer);
        var session = harness.Session();

        await session.StartAsync(null, CancellationToken.None);
        Assert.StartsWith(ConsoleAnnouncementFormatter.Format(harness.Clock, "Owner digest: landed=2"), harness.Output.Text);
        Assert.Empty(harness.Answers.Calls);
        harness.Questions.Items.Add(new OwnerQuestion("wait-1", goal.Id.Value,
            OwnerQuestionKind.HumanInput, "Ship it?", ProposedDefault: "ship it"));
        await session.HandleEventAsync(new OwnerConductEvent(harness.Clock.GetUtcNow(),
            "goal-escalation", goal.Id.Value, "waiting"), CancellationToken.None);
        Assert.Contains('\a', harness.Output.Text);
        await session.HandleCommandAsync("accept 1", CancellationToken.None);
        Assert.Equal([("wait-1", "ship it")], harness.Answers.Calls);

        await session.HandleCommandAsync("bell off", CancellationToken.None);
        var bellCount = harness.Output.Text.Count(ch => ch == '\a');
        harness.Questions.Items.Add(new OwnerQuestion("wait-2", goal.Id.Value,
            OwnerQuestionKind.HumanInput, "No default?"));
        await session.HandleEventAsync(new OwnerConductEvent(harness.Clock.GetUtcNow(),
            "goal-escalation", goal.Id.Value, "waiting"), CancellationToken.None);
        await session.HandleCommandAsync("accept 2", CancellationToken.None);

        Assert.Equal(bellCount, harness.Output.Text.Count(ch => ch == '\a'));
        Assert.Contains("no proposed default", harness.Output.Text);
        Assert.Single(harness.Answers.Calls);
    }
}
