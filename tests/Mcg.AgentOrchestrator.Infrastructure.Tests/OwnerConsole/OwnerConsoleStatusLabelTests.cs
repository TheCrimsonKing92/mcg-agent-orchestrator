using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: pure formatting and per-test in-memory goals; no terminal or shared state.
public sealed class OwnerConsoleStatusLabelTests
{
    private static readonly IReadOnlyDictionary<GoalStatus, string> Expected = new Dictionary<GoalStatus, string>
    {
        [GoalStatus.Draft] = "draft",
        [GoalStatus.Active] = "in progress",
        [GoalStatus.WaitingForHuman] = "waiting for an answer",
        [GoalStatus.Parked] = "parked",
        [GoalStatus.Verifying] = "in gate",
        [GoalStatus.Verified] = "reviewed, awaiting gate",
        [GoalStatus.AcceptanceFailed] = "gate failed",
        [GoalStatus.Completed] = "landed",
        [GoalStatus.Failed] = "failed",
        [GoalStatus.Cancelled] = "cancelled",
        [GoalStatus.Superseded] = "superseded"
    };

    public static IEnumerable<object[]> Labels => Expected.Select(pair => new object[] { pair.Key, pair.Value });

    [Theory]
    [MemberData(nameof(Labels))]
    public void For_DeclaredStatus_ReturnsExactPlainLabel(GoalStatus status, string label)
    {
        Assert.Equal(label, OwnerConsoleStatusLabel.For(status));
        Assert.Equal(label, OwnerConsoleStatusLabel.For(status.ToString()));
    }

    [Fact]
    public void For_EveryEnumValue_HasAnExplicitExpectedLabel()
    {
        var statuses = Enum.GetValues<GoalStatus>();
        Assert.Equal(statuses.Order(), Expected.Keys.Order());
        foreach (var status in statuses)
        {
            Assert.Equal(Expected[status], OwnerConsoleStatusLabel.For(status));
            Assert.NotEqual(status.ToString(), OwnerConsoleStatusLabel.For(status));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(999)]
    public void For_UndefinedStatus_ThrowsInsteadOfDisplayingANumber(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OwnerConsoleStatusLabel.For((GoalStatus)value));
        Assert.Throws<ArgumentOutOfRangeException>(() => OwnerConsoleStatusLabel.For(value.ToString()));
    }

    [Fact]
    public void For_InvalidStoredName_ThrowsInsteadOfDisplayingIt() =>
        Assert.Throws<ArgumentException>(() => OwnerConsoleStatusLabel.For("not-a-status"));

    [Theory]
    [InlineData(GoalStatus.Verified, "Status: reviewed, awaiting gate")]
    [InlineData(GoalStatus.Completed, "Status: landed")]
    public void Detail_ReviewedAndLandedGoals_UsePlainStatusLines(GoalStatus status, string expected)
    {
        var lines = Detail(status);

        Assert.Equal(expected, lines[1]);
        Assert.Single(lines, line => line.StartsWith("Status: ", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("Status: Verified", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("Status: Completed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(GoalStatus.Failed, null)]
    [InlineData(GoalStatus.Verified, null)]
    [InlineData(GoalStatus.Active, "Stage: Created")]
    [InlineData(GoalStatus.Completed, "Stage: Verified")]
    public void Detail_LifecycleStage_ShowsOnlyDistinctExistingStage(GoalStatus status, string? expected)
    {
        var stages = Detail(status).Where(line => line.StartsWith("Stage: ", StringComparison.Ordinal)).ToArray();

        if (expected is null) Assert.Empty(stages);
        else Assert.Equal(expected, Assert.Single(stages));
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void Loading_StoredStatus_UsesLabelAndPreservesStatePrefix(GoalStatus status, string label)
    {
        OwnerConsoleBoardRow row = new("11111111", "", "Readable goal", status.ToString(),
            "fixture stage", null, "11111111-detail");

        var lines = OwnerConsoleGoalDetailLoading.Placeholder(row.GoalId, row).Text.Split(Environment.NewLine);

        Assert.Equal("State: " + label, lines[1]);
        Assert.Equal("Stage: fixture stage", lines[2]);
        Assert.Equal(OwnerConsoleGoalDetailLoading.LoadingLine, lines[3]);
    }

    private static string[] Detail(GoalStatus status)
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("11111111-detail", "Readable goal", AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(item => item with { Status = status }).ToArray()
        });
        goal = harness.Kernel.GetGoal(goal.Id);
        var output = new OwnerConsoleHarness.FakeOutput();
        OwnerConsoleGoalDetailFormatter.Render(new(goal, null, []), output, TimeZoneInfo.Utc);
        return output.Text.Split(Environment.NewLine);
    }
}
