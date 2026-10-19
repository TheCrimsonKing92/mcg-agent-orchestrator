using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = ConductorInvariantSliceTwoWiringTests.Fixture;

// Parallel-safe: each replay owns its logs, sidecars, SQLite store and fixture clock.
public sealed class ConductorInvariantFinishedAttemptDiagnosisReplayTests
{
    [Fact]
    public void Evaluate_SettledHolderStillHeld_ReportsIncidentOnceAtSixMinutes()
    {
        using var fixture = new Fixture();
        var finish = fixture.Clock.Now;
        var attempt = fixture.Attempt();
        fixture.Save(attempt);
        Sidecars(attempt);
        IncidentLines(fixture, attempt, finish);
        var suffix = $",holderSettled:true,holderPhase:Claimed,lastGoalLineAt:{finish.AddMinutes(4):O}," +
            "goalLinesSinceFinish:2,heldOnAttemptSinceFinish:2";
        for (var minute = 0; minute <= 26; minute++)
        {
            fixture.Tick(finish.AddMinutes(minute));
            if (minute < 6)
            {
                Assert.Empty(fixture.Events("INVARIANT_BREACH"));
                Assert.Empty(fixture.OpenItems());
                continue;
            }
            var breach = Assert.Single(fixture.Events("INVARIANT_BREACH"));
            var detail = breach.GetProperty("detail").GetString()!;
            Assert.Equal(finish.AddMinutes(6), breach.GetProperty("timestamp").GetDateTimeOffset());
            Assert.StartsWith($"INVARIANT_BREACH kind=finished-unreconciled record={attempt.AttemptId} ", detail);
            Assert.EndsWith(suffix, detail);
            var evidence = detail.Split(" evidence=", 2)[1];
            Assert.DoesNotContain(" ", evidence);
            Assert.Contains(evidence, Assert.Single(fixture.OpenItems()).Body);
        }
        Assert.Empty(fixture.Events("INVARIANT_CLEARED"));
    }

    [Theory]
    [InlineData("no-later-lines", "true", "Claimed", -12, 0, 0)]
    [InlineData("no-sidecars", "none", "none", 4, 2, 2)]
    [InlineData("malformed-sidecars", "unreadable", "unreadable", 4, 2, 2)]
    [InlineData("different-attempt", "true", "Claimed", 4, 2, 0)]
    [InlineData("exact-finish", "true", "Claimed", 0, 0, 0)]
    public void Evaluate_DiagnosisVariants_ReportsExactSuffix(string control,
        string settled, string phase, int lastMinute, int lines, int held)
    {
        using var fixture = new Fixture();
        var finish = fixture.Clock.Now;
        var attempt = fixture.Attempt();
        fixture.Save(attempt);
        if (control != "no-sidecars") Sidecars(attempt, control == "malformed-sidecars");
        GoalLine(fixture, attempt.AttemptId, finish.AddMinutes(-12));
        if (control == "exact-finish") GoalLine(fixture, attempt.AttemptId, finish);
        else if (control != "no-later-lines")
            foreach (var minute in new[] { 2, 4 })
                GoalLine(fixture, control == "different-attempt" ? "other-attempt" : attempt.AttemptId, finish.AddMinutes(minute));
        for (var minute = 0; minute <= 6; minute++) fixture.Tick(finish.AddMinutes(minute));
        var detail = Assert.Single(fixture.Events("INVARIANT_BREACH")).GetProperty("detail").GetString()!;
        Assert.EndsWith($",holderSettled:{settled},holderPhase:{phase},lastGoalLineAt:{finish.AddMinutes(lastMinute):O}," +
            $"goalLinesSinceFinish:{lines},heldOnAttemptSinceFinish:{held}", detail);
        Assert.Single(fixture.OpenItems());
    }

    [Theory]
    [InlineData("incident")]
    [InlineData("reconciled-before-threshold")]
    [InlineData("running-without-result")]
    [InlineData("parked-holder-identity-changed")]
    [InlineData("superseded-by-running")]
    [InlineData("outside-scan-window")]
    public void Evaluate_EnrichedAndBaseline_PreserveEveryMinuteDecision(string control)
    {
        Assert.Equal(Replay(control, enriched: false), Replay(control, enriched: true));
    }

    [Fact]
    public void Read_IncidentLog_PreservesOutputsWithAttemptRootsDisabled()
    {
        using var fixture = new Fixture();
        var finish = fixture.Clock.Now;
        var attempt = fixture.Attempt();
        fixture.Save(attempt);
        Sidecars(attempt);
        IncidentLines(fixture, attempt, finish);
        var withoutAttempts = new ConductorInvariantObservationReader(fixture.Log,
            Path.Combine(fixture.Workspace.OrchestratorDirectory, "grouped-gate-attempts"),
            now => GateHeartbeatArtifacts.ReadStableSlots(new DotnetBuildStorageRoot(Path.Combine(fixture.Root, "build-storage")), now),
            Path.Combine(fixture.Workspace.OrchestratorDirectory, "acceptance-gate-attempts"), attemptRoots: null);
        for (var minute = 0; minute <= 26; minute++)
        {
            var now = finish.AddMinutes(minute);
            var actual = fixture.Reader.Read(fixture.Goals, now);
            var baseline = withoutAttempts.Read(fixture.Goals, now);
            Assert.Single(actual.FinishedAttempts!);
            Assert.Empty(baseline.FinishedAttempts!);
            Assert.Equal(baseline.GateReadyGoals, actual.GateReadyGoals);
            Assert.Equal(baseline.LastGateProgressAt, actual.LastGateProgressAt);
            Assert.Equal(baseline.Refusals, actual.Refusals);
            Assert.Equal(baseline.Relaunch, actual.Relaunch);
        }
    }

