using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

// The existing collection owns batch fairness statics; all I/O remains test-local and injected.
[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsExplicitParkReconciliation : ConductorBatchLoopTests
{
    // Hang-only failsafe for draining the held terminal attempt across ticks.
    private const int TerminalReconciliationHangFailsafeMilliseconds = 30_000;

    public ConductorBatchLoopTestsExplicitParkReconciliation(ITestOutputHelper output) : base(output) { }

    [Fact]
    public async Task Reconcile_AlreadyVisiblePark_RetainsRedAndReleasesRetryOnce()
    {
        using var f = new ExplicitParkTestFixture();
        await f.Repository.SaveAsync(f.Kernel);
        await f.Park();
        await f.Reload();
        var baseline = f.Kernel.ExportGoalSnapshot(f.Goal.Id);
        var driver = f.Driver();
        f.ReconcileRed(driver);
        Assert.Equal(GoalStatus.Parked, f.Goal.Status);
        f.AssertFailure();
        var saved = Assert.Single(await f.Repository.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, f.Kernel.ExportGoalSnapshot(f.Goal.Id))]));
        Assert.Equal(GoalSnapshotSaveDisposition.Saved, saved.Disposition);
        Assert.Equal(GoalStatus.Parked, (await f.Repository.LoadGoalAsync(f.Goal.Id))!.Status);
        await f.Reload();
        var replay = f.ReconcileRed(f.Driver());
        AssertGuardHold(replay, GoalLifecycleState.WorkspaceReady, 1, "acceptance-retry-already-prepared",
            "Acceptance retry is already prepared; terminal replay admits no new work.",
            new(null, GoalStatus.Active, true, false));
        f.AssertFailure();
        Assert.Equal(0, f.Dispatches + f.Acceptances + f.Landings);
        await f.Unpark();
        await f.Reload();
        driver = f.Driver();
        driver.AdvanceOnce(f.Goal, ExplicitParkTestFixture.Policy);
        Assert.Equal(1, f.Dispatches);
        var releasedReplay = f.ReconcileRed(driver);
        AssertGuardHold(releasedReplay, GoalLifecycleState.WorkspaceReady, 1, "acceptance-retry-already-prepared",
            "Acceptance retry is already prepared; terminal replay admits no new work.",
            new(null, GoalStatus.Active, true, false));
        f.AssertFailure();
        Assert.Equal(1, f.Dispatches);
    }

    [Fact]
    public async Task Reconcile_RedFirstConsumedAfterUnpark_UsesNormalRetryPolicy()
    {
        using var f = new ExplicitParkTestFixture();
        await f.Repository.SaveAsync(f.Kernel);
        await f.Park();
        await f.Unpark();
        await f.Reload();
        var result = f.ReconcileRed(f.Driver());
        Assert.NotEqual("landing-goal-state-guard", VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(result.Outcome).Decision?.Stage);
        Assert.Equal(GoalStatus.Active, f.Goal.Status);
        Assert.Equal(WorkTaskStatus.Assigned, f.Task.Status);
        Assert.Null(f.Goal.ParkedLifecycleStatus);
        f.AssertFailure();
    }

    [Fact]
    public async Task Complete_PassedWhileParked_DoesNotLand()
    {
        using var f = new ExplicitParkTestFixture();
        await f.Repository.SaveAsync(f.Kernel);
        await f.Park();
        await f.Reload();
        Assert.True(f.Kernel.ReconcileGoalAcceptanceVerified(f.Goal.Id, "Owned gate passed"));
        var candidate = f.Red().Run.Candidate;
        var result = f.Driver().CompleteParallelLandingAcceptance(candidate,
            ExplicitParkTestFixture.Policy, AcceptanceVerificationSummary.PassedWithNoUnmetCriteria, out var leaseHeld);
        Assert.Equal(0, f.Landings);
        Assert.False(leaseHeld);
        AssertGuardHold(result, GoalLifecycleState.Verified, 2, "parked-landing-held",
            "Goal is parked; landing remains held.", new(GoalStatus.Parked, null, null, true));
        Assert.Equal(GoalStatus.Parked, f.Goal.Status);
        Assert.Equal(GoalStatus.Verified, f.Goal.LifecycleStatus);
    }

    private static void AssertGuardHold(ConductorAdvanceResult result, GoalLifecycleState state, int rung,
        string evidence, string reason, LandingGoalStateGuardFacts expectedFacts)
    {
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(state, held.State);
        Assert.Equal(reason, held.Reason);
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal("landing-goal-state-guard", decision.Stage);
        Assert.Equal("Hold", decision.Action);
        Assert.Equal(rung, decision.Rung);
        Assert.Equal(evidence, decision.DiscriminatingEvidence);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(expectedFacts.ToRecordedFacts().ToArray(), decision.Facts.ToArray());
        var payload = VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(held);
        var restored = JsonSerializer.Deserialize<ConductorTickOutcomePayload>(JsonSerializer.Serialize(payload))!;
        Assert.Equal("Held", restored.OutcomeKind);
        Assert.Equal(state.ToString(), restored.LifecycleState);
        Assert.Null(restored.EscalationKind);
        var recorded = Assert.IsType<PolicyDecisionRecord>(restored.Decision);
        var facts = LandingGoalStateGuardFacts.FromRecordedFacts(recorded.Facts);
        Assert.Equal(expectedFacts, facts);
        var replay = LandingGoalStateGuardPolicy.Evaluate(facts).ToRecord();
        Assert.Equal(decision with { Facts = recorded.Facts }, recorded);
        Assert.Equal(recorded with { Facts = replay.Facts }, replay);
    }

    [Fact]
    public async Task CancellationProbe_ExplicitPark_PreservesGateCancellation()
    {
        using var f = new ExplicitParkTestFixture();
        await f.Repository.SaveAsync(f.Kernel);
        await f.Park();
        var workspace = OrchestratorWorkspace.ForDirectory(f.Root);
        var decision = ConductorDriver.GetAcceptanceAttemptCancellationDecision(workspace, f.Goal.Id);
        Assert.True(decision.ShouldCancel);
        Assert.Equal(AcceptanceAttemptCancellationCause.StoppedDisposition, decision.Cause);
        Assert.Equal(GoalStatus.Parked, decision.ObservedStatus);
        Assert.True(ConductorDriver.GetAcceptanceAttemptCancellationDecision(workspace, f.Goal.Id,
            attemptInvalidationRecorded: () => true).ShouldCancel);
        Assert.True(ConductorDriver.GetAcceptanceAttemptCancellationDecision(workspace, f.Goal.Id,
            loadGoalStatus: _ => GoalStatus.Parked).ShouldCancel); // Legacy authority remains unchanged.
        await f.Reload();
        f.Kernel.CancelGoal(f.Goal.Id, "Separate explicit stop");
        Assert.Equal(GoalStatus.Cancelled, f.Goal.Status);
        Assert.True(f.Goal.IsTerminal);
        await f.Repository.SaveAsync(f.Kernel);
        var stopped = ConductorDriver.GetAcceptanceAttemptCancellationDecision(workspace, f.Goal.Id);
        Assert.True(stopped.ShouldCancel);
        Assert.Equal(GoalStatus.Cancelled, stopped.ObservedStatus);
    }

    [Fact]
    public async Task Reconcile_ExhaustedRetry_PreservesNormalFailureRouting()
    {
        using var held = new ExplicitParkTestFixture();
        using var active = new ExplicitParkTestFixture();
        await held.Repository.SaveAsync(held.Kernel);
        await held.Park();
        await held.Reload();
        var policy = ExplicitParkTestFixture.Policy with { MaxCriterionRetries = 0 };
        ConductorAdvanceOutcome? normalOutcome = null;
        foreach (var f in new[] { active, held })
        {
            var (run, attempt) = f.Red();
            ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(f.Kernel, f.Goal, run, attempt);
            var result = ConductorBatchLoop.CompleteParallelAcceptanceRun(
                f.Driver(), policy, run, attempt, out var leaseHeld);
            Assert.False(leaseHeld);
            var outcome = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
            if (normalOutcome is ConductorAdvanceOutcome.Escalated normal)
                Assert.Equal(normal.Reason, outcome.Reason);
            else
                normalOutcome = outcome;
            Assert.Equal(GoalStatus.AcceptanceFailed, f.Goal.LifecycleStatus);
            Assert.Equal(0, f.Goal.AutomaticAcceptanceRetryCount);
            Assert.Equal(0, f.Task.CriterionRetryCount);
            Assert.False(f.Goal.AcceptanceFailureDeferredForRetry);
            var failure = Assert.IsType<AcceptanceFailureSummary>(f.Goal.RetainedAcceptanceFailure);
            Assert.Equal(new[] { "focused RED" }, failure.FailedChecks);
            Assert.Equal("branch-identity", failure.BranchHeadSha);
            Assert.Equal("main-identity", failure.MainHeadSha);
            Assert.Equal("baseline-receipt", failure.BaselineAttestation);
            Assert.Equal("failure detail", Assert.Single(failure.CheckAttributions!).Evidence);
            Assert.Equal(0, f.Dispatches + f.Acceptances + f.Landings);
        }
        Assert.Equal(GoalStatus.AcceptanceFailed, active.Goal.Status);
        Assert.Equal(GoalStatus.Parked, held.Goal.Status);
        await held.Repository.SaveAsync(held.Kernel);
        await held.Unpark();
        await held.Reload();
        Assert.Equal(active.Goal.Status, held.Goal.Status);
        Assert.Equal(GoalStatus.AcceptanceFailed, held.Goal.Status);
        Assert.NotNull(held.Goal.LatestAcceptanceFailure);
        Assert.Equal(0, held.Goal.AutomaticAcceptanceRetryCount);
        Assert.False(held.Goal.AcceptanceFailureDeferredForRetry);
    }

    [Theory]
    [InlineData(GoalLifecycleState.WorkspaceReady)]
    [InlineData(GoalLifecycleState.Dispatched)]
    [InlineData(GoalLifecycleState.Verifying)]
    [InlineData(GoalLifecycleState.Verified)]
    public async Task AdvanceAndBatch_ReloadedPark_BlocksEveryAdmission(
        GoalLifecycleState readiness)
    {
        using var f = new ExplicitParkTestFixture();
        var snapshot = f.Kernel.ExportGoalSnapshot(f.Goal.Id);
        snapshot = snapshot with
        {
            Status = readiness is GoalLifecycleState.Verified ? GoalStatus.Verified :
                readiness is GoalLifecycleState.Verifying ? GoalStatus.Verifying : GoalStatus.Active,
            Tasks = snapshot.Tasks.Select(t => readiness is GoalLifecycleState.WorkspaceReady or GoalLifecycleState.Dispatched
                ? t with { Status = readiness == GoalLifecycleState.Dispatched ? WorkTaskStatus.Running : WorkTaskStatus.Assigned,
                    LastVerification = null, VerificationHistory = [] }
                : t).ToArray()
        };
        await f.Repository.SaveGoalSnapshotsAsync([snapshot]);
        await f.Reload();
        Assert.Equal(readiness, GoalLifecycle.ResolveState(f.Goal, new(WorkspaceExists: true)));
        await f.Park();
        await f.Reload();
        var driver = f.Driver();
        var heldResult = driver.AdvanceOnce(f.Goal, ExplicitParkTestFixture.Policy);
        new ConductorBatchLoop().Run(f.Kernel, driver, ExplicitParkTestFixture.Policy,
            Path.Combine(f.Root, "stop"), maxIterations: 1);
        Assert.Equal(0, f.Dispatches);
        Assert.Equal(0, f.Acceptances);
        Assert.Equal(0, f.Landings);
        Assert.IsType<ConductorAdvanceOutcome.Held>(heldResult.Outcome);
        Assert.Null(driver.TryBuildParallelAcceptanceCandidate(f.Goal,
            ExplicitParkTestFixture.Policy, 0, out var decline));
        Assert.Equal(ConductorDriver.CandidateDeclineHoldActive, decline);
        await f.Unpark();
        await f.Reload();
        f.Driver().AdvanceOnce(f.Goal, ExplicitParkTestFixture.Policy);
        if (readiness is GoalLifecycleState.WorkspaceReady or GoalLifecycleState.Dispatched)
            Assert.Equal(1, f.Dispatches);
        else
            Assert.Equal(1, f.Acceptances);
    }

    [Fact(Timeout = TerminalReconciliationHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task Batch_TerminalAfterVisiblePark_DrainsWithoutAdmission()
    {
        using var f = new ExplicitParkTestFixture();
        var time = new ManualConductorTimeProviderForTests(DateTimeOffset.UtcNow);
        await using var held = new HoldingAcceptanceAttemptsAcrossTicksFixture(time, f.Red().Run.Acceptance);
        var driver = MakeDriver(getFacts: _ => new(WorkspaceExists: true),
            parallelAcceptanceAttemptCoordinator: held.AttemptCoordinator,
            runAcceptanceWithSlot: (_, _) => throw new InvalidOperationException("Gate must use owned attempt"),
            retryTask: (id, task, reason) => f.Kernel.RetryTask(id, task, reason),
            recordCriterionRetryFeedback: (id, task, feedback) =>
                f.Kernel.RecordCriterionRetryFeedback(id, task, feedback),
            recordAcceptanceFailure: (g, checks, branch, main, attrs, baseline) =>
                f.Kernel.RecordAcceptanceFailure(g.Id, checks, branch, main, attrs, baseline),
            land: _ => throw new InvalidOperationException("Parked goal cannot land"));
        held.RunTickForTests(f.Kernel, driver);
        var handle = held.RequiredHandleForTests(f.Goal);
        var attemptId = handle.Attempt.AttemptId;
        Assert.Equal(1, held.HeldAttemptCount);
        Assert.Equal(0, held.ProcessSpawnCount);
        Assert.True(held.AttemptCoordinator.HasLiveAttempt(f.Goal.Id.Value));
        await f.Repository.SaveAsync(f.Kernel);
        await f.Park();
        await f.Reload();
        handle.CompleteForTests();
        Assert.False(held.AttemptCoordinator.HasLiveAttempt(f.Goal.Id.Value));
        held.RunTickForTests(f.Kernel, driver);
        Assert.Equal(GoalStatus.Parked, f.Goal.Status);
        Assert.NotNull(f.Goal.RetainedAcceptanceFailure);
        Assert.Equal(1, f.Goal.AutomaticAcceptanceRetryCount);
        Assert.Equal(0, held.HeldAttemptCount);
        Assert.Equal(0, held.ProcessSpawnCount);
        Assert.False(held.AttemptCoordinator.HasLiveAttempt(f.Goal.Id.Value));
        Assert.Empty(held.AttemptCoordinator.GetUnreconciledAttempts([f.Goal.Id.Value]));
        Assert.Contains(f.Goal.Timeline, e => e.Message.Contains(attemptId, StringComparison.Ordinal));
    }
}
