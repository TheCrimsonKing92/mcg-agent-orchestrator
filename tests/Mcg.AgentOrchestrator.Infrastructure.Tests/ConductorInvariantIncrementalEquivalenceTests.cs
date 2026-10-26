using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = ConductorInvariantObservationReaderTests.Fixture;

// Parallel-safe: the fixture owns all attempts, heartbeat files, clock and database.
public sealed class ConductorInvariantIncrementalEquivalenceTests
{
    [Fact]
    public void Read_ScriptedChangesRotationsAndPrune_MatchesFullReadAtEveryStep()
    {
        using var fixture = new Fixture();
        var writer = new ConductEventLogWriter(fixture.Log, maxBytes: 2048, utcNow: () => fixture.Clock.Now, rotatedGenerationCount: 2);
        var laneRoot = Path.Combine(fixture.Root, "acceptance-gate-attempts");
        var preReviewRoot = Path.Combine(fixture.Root, "pre-review-evidence-attempts");
        var reader = NewReader(true);
        var steps = 0;
        Check();
        for (var goal = 0; goal < 3; goal++)
        {
            Append($"GOAL goal={fixture.Goals[goal].Id.Value[..8]} result=held reason=acceptance_width_1_reached");
            Check();
        }
        Assert.Equal(3, reader.Read(fixture.Goals, fixture.Clock.Now).GateReadyGoals.Count);
        fixture.Goals[0] = (fixture.Goals[0].Id, GoalStatus.Parked);
        Assert.Equal(2, Check().GateReadyGoals.Count);
        fixture.Goals[0] = (fixture.Goals[0].Id, GoalStatus.Verified);
        Assert.Equal(3, Check().GateReadyGoals.Count);

        var futureAt = fixture.Clock.Now.AddMinutes(3);
        writer.Append("goal", null, $"GOAL goal={fixture.Goals[2].Id.Value[..8]} result=executed", futureAt);
        Assert.Equal(3, Check().GateReadyGoals.Count);
        fixture.Clock.Now = futureAt;
        Assert.Equal(2, Check().GateReadyGoals.Count);
        Append($"LOOP_RELAUNCH_SCHEDULED tick=7 goal={fixture.Goals[0].Id.Value} changedFiles=1 coalesced=true");
        Check();
        Append("LOOP_RELAUNCH_DRAIN tick=8 liveGates=1 elapsedSeconds=30");
        var drainAt = fixture.Clock.Now;
        Assert.Equal(drainAt, Check().Relaunch!.LastLiveGateAt);
        Append($"ACCEPTANCE_COHORT_ADOPTION_REFUSED members={fixture.MembersKey} reason=identity-changed field=main");
        Assert.Single(Check().Refusals);

        var grouped = fixture.NewAttempt() with { Detail = "refused: identity-changed", ReconciledAt = fixture.Clock.Now };
        ConductorGroupedGateAttemptCoordinator.Save(grouped);
        Assert.Equal(drainAt, Check().LastGateProgressAt);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
        grouped = grouped with { ReconciledAt = fixture.Clock.Now, Detail = "passed", Outcome = "Reconciled" };
        ConductorGroupedGateAttemptCoordinator.Save(grouped);
        Assert.Equal(fixture.Clock.Now, Check().LastGateProgressAt);

        WriteLane(0);
        Assert.Equal(fixture.Clock.Now, Check().LastGateProgressAt);
        fixture.Goals[3] = (fixture.Goals[3].Id, GoalStatus.Completed);
        WriteLane(3);
        Assert.Equal(fixture.Clock.Now, Check().LastGateProgressAt);
        SaveFinished(laneRoot, "gate");
        Assert.Single(Check().FinishedAttempts!);
        SaveFinished(preReviewRoot, "pre-review-evidence");
        Assert.Equal(2, Check().FinishedAttempts!.Count);

        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
        var partial = JsonSerializer.Serialize(new { timestamp = fixture.Clock.Now,
            detail = $"GOAL goal={fixture.Goals[1].Id.Value[..8]} result=executed" });
        var split = partial.Length / 2;
        File.AppendAllText(fixture.Log, partial[..split]);
        Assert.Contains(Check().GateReadyGoals, goal => goal.GoalId == fixture.Goals[1].Id.Value);
        File.AppendAllText(fixture.Log, partial[split..] + "\n");
        Assert.DoesNotContain(Check().GateReadyGoals, goal => goal.GoalId == fixture.Goals[1].Id.Value);

        var rotations = new HashSet<string>(StringComparer.Ordinal);
        string? oldest = null;
        var pruned = false;
        for (var index = 0; index < 32; index++)
        {
            Append("ACCEPTANCE_COHORT_INFLIGHT sequence=" + index + " padding=" + new string('x', 600));
            Check();
            var paths = Directory.GetFiles(fixture.Root, "conduct-events-*.log").Order(StringComparer.Ordinal).ToArray();
            foreach (var path in paths) rotations.Add(path);
            oldest ??= paths.FirstOrDefault();
            if (oldest is not null && !File.Exists(oldest)) { pruned = true; break; }
        }
        Assert.True(steps >= 14);
        Assert.True(rotations.Count >= 2, "At least two rotations must be exercised.");
        Assert.True(pruned, "The full-read comparison must exercise a pruned generation.");

        ConductorInvariantObservationReader NewReader(bool incremental) => new(fixture.Log, fixture.AttemptRoot,
            _ => [], laneRoot, [laneRoot, preReviewRoot], incremental: incremental);

        ConductorInvariantObservation Check()
        {
            steps++;
            var actual = reader.Read(fixture.Goals, fixture.Clock.Now);
            var expected = NewReader(false).Read(fixture.Goals, fixture.Clock.Now);
            Assert.Equal(expected.Now, actual.Now);
            Assert.Equal(expected.GateReadyGoals.ToArray(), actual.GateReadyGoals.ToArray());
            Assert.Equal(expected.LastGateProgressAt, actual.LastGateProgressAt);
            Assert.Equal(expected.Refusals.ToArray(), actual.Refusals.ToArray());
            Assert.Equal(expected.FinishedAttempts!.ToArray(), actual.FinishedAttempts!.ToArray());
            Assert.Equal(expected.Relaunch, actual.Relaunch);
            Assert.Equal(expected.MainSha, actual.MainSha);
            return actual;
        }

        void Append(string detail)
        {
            fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
            writer.Append("replay", null, detail);
        }

        void WriteLane(int goal)
        {
            fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
            var at = fixture.Clock.Now;
            var path = Path.Combine(laneRoot, fixture.Goals[goal].Id.Value, "lane.gate-heartbeat.json");
            GateHeartbeatArtifacts.Write(path, new(null, "test", "lane", null, null, null, "running", at, at, at, 0, 0, 0));
            File.SetLastWriteTimeUtc(path, at.UtcDateTime);
            Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(path)!, at.UtcDateTime);
        }

        void SaveFinished(string root, string kind)
        {
            fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
            var at = fixture.Clock.Now;
            var goal = fixture.Goals[0].Id.Value;
            var id = goal + "-" + kind;
            var directory = Path.Combine(root, goal);
            Directory.CreateDirectory(directory);
            string PathFor(string suffix) => Path.Combine(directory, id + suffix);
            var attempt = new ConductorParallelAcceptanceAttempt(id, goal, goal[..8], 0, "candidate", "main",
                at.AddMinutes(-1), at, 0, ConductorParallelAcceptanceAttemptOutcome.Passed,
                PathFor(".out.log"), PathFor(".err.log"), PathFor(".exit.txt"), PathFor(".heartbeat.json"),
                PathFor(".result.json"), PathFor(".attempt.json"), CompletedAt: at, Kind: kind);
            File.WriteAllText(attempt.MetadataPath, JsonSerializer.Serialize(attempt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            File.SetLastWriteTimeUtc(attempt.MetadataPath, at.UtcDateTime);
            File.WriteAllText(attempt.ExitCodePath, "0");
            File.SetLastWriteTimeUtc(attempt.ExitCodePath, at.UtcDateTime);
        }
    }
}
