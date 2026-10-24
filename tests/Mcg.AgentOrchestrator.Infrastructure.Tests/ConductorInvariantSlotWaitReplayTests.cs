using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Fixture = ConductorInvariantSliceTwoWiringTests.Fixture;

// Parallel-safe: each replay owns its files, attention database and injected clock.
public sealed class ConductorInvariantSlotWaitReplayTests
{
    [Fact]
    public void Evaluate_CapWaitThenReconciliation_NeverRaisesOrClears()
    {
        using var fixture = new Fixture();
        var attempt = Seed(fixture);
        var finish = attempt.CompletedAt!.Value;
        AssertCapObserved(fixture, finish);
        TickSilent(fixture, finish, 0, 17);

        fixture.Clock.Now = finish.AddMinutes(17).AddSeconds(30);
        fixture.Save(attempt with { ReconciledAt = fixture.Clock.Now });
        Hold(fixture, Running(attempt), fixture.Clock.Now);
        TickSilent(fixture, finish, 18, 30);
        Assert.Empty(fixture.Reader.Read(fixture.Goals, fixture.Clock.Now).FinishedAttempts!);
        Assert.Empty(fixture.Events("INVARIANT_CLEARED"));
    }

    [Fact]
    public void Evaluate_AbandonedWithoutCap_RaisesOnceAtMinuteSix()
    {
        using var fixture = new Fixture();
        var attempt = Seed(fixture, includeCap: false);
        var finish = attempt.CompletedAt!.Value;
        Assert.Null(Assert.Single(fixture.Reader.Read(fixture.Goals, finish)
            .FinishedAttempts!).LatestGoalLine!.Decision);
        TickSilent(fixture, finish, 0, 5);
        for (var minute = 6; minute <= 10; minute++) fixture.Tick(finish.AddMinutes(minute));
        AssertBreach(fixture, attempt, 6);
        Assert.DoesNotContain("slotWaitSince:", Assert.Single(fixture.Events("INVARIANT_BREACH"))
            .GetProperty("detail").GetString()!);
    }

    [Fact]
    public void Evaluate_UnreconciledCapWait_RaisesOnceAfterInclusiveCeiling()
    {
        using var fixture = new Fixture();
        var capAt = fixture.Clock.Now.AddSeconds(20);
        var attempt = Seed(fixture);
        var finish = attempt.CompletedAt!.Value;
        AssertCapObserved(fixture, finish);
        TickSilent(fixture, finish, 0, 60);
        for (var minute = 61; minute <= 65; minute++) fixture.Tick(finish.AddMinutes(minute));
        AssertBreach(fixture, attempt, 61);
        Assert.EndsWith($",slotWaitSince:{capAt:O}", Assert.Single(fixture.Events("INVARIANT_BREACH"))
            .GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("rung-two")]
    [InlineData("other-goal")]
    [InlineData("gate")]
    [InlineData("newer-hold")]
    public void Evaluate_NonSlotWaitShape_MatchesCapAbsentBaseline(string control)
    {
        using var baseline = new Fixture();
        using var variant = new Fixture();
        using var twin = new Fixture();
        var attempt = Seed(variant, control: control);
        var baselineAttempt = Seed(baseline, includeCap: false, control: control);
        var twinAttempt = Seed(twin, control: control, eligibleTwin: true);
        var finish = attempt.CompletedAt!.Value;
        Assert.Equal(finish, baselineAttempt.CompletedAt);
        Assert.Equal(finish, twinAttempt.CompletedAt);

        var observed = Assert.Single(variant.Reader.Read(variant.Goals, finish.AddMinutes(6)).FinishedAttempts!);
        Assert.Equal(control == "gate" ? "gate" : "pre-review-evidence", observed.Kind);
        Assert.Equal(control switch
        {
            "rung-two" => "dispatch-admission/2/slice-batch-admission",
            "gate" => "dispatch-admission/1/worker-cap",
            _ => null
        }, observed.LatestGoalLine!.Decision);
        AssertCapObserved(twin, finish.AddMinutes(6));

        for (var minute = 0; minute <= 10; minute++)
        {
            baseline.Tick(finish.AddMinutes(minute));
            variant.Tick(finish.AddMinutes(minute));
            twin.Tick(finish.AddMinutes(minute));
            Assert.Empty(twin.Events("INVARIANT_BREACH"));
            Assert.Empty(twin.OpenItems());
        }

        AssertBreach(baseline, baselineAttempt, 6);
        AssertBreach(variant, attempt, 6);
        var expected = baseline.Events("INVARIANT_BREACH").Select(row =>
            (row.GetProperty("timestamp").GetDateTimeOffset(), row.GetProperty("detail").GetString())).ToArray();
        var actual = variant.Events("INVARIANT_BREACH").Select(row =>
            (row.GetProperty("timestamp").GetDateTimeOffset(), row.GetProperty("detail").GetString())).ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual); // Includes the complete evidence, not just the breach kind.
        Assert.Equal(baseline.OpenItems().Select(item => item.CorrelationKey).ToArray(),
            variant.OpenItems().Select(item => item.CorrelationKey).ToArray());
        Assert.Empty(variant.Events("INVARIANT_CLEARED"));
    }

