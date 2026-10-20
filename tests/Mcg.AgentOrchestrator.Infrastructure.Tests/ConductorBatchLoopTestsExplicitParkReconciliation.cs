using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// The existing collection owns batch fairness statics; all I/O remains test-local and injected.
[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsExplicitParkReconciliation : ConductorBatchLoopTests
{
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
        f.ReconcileRed(f.Driver());
        f.AssertFailure();
        Assert.Equal(0, f.Dispatches + f.Acceptances + f.Landings);
        await f.Unpark();
        await f.Reload();
        driver = f.Driver();
        driver.AdvanceOnce(f.Goal, ExplicitParkTestFixture.Policy);
        Assert.Equal(1, f.Dispatches);
        f.ReconcileRed(driver);
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
        f.ReconcileRed(f.Driver());
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
        Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalStatus.Parked, f.Goal.Status);
        Assert.Equal(GoalStatus.Verified, f.Goal.LifecycleStatus);
    }

    [Fact]
    public async Task CancellationProbe_ExplicitPark_PreservesAlreadyOwnedWork()
    {
        using var f = new ExplicitParkTestFixture();
        await f.Repository.SaveAsync(f.Kernel);
        await f.Park();
        var workspace = OrchestratorWorkspace.ForDirectory(f.Root);
        var decision = ConductorDriver.GetAcceptanceAttemptCancellationDecision(workspace, f.Goal.Id);
        Assert.False(decision.ShouldCancel);
        Assert.Equal(GoalStatus.Parked, decision.ObservedStatus);
        Assert.True(ConductorDriver.GetAcceptanceAttemptCancellationDecision(workspace, f.Goal.Id,
            attemptInvalidationRecorded: () => true).ShouldCancel);
        Assert.True(ConductorDriver.GetAcceptanceAttemptCancellationDecision(workspace, f.Goal.Id,
            loadGoalStatus: _ => GoalStatus.Parked).ShouldCancel); // Legacy authority remains unchanged.
        await f.Reload();
        f.Kernel.CancelGoal(f.Goal.Id, "Separate explicit stop");
        await f.Repository.SaveAsync(f.Kernel);
        Assert.True(ConductorDriver.GetAcceptanceAttemptCancellationDecision(workspace, f.Goal.Id).ShouldCancel);
    }

    [Fact]
    public async Task Reconcile_ExhaustedRetryWhileParked_ReleasesFailed()
    {
        using var f = new ExplicitParkTestFixture();
        await f.Repository.SaveAsync(f.Kernel);
        await f.Park();
        await f.Reload();
        var (run, attempt) = f.Red();
        var driver = f.Driver();
        ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(f.Kernel, f.Goal, run, attempt);
        ConductorBatchLoop.CompleteParallelAcceptanceRun(driver,
            ExplicitParkTestFixture.Policy with { MaxCriterionRetries = 0 }, run, attempt, out _);
        Assert.Equal(GoalStatus.Parked, f.Goal.Status);
        Assert.Equal(GoalStatus.Failed, f.Goal.LifecycleStatus);
        Assert.Equal(0, f.Goal.AutomaticAcceptanceRetryCount);
        Assert.False(f.Goal.AcceptanceFailureDeferredForRetry);
        await f.Repository.SaveAsync(f.Kernel);
        await f.Unpark();
        await f.Reload();
        Assert.Equal(GoalStatus.Failed, f.Goal.Status);
        Assert.NotNull(f.Goal.RetainedAcceptanceFailure);
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

    [Fact(Timeout = 30000)]
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
        Assert.Equal(1, held.ProcessSpawnCount);
        Assert.Empty(held.AttemptCoordinator.GetUnreconciledAttempts([f.Goal.Id.Value]));
        Assert.Contains(f.Goal.Timeline, e => e.Message.Contains(attemptId, StringComparison.Ordinal));
    }
}
