using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: per-test kernels, fixed clocks and a uniquely owned database fixture.
public sealed class OwnerConsoleLandingBlockerViewTests
{
    private const string Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TypedLine = "Landing blocker: verified-admission/acceptance-width-admission since 17:50; next: conductor next-tick; detail: acceptance width 2 of 2 in use";
    private const string UntypedLine = "Landing blocker: untyped/AcceptanceQueue since 17:50; owner: AcceptanceQueue; next: not mapped; detail: queue";
    private static readonly DateTimeOffset Since = new(2026, 10, 24, 17, 50, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 18, 2, 30, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 10, 24, 19, 55, 0, TimeSpan.Zero);
    private static readonly GoalLandingBlocker Typed = new("verified-admission/acceptance-width-admission",
        "acceptance width 2 of 2 in use", "typed", Since, "conductor", "next-tick");
    private static readonly GoalLandingBlocker Untyped = new("untyped/AcceptanceQueue", "queue", "untyped",
        Since, "unmapped", "unmapped");

    [Theory]
    [InlineData(GoalStatus.Verified, false, "held: acceptance width admission (12m)")]
    [InlineData(GoalStatus.Verifying, false, "held: AcceptanceQueue (12m)")]
    [InlineData(GoalStatus.Verified, true, "held: acceptance width admission (2h5m)")]
    public async Task BuildBoard_RecordedBlocker_ShowsExactLabelAndAge(
        GoalStatus status, bool later, string expected)
    {
        var harness = Fixture(status, status == GoalStatus.Verifying ? Untyped : Typed);

        var board = await Board(harness, later ? Later : Now, OwnerConsoleGateProgress.Idle());

        var row = Assert.Single(board.Board);
        Assert.Equal(Id, row.GoalId);
        Assert.Equal(expected, row.Stage);
        Assert.Equal(expected, row.StageWithoutQuestion);
    }

