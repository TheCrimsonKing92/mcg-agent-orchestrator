using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: immutable fixtures and an injected clock; no process or shared files.
public sealed class OwnerConsoleGateProgressTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 1, 0, 0, TimeSpan.Zero);
    private static readonly OwnerConsoleGateManifestLane[] Manifest = [new("A", 100), new("B", 200), new("C", 300)];

    [Fact]
    public void CohortFixtureJoinsMembersLanesRemoteExecutorAndNewestTenMedians()
    {
        var events = new[]
        {
            Event(-600, "acceptance-cohort", "ACCEPTANCE_COHORT_ENTRY members=776a132e,eade84cb"),
            Event(-590, "gate-progress", Progress("verification-check", "A", 0)),
            Event(-500, "gate-progress", Progress("shard-complete", "A", 90000) + " outcome=passed failed=0"),
            Event(-80, "gate-progress", Progress("verification-check", "B", 20000) + " child_pid=123 output_bytes=100 heartbeat=build-slots/activity-2.heartbeat.json"),
            Event(-50, "remote-lane", "REMOTE_LANE executor=machine-two lane=C attempt=1 outcome=completed", null),
            Event(-50, "gate-progress", Progress("remote-shard-complete", "C", 300000)),
            Event(-10, "acceptance-cohort", "ACCEPTANCE_COHORT_INFLIGHT members=776a132e,eade84cb")
        };
        var history = new List<OwnerConsoleGateDuration> { new("infrastructure tests: A", 100, Now), new("infrastructure tests: B", 200, Now) };
        history.Add(new("infrastructure tests: C", 99999, Now.AddDays(-1)));
        history.AddRange(Enumerable.Range(1, 10).Select(index => new OwnerConsoleGateDuration("infrastructure tests: C", 245 + index * 10, Now.AddMinutes(-index))));

        var gate = OwnerConsoleGateProgress.Build(Now, events, Manifest, history, id => "Title " + id);

        Assert.True(gate.Live);
        Assert.Equal("cohort", gate.Kind);
        Assert.Equal(["776a132e", "eade84cb"], gate.Members.Select(member => member.Id));
        Assert.Equal("Title 776a132e", gate.Members[0].Title);
        Assert.Equal(Now.AddSeconds(-600), gate.StartedAt);
        Assert.Equal(TimeSpan.FromMinutes(10), gate.Elapsed);
        Assert.Equal([OwnerConsoleGateLaneState.Done, OwnerConsoleGateLaneState.Running, OwnerConsoleGateLaneState.Done], gate.Lanes.Select(lane => lane.State));
        Assert.Equal("passed", gate.Lanes[0].Outcome);
        Assert.Equal("unknown", gate.Lanes[2].Outcome);
        Assert.Equal("local slot 2", gate.Lanes[1].Location);
        Assert.Equal("machine-two", gate.Lanes[2].Location);
        Assert.Equal(300d, gate.Lanes[2].MedianSeconds);
        Assert.Equal(6d / 7, gate.Fraction, 10); // 600s gate elapsed / (600s + 100s remaining).
        Assert.Equal(100, gate.RemainingSeconds);
        Assert.Equal(2, gate.DoneCount);
        Assert.Equal(3, gate.TotalCount);
    }

    [Fact]
    public void NoLiveEventsShowsIdleSinceLastChildCompleted()
    {
        var gate = OwnerConsoleGateProgress.Build(Now,
            [Event(-720, "acceptance-cohort", "ACCEPTANCE_COHORT_CHILD_COMPLETED kind=cohort members=776a132e+eade84cb identity=cohort:old")], Manifest, []);
        Assert.False(gate.Live);
        Assert.Equal(TimeSpan.FromMinutes(12), gate.IdleAge);
        Assert.Empty(gate.Lanes);
        Assert.False(OwnerConsoleGateProgress.Build(Now, [], Manifest, []).Live);
    }

    [Fact]
    public void GatheringAndHeldAfterFaultDoNotOpenPhantomGates()
    {
        var events = new[]
        {
            Event(-600, "acceptance-cohort", "ACCEPTANCE_COHORT tick=1 outcome=gathering goal=776a132e"),
            Event(-500, "acceptance-cohort", "ACCEPTANCE_COHORT_ENTRY members=776a132e,eade84cb"),
            Event(-400, "acceptance-cohort", "ACCEPTANCE_COHORT_RECONCILED_DEAD kind=cohort members=776a132e+eade84cb identity=cohort:old"),
            Event(-300, "acceptance-cohort", "ACCEPTANCE_COHORT tick=2 goal=776a132e result=held reason=cohort-fault"),
            Event(-200, "acceptance-cohort", "ACCEPTANCE_COHORT tick=3 goal=776a132e result=escalated reason=cohort-fault")
        };

        AssertIdle(events.Take(1));
        AssertIdle(events);
        AssertIdle(events.Skip(3));

        static void AssertIdle(IEnumerable<OwnerConductEvent> events)
        {
            var gate = OwnerConsoleGateProgress.Build(Now, events, Manifest, []);
            Assert.False(gate.Live);
            Assert.Equal("idle", gate.Kind);
            Assert.Empty(gate.Members);
            Assert.Empty(gate.Lanes);
        }
    }

    [Fact]
    public void GatheringBeforePairingDoesNotHideTheRunningCohort()
    {
        var gate = OwnerConsoleGateProgress.Build(Now,
        [
            Event(-600, "acceptance-cohort", "ACCEPTANCE_COHORT tick=1 outcome=gathering goal=776a132e"),
            Event(-100, "acceptance-cohort", "ACCEPTANCE_COHORT_ENTRY members=776a132e,eade84cb"),
            Event(-10, "gate-progress", Progress("verification-check", "A", 0))
        ], Manifest, []);

        Assert.True(gate.Live);
        Assert.Equal("cohort", gate.Kind);
        Assert.Equal(["776a132e", "eade84cb"], gate.Members.Select(member => member.Id));
        Assert.Equal(Now.AddSeconds(-100), gate.StartedAt);
        Assert.Empty(gate.OtherLiveGates!);
    }

    [Fact]
    public void GoalOnlyCohortDiagnosticsCanStillJoinAnExistingLiveGate()
    {
        var gate = OwnerConsoleGateProgress.Build(Now,
        [
            Event(-1000, "acceptance-cohort", "ACCEPTANCE_COHORT_ENTRY members=776a132e,eade84cb"),
            Event(-10, "acceptance-cohort", "ACCEPTANCE_COHORT tick=2 outcome=inflight goal=776a132e")
        ], Manifest, []);

        Assert.True(gate.Live);
        Assert.Equal("cohort", gate.Kind);
        Assert.Equal(["776a132e", "eade84cb"], gate.Members.Select(member => member.Id));
        Assert.Equal(Now.AddSeconds(-1000), gate.StartedAt);
    }

    [Fact]
    public void RunningProgressKeepsFractionalModeledRemainingBeforeDisplayTruncation()
    {
        var gate = OwnerConsoleGateProgress.Build(Now,
        [
            Event(-1, "acceptance-cohort", "ACCEPTANCE_COHORT_ENTRY members=776a132e,eade84cb"),
            Event(0, "gate-progress", Progress("verification-check", "A", 500))
        ], [new("A", 1.25)], []);

        Assert.Equal(TimeSpan.FromSeconds(1), gate.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(0.5), Assert.Single(gate.Lanes).Elapsed);
        Assert.Equal(0, gate.RemainingSeconds); // The raw model has 0.75s left.
        Assert.Equal(4d / 7, gate.Fraction, 10);
        Assert.Equal("[#####-----] estimate 57%  ~0m left", OwnerConsoleGateFormatter.ProgressLine(gate));
        Assert.Equal(0, gate.DoneCount);
    }

    [Theory]
    [InlineData("ACCEPTANCE goal=776a132e result=started", "solo", 1)]
    [InlineData("ACCEPTANCE_COHORT_ENTRY members=776a132e,eade84cb", "cohort", 2)]
    [InlineData("ACCEPTANCE_COHORT_ENTRY members=776a132e,eade84cb train=abc", "train", 2)]
    public void GateWallTimeBeforeLaneStartEarnsEstimatedProgress(
        string entry, string kind, int members)
    {
        var gate = OwnerConsoleGateProgress.Build(Now,
        [
            Event(-60, "acceptance-cohort", entry),
            Event(-40, "gate-progress", "PHASE_PROGRESS goal=776a132e phase=verification-check target=A elapsed_ms=0")
        ], [new("A", 100), new("B", 100)], []);

        Assert.True(gate.Live);
        Assert.Equal(kind, gate.Kind);
        Assert.Equal(members, gate.Members.Count);
        Assert.Equal(TimeSpan.FromSeconds(60), gate.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(40), gate.Lanes[0].Elapsed);
        Assert.Equal([OwnerConsoleGateLaneState.Running, OwnerConsoleGateLaneState.Queued],
            gate.Lanes.Select(lane => lane.State));
        Assert.Equal(160, gate.RemainingSeconds);
        Assert.Equal(3d / 11, gate.Fraction, 10);
        Assert.Equal(0, gate.DoneCount);
        Assert.Equal(2, gate.TotalCount);
        Assert.False(gate.Failing);
        Assert.Contains("0/2", OwnerConsoleGateFormatter.StatusSegment(gate));
        var lines = OwnerConsoleGateLayout.Lines(gate, 120, false);
        Assert.Equal(" x 0 failed   > 1 running   + 0 passed   = 0 cached   . 1 queued", lines[1 + members]);
        Assert.Equal("[##--------] estimate 27%  ~3m left", lines[2 + members]);
    }

    [Theory]
    [InlineData(100, 0, 0, 0, 100)] // No gate elapsed, positive remaining.
    [InlineData(100, 100000, 0, 0, 0)] // Zero denominator with unfinished work.
    [InlineData(100, 120000, 10, 0.99, 0)] // Running beyond its median is still unfinished.
    [InlineData(100, 0, 1000000, 0.99, 100)] // Long pre-lane wait must also cap at 99%.
    [InlineData(0, 0, 60, 0, 0)] // No modeled duration, despite positive elapsed time.
    public void UnfinishedEstimatesAreFiniteBoundedAndNeverDisplayCompletion(
        double modeledSeconds, int laneElapsedMs, int gateElapsedSeconds, double fraction, int remaining)
    {
        var gate = OwnerConsoleGateProgress.Build(Now,
        [
            Event(-gateElapsedSeconds, "acceptance-cohort", "ACCEPTANCE_COHORT_ENTRY members=776a132e,eade84cb"),
            Event(0, "gate-progress", Progress("verification-check", "A", laneElapsedMs))
        ], [new("A", modeledSeconds)], []);

        Assert.True(gate.Live);
        Assert.True(double.IsFinite(gate.Fraction));
        Assert.InRange(gate.Fraction, 0, 0.99);
        Assert.Equal(fraction, gate.Fraction, 10);
        Assert.Equal(remaining, gate.RemainingSeconds);
        Assert.Equal(OwnerConsoleGateLaneState.Running, Assert.Single(gate.Lanes).State);
        Assert.Equal(0, gate.DoneCount);
        Assert.Equal(1, gate.TotalCount);
        var percent = (int)Math.Floor(fraction * 100);
        Assert.Contains($"estimate {percent}%", OwnerConsoleGateFormatter.ProgressLine(gate));
        Assert.DoesNotContain("100%", OwnerConsoleGateFormatter.ProgressLine(gate));
    }

    [Theory]
    [InlineData("shard-complete", "passed", false)]
    [InlineData("shard-complete", "failed", true)]
    [InlineData("cache-hit", "unknown", false)]
    public void FinishedModeledWorkAtZeroElapsedPreservesCountsAndOutcomes(
        string phase, string outcome, bool failing)
    {
        var gate = OwnerConsoleGateProgress.Build(Now,
            [Event(0, "gate-progress", Progress(phase, "A", 1000) + " outcome=" + outcome)],
            [new("A", 100)], []);

        Assert.Equal(TimeSpan.Zero, gate.Elapsed);
        Assert.Equal(0, gate.RemainingSeconds);
        Assert.Equal(1, gate.Fraction);
        Assert.Equal(1, gate.DoneCount);
        Assert.Equal(1, gate.TotalCount);
        Assert.Equal(failing, gate.Failing);
        var lane = Assert.Single(gate.Lanes);
        Assert.Equal(outcome, lane.Outcome);
        Assert.Equal(phase == "cache-hit" ? OwnerConsoleGateLaneState.Cached : OwnerConsoleGateLaneState.Done, lane.State);
        var lines = OwnerConsoleGateLayout.Lines(gate, 120, false);
        if (failing) Assert.DoesNotContain(lines, line => line.Contains("estimate"));
        else Assert.Contains("[##########] estimate 100%  ~0m left", lines);
    }

    [Fact]
    public void UnknownChecksWithoutAnyDurationModelKeepZeroProgress()
    {
        var gate = OwnerConsoleGateProgress.Build(Now,
            [Event(-60, "gate-progress", Progress("verification-check", "unknown", 0))], [], []);

        Assert.True(gate.Live);
        Assert.Equal(TimeSpan.FromSeconds(60), gate.Elapsed);
        Assert.Null(Assert.Single(gate.Lanes).MedianSeconds);
        Assert.Equal(0, gate.Fraction);
        Assert.Equal(0, gate.RemainingSeconds);
        Assert.Equal(0, gate.DoneCount);
        Assert.Equal(0, gate.TotalCount);
    }

    [Fact]
    public void FutureGateStartUsesNonnegativeElapsed()
    {
        var gate = OwnerConsoleGateProgress.Build(Now,
        [
            Event(1, "acceptance-cohort", "ACCEPTANCE_COHORT_ENTRY members=776a132e,eade84cb"),
            Event(2, "gate-progress", Progress("verification-check", "A", 0))
        ], [new("A", 100)], []);

        Assert.True(gate.Live);
        Assert.Equal(TimeSpan.Zero, gate.Elapsed);
        Assert.Equal(0, gate.Fraction);
        Assert.Equal(100, gate.RemainingSeconds);
    }

    [Fact]
    public void MissingHistoryUsesManifestEstimateInFractionAndRemaining()
    {
        var gate = OwnerConsoleGateProgress.Build(Now,
            [Event(-10, "gate-progress", "PHASE_PROGRESS goal=776a132e phase=shard-complete target=A elapsed_ms=1000 outcome=failed failed=2")],
            Manifest, [new("A", 100, Now)]);
        Assert.Equal(1d / 51, gate.Fraction, 10); // 10s gate elapsed, not the failed lane's 1s.
        Assert.Equal(500, gate.RemainingSeconds);
        Assert.True(gate.Lanes[1].IsEstimate);
        Assert.Equal(200d, gate.Lanes[1].MedianSeconds);
        Assert.Equal("failed", gate.Lanes[0].Outcome);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReconciledDeadFullIdsCloseShortIdCohortAndLateDiagnosticsCannotReviveIt(bool entryInTail)
    {
        var gate = OwnerConsoleGateProgress.Build(Now,
        new OwnerConductEvent[]
        {
            Event(-500, "acceptance-cohort", "ACCEPTANCE_COHORT_ENTRY members=776a132e,eade84cb"),
            Event(-400, "gate-progress", Progress("verification-check", "A", 1000)),
            Event(-300, "acceptance-cohort", "ACCEPTANCE_COHORT_RECONCILED_DEAD kind=cohort members=eade84cb000000000000000000000000+776a132e000000000000000000000000 attempt=1 identity=cohort:old"),
            Event(-200, "gate-progress", Progress("verification-check", "A", 1000))
        }.Where(item => entryInTail || item.Timestamp >= Now.AddSeconds(-300)), Manifest, []);
        Assert.False(gate.Live);
        Assert.Empty(gate.Lanes);
    }

    [Theory]
    [InlineData("ACCEPTANCE_COHORT_EXIT members=776a132e,eade84cb outcome=passed", false)]
    [InlineData("ACCEPTANCE_COHORT_EXIT members=776a132e,eade84cb outcome=inflight", true)]
    [InlineData("ACCEPTANCE_COHORT members=776a132e,eade84cb outcome=failed", false)]
    public void ExitDistinguishesInFlightFromTerminal(string end, bool live)
    {
        var gate = OwnerConsoleGateProgress.Build(Now,
            [Event(-100, "acceptance-cohort", "ACCEPTANCE_COHORT_ENTRY members=776a132e,eade84cb"), Event(-10, "acceptance-cohort", end)], Manifest, []);
        Assert.Equal(live, gate.Live);
    }

    [Fact]
    public void TrainUnknownLaneAndRerunRemainVisibleWithoutDistortingManifestCounts()
    {
        var gate = OwnerConsoleGateProgress.Build(Now,
        [
            Event(-90, "gate-progress", Progress("shard-complete", "A", 1000) + " outcome=passed train=abc scope=merge-train"),
            Event(-40, "gate-progress", Progress("verification-check", "A", 10000)),
            Event(-10, "gate-progress", Progress("verification-check", "extra", 0))
        ], Manifest, []);
        Assert.Equal("train", gate.Kind);
        Assert.Equal(4, gate.Lanes.Count);
        Assert.Null(gate.Lanes[3].MedianSeconds);
        Assert.False(gate.Lanes[3].IsManifestLane);
        Assert.Equal(3, gate.TotalCount);
        Assert.Equal(90d / 640, gate.Fraction, 10); // Rerunning A still earns elapsed modeled progress.
        Assert.Equal(550, gate.RemainingSeconds);
        Assert.Equal(OwnerConsoleGateLaneState.Running, gate.Lanes[0].State);
    }

    [Fact]
    public void EarliestGateIsSelectedAndAllLiveMembersCanBeLocated()
    {
        var gate = OwnerConsoleGateProgress.Build(Now,
        [
            Event(-600, "gate-progress", "PHASE_PROGRESS goal=11111111 phase=verification-check target=A elapsed_ms=0"),
            Event(-300, "gate-progress", "PHASE_PROGRESS goal=22222222 phase=verification-check target=B elapsed_ms=0")
        ], Manifest, []);
        Assert.Equal("11111111", gate.Members[0].Id);
        Assert.NotNull(gate.ForMember("22222222full"));
        Assert.Null(gate.ForMember("33333333"));
    }

    [Fact]
    public void QuietTimeTracksConstantOutputRunAndResetsOnChildOrByteChange()
    {
        OwnerConductEvent Sample(int age, string bytes, string pid = "123") => Event(age, "gate-progress",
            Progress("verification-check", "A", 0) + $" child_pid={pid} output_bytes={bytes}");
        var gate = OwnerConsoleGateProgress.Build(Now,
            [Sample(-480, "100"), Sample(-420, "200"), Sample(-60, "200")], Manifest, []);
        Assert.Equal(420d, gate.Lanes[0].QuietSeconds);
        var changed = OwnerConsoleGateProgress.Build(Now,
            [Sample(-480, "100"), Sample(-420, "200"), Sample(-60, "200", "456")], Manifest, []);
        Assert.Equal(60d, changed.Lanes[0].QuietSeconds);
    }

    [Fact]
    public void LocalRerunDoesNotInheritPriorRemoteExecutor()
    {
        var gate = OwnerConsoleGateProgress.Build(Now,
        [
            Event(-100, "acceptance-cohort", "ACCEPTANCE_COHORT_ENTRY members=776a132e,eade84cb"),
            Event(-80, "remote-lane", "REMOTE_LANE executor=old-machine lane=A attempt=1 outcome=completed", null),
            Event(-80, "gate-progress", Progress("remote-shard-complete", "A", 1000)),
            Event(-60, "gate-progress", Progress("verification-check", "A", 0) + " heartbeat=activity-2.heartbeat.json")
        ], Manifest, []);
        Assert.Equal(OwnerConsoleGateLaneState.Running, gate.Lanes[0].State);
        Assert.Equal("local slot 2", gate.Lanes[0].Location);
    }

    [Fact]
    public void LoaderReadsRealFilesSkipsMalformedLinesAndUsesTitlesAndHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gate-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var log = Path.Combine(directory, "conduct-events.log");
            var manifest = Path.Combine(directory, "acceptance-manifest.json");
            var history = Path.Combine(directory, "durations.jsonl");
            File.WriteAllText(log, "malformed\n" + JsonSerializer.Serialize(new
            {
                timestamp = Now.AddSeconds(-10), eventKind = "gate-progress", goalId = "776a132e",
                detail = "PHASE_PROGRESS goal=776a132e phase=verification-check target=\"infrastructure tests: A\" elapsed_ms=20000"
            }));
            File.WriteAllText(manifest, """{"engine":{"infrastructureTestLanes":[{"name":"A","estimatedSerialSeconds":100}]}}""");
            File.WriteAllText(history, "invalid\n" + JsonSerializer.Serialize(new { laneName = "infrastructure tests: A", durationSeconds = 120, recordedAt = Now }));
            var loader = new OwnerConsoleGateProgressLoader(log, manifest, history, new OwnerConsoleTestClock { Now = Now });

            var gate = Assert.IsType<OwnerConsoleGateProgress>(loader.Load(new Dictionary<string, string> { ["776a132efull"] = "A title" }));

            Assert.True(gate.Live);
            Assert.Equal("A title", gate.Members[0].Title);
            Assert.Equal(120d, Assert.Single(gate.Lanes).MedianSeconds);
            Assert.Equal(90, gate.RemainingSeconds);
            File.Delete(history);
            Assert.True(loader.Load(new Dictionary<string, string>())!.Lanes[0].IsEstimate);
            File.WriteAllText(manifest, "invalid");
            Assert.Null(loader.Load(new Dictionary<string, string>()));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string Progress(string phase, string lane, int elapsed) =>
        $"PHASE_PROGRESS goal=776a132e cohort=test members=776a132e,eade84cb phase={phase} target=\"infrastructure tests: {lane}\" elapsed_ms={elapsed}";

    // Round-trip the real JSONL envelope instead of relying on an invented text-only log.
    private static OwnerConductEvent Event(int age, string kind, string detail, string? goal = "776a132e") =>
        OwnerConsoleGateProgress.ParseEvent(JsonSerializer.Serialize(new { timestamp = Now.AddSeconds(age), eventKind = kind, goalId = goal, detail }))!;
}
