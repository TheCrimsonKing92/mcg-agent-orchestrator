using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Fixture = ConductorInvariantSliceTwoWiringTests.Fixture;

// Parallel-safe: every replay owns its files, attention database and injected clock.
public sealed class ConductorInvariantFinishedAttemptReplayTests
{
    [Fact]
    public void Evaluate_PassedButUnreconciled_RaisesOnceAndResolvesAfterSave()
    {
        using var fixture = new Fixture();
        var finish = fixture.Clock.Now;
        var attempt = fixture.Attempt();
        fixture.Save(attempt, exitAt: finish.AddSeconds(10));
        using (var document = JsonDocument.Parse(File.ReadAllText(attempt.MetadataPath)))
            Assert.Equal(1, document.RootElement.GetProperty("outcome").GetInt32());
        var observed = Assert.Single(fixture.Reader.Read(fixture.Goals, finish).FinishedAttempts!);
        Assert.Equal(finish, observed.FinishedAt); // CompletedAt is earlier than the exit write.
        for (var minute = 0; minute <= 5; minute++)
        {
            fixture.Tick(finish.AddMinutes(minute));
            Assert.Empty(fixture.Events("INVARIANT_BREACH"));
            Assert.Empty(fixture.OpenItems());
        }
        fixture.Tick(finish.AddMinutes(6));
        var breach = Assert.Single(fixture.Events("INVARIANT_BREACH"));
        Assert.StartsWith($"INVARIANT_BREACH kind=finished-unreconciled record={attempt.AttemptId} ",
            breach.GetProperty("detail").GetString());
        Assert.Equal(finish.AddMinutes(6), breach.GetProperty("timestamp").GetDateTimeOffset());
        Assert.Equal($"invariant-breach:finished-unreconciled:{attempt.AttemptId}",
            Assert.Single(fixture.OpenItems()).CorrelationKey);
        for (var minute = 7; minute <= 26; minute++)
        {
            fixture.Tick(finish.AddMinutes(minute));
            Assert.Single(fixture.Events("INVARIANT_BREACH"));
            Assert.Single(fixture.OpenItems());
        }
        fixture.Save(attempt with { ReconciledAt = fixture.Clock.Now });
        fixture.Tick(finish.AddMinutes(27));
        Assert.Equal($"INVARIANT_CLEARED kind=finished-unreconciled record={attempt.AttemptId}",
            Assert.Single(fixture.Events("INVARIANT_CLEARED")).GetProperty("detail").GetString());
        Assert.Empty(fixture.OpenItems());
        fixture.Tick(finish.AddMinutes(28));
        Assert.Single(fixture.Events("INVARIANT_BREACH"));
        Assert.Single(fixture.Events("INVARIANT_CLEARED"));
    }

    [Theory]
    [InlineData("reconciled-before-threshold")]
    [InlineData("running-without-result")]
    [InlineData("parked-holder-identity-changed")]
    [InlineData("superseded-by-running")]
    [InlineData("outside-scan-window")]
    public void Evaluate_NonQualifyingAttempt_StaysSilentForThirtyMinutes(string control)
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
        for (var minute = 0; minute <= 30; minute++)
        {
            if (control == "reconciled-before-threshold" && minute == 5)
            {
                fixture.Clock.Now = start.AddMinutes(4).AddSeconds(59);
                fixture.Save(attempt with { ReconciledAt = fixture.Clock.Now });
            }
            if (control == "running-without-result")
            {
                fixture.Clock.Now = start.AddMinutes(minute);
                fixture.Save(attempt with { LastHeartbeatAt = fixture.Clock.Now }, exit: false);
            }
            fixture.Tick(start.AddMinutes(minute));
            Assert.Empty(fixture.Events("INVARIANT_BREACH"));
            Assert.Empty(fixture.OpenItems());
        }
    }

    [Theory]
    [InlineData(GoalStatus.Parked)]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Failed)]
    [InlineData(GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Superseded)]
    public void Read_NonActiveGoal_DropsFinishedAttempt(GoalStatus status)
    {
        using var fixture = new Fixture();
        fixture.Save(fixture.Attempt());
        fixture.Goals[0] = (fixture.Goals[0].Id, status);
        Assert.Empty(fixture.Reader.Read(fixture.Goals, fixture.Clock.Now).FinishedAttempts!);
        Assert.Empty(fixture.Reader.Read([], fixture.Clock.Now).FinishedAttempts!);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Read_ResultOnlyOrSiblingFallback_UsesFinishWriteTime(bool completed, bool fallback)
    {
        using var fixture = new Fixture();
        var finish = fixture.Clock.Now;
        var attempt = fixture.Attempt() with
        {
            CompletedAt = completed ? finish.AddSeconds(10) : null,
            ExitCodePath = "missing.exit.txt",
            ResultPath = fallback ? "" : "relative.result.json"
        };
        fixture.Save(attempt, exit: false);
        var resultPath = Path.Combine(Path.GetDirectoryName(attempt.MetadataPath)!,
            fallback ? attempt.AttemptId + ".result.json" : attempt.ResultPath);
        File.WriteAllText(resultPath, "{}");
        File.SetLastWriteTimeUtc(resultPath, finish.UtcDateTime);
        var observed = Assert.Single(fixture.Reader.Read(fixture.Goals, finish).FinishedAttempts!);
        Assert.Equal(finish, observed.FinishedAt);
    }

    [Fact]
    public void Read_NewerReconciledAcrossStores_HidesOldAttemptOfSameKind()
    {
        using var fixture = new Fixture();
        var old = fixture.Attempt();
        fixture.Save(old);
        fixture.Save(fixture.Attempt(store: "acceptance-gate-attempts", suffix: "newer") with
        {
            Kind = old.Kind, StartedAt = old.StartedAt.AddSeconds(1), ReconciledAt = fixture.Clock.Now
        });
        Assert.Empty(fixture.Reader.Read(fixture.Goals, fixture.Clock.Now).FinishedAttempts!);
        // Different kinds remain independent even across stores.
        var otherKind = fixture.Attempt(store: "acceptance-gate-attempts", suffix: "other-kind");
        fixture.Save(otherKind);
        Assert.Equal(otherKind.AttemptId, Assert.Single(
            fixture.Reader.Read(fixture.Goals, fixture.Clock.Now).FinishedAttempts!).AttemptId);
    }

    [Fact]
    public void Read_MalformedMetadataAndSidecar_KeepValidAttempt()
    {
        using var fixture = new Fixture();
        var attempt = fixture.Attempt();
        fixture.Save(attempt);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(attempt.MetadataPath)!, "bad.attempt.json"), "{bad");
        File.WriteAllText(attempt.MetadataPath + ".holder.json", "{sidecar");
        Assert.Equal(attempt.AttemptId, Assert.Single(
            fixture.Reader.Read(fixture.Goals, fixture.Clock.Now).FinishedAttempts!).AttemptId);
    }
}