    [Theory]
    [InlineData(false, TypedLine)]
    [InlineData(true, UntypedLine)]
    public async Task Compose_RecordedBlocker_WritesExactLineAfterLanding(bool untyped, string expected)
    {
        var harness = Fixture(untyped ? GoalStatus.Verifying : GoalStatus.Verified, untyped ? Untyped : Typed);
        var query = new OwnerGoalDetailQuery(harness.State, harness.Tail, () => OwnerConsoleGateProgress.Idle());
        var detail = await query.LoadAsync(Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(untyped ? Untyped : Typed, detail.Goal.LandingBlocker);
        var output = new OwnerConsoleHarness.FakeOutput();

        var composed = await OwnerConsoleGoalDetailFormatter.ComposeAsync(query, Id, output,
            CancellationToken.None, TimeZoneInfo.Utc);

        Assert.Same(detail.Goal, composed);
        AssertBlockerLine(output.Text, expected);
        Assert.Equal(Render(detail), output.Text);
    }

    [Fact]
    public async Task Compose_ReadOnlyDatabase_ShowsPersistedBlocker()
    {
        using var database = new ConductorVerbStartTests.Fixture();
        var harness = Fixture(GoalStatus.Verified, Typed);
        await InfrastructureTestSupport.CreateMigratedStateRepository(database.Workspace.SqliteStatePath)
            .SaveAsync(harness.Kernel);
        var readOnly = SqliteOrchestratorStateRepository.OpenReadOnly(database.Workspace.SqliteStatePath);
        var query = new OwnerGoalDetailQuery(readOnly, new OwnerConsoleHarness.FakeTail());
        var detail = await query.LoadAsync(Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(GoalStatus.Verified, detail.Goal.Status);
        Assert.Equal(Typed, detail.Goal.LandingBlocker);
        var output = new OwnerConsoleHarness.FakeOutput();

        await OwnerConsoleGoalDetailFormatter.ComposeAsync(query, Id, output,
            CancellationToken.None, TimeZoneInfo.Utc);

        AssertBlockerLine(output.Text, TypedLine);
    }

    [Theory]
    [InlineData("live")]
    [InlineData("unobserved")]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("developer")]
    public async Task Views_ExcludedBlocker_MatchFullBaseline(string scenario)
    {
        var status = scenario switch
        {
            "completed" => GoalStatus.Completed,
            "failed" => GoalStatus.Failed,
            "developer" => GoalStatus.Active,
            _ => GoalStatus.Verifying
        };
        var now = scenario == "failed" ? new OwnerConsoleTestClock().Now : Now;
        var harness = Fixture(status, Typed, now.AddHours(-1));
        var gate = scenario switch
        {
            "live" => new OwnerConsoleGateProgress(true, "solo", [new(Id, "Member")], Since,
                TimeSpan.FromMinutes(12), [], 0.5, 360, 2, 4),
            "unobserved" => null,
            _ => OwnerConsoleGateProgress.Idle()
        };
        var query = new OwnerGoalDetailQuery(harness.State, harness.Tail, gate is null ? null : () => gate);
        var withRecord = await query.LoadAsync(Id, CancellationToken.None);
        Assert.NotNull(withRecord);
        Assert.Equal(Typed, withRecord.Goal.LandingBlocker);
        Assert.Equal(status, withRecord.Goal.Status);
        if (scenario == "live") Assert.NotNull(withRecord.Gate);
        if (scenario == "unobserved") Assert.False(withRecord.GateObserved);
        var actualBoard = await Board(harness, now, gate);
        var actualText = Render(withRecord);

        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with { LandingBlocker = null }).ToArray()
        });
        var baseline = await query.LoadAsync(Id, CancellationToken.None);
        Assert.NotNull(baseline);
        Assert.Null(baseline.Goal.LandingBlocker);
        var baselineBoard = await Board(harness, now, gate);

        if (scenario == "completed")
        {
            Assert.Empty(baselineBoard.Board);
            Assert.Empty(actualBoard.Board);
        }
        else
        {
            var expected = Assert.Single(baselineBoard.Board);
            var actual = Assert.Single(actualBoard.Board);
            Assert.Equal(Id, actual.GoalId);
            Assert.Equal(expected.Stage, actual.Stage);
            Assert.Equal(expected.StageWithoutQuestion, actual.StageWithoutQuestion);
            if (scenario == "failed") Assert.StartsWith("failed:", expected.Stage);
            if (scenario == "developer")
            {
                Assert.Equal(OwnerConsoleAttention.WorkerRunning, expected.SortKey.Rank);
                Assert.Equal("Developer", expected.Stage);
            }
        }
        Assert.Equal(Render(baseline), actualText);
        Assert.DoesNotContain("Landing blocker:", actualText);
    }

    [Theory]
    [InlineData("stage/one-two/three-four", "one two/three four", 59, "0m")]
    [InlineData("plain-kind", "plain-kind", 3600, "1h0m")]
    [InlineData("untyped/Owner-Name", "Owner-Name", -60, "0m")]
    public void Board_KindAndAgeEdges_UseRecordedLabelAndClampedMinutes(
        string kind, string label, int seconds, string age)
    {
        var goal = Fixture(GoalStatus.Verified, Typed with { Kind = kind }).Kernel.Goals.Single();

        Assert.Equal($"held: {label} ({age})", OwnerConsoleLandingBlockerText.Board(goal, Since.AddSeconds(seconds)));
    }

    [Fact]
    public void Detail_ZonedTimeAndMultilineDetail_PreserveRecordedAction()
    {
        var goal = Fixture(GoalStatus.Verified, Typed with { Detail = "first\r\nsecond\nthird\rfourth" })
            .Kernel.Goals.Single();
        var zone = TimeZoneInfo.CreateCustomTimeZone("fixed-offset", TimeSpan.FromHours(2), "fixed-offset", "fixed-offset");

        Assert.Equal("verified-admission/acceptance-width-admission since 19:50; next: conductor next-tick; detail: first  second third fourth",
            OwnerConsoleLandingBlockerText.Detail(goal, zone));
    }

    private static void AssertBlockerLine(string text, string expected)
    {
        var lines = text.Split(Environment.NewLine);
        Assert.Equal(expected, Assert.Single(lines.Where(line => line.StartsWith("Landing blocker:", StringComparison.Ordinal))));
        var landing = Assert.Single(lines.Where(line => line.StartsWith("Landing:", StringComparison.Ordinal)));
        Assert.Equal(Array.IndexOf(lines, landing) + 1, Array.IndexOf(lines, expected));
    }

    private static string Render(OwnerGoalDetailSnapshot detail)
    {
        var output = new OwnerConsoleHarness.FakeOutput();
        OwnerConsoleGoalDetailFormatter.Render(detail, output, TimeZoneInfo.Utc);
        return output.Text;
    }

    private static Task<OwnerConsoleViewModel> Board(OwnerConsoleHarness harness, DateTimeOffset now,
        OwnerConsoleGateProgress? gate) =>
        new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness, new Epics(),
            new OwnerConsoleTestClock { Now = now }, gateProgress: gate is null ? null : _ => gate).BuildBoardAsync();

    private static OwnerConsoleHarness Fixture(GoalStatus status, GoalLandingBlocker blocker, DateTimeOffset? at = null)
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal(Id, "Member", AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                Status = status,
                LandingBlocker = blocker,
                Tasks = goal.Tasks.Select(task => task with
                {
                    Status = status == GoalStatus.Active ? WorkTaskStatus.Running : WorkTaskStatus.Completed
                }).ToArray(),
                Timeline = [new(goal.Id, null, status == GoalStatus.Failed ? ProgressKind.TaskFailed : ProgressKind.GoalPolicyDecision,
                    status == GoalStatus.Failed ? "explicit failure" : "entered " + status, at ?? Since)]
            }).ToArray()
        });
        return harness;
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }
}
