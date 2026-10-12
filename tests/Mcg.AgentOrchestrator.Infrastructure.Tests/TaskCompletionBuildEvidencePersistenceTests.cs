using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: per-test SQLite repository and controlled process identity.
public sealed class TaskCompletionBuildEvidencePersistenceTests
{
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
}
