using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: per-test in-memory kernel, fixed fixture clock, fake output and event tail.
public sealed class OwnerConsoleGeneratedTitleScenarioTests
{
    [Fact]
    public async Task Build_GeneratedAndAuthoredTitles_AgreeAcrossBoardDetailAndActivity()
    {
        var harness = new OwnerConsoleHarness();
        var objective = string.Join(Environment.NewLine,
            BacklogIntakePlanner.GeneratedHeadingPrefix + "High priority: Fix the lane", "", "Body",
            BacklogIntakePlanner.TargetScopeHeadingLine, BacklogIntakePlanner.UnknownScopeMarkerLine);
        var generated = harness.AddGoal("11111111-generated", objective, AgentRole.Developer);
        var authored = harness.AddGoal("22222222-authored", "# Owner console: keep this colon\nBody", AgentRole.Developer);
        const string sourceId = "aaaaaaaa-backlog";
        LinkSource(harness, generated.Id, sourceId, SourceBacklogCoverage.Slice);
        var builder = new OwnerConsoleViewModelBuilder(harness.State, harness.Questions,
            harness.Liveness, new Epics(), harness.Clock);
        var now = harness.Clock.GetUtcNow();
        var model = await builder.BuildAsync(new(now, now,
            [new OwnerConductEvent(now, "goal-lifecycle", generated.Id.Value, "TaskDispatched role=Developer")], 0));

        var generatedRow = Assert.Single(model.Board, row => row.GoalId == generated.Id.Value);
        Assert.Equal("High priority: Fix the lane", generatedRow.Title);
        Assert.Equal("Owner console: keep this colon", Assert.Single(model.Board, row => row.GoalId == authored.Id.Value).Title);
        Assert.DoesNotContain("Backlog slice:", generatedRow.ToString());

        var detail = await Detail(harness, generated.Id);
        var lines = detail.Split(Environment.NewLine);
        Assert.Equal("11111111  High priority: Fix the lane", lines[0]);
        Assert.StartsWith("Status: ", lines[1]);
        Assert.Single(lines, line => line == "Source: backlog aaaaaaaa, slice");
        Assert.DoesNotContain("Backlog slice:", detail);
        Assert.DoesNotContain((await Detail(harness, authored.Id)).Split(Environment.NewLine),
            line => line.StartsWith("Source:", StringComparison.Ordinal));

        var activity = Assert.Single(model.Activity, item => item.GoalPrefix == "11111111");
        Assert.Equal("High priority: Fix the lane", activity.GoalTitle);
        Assert.Contains("High priority: Fix the lane", activity.Phrase);
        Assert.DoesNotContain("Backlog slice:", activity.ToString());
    }

    [Theory]
    [InlineData(SourceBacklogCoverage.Full, "aaaaaaaa-backlog", "Source: backlog aaaaaaaa, full")]
    [InlineData(null, "aaaaaaaa-backlog", "Source: backlog aaaaaaaa")]
    [InlineData(SourceBacklogCoverage.Slice, "short", "Source: backlog short, slice")]
    public async Task Render_RecordedSource_ShowsCoverageBetweenIsolationAndStage(
        SourceBacklogCoverage? coverage, string sourceId, string expected)
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("11111111-generated", "# Heading", AgentRole.Developer);
        LinkSource(harness, goal.Id, sourceId, coverage);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(item => item with
        {
            Isolation = "solo",
            CurrentHold = new GoalHoldSnapshot("hold", "owner-review-hold", "waiting_for_approval", harness.Clock.GetUtcNow())
        }).ToArray() });

        var lines = (await Detail(harness, goal.Id)).Split(Environment.NewLine);

        Assert.Single(lines, line => line == expected);
        var sourceIndex = Array.IndexOf(lines, expected);
        Assert.Equal("Isolation: solo", lines[sourceIndex - 1]);
        Assert.StartsWith("Stage: ", lines[sourceIndex + 1]);
    }

    private static void LinkSource(OwnerConsoleHarness harness, GoalId id, string sourceId, SourceBacklogCoverage? coverage)
    {
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(item => item.Id == id.Value
            ? item with { SourceBacklogItemId = sourceId, SourceBacklogCoverage = coverage }
            : item).ToArray() });
    }

    private static async Task<string> Detail(OwnerConsoleHarness harness, GoalId id)
    {
        var output = new OwnerConsoleHarness.FakeOutput();
        var goal = await OwnerConsoleGoalDetailFormatter.ComposeAsync(harness.State, harness.Tail, id.Value,
            output, TestContext.Current.CancellationToken);
        Assert.NotNull(goal);
        return output.Text;
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }
}
