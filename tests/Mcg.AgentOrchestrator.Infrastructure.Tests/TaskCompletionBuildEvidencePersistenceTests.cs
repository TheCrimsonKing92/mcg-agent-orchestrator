using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: per-test SQLite repository and controlled process identity.
public sealed class TaskCompletionBuildEvidencePersistenceTests
{
    [Fact]
    public async Task CurrentWorkerReceiptApplicationStillPersistsOverDurableTerminalBuildFact()
    {
        var f = new CompletionBuildEvidenceFixture();
        var runner = f.Runner();
        runner.RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        var baseline = f.Kernel.ExportSnapshot().Goals.Single();
        f.Release();
        runner.RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        var current = f.Kernel.ExportSnapshot().Goals.Single();
        Assert.True(f.Task.CompletionBuildEvidence!.Applied);
        var repository = new SqliteOrchestratorStateRepository(f.StatePath);
        await repository.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, current)]);
        f.Reload();
        Assert.Equal(CompletionBuildEvidencePhase.Success, f.Task.CompletionBuildEvidence!.Phase);
        Assert.True(f.Task.CompletionBuildEvidence.Applied);
        Assert.True(f.Task.CompletionBuildEvidence.CustodyReleased);
        Assert.Equal(WorkTaskStatus.Completed, f.Task.Status);
        Assert.Single(f.Task.VerificationHistory);
        Assert.NotNull(f.Task.LastDispatch!.ResultCommit);
    }

    [Fact]
    public async Task StaleCancellationSnapshotCannotOverwriteStoredTerminalSuccess()
    {
        var f = new CompletionBuildEvidenceFixture();
        f.Runner().RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        var baseline = f.Kernel.ExportSnapshot().Goals.Single();
        var stale = baseline with { Tasks = baseline.Tasks.Select(t => t.Id == f.TaskId.Value ? t with
        { CompletionBuildEvidence = t.CompletionBuildEvidence! with { Phase = CompletionBuildEvidencePhase.Cancelled } } : t).ToArray() };
        f.Release(receipt: false);
        f.ResetWorkerMetadata();
        f.Runner().ReconcileCompletionBuildEvidence(f.Kernel, f.GoalId, f.TaskId,
            CompletionBuildEvidenceTarget.From(f.Task.CompletionBuildEvidence!));
        var repository = new SqliteOrchestratorStateRepository(f.StatePath);
        await repository.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, stale)]);
        f.Reload();
        Assert.Equal(CompletionBuildEvidencePhase.Success, f.Task.CompletionBuildEvidence!.Phase);
        Assert.True(f.Task.CompletionBuildEvidence.CustodyReleased);
        Assert.Null(f.Task.LastProcess);
        Assert.Null(f.Task.LastVerification);
    }

    [Fact]
    public void SnapshotRoundTripPreservesDispatchCandidateChildAndOriginalWorkerEvidence()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        fixture.Runner().RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var operation = fixture.Task.CompletionBuildEvidence!;
        fixture.Reload();
        var restored = fixture.Task.CompletionBuildEvidence!;
        Assert.Equal(operation.Id, restored.Id);
        Assert.Equal(operation.DispatchId, restored.DispatchId);
        Assert.Equal(operation.TreeDigest, restored.TreeDigest);
        Assert.Equal(operation.Projects, restored.Projects);
        Assert.Equal(operation.ChildPid, restored.ChildPid);
        Assert.Equal(operation.ChildStartedAt, restored.ChildStartedAt);
        Assert.Equal(operation.ChildImagePath, restored.ChildImagePath);
        Assert.Equal(operation.WorkerProcess.ProcessId, restored.WorkerProcess.ProcessId);
        Assert.Equal(operation.WorkerProcess.StartedAt, restored.WorkerProcess.StartedAt);
        Assert.Equal(operation.WorkerProcess.ExitCode, restored.WorkerProcess.ExitCode);
        Assert.Equal(operation.WorkerProcess.StandardOutputPath, restored.WorkerProcess.StandardOutputPath);
        Assert.Equal(operation.WorkerProcess.StandardErrorPath, restored.WorkerProcess.StandardErrorPath);
        Assert.Equal(operation.LeaseOwner, restored.LeaseOwner);
        Assert.Equal(CompletionBuildEvidencePhase.Pending, restored.Phase);
        Assert.Null(fixture.Task.LastVerification);
    }

    [Fact]
    public async Task ConcurrentSnapshotMergeCannotEraseOrResurrectCancelledOperation()
    {
        var fixture = new CompletionBuildEvidenceFixture();
        var repository = new SqliteOrchestratorStateRepository(fixture.StatePath);
        var before = fixture.Kernel.ExportSnapshot().Goals.Single();
        fixture.Runner().RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var pending = fixture.Kernel.ExportSnapshot().Goals.Single();
        fixture.Coordinator().Cancel(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var result = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(before, pending)]));
        Assert.NotEqual(GoalSnapshotSaveDisposition.Skipped, result.Disposition);
        fixture.Reload();
        Assert.Equal(CompletionBuildEvidencePhase.Cancelled, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.Null(fixture.Task.LastVerification);
        Assert.Equal(1, fixture.Launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleWaitingSnapshot_PendingChildStored_PollsWithoutAnotherBuild(bool changedDiagnostic)
    {
        var fixture = new CompletionBuildEvidenceFixture { BusyLease = true };
        var runner = fixture.Runner();
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.WaitingForLease, fixture.Task.CompletionBuildEvidence!.Phase);
        Assert.Equal(0, fixture.Authorizations);
        var baseline = fixture.Kernel.ExportSnapshot().Goals.Single();
        var stale = baseline with
        {
            Tasks = baseline.Tasks.Select(task => task.Id == fixture.TaskId.Value ? task with
            {
                CompletionBuildEvidence = task.CompletionBuildEvidence! with
                {
                    Diagnostic = changedDiagnostic ? "stale tick diagnostic" : task.CompletionBuildEvidence.Diagnostic
                }
            } : task).ToArray()
        };

        fixture.BusyLease = false;
        fixture.Live = true;
        runner.RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        var pending = fixture.Task.CompletionBuildEvidence!;
        Assert.Equal(CompletionBuildEvidencePhase.Pending, pending.Phase);
        Assert.NotNull(pending.ChildPid);
        Assert.NotNull(pending.LeaseOwner);
        Assert.Equal(1, fixture.Authorizations);
        var launches = fixture.Launches;
        var repository = new SqliteOrchestratorStateRepository(fixture.StatePath);
        var result = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, stale)]));
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        fixture.Reload();
        var restored = fixture.Task.CompletionBuildEvidence!;
        Assert.Equal(pending.Id, restored.Id);
        Assert.Equal(CompletionBuildEvidencePhase.Pending, restored.Phase);
        Assert.Equal(pending.ChildPid, restored.ChildPid);
        Assert.Equal(pending.ChildStartedAt, restored.ChildStartedAt);
        Assert.Equal(pending.ChildImagePath, restored.ChildImagePath);
        Assert.Equal(pending.LeaseOwner, restored.LeaseOwner);

        var outcome = fixture.Runner().RefreshLatestProcessWithOutcome(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.Pending, outcome.PendingBuildEvidence!.Phase);
        Assert.Null(outcome.Verification);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
        Assert.Equal(launches, fixture.Launches);
        Assert.Equal(1, fixture.Authorizations);
        Assert.Equal(WorkTaskStatus.Running, fixture.Task.Status);
        fixture.Release();
        fixture.Runner().RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.Single(fixture.Task.VerificationHistory);
    }
}
