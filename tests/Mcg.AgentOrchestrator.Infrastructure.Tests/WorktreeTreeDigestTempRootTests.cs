using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorktreeTreeDigestTempRootTests : WorkerDispatchTestSupport
{
    [Fact]
    public void SuccessfulDigestRemovesWorkingFolder()
    {
        var root = CreateSeededDispatchRepository();
        string? observed = null;
        var existedDuringCall = false;
        Assert.True(WorktreeTreeDigest.TryCompute(root, out _, out var reason, path =>
        {
            observed = path;
            existedDuringCall = Directory.Exists(path);
        }), reason);
        Assert.True(existedDuringCall);
        AssertWorkingFolderRemoved(observed);
    }

    [Fact]
    public void FailedDigestRemovesWorkingFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"digest-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string? observed = null;
            var existedDuringCall = false;
            Assert.False(WorktreeTreeDigest.TryCompute(root, out _, out _, path =>
            {
                observed = path;
                existedDuringCall = Directory.Exists(path);
            }));
            Assert.True(existedDuringCall);
            AssertWorkingFolderRemoved(observed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void AssertWorkingFolderRemoved(string? path)
    {
        Assert.NotNull(path);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "mcg-run", "tmp", "worktree-digest"),
            Path.GetDirectoryName(path));
        Assert.False(Directory.Exists(path));
    }
}