    private static string[] Replay(string control, bool enriched)
    {
        using var fixture = new Fixture();
        var start = fixture.Clock.Now;
        var attempt = fixture.Attempt(finishedAt: control == "outside-scan-window" ? start.AddHours(-25) : start);
        if (control == "running-without-result")
            attempt = attempt with { Outcome = ConductorParallelAcceptanceAttemptOutcome.Running,
                CompletedAt = null, LastHeartbeatAt = start };
        if (control == "parked-holder-identity-changed")
        {
            fixture.Goals[0] = (fixture.Goals[0].Id, GoalStatus.Parked);
            attempt = attempt with { Outcome = ConductorParallelAcceptanceAttemptOutcome.Failed,
                Detail = "Acceptance holder identity changed while claiming the attempt." };
        }
        fixture.Save(attempt, exit: control != "running-without-result",
            modifiedAt: control == "outside-scan-window" ? start.AddHours(-25) : start);
        if (control == "superseded-by-running")
            fixture.Save(fixture.Attempt(suffix: "newer") with
            {
                StartedAt = start, CompletedAt = null, Outcome = ConductorParallelAcceptanceAttemptOutcome.Running
            }, exit: false);
        if (enriched)
        {
            Sidecars(attempt);
            IncidentLines(fixture, attempt, start);
        }
        var snapshots = new List<string>();
        IReadOnlyDictionary<string, DateTimeOffset> active = new Dictionary<string, DateTimeOffset>();
        for (var minute = 0; minute <= 30; minute++)
        {
            if ((control == "reconciled-before-threshold" && minute == 5) || (control == "incident" && minute == 27))
            {
                fixture.Clock.Now = start.AddMinutes(minute - 1).AddSeconds(59);
                fixture.Save(attempt with { ReconciledAt = fixture.Clock.Now });
            }
            if (control == "running-without-result")
            {
                fixture.Clock.Now = start.AddMinutes(minute);
                fixture.Save(attempt with { LastHeartbeatAt = fixture.Clock.Now }, exit: false);
            }
            var now = start.AddMinutes(minute);
            var delta = ConductorInvariantWatchdog.Evaluate(fixture.Reader.Read(fixture.Goals, now), active);
            active = delta.Active;
            fixture.Tick(now);
            var raised = string.Join(";", delta.Raise.Select(b => $"{b.Key}@{b.Since:O}"));
            var cleared = string.Join(";", delta.Clear.Select(b => $"{b.Key}@{b.Since:O}"));
            var events = fixture.Events("INVARIANT_BREACH").Concat(fixture.Events("INVARIANT_CLEARED"))
                .OrderBy(row => row.GetProperty("timestamp").GetDateTimeOffset()).Select(EventIdentity);
            var count = fixture.OpenItems().Count;
            snapshots.Add($"{minute}|raise:{raised}|clear:{cleared}|events:{string.Join(";", events)}|open:{count}");
            Assert.Equal(control == "incident" && minute is >= 6 and <= 26 ? 1 : 0, count);
        }
        if (control == "incident")
        {
            Assert.Single(fixture.Events("INVARIANT_BREACH"));
            Assert.Single(fixture.Events("INVARIANT_CLEARED"));
        }
        else
        {
            Assert.Empty(fixture.Events("INVARIANT_BREACH"));
            Assert.Empty(fixture.Events("INVARIANT_CLEARED"));
        }
        return snapshots.ToArray();
    }

    private static string EventIdentity(JsonElement row) =>
        row.GetProperty("timestamp").GetDateTimeOffset().ToString("O") + ":" +
        row.GetProperty("detail").GetString()!.Split(" evidence=", 2)[0];

    private static void Sidecars(ConductorParallelAcceptanceAttempt attempt, bool malformed = false)
    {
        File.WriteAllText(attempt.MetadataPath + ".holder-settled.json", malformed ? "{bad" : "{\"Settled\":true}");
        File.WriteAllText(attempt.MetadataPath + ".holder.json", malformed ? "{bad" : "{\"Phase\":\"Claimed\"}");
    }

    private static void IncidentLines(Fixture fixture, ConductorParallelAcceptanceAttempt attempt, DateTimeOffset finish)
    {
        foreach (var minute in new[] { -12, 2, 4 }) GoalLine(fixture, attempt.AttemptId, finish.AddMinutes(minute));
    }

    private static void GoalLine(Fixture fixture, string attemptId, DateTimeOffset at) =>
        fixture.Append($"GOAL goal={fixture.Goals[0].Id.Value[..8]} result=held state=WorkspaceReady " +
            $"reason=Background_pre-tester-deferred_focused_evidence_is_running_in_attempt_{attemptId}.", at);
}
