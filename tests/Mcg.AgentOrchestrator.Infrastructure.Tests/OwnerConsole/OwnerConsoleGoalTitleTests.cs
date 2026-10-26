using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

public sealed class OwnerConsoleGoalTitleTests
{
    private const string GoalId = "33333333333333333333333333333333";
    private static readonly string Body = "Distinctive brief body " + new string('z', 220);

    [Fact]
    public async Task BoardUsesHeadingWithoutBriefBody()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal(GoalId, "\r\n# Short heading\n" + Body, AgentRole.Developer);

        await harness.Session().HandleCommandAsync("board", CancellationToken.None);

        var row = Assert.Single(harness.Output.Text.Split(Environment.NewLine),
            line => line.StartsWith("33333333 | ", StringComparison.Ordinal));
        Assert.Contains("Short heading", row);
        Assert.DoesNotContain("Distinctive brief body", row);
    }

    [Fact]
    public async Task GoalHeaderUsesHeadingWithoutBriefBody()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal(GoalId, "# Short heading\n" + Body, AgentRole.Developer);

        await harness.Session().HandleCommandAsync("goal 33333333", CancellationToken.None);

        var header = Assert.Single(harness.Output.Text.Split(Environment.NewLine),
            line => line.StartsWith(ConsoleAnnouncementFormatter.Format(harness.Clock, GoalId), StringComparison.Ordinal));
        Assert.Contains("Short heading", header);
        Assert.DoesNotContain("Distinctive brief body", harness.Output.Text);
    }

    [Fact]
    public void LongTitleRetainsAllCharactersBeforeRendering()
    {
        var title = OwnerGoalTitle.From("\r\n# " + new string('a', 150));
        Assert.Equal(new string('a', 150), title);
        Assert.Equal(new string('a', 100), OwnerGoalTitle.From(new string('a', 100)));
        Assert.Equal(string.Empty, OwnerGoalTitle.From("###"));
    }
}
