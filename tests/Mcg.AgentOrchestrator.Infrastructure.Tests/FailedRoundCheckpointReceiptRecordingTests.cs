using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its git repository and injects process liveness and time.
public sealed class FailedRoundCheckpointReceiptRecordingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void FailedDirtyDeveloper_NoCommit_RecordsInspectionReceipt(int exitCode)
    {
        using var f = new FailedRoundCheckpointFixture();
        f.WriteEdits();
        f.Complete(exitCode, error: exitCode == 0 ? "" : "worker failed");
        Assert.False(f.Worker.LastVerification!.Succeeded);
        var receipt = Assert.IsType<FailedRoundCheckpointReceipt>(f.Worker.LastDispatch!.FailedRoundCheckpointReceipt);
        var inspection = new DispatchWorktreeCommitter().InspectGoalWorktree(
            f.Worktree, f.Goal.Id, f.DispatchedAt, forceRefresh: true);
        Assert.True(inspection.IsAvailable);
        Assert.Equal(0, inspection.Evidence.CommitsAfterDispatch);
        Assert.Equal(f.DispatchId, receipt.DispatchId);
        Assert.Equal(inspection.Evidence.DirtyStateHash, receipt.DirtyStateHash);
        Assert.Equal(inspection.Evidence.DirtyPaths, receipt.DirtyPaths);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void LowIntegrity_AbsentConfinement_LeavesDirtyWithoutReceipt(int exitCode)
    {
        using var f = new FailedRoundCheckpointFixture(lowIntegrity: true);
        f.WriteEdits();
        var before = FailedRoundCheckpointFixture.Git(f.Worktree, "rev-parse", "HEAD");
        f.Complete(exitCode, error: "");
        Assert.False(f.Worker.LastVerification!.Succeeded);
        Assert.Null(f.Worker.LastDispatch!.FailedRoundCheckpointReceipt);
        Assert.NotEmpty(FailedRoundCheckpointFixture.Git(f.Worktree, "status", "--short"));
        f.Retry();
        Assert.False(new Mcg.AgentOrchestrator.App.Orchestration.FailedRoundCheckpointPreDispatch(
            f.Kernel, f.Repository, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default).IntegrateMainBeforeDeveloperDispatch(f.Goal).CanDispatch);
        Assert.Equal(before, FailedRoundCheckpointFixture.Git(f.Worktree, "rev-parse", "HEAD"));
    }

    [Fact]
    public void AcceptedCommitOnBehalf_DirtyDeveloper_ProducesNoFailedReceipt()
    {
        using var f = new FailedRoundCheckpointFixture();
        f.WriteEdits();
        f.Complete(0, output: "WORKER_RESULT:\nfiles: seed.txt, new-untracked.txt\ncommands: source edit\n" +
            "tests: pass - 1/1\ncommit: none\nblockers: none\nassigned_scope_complete: true\n" +
            "model_fit: OpenAI/test - adequate - test\nskills: none\nconfidence: high\nEND_WORKER_RESULT", error: "");
        Assert.True(f.Worker.LastVerification!.Succeeded, f.Worker.LastVerification.StandardError);
        Assert.Empty(FailedRoundCheckpointFixture.Git(f.Worktree, "status", "--short"));
        Assert.NotNull(f.Worker.LastDispatch!.ResultCommit);
        Assert.Null(f.Worker.LastDispatch.FailedRoundCheckpointReceipt);
    }

    [Fact]
    public void FailedDeveloper_WorkerCommitAndLeftovers_ProducesNoReceipt()
    {
        using var f = new FailedRoundCheckpointFixture();
        f.WriteEdits();
        FailedRoundCheckpointFixture.Git(f.Worktree, "add", "seed.txt");
        FailedRoundCheckpointFixture.Git(f.Worktree, "commit", "-m", "Worker committed tracked edit");
        Assert.Equal(1, new DispatchWorktreeCommitter().InspectGoalWorktree(
            f.Worktree, f.Goal.Id, f.DispatchedAt).Evidence.CommitsAfterDispatch);
        f.Complete();
        Assert.False(f.Worker.LastVerification!.Succeeded);
        Assert.Null(f.Worker.LastDispatch!.FailedRoundCheckpointReceipt);
        Assert.Contains("new-untracked.txt", FailedRoundCheckpointFixture.Git(f.Worktree, "status", "--short"));
    }

    [Fact]
    public void FailedTester_DirtyWithoutCommit_ProducesNoReceipt()
    {
        using var f = new FailedRoundCheckpointFixture(AgentRole.Tester);
        f.WriteEdits();
        f.Complete();
        Assert.False(f.Worker.LastVerification!.Succeeded);
        Assert.Null(f.Worker.LastDispatch!.FailedRoundCheckpointReceipt);
    }

    [Fact]
    public void ReceiptAndConsumedDecision_JsonSnapshot_RoundTripsAcrossRetry()
    {
        using var f = new FailedRoundCheckpointFixture();
        f.WriteEdits();
        f.Complete();
        var original = f.Worker.LastDispatch!.FailedRoundCheckpointReceipt!;
        var restored = Restore(f.Kernel);
        var worker = restored.GetTask(f.Goal.Id, f.Worker.Id);
        Assert.Equal(original.DispatchId, worker.LastDispatch!.FailedRoundCheckpointReceipt!.DispatchId);
        Assert.Equal(original.DirtyStateHash, worker.LastDispatch.FailedRoundCheckpointReceipt.DirtyStateHash);
        Assert.Equal(original.DirtyPaths, worker.LastDispatch.FailedRoundCheckpointReceipt.DirtyPaths);
        restored.RetryTask(f.Goal.Id, f.Worker.Id, "Resume edits.", RetryCause.EnvironmentApparatusFailure);
        Assert.Null(worker.LastDispatch);
        var goal = restored.GetGoal(f.Goal.Id);
        Assert.True(new Mcg.AgentOrchestrator.App.Orchestration.FailedRoundCheckpointPreDispatch(
            restored, f.Repository, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default).IntegrateMainBeforeDeveloperDispatch(goal).CanDispatch);
        var sha = FailedRoundCheckpointFixture.Git(f.Worktree, "rev-parse", "HEAD");
        var persisted = Restore(restored).GetTask(f.Goal.Id, f.Worker.Id);
        Assert.Null(persisted.LastDispatch);
        var dispatch = Assert.Single(persisted.DispatchHistory);
        Assert.Null(dispatch.FailedRoundCheckpointReceipt);
        Assert.Equal(sha, dispatch.FailedRoundCheckpointDecision!.CommitSha);
        Assert.Equal(original.DispatchId, dispatch.FailedRoundCheckpointDecision.DispatchId);
    }

    private static AgentOrchestratorKernel Restore(AgentOrchestratorKernel kernel) =>
        AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
            JsonSerializer.Serialize(kernel.ExportSnapshot()))!);

    [Fact]
    public async Task FailedReceiptAndDecision_SqliteReload_PreserveOneShotState()
    {
        using var f = new FailedRoundCheckpointFixture();
        f.WriteEdits();
        f.Complete();
        var original = f.Worker.LastDispatch!.FailedRoundCheckpointReceipt!;
        _ = StateDbMigrations.EnsureUpToDate(f.StateDatabase);
        var repository = new SqliteOrchestratorStateRepository(f.StateDatabase);
        await repository.SaveAsync(f.Kernel);
        var loaded = await new SqliteOrchestratorStateRepository(f.StateDatabase).LoadGoalsAsync([f.Goal.Id]);
        var task = loaded.GetTask(f.Goal.Id, f.Worker.Id);
        Assert.Equal(original.DispatchId, task.LastDispatch!.FailedRoundCheckpointReceipt!.DispatchId);
        Assert.Equal(original.DirtyStateHash, task.LastDispatch.FailedRoundCheckpointReceipt.DirtyStateHash);
        Assert.Equal(original.DirtyPaths, task.LastDispatch.FailedRoundCheckpointReceipt.DirtyPaths);
        loaded.RetryTask(f.Goal.Id, f.Worker.Id, "Resume preserved edits.", RetryCause.EnvironmentApparatusFailure);
        Assert.True(new Mcg.AgentOrchestrator.App.Orchestration.FailedRoundCheckpointPreDispatch(
            loaded, f.Repository, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default).IntegrateMainBeforeDeveloperDispatch(loaded.GetGoal(f.Goal.Id)).CanDispatch);
        await repository.SaveAsync(loaded);
        var reloaded = await new SqliteOrchestratorStateRepository(f.StateDatabase).LoadGoalsAsync([f.Goal.Id]);
        var dispatch = Assert.Single(reloaded.GetTask(f.Goal.Id, f.Worker.Id).DispatchHistory);
        Assert.Null(dispatch.FailedRoundCheckpointReceipt);
        Assert.Equal(original.DispatchId, dispatch.FailedRoundCheckpointDecision!.DispatchId);
        Assert.Equal(FailedRoundCheckpointOutcome.Committed, dispatch.FailedRoundCheckpointDecision.Outcome);
        Assert.Equal(FailedRoundCheckpointFixture.Git(f.Worktree, "rev-parse", "HEAD"),
            dispatch.FailedRoundCheckpointDecision.CommitSha);
    }
}
