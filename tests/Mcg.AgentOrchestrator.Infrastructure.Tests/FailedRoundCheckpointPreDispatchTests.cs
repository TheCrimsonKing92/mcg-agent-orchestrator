using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: owned git repositories and injected dispatch callbacks only.
public sealed class FailedRoundCheckpointPreDispatchTests
{
    [Fact]
    public void FailedEdits_ExactReceipt_CheckpointsIntegratesAndStartsDispatch()
    {
        using var f = new FailedRoundCheckpointFixture();
        f.WriteEdits();
        File.WriteAllText(Path.Combine(f.Worktree, "ignored.txt"), "never staged");
        f.Complete();
        Assert.False(f.Worker.LastVerification!.Succeeded);
        var receipt = Assert.IsType<FailedRoundCheckpointReceipt>(f.Worker.LastDispatch!.FailedRoundCheckpointReceipt);
        Assert.Equal(new[] { "new-untracked.txt", "seed.txt" }, receipt.DirtyPaths);
        f.Retry();
        Assert.Null(f.Worker.LastDispatch);
        File.WriteAllText(Path.Combine(f.Repository, "main.txt"), "current main");
        FailedRoundCheckpointFixture.Git(f.Repository, "add", "main.txt");
        FailedRoundCheckpointFixture.Git(f.Repository, "commit", "-m", "Main advanced");
        var starts = 0;
        var advance = f.Driver(_ =>
        {
            starts++;
            Assert.Empty(FailedRoundCheckpointFixture.Git(f.Worktree, "status", "--short"));
            f.Kernel.RecordTaskDispatch(f.Goal.Id, f.Worker.Id, new TaskDispatchRecord(
                "codex-cli", "retried worker", f.Worktree, f.DispatchedAt.AddMinutes(2)));
        }).AdvanceOnce(f.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(advance.Outcome);
        Assert.Equal(1, starts);
        var sha = FailedRoundCheckpointFixture.Git(f.Worktree, "log", "--format=%H", "--fixed-strings",
            "--grep=checkpoint: preserved uncommitted edits from failed dispatch " + receipt.DispatchId);
        Assert.Equal(40, sha.Length);
        Assert.Equal("checkpoint: preserved uncommitted edits from failed dispatch " + receipt.DispatchId,
            FailedRoundCheckpointFixture.Git(f.Worktree, "show", "-s", "--format=%s", sha));
        var history = f.Worker.DispatchHistory.Single(d => d.DispatchedAt == f.DispatchedAt);
        Assert.Null(history.FailedRoundCheckpointReceipt);
        var decision = Assert.IsType<FailedRoundCheckpointDecision>(history.FailedRoundCheckpointDecision);
        Assert.Equal(FailedRoundCheckpointOutcome.Committed, decision.Outcome);
        Assert.Equal(receipt.DispatchId, decision.DispatchId);
        Assert.Equal(sha, decision.CommitSha);
        Assert.Equal("modified tracked content", FailedRoundCheckpointFixture.Git(f.Worktree, "show", sha + ":seed.txt"));
        Assert.Equal("new untracked content", FailedRoundCheckpointFixture.Git(f.Worktree, "show", sha + ":new-untracked.txt"));
        Assert.DoesNotContain("ignored.txt", FailedRoundCheckpointFixture.Git(f.Worktree, "ls-tree", "-r", "--name-only", sha));
        Assert.True(File.Exists(Path.Combine(f.Worktree, "main.txt")));
        Assert.Empty(FailedRoundCheckpointFixture.Git(f.Worktree, "status", "--short"));
    }

    [Fact]
    public void FailedEdits_ExtraPath_HoldsWithPathsAndUnchangedHead()
    {
        using var f = FailedFixture();
        File.WriteAllText(Path.Combine(f.Worktree, "extra.txt"), "unattributed");
        AssertHold(f, "extra.txt", "new-untracked.txt", "seed.txt");
        Assert.Null(f.Worker.DispatchHistory[^1].FailedRoundCheckpointDecision);
    }

    [Fact]
    public void FailedEdits_DifferentAssignedTask_HoldsWithoutCheckpoint()
    {
        using var f = FailedFixture();
        // The receipt remains on the failed task; a different assigned task owns this advance.
        f.Kernel.AddTask(f.Goal.Id, AgentRole.Developer, "Unrelated Developer", AgentCatalog.Default().Agents);
        f.Kernel.ReportTaskProgress(f.Goal.Id, f.Worker.Id, WorkTaskStatus.Cancelled, "Other Developer owns retry.");
        AssertHold(f, "new-untracked.txt", "seed.txt");
    }

    [Fact]
    public void DirtyWorktree_NoReceipt_HoldsAndBoundsPathList()
    {
        using var f = new FailedRoundCheckpointFixture();
        f.Complete();
        f.Retry();
        for (var i = 0; i < 25; i++)
            File.WriteAllText(Path.Combine(f.Worktree, $"dirty-{i:00}.txt"), "dirty");
        var reason = AssertHold(f, "dirty-00.txt", "dirty-19.txt", "(+5 more)");
        Assert.DoesNotContain("dirty-20.txt", reason);
        Assert.DoesNotContain("dirty-24.txt", reason);
    }

    [Fact]
    public void FailedEdits_OutsideReceiptPath_HoldsWithoutCommit()
    {
        using var f = new FailedRoundCheckpointFixture();
        f.WriteEdits();
        f.Complete();
        var receipt = f.Worker.LastDispatch!.FailedRoundCheckpointReceipt!;
        f.Kernel.RecordFailedRoundCheckpointReceipt(f.Goal.Id, f.Worker.Id,
            receipt with { DirtyPaths = ["../outside.txt", "new-untracked.txt", "seed.txt"] });
        f.Retry();
        AssertHold(f, "new-untracked.txt", "seed.txt");
    }

    [Fact]
    public void FailedEdits_IndexLock_RecordsFailureAndKeepsReceiptAndHead()
    {
        using var f = FailedFixture();
        var beforeStatus = FailedRoundCheckpointFixture.Git(f.Worktree, "status", "--porcelain=v1");
        var indexLock = FailedRoundCheckpointFixture.Git(f.Worktree, "rev-parse", "--git-path", "index.lock");
        File.WriteAllText(indexLock, "owned test lock");
        try
        {
            AssertHold(f, "new-untracked.txt", "seed.txt");
            Assert.Equal(beforeStatus, FailedRoundCheckpointFixture.Git(f.Worktree, "status", "--porcelain=v1"));
            var dispatch = f.Worker.DispatchHistory[^1];
            Assert.NotNull(dispatch.FailedRoundCheckpointReceipt);
            Assert.Equal(FailedRoundCheckpointOutcome.CommitFailed, dispatch.FailedRoundCheckpointDecision!.Outcome);
            Assert.Null(dispatch.FailedRoundCheckpointDecision.CommitSha);
            Assert.Contains("index.lock", dispatch.FailedRoundCheckpointDecision.Diagnostic);
        }
        finally { File.Delete(indexLock); }
    }

    [Fact]
    public void FailedEdits_CommitFailure_RestoresOriginalIndex()
    {
        using var f = FailedFixture();
        var status = FailedRoundCheckpointFixture.Git(f.Worktree, "status", "--porcelain=v1");
        var before = FailedRoundCheckpointFixture.Git(f.Worktree, "rev-parse", "HEAD");
        var committer = new DispatchWorktreeCommitter(runGit: (path, args) => args[0] == "commit"
            ? new GitCli.GitResult(1, "", "injected commit failure") : GitCli.Run(path, args));
        var result = new FailedRoundCheckpointPreDispatch(f.Kernel, f.Repository, committer)
            .IntegrateMainBeforeDeveloperDispatch(f.Goal);
        Assert.False(result.CanDispatch);
        Assert.Equal(before, FailedRoundCheckpointFixture.Git(f.Worktree, "rev-parse", "HEAD"));
        Assert.Equal(status, FailedRoundCheckpointFixture.Git(f.Worktree, "status", "--porcelain=v1"));
        Assert.Equal(FailedRoundCheckpointOutcome.CommitFailed,
            f.Worker.DispatchHistory[^1].FailedRoundCheckpointDecision!.Outcome);
    }

    [Fact]
    public void FailedEdits_UnrelatedStagedArtifact_HoldsWithoutAbsorbingIt()
    {
        using var f = new FailedRoundCheckpointFixture();
        f.WriteEdits();
        Directory.CreateDirectory(Path.Combine(f.Worktree, ".scratch"));
        File.WriteAllText(Path.Combine(f.Worktree, ".scratch", "debris.txt"), "unrelated apparatus artifact");
        FailedRoundCheckpointFixture.Git(f.Worktree, "add", ".scratch/debris.txt");
        f.Complete();
        var receipt = Assert.IsType<FailedRoundCheckpointReceipt>(f.Worker.LastDispatch!.FailedRoundCheckpointReceipt);
        Assert.DoesNotContain(".scratch/debris.txt", receipt.DirtyPaths);
        var inspection = new DispatchWorktreeCommitter().InspectGoalWorktree(
            f.Worktree, f.Goal.Id, f.DispatchedAt, forceRefresh: true);
        Assert.Equal(receipt.DirtyStateHash, inspection.Evidence.DirtyStateHash);
        f.Retry();
        AssertHold(f, "new-untracked.txt", "seed.txt");
        Assert.Equal(".scratch/debris.txt", FailedRoundCheckpointFixture.Git(f.Worktree,
            "diff", "--cached", "--name-only"));
    }

    private static FailedRoundCheckpointFixture FailedFixture()
    {
        var f = new FailedRoundCheckpointFixture();
        f.WriteEdits();
        f.Complete();
        Assert.NotNull(f.Worker.LastDispatch!.FailedRoundCheckpointReceipt);
        f.Retry();
        return f;
    }

    private static string AssertHold(FailedRoundCheckpointFixture f, params string[] paths)
    {
        var before = FailedRoundCheckpointFixture.Git(f.Worktree, "rev-parse", "HEAD");
        string? reason = null;
        var advance = f.Driver(_ => Assert.Fail("Dirty unattributed edits must not dispatch."),
            value => reason = value).AdvanceOnce(f.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(advance.Outcome);
        Assert.NotNull(reason);
        Assert.Contains("conductor integration cannot start from a dirty worktree.", reason);
        foreach (var path in paths) Assert.Contains(path, reason);
        Assert.Equal(before, FailedRoundCheckpointFixture.Git(f.Worktree, "rev-parse", "HEAD"));
        Assert.Empty(FailedRoundCheckpointFixture.Git(f.Worktree, "log", "--format=%H",
            "--grep=checkpoint: preserved uncommitted edits from failed dispatch"));
        return reason;
    }
}
