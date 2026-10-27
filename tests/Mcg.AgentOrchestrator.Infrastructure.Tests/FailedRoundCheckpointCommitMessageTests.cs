using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: owned git repositories and injected dispatch callbacks only; no worker is spawned.
public sealed class FailedRoundCheckpointCommitMessageTests
{
    [Fact]
    public void Checkpoint_OnlyBranchCommit_IsDiscoverableByGoalId()
    {
        using var f = new FailedRoundCheckpointFixture();
        var before = FailedRoundCheckpointFixture.Git(f.Worktree, "rev-parse", "HEAD");
        f.WriteEdits();
        f.Complete();
        Assert.False(f.Worker.LastVerification!.Succeeded);
        var receipt = Assert.IsType<FailedRoundCheckpointReceipt>(f.Worker.LastDispatch!.FailedRoundCheckpointReceipt);
        f.Retry();

        var integration = new FailedRoundCheckpointPreDispatch(f.Kernel, f.Repository, TrunkBranchName.Default)
            .IntegrateMainBeforeDeveloperDispatch(f.Goal);

        Assert.True(integration.CanDispatch);
        var range = before + "..HEAD";
        var sha = Assert.Single(FailedRoundCheckpointFixture.Git(f.Worktree, "log", "--format=%H", range)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var goalPrefix = f.Goal.Id.Value[..8];
        Assert.StartsWith($"Developer({goalPrefix}): ",
            FailedRoundCheckpointFixture.Git(f.Worktree, "show", "-s", "--format=%s", sha));
        Assert.Equal(sha, FailedRoundCheckpointFixture.Git(f.Worktree, "log", "--format=%H",
            "--fixed-strings", "--grep=" + goalPrefix, range));
        var body = FailedRoundCheckpointFixture.Git(f.Worktree, "show", "-s", "--format=%b", sha)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains($"Summary: preserved uncommitted edits from failed dispatch {receipt.DispatchId}", body);
        Assert.Contains($"Goal: {f.Goal.Id.Value}", body);
        Assert.Contains($"Task-Id: {f.Worker.Id.Value}", body);
        Assert.Contains($"Dispatch: {receipt.DispatchId}", body);
    }
}
