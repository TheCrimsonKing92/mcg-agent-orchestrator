using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: per-test state, fixed clocks and uniquely owned temporary folders.
public sealed class OwnerConsoleVerifyingWithoutGateTests
{
    private const string Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset Entered = new(2026, 10, 14, 21, 4, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndedAt = Entered.AddSeconds(20);
    private static readonly string[] FixtureLines =
    [
        """{"timestamp":"2026-10-14T21:04:08Z","eventKind":"acceptance","goalId":"aaaaaaaa","detail":"ACCEPTANCE goal=aaaaaaaa slot=slot-0 result=started attempt=aaaaaaaa-0-20261014210408412 tick=7"}""",
        """{"timestamp":"2026-10-14T21:04:20Z","eventKind":"acceptance","goalId":"aaaaaaaa","detail":"ACCEPTANCE goal=aaaaaaaa slot=slot-0 result=blocked-build-slot attempt=aaaaaaaa-0-20261014210408412 tick=8"}""",
        """{"timestamp":"2026-10-14T21:30:00Z","eventKind":"acceptance","goalId":"bbbbbbbb","detail":"ACCEPTANCE goal=bbbbbbbb slot=slot-1 result=running attempt=bbbbbbbb-0-20261014212900000 tick=9"}"""
    ];

    [Fact]
    public void Read_IncidentTail_RetainsOnlyTheTerminalSoloEvent()
    {
        var events = FixtureLines.Select(line => Assert.IsType<OwnerConductEvent>(OwnerConsoleGateProgress.ParseEvent(line)));

        var ends = OwnerConsoleGateEnd.Read(events);

        Assert.Single(ends);
        var end = Assert.IsType<OwnerConsoleGateEnd>(OwnerConsoleGateEnd.LatestFor(ends, Id));
        Assert.Equal("blocked-build-slot", end.Result);
        Assert.Equal(EndedAt, end.At);
        Assert.Null(OwnerConsoleGateEnd.LatestFor(ends, OtherId));
        Assert.Null(OwnerConsoleGateEnd.LatestFor(ends, "cccccccccccccccccccccccccccccccc"));
    }

    [Theory]
    [InlineData("ACCEPTANCE goal=aaaaaaaa result=unfamiliar", "unfamiliar")]
    [InlineData("ACCEPTANCE result=aborted\tgoal=aaaaaaaa", "aborted")]
    [InlineData("ACCEPTANCE goal=aaaaaaaa result=Running", "Running")]
    [InlineData("ACCEPTANCE goal=aaaaaaaa result=failed=detail", "failed=detail")]
    [InlineData("ACCEPTANCE goal=aaaaaaaa result=", null)]
    [InlineData("ACCEPTANCE goal= result=failed", null)]
    [InlineData("ACCEPTANCE result=failed", null)]
    [InlineData("ACCEPTANCE goal=aaaaaaaa", null)]
    [InlineData("ACCEPTANCE goal=aaaaaaaa result=started", null)]
    [InlineData("ACCEPTANCE goal=aaaaaaaa result=running", null)]
    [InlineData("ACCEPTANCE GOAL=aaaaaaaa result=failed", null)]
    [InlineData("ACCEPTANCE goal=aaaaaaaa RESULT=failed", null)]
    [InlineData("acceptance goal=aaaaaaaa result=failed", null)]
    [InlineData("ACCEPTANCE_COHORT_EXIT goal=aaaaaaaa result=failed", null)]
    public void Read_DetailFields_IgnoreEnvelopeAndKeepUnknownResults(string detail, string? result)
    {
        var ends = OwnerConsoleGateEnd.Read([new(EndedAt, "unrelated-kind", OtherId, detail)]);

        if (result is null) Assert.Empty(ends);
        else
        {
            var end = Assert.Single(ends);
            Assert.Equal("aaaaaaaa", end.GoalPrefix);
            Assert.Equal(result, end.Result);
            Assert.Equal(EndedAt, end.At);
        }
    }

    [Fact]
    public void LatestFor_UnsortedEnds_SelectsNewestThenLaterPositionIgnoringCase()
    {
        var first = new OwnerConsoleGateEnd("AAAAAAAA", EndedAt, "failed");
        var tied = new OwnerConsoleGateEnd("aaaa", EndedAt, "aborted");
        var older = new OwnerConsoleGateEnd("aaaaaaaa", EndedAt.AddMinutes(-1), "passed");
        var unrelated = new OwnerConsoleGateEnd("bbbbbbbb", EndedAt.AddMinutes(1), "failed");
        var events = new[] { first, tied, older, unrelated }.Select(end => new OwnerConductEvent(
            end.At, "acceptance", null, $"ACCEPTANCE goal={end.GoalPrefix} result={end.Result}"));

        var ends = OwnerConsoleGateEnd.Read(events.Concat(events.Take(1)));

        Assert.Equal(new[] { first, tied, older, unrelated, first }, ends);
        Assert.Same(ends[4], OwnerConsoleGateEnd.LatestFor(ends, Id));
        Assert.Same(tied, OwnerConsoleGateEnd.LatestFor([first, tied, older, unrelated], Id));
        Assert.Null(OwnerConsoleGateEnd.LatestFor(null, Id));
        Assert.Null(OwnerConsoleGateEnd.LatestFor([], Id));
    }

    [Fact]
    public void Load_IncidentTail_ExposesRecordedEndFromTheParsedTail()
    {
        var directory = Path.Combine(Path.GetTempPath(), "verifying-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var log = Path.Combine(directory, "conduct-events.log");
            var manifest = Path.Combine(directory, "acceptance-manifest.json");
            File.WriteAllLines(log, FixtureLines);
            File.WriteAllText(manifest, """{"engine":{"infrastructureTestLanes":[{"name":"A","estimatedSerialSeconds":100}]}}""");
            var loader = new OwnerConsoleGateProgressLoader(log, manifest, Path.Combine(directory, "missing-durations.jsonl"),
                new OwnerConsoleTestClock { Now = Entered.AddMinutes(27) });

            var gate = Assert.IsType<OwnerConsoleGateProgress>(loader.Load(new Dictionary<string, string>()));

            var end = Assert.IsType<OwnerConsoleGateEnd>(OwnerConsoleGateEnd.LatestFor(gate.Ended, Id));
            Assert.Equal("blocked-build-slot", end.Result);
            Assert.Equal(EndedAt, end.At);
            Assert.Null(gate.ForMember(Id));
            Assert.NotNull(gate.ForMember(OtherId));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("aaaaaaaa", "verifying (no live gate; last gate ended 441m ago: blocked-build-slot)")]
    [InlineData("bbbbbbbb", "verifying (no live gate)")]
    public async Task BuildBoard_ObservedIdleGate_UsesOnlyMatchingEnd(string prefix, string expected)
    {
        var harness = VerifyingHarness();
        var gate = OwnerConsoleGateProgress.Idle() with { Ended = [new(prefix, EndedAt, "blocked-build-slot")] };
        var clock = new OwnerConsoleTestClock { Now = EndedAt.AddMinutes(441) };

        var board = await new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness,
            new Epics(), clock, gateProgress: _ => gate).BuildBoardAsync();

        var row = Assert.Single(board.Board);
        Assert.Equal(Id, row.GoalId);
        Assert.Equal(expected, row.Stage);
        Assert.Equal(expected, row.StageWithoutQuestion);
    }

    [Theory]
    [InlineData(119, "verifying (no live gate; last gate ended 1m ago: blocked-build-slot)")]
    [InlineData(-60, "verifying (no live gate; last gate ended 0m ago: blocked-build-slot)")]
    public void Stage_FractionalOrFutureAge_FloorsAndClampsMinutes(int seconds, string expected)
    {
        var goal = VerifyingHarness().Kernel.Goals.Single();
        var gate = OwnerConsoleGateProgress.Idle() with { Ended = [End()] };

        Assert.Equal(expected, OwnerConsoleVerifyingGate.Stage(goal, gate, EndedAt.AddSeconds(seconds)));
    }

    [Theory]
    [InlineData("ended", "Landing: verifying since 21:04, no live gate; last gate ended 21:04: blocked-build-slot")]
    [InlineData("idle", "Landing: verifying since 21:04, no live gate")]
    [InlineData("live", "Landing: gate running since 21:10")]
    public void Render_ObservedGate_WritesExactLandingLine(string scenario, string expected)
    {
        var goal = VerifyingHarness().Kernel.Goals.Single();
        var detail = new OwnerGoalDetailSnapshot(goal, null, [], GateObserved: true,
            Gate: scenario == "live" ? LiveGate(Entered.AddMinutes(6)) : null,
            LastGateEnd: scenario == "ended" ? End() : null);

        Assert.Equal(expected, LandingLine(Render(detail)));
    }

    [Fact]
    public void Render_UnobservedGate_PreservesBaselineDespiteRecordedEnd()
    {
        var goal = VerifyingHarness().Kernel.Goals.Single();
        var baseline = new OwnerGoalDetailSnapshot(goal, null, []);

        Assert.Equal(Render(baseline), Render(baseline with { LastGateEnd = End() }));
        Assert.Equal("Landing: gate running since 21:04", LandingLine(Render(baseline)));
    }

    [Theory]
    [InlineData(true, true, "Landing: gate running since 21:04")]
    [InlineData(false, true, "Landing: gate running")]
    [InlineData(false, false, "Landing: verifying, no live gate")]
    public void Render_MissingTimes_UsesTimelineOrOmitsSince(bool timeline, bool live, string expected)
    {
        var goal = VerifyingHarness(timeline).Kernel.Goals.Single();
        var detail = new OwnerGoalDetailSnapshot(goal, null, [], GateObserved: true,
            Gate: live ? LiveGate(null) : null);

        Assert.Equal(expected, LandingLine(Render(detail)));
    }

    [Fact]
    public void Render_LocalZone_ConvertsBothTimelineAndEndTimes()
    {
        var goal = VerifyingHarness().Kernel.Goals.Single();
        var detail = new OwnerGoalDetailSnapshot(goal, null, [], GateObserved: true, LastGateEnd: End());
        var zone = TimeZoneInfo.CreateCustomTimeZone("fixed-offset", TimeSpan.FromHours(2), "fixed-offset", "fixed-offset");
        var output = new OwnerConsoleHarness.FakeOutput();

        OwnerConsoleGoalDetailFormatter.Render(detail, output, zone);

        Assert.Equal("Landing: verifying since 23:04, no live gate; last gate ended 23:04: blocked-build-slot",
            LandingLine(output.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compose_ObservedIdleGate_ReadsSourceOnceAndMatchesQuery(bool useOverride)
    {
        var harness = VerifyingHarness();
        var gate = OwnerConsoleGateProgress.Idle() with { Ended = [End()] };
        var calls = 0;
        OwnerConsoleGateProgress Source() { calls++; return gate; }
        var output = new OwnerConsoleHarness.FakeOutput();

        var composed = useOverride
            ? await OwnerConsoleGoalDetailFormatter.ComposeAsync(new OwnerGoalDetailQuery(harness.State, harness.Tail),
                Id, output, CancellationToken.None, TimeZoneInfo.Utc, Source)
            : await OwnerConsoleGoalDetailFormatter.ComposeAsync(harness.State, harness.Tail, Id, output,
                CancellationToken.None, TimeZoneInfo.Utc, Source);

        Assert.Equal(1, calls);
        calls = 0;
        var detail = await new OwnerGoalDetailQuery(harness.State, harness.Tail, Source).LoadAsync(Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(1, calls);
        Assert.Same(detail.Goal, composed);
        Assert.True(detail.GateObserved);
        Assert.Null(detail.Gate);
        Assert.Equal("blocked-build-slot", detail.LastGateEnd?.Result);
        Assert.Equal(EndedAt, detail.LastGateEnd?.At);
        Assert.Equal(Render(detail), output.Text);
        Assert.Equal("Landing: verifying since 21:04, no live gate; last gate ended 21:04: blocked-build-slot",
            LandingLine(output.Text));
    }

    private static OwnerConsoleGateEnd End() => new("aaaaaaaa", EndedAt, "blocked-build-slot");

    private static OwnerConsoleGateProgress LiveGate(DateTimeOffset? at) =>
        new(true, "solo", [new(Id, "Member")], at, TimeSpan.Zero, [], 0, 0, 0, 0);

    private static string LandingLine(string text) =>
        Assert.Single(text.Split(Environment.NewLine).Where(line => line.StartsWith("Landing:", StringComparison.Ordinal)));

    private static string Render(OwnerGoalDetailSnapshot detail)
    {
        var output = new OwnerConsoleHarness.FakeOutput();
        OwnerConsoleGoalDetailFormatter.Render(detail, output, TimeZoneInfo.Utc);
        return output.Text;
    }

    private static OwnerConsoleHarness VerifyingHarness(bool timeline = true)
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal(Id, "Member", AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(goal => goal with
        {
            Status = GoalStatus.Verifying,
            Tasks = goal.Tasks.Select(task => task with { Status = WorkTaskStatus.Completed }).ToArray(),
            Timeline = timeline ? [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "entered Verifying", Entered)] : []
        }).ToArray() });
        return harness;
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }
}
