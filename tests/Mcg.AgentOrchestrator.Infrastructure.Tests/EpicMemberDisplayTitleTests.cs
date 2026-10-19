using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixed in-memory portfolio and goal metadata, no external resources.
public sealed class EpicMemberDisplayTitleTests
{
    [Fact]
    public void Build_MemberObjectives_ProjectsDisplayTitlesBeforeCapping()
    {
        var epoch = DateTimeOffset.UnixEpoch;
        var epic = new PortfolioEpic("epic", "Titles", null, epoch, "test", epoch, "test");
        var goals = new GoalSummary[]
        {
            new("generated", "Active", "Backlog slice: Meaningful heading\r\n\r\nBody\r\n" +
                "Target files/scopes:\r\nScope confidence: unknown", epoch.ToString("O")),
            new("h1", "Active", "# Heading\nBody", epoch.ToString("O")),
            new("blank", "Active", "\nFirst nonblank line\nBody", epoch.ToString("O")),
            new("long", "Active", new string('x', 150) + "\nBody", epoch.ToString("O"))
        };
        var members = goals.Select(goal => new PortfolioEpicMember(
            epic.Id, PortfolioMemberKind.Goal, goal.Id, epoch, "test")).ToArray();

        var result = Assert.Single(EpicProgressReadModel.Build([epic], [], members, goals, []));

        Assert.Equal(4, result.MemberGoals.Count);
        Assert.Equal("Meaningful heading", result.MemberGoals.Single(goal => goal.Id == "generated").Title);
        Assert.Equal("Heading", result.MemberGoals.Single(goal => goal.Id == "h1").Title);
        Assert.Equal("First nonblank line", result.MemberGoals.Single(goal => goal.Id == "blank").Title);
        Assert.Equal(new string('x', 100), result.MemberGoals.Single(goal => goal.Id == "long").Title);
    }
}
