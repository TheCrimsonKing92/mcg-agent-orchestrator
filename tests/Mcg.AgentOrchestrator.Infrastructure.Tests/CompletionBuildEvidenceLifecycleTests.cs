using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: injected child lifecycle/clock and unique SQLite/journal roots.
public sealed class CompletionBuildEvidenceLifecycleTests
{
    [Fact]
    public void UnknownBuildChildIdentityCannotSilentlyCancelCurrentWorker()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var runner = fixture.Runner();
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        fixture.Unknown = true;
        Assert.Contains("custody is retained", Assert.Throws<InvalidOperationException>(() =>
            runner.CancelLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId)).Message);
        Assert.Equal(WorkTaskStatus.Running, fixture.Task.Status);
        Assert.False(fixture.Task.LastProcess!.WasCancelled);
        Assert.True(fixture.Task.CompletionBuildEvidence!.IsPending);
        Assert.Equal(fixture.Task.CompletionBuildEvidence.LeaseOwner,
            new ReconcileSweepRemediationStore(fixture.StatePath).TryGetAcceptanceLeaseOwner(fixture.GoalId.Value));
        Assert.Equal(0, fixture.Stops);
    }

    [Theory]
    [InlineData(CompletionBuildEvidencePhase.Launching)]
    [InlineData(CompletionBuildEvidencePhase.WaitingForLease)]
    [InlineData(CompletionBuildEvidencePhase.Pending)]
    public void PendingCompletionBuildKeepsExitedWorkerInRunningLifecycle(CompletionBuildEvidencePhase phase)
    {
        var fixture = new CompletionBuildEvidenceFixture();
        fixture.Runner().RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var operation = fixture.Task.CompletionBuildEvidence!;
        fixture.Kernel.RecordCompletionBuildEvidence(fixture.GoalId, fixture.TaskId, operation with { Phase = phase });

        Assert.False(fixture.Task.LastProcess!.IsRunning);
        Assert.Equal(WorkTaskStatus.Running, fixture.Task.Status);
        Assert.Equal(GoalLifecycleState.Running, GoalLifecycle.ResolveState(fixture.Goal));
        Assert.Null(fixture.Task.LastVerification);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);

        fixture.Kernel.RecordCompletionBuildEvidence(fixture.GoalId, fixture.TaskId,
            operation with { Phase = CompletionBuildEvidencePhase.Cancelled });
        Assert.Equal(GoalLifecycleState.Dispatched, GoalLifecycle.ResolveState(fixture.Goal));
    }

    [Fact]
    public void DeadlineStopsOwnedChildAndReleasesLeaseWithoutProductTestVerdict()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var runner = fixture.Runner();
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        fixture.Clock.Advance(TimeSpan.FromMinutes(8));
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.TimedOut, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.Equal(1, fixture.Stops);
        Assert.Null(new ReconcileSweepRemediationStore(fixture.StatePath).TryGetAcceptanceLeaseOwner(fixture.GoalId.Value));
        Assert.NotEqual(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
    }

    [Theory]
    [InlineData(CompletionBuildEvidencePhase.CompilerFailure)]
    [InlineData(CompletionBuildEvidencePhase.ApparatusFailure)]
    [InlineData(CompletionBuildEvidencePhase.TimedOut)]
    public void TerminalBuildOutcomePreservesTypedReasonAndDiagnostics(CompletionBuildEvidencePhase phase)
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var runner = fixture.Runner();
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        fixture.Release(phase, receipt: false);
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(phase, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.Contains("controlled owned build result", fixture.Task.LastVerification!.StandardError);
        Assert.False(fixture.Task.LastVerification.Succeeded);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
    }

    [Fact]
    public void WorkerCancellationPreservesAlreadyTerminalBuildSuccessAfterReload()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var runner = fixture.Runner();
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        fixture.Release();
        runner.CancelLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.Success, fixture.Task.CompletionBuildEvidence!.Phase);
        fixture.Reload();
        Assert.Equal(CompletionBuildEvidencePhase.Success, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.Null(fixture.Task.LastVerification);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
        Assert.Equal(0, fixture.Stops);
        Assert.Equal(WorkTaskStatus.Cancelled, fixture.Task.Status);
    }

    [Theory]
    [InlineData("launch")]
    [InlineData("checkpoint")]
    public void FailedLaunchAuthorizationCannotExecuteBuild(string failure)
    {
        var fixture = new CompletionBuildEvidenceFixture
        { FailLaunch = failure == "launch", FailChildCheckpoint = failure == "checkpoint", BusyLease = failure == "lease" };
        fixture.Runner().RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(0, fixture.Authorizations);
        Assert.Equal(CompletionBuildEvidencePhase.ApparatusFailure, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.NotEqual(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
        if (failure != "launch") Assert.Equal(1, fixture.Stops);
    }

    [Fact]
    public void UnknownLivenessHoldsAndDeadChildWithoutReceiptFailsWithoutRelaunch()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        fixture.Runner().RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        fixture.Reload();
        fixture.Unknown = true;
        var outcome = fixture.Runner().RefreshLatestProcessWithOutcome(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.True(outcome.PendingBuildEvidence!.IsPending);
        Assert.Null(outcome.Verification);
        Assert.Equal(1, fixture.Launches);
        fixture.Unknown = false;
        fixture.Live = false;
        fixture.Runner().RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.ApparatusFailure, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.Equal(1, fixture.Launches);
        Assert.Null(new ReconcileSweepRemediationStore(fixture.StatePath).TryGetAcceptanceLeaseOwner(fixture.GoalId.Value));
    }

    [Fact]
    public void BusyLeaseHoldsCompletionWithoutExecutingBuildOrSpendingWorkerRound()
    {
        var fixture = new CompletionBuildEvidenceFixture { BusyLease = true };
        var outcome = fixture.Runner().RefreshLatestProcessWithOutcome(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.WaitingForLease, outcome.PendingBuildEvidence!.Phase);
        Assert.Null(outcome.Verification);
        Assert.Equal(0, fixture.Authorizations);
        Assert.Equal(WorkTaskStatus.Running, fixture.Task.Status);
        fixture.BusyLease = false;
        fixture.Live = true;
        fixture.Runner().RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(1, fixture.Authorizations);
        Assert.Equal(CompletionBuildEvidencePhase.Pending, fixture.Task.CompletionBuildEvidence!.Phase);
    }

    [Fact]
    public void FailedLaunchIntentPersistenceReturnsApparatusOutcomeWithoutLaunchingChild()
    {
        var fixture = new CompletionBuildEvidenceFixture { FailLaunchIntent = true };
        var outcome = fixture.Runner().RefreshLatestProcessWithOutcome(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.ApparatusFailure, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.Equal(0, fixture.Launches);
        Assert.Equal(0, fixture.Authorizations);
        Assert.Contains("launch intent could not be persisted", outcome.Verification!.StandardError);
        Assert.False(outcome.Verification.Succeeded);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
    }
}
