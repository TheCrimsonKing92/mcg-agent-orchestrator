using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure in-memory inputs, including fixed timestamps.
public sealed class EpicProgressReadModelTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly PortfolioEpic Epic = new("epic", "Progress", null, Epoch, "test", Epoch, "test");

    [Fact]
    public void Build_AllStatusesAndMissing_CountEachMembershipExactlyOnce()
    {
        var expected = new Dictionary<GoalStatus, EpicProgressBucket>
        {
            [GoalStatus.Draft] = EpicProgressBucket.Active,
            [GoalStatus.Active] = EpicProgressBucket.Active,
            [GoalStatus.WaitingForHuman] = EpicProgressBucket.Active,
            [GoalStatus.Verifying] = EpicProgressBucket.Verifying,
            [GoalStatus.Verified] = EpicProgressBucket.Verified,
            [GoalStatus.AcceptanceFailed] = EpicProgressBucket.Failed,
            [GoalStatus.Failed] = EpicProgressBucket.Failed,
            [GoalStatus.Parked] = EpicProgressBucket.Parked,
            [GoalStatus.Completed] = EpicProgressBucket.Landed,
            [GoalStatus.Cancelled] = EpicProgressBucket.Closed,
            [GoalStatus.Superseded] = EpicProgressBucket.Closed
        };
        var goals = Enum.GetValues<GoalStatus>().Select((status, index) =>
            new GoalSummary(status.ToString(), status.ToString(), $"{status} title", Epoch.AddMinutes(index).ToString("O"))).ToArray();
        var members = goals.Select(goal => Member(goal.Id)).Append(Member("missing")).ToArray();
        var rollup = Assert.Single(EpicProgressReadModel.Build([Epic], [], members, goals, []));

        Assert.Equal(Enum.GetValues<GoalStatus>().Length, expected.Count);
        foreach (var goal in goals)
        {
            var member = Assert.Single(rollup.MemberGoals.Where(member => member.Id == goal.Id));
            Assert.Equal(expected[Enum.Parse<GoalStatus>(goal.Status)], member.Bucket);
        }
        Assert.Equal(EpicProgressBucket.Missing, Assert.Single(rollup.MemberGoals.Where(m => m.Id == "missing")).Bucket);
        Assert.Equal(members.Length, rollup.GoalCount);
        Assert.Equal(3, rollup.ActiveCount);
        Assert.Equal(1, rollup.VerifyingCount);
        Assert.Equal(1, rollup.VerifiedCount);
        Assert.Equal(2, rollup.FailedCount);
        Assert.Equal(1, rollup.ParkedCount);
        Assert.Equal(1, rollup.LandedCount);
        Assert.Equal(2, rollup.ClosedCount);
        Assert.Equal(1, rollup.MissingCount);
        Assert.Equal(rollup.GoalCount, rollup.ActiveCount + rollup.VerifyingCount + rollup.VerifiedCount
            + rollup.FailedCount + rollup.ParkedCount + rollup.LandedCount + rollup.ClosedCount + rollup.MissingCount);
    }

    [Fact]
    public void Build_BacklogStatusesAndMissing_SplitAllBacklogMemberships()
    {
        var backlog = Enum.GetValues<BacklogItemStatus>().Select(status =>
            new BacklogItem(status.ToString(), "Title", "Body", status, Epoch, Epoch, null)).ToArray();
        var members = backlog.Select(item => Member(item.Id, PortfolioMemberKind.BacklogItem))
            .Append(Member("missing", PortfolioMemberKind.BacklogItem)).ToArray();
        var rollup = Assert.Single(EpicProgressReadModel.Build([Epic], [], members, [], backlog));

        Assert.Equal(4, rollup.BacklogItemCount);
        Assert.Equal(2, rollup.BacklogOpenCount);
        Assert.Equal(2, rollup.BacklogDoneCount);
        Assert.Equal(rollup.BacklogItemCount, rollup.BacklogOpenCount + rollup.BacklogDoneCount);
        Assert.Equal(0, rollup.GoalCount);
        Assert.Null(rollup.NewestUpdatedAt);
    }

    [Fact]
    public void Build_MemberUpdates_UsesNewestUpdateAndStableGroupedOrdering()
    {
        var goals = new GoalSummary[]
        {
            new("parked", "Parked", "Parked title", Epoch.AddDays(4).ToString("O")),
            new("landed", "Completed", "Landed title", Epoch.AddDays(5).ToString("O")),
            new("active-b", "Active", new string('x', 120) + "\r\nSecond line", Epoch.ToString("O")),
            new("active-a", "Active", "Active title", Epoch.ToString("O")),
            new("verifying", "Verifying", "Verifying title", Epoch.AddDays(1).ToString("O")),
            new("verified", "Verified", "Verified title", Epoch.AddDays(2).ToString("O")),
            new("failed", "AcceptanceFailed", "Failed title", Epoch.AddDays(3).ToString("O")),
            new("unrelated", "Active", "Unrelated title", Epoch.AddYears(1).ToString("O"))
        };
        var members = goals.Where(goal => goal.Id != "unrelated").Select(goal => Member(goal.Id))
            .Append(Member("missing")).ToArray();
        var rollup = Assert.Single(EpicProgressReadModel.Build([Epic], [], members, goals, []));

        Assert.Equal(Epoch.AddDays(5), rollup.NewestUpdatedAt);
        Assert.Equal(new[] { "failed", "verified", "verifying", "active-a", "active-b", "parked", "landed", "missing" },
            rollup.MemberGoals.Select(member => member.Id));
        Assert.Equal(new string('x', 100), rollup.MemberGoals.Single(member => member.Id == "active-b").Title);
        Assert.Equal("Missing", rollup.MemberGoals.Single(member => member.Id == "missing").Status);
    }

    [Fact]
    public void Build_UnknownStatusAndInvalidUpdate_KeepsGoalVisibleInFlight()
    {
        var goal = new GoalSummary("unknown", "FutureStatus", "Unknown title", "invalid");
        var rollup = Assert.Single(EpicProgressReadModel.Build([Epic], [], [Member(goal.Id)], [goal], []));

        Assert.Equal(1, rollup.ActiveCount);
        Assert.Equal(rollup.GoalCount, rollup.ActiveCount);
        Assert.Equal("FutureStatus", Assert.Single(rollup.MemberGoals).Status);
        Assert.Null(rollup.NewestUpdatedAt);
    }

    private static PortfolioEpicMember Member(string id, PortfolioMemberKind kind = PortfolioMemberKind.Goal) =>
        new(Epic.Id, kind, id, Epoch, "test");
}