    [Fact]
    public void Read_CapDecision_RetainsTimestampWithoutChangingDiagnosis()
    {
        using var fixture = new Fixture();
        using var withoutDecision = new Fixture();
        var capAt = fixture.Clock.Now.AddSeconds(20);
        var attempt = Seed(fixture);
        Seed(withoutDecision, includeDecision: false);
        var now = attempt.CompletedAt!.Value.AddMinutes(1);
        var observed = Assert.Single(fixture.Reader.Read(fixture.Goals, now).FinishedAttempts!);
        var baseline = Assert.Single(withoutDecision.Reader.Read(withoutDecision.Goals, now).FinishedAttempts!);
        Assert.Equal("dispatch-admission/1/worker-cap", observed.LatestGoalLine!.Decision);
        Assert.Equal("held", observed.LatestGoalLine.Result);
        Assert.Equal(capAt, observed.LatestGoalLine.At);
        Assert.Null(baseline.LatestGoalLine!.Decision);
        Assert.NotNull(observed.Diagnosis);
        Assert.Equal(baseline.Diagnosis, observed.Diagnosis);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_LaterArrival_SelectsNewestTimestampThenLastTie(bool sameTimestamp)
    {
        using var fixture = new Fixture();
        var start = fixture.Clock.Now;
        var attempt = Seed(fixture);
        var capAt = start.AddSeconds(20);
        var laterArrivalAt = sameTimestamp ? capAt : capAt.AddSeconds(-1);
        Hold(fixture, Running(attempt), laterArrivalAt);
        var observed = Assert.Single(fixture.Reader.Read(fixture.Goals, attempt.CompletedAt!.Value)
            .FinishedAttempts!);
        Assert.Equal(capAt, observed.LatestGoalLine!.At);
        Assert.Equal(sameTimestamp ? null : "dispatch-admission/1/worker-cap",
            observed.LatestGoalLine.Decision);
    }

    private static ConductorParallelAcceptanceAttempt Seed(Fixture fixture, bool includeCap = true,
        string? control = null, bool eligibleTwin = false, bool includeDecision = true)
    {
        var start = fixture.Clock.Now;
        var finish = start.AddSeconds(23);
        var attempt = fixture.Attempt(finishedAt: finish,
            store: control == "gate" && !eligibleTwin ? "acceptance-gate-attempts" : "pre-review-evidence-attempts");
        Hold(fixture, Running(attempt), start);
        // The gate baseline keeps the same diagnosis timestamp when the cap line is omitted.
        if (control == "gate") Hold(fixture, Running(attempt), start.AddSeconds(20));
        if (includeCap)
        {
            var decision = DispatchAdmissionPolicy.Evaluate(new DispatchAdmissionFacts(4, 4, 4, 0, 4, 4));
            Assert.Equal(DispatchAdmissionAction.Hold, decision.Action);
            Assert.Equal(1, decision.DiscriminatingRung);
            Assert.Equal("worker-cap", decision.DiscriminatingEvidence);
            var held = new ConductorAdvanceOutcome.Held(GoalLifecycleState.WorkspaceReady, decision.Reason)
            {
                Decision = includeDecision ? decision.ToRecord() : null,
                Owner = ConductorHoldOwner.WorkerCapacity
            };
            Hold(fixture, held, start.AddSeconds(20), control == "other-goal" && !eligibleTwin ? 1 : 0);
        }
        if (control == "rung-two" && !eligibleTwin)
        {
            var decision = DispatchAdmissionPolicy.Evaluate(
                new DispatchAdmissionFacts(3, 4, 4, 0, 4, 4, false, "slice batch refused"));
            Assert.Equal(DispatchAdmissionAction.Hold, decision.Action);
            Assert.Equal(2, decision.DiscriminatingRung);
            Hold(fixture, new ConductorAdvanceOutcome.Held(GoalLifecycleState.WorkspaceReady, decision.Reason)
            {
                Decision = decision.ToRecord()
            }, start.AddSeconds(21));
        }
        if (control == "newer-hold" && !eligibleTwin)
            Hold(fixture, Running(attempt), finish.AddMinutes(1));
        fixture.Clock.Now = finish;
        fixture.Save(attempt);
        return attempt;
    }

    private static ConductorAdvanceOutcome.Held Running(ConductorParallelAcceptanceAttempt attempt) =>
        new(GoalLifecycleState.WorkspaceReady,
            $"Background pre-tester-deferred focused evidence is running in attempt {attempt.AttemptId}.");

    private static void Hold(Fixture fixture, ConductorAdvanceOutcome.Held held, DateTimeOffset at, int goal = 0) =>
        fixture.Append(ConductorBatchLoop.FormatGoalProgressLine(fixture.Goals[goal].Id.Value[..8], held), at);

    private static void AssertCapObserved(Fixture fixture, DateTimeOffset now)
    {
        var observed = Assert.Single(fixture.Reader.Read(fixture.Goals, now).FinishedAttempts!);
        Assert.Equal("pre-review-evidence", observed.Kind);
        Assert.Equal("held", observed.LatestGoalLine!.Result);
        Assert.Equal("dispatch-admission/1/worker-cap", observed.LatestGoalLine.Decision);
    }

    private static void TickSilent(Fixture fixture, DateTimeOffset finish, int first, int last)
    {
        for (var minute = first; minute <= last; minute++)
        {
            fixture.Tick(finish.AddMinutes(minute));
            Assert.Empty(fixture.Events("INVARIANT_BREACH"));
            Assert.Empty(fixture.Events("INVARIANT_CLEARED"));
            Assert.Empty(fixture.OpenItems());
        }
    }

    private static void AssertBreach(Fixture fixture, ConductorParallelAcceptanceAttempt attempt, int minute)
    {
        var finish = attempt.CompletedAt!.Value;
        var breach = Assert.Single(fixture.Events("INVARIANT_BREACH"));
        Assert.Equal(finish.AddMinutes(minute), breach.GetProperty("timestamp").GetDateTimeOffset());
        var detail = breach.GetProperty("detail").GetString();
        Assert.StartsWith($"INVARIANT_BREACH kind=finished-unreconciled record={attempt.AttemptId} ", detail);
        Assert.Contains($"since={finish:O} ", detail);
        Assert.Equal($"invariant-breach:finished-unreconciled:{attempt.AttemptId}",
            Assert.Single(fixture.OpenItems()).CorrelationKey);
    }
}
