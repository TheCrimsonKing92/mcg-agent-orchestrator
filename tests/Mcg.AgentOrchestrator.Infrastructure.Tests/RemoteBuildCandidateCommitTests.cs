using Mcg.AgentOrchestrator.Infrastructure;
using static RemoteWorkerBuildCheckScenarioTests;

// Parallel-safe: all git writes are confined to a unique repository and a private index.
public sealed class RemoteBuildCandidateCommitTests : WorkerDispatchTestSupport
{
    [Fact]
    public void TryCreate_DirtyCandidate_PreservesCheckoutAndMatchesDigest()
    {
        var root = CreateSeededDispatchRepository();
        var tracked = Git(root, "ls-files").Split('\n')[0].Trim();
        File.AppendAllText(Path.Combine(root, tracked), "\ncandidate edit\n");
        File.WriteAllText(Path.Combine(root, "new-candidate.txt"), "new untracked file");
        var before = Snapshot(root);
        Assert.True(WorktreeTreeDigest.TryCompute(root, out var digest, out var reason), reason);
        Assert.True(RemoteBuildCandidateCommit.TryCreate(root, RemoteBuildCandidateCommit.RunGit, out var commit, out var tree, out reason), reason);
        Assert.Equal(before.Head, Git(root, "rev-parse", commit + "^"));
        Assert.Equal(tree, Git(root, "rev-parse", commit + "^{tree}"));
        Assert.Equal(digest, "wbc1:git-tree:" + tree);
        Assert.Equal(before, Snapshot(root));
        Assert.Equal("new untracked file", Git(root, "show", commit + ":new-candidate.txt"));
    }

    [Fact]
    public void TryCreate_CleanCandidate_ReturnsHeadWithoutChanges()
    {
        var root = CreateSeededDispatchRepository();
        Assert.Equal("", Git(root, "status", "--porcelain"));
        var before = Snapshot(root);
        Assert.True(RemoteBuildCandidateCommit.TryCreate(root, RemoteBuildCandidateCommit.RunGit, out var commit, out var tree, out var reason), reason);
        Assert.Equal(before.Head, commit);
        Assert.Equal(Git(root, "rev-parse", "HEAD^{tree}"), tree);
        Assert.Equal(before, Snapshot(root));
    }

    [Fact]
    public void TryCreate_NonRepository_ReturnsReason()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            Assert.False(RemoteBuildCandidateCommit.TryCreate(root, RemoteBuildCandidateCommit.RunGit, out _, out _, out var reason));
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
        finally { Directory.Delete(root, true); }
    }
}
