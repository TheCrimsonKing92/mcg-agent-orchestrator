using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteExecutorOccupancyTests
{
    [Xunit.Fact]
    public void EachAttemptKeepsExecutorOccupiedUntilItsOwnRelease()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            RemoteExecutorOccupancy.Claim(root, "one", "a");
            Assert.True(RemoteExecutorOccupancy.IsOccupied(root, "one"));
            RemoteExecutorOccupancy.Claim(root, "one", "b");
            RemoteExecutorOccupancy.Release(root, "one", "a");
            Assert.True(RemoteExecutorOccupancy.IsOccupied(root, "one"));
            RemoteExecutorOccupancy.Release(root, "one", "b");
            Assert.False(RemoteExecutorOccupancy.IsOccupied(root, "one"));
            RemoteExecutorOccupancy.Claim(root, "one", "a");
            RemoteExecutorOccupancy.Release(root, "one", "a");
            Assert.False(RemoteExecutorOccupancy.IsOccupied(root, "one"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Xunit.Fact]
    public void DeadOrReusedPidDoesNotOccupyExecutor()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            RemoteExecutorOccupancy.Claim(root, "one", "a");
            Assert.False(RemoteExecutorOccupancy.IsOccupied(root, "one", _ => null));
            Assert.False(RemoteExecutorOccupancy.IsOccupied(root, "one",
                pid => RemoteExecutorOccupancy.DefaultStartTime(pid)?.AddSeconds(1)));
            File.WriteAllText(Path.Combine(root, ".orchestrator", "remote-executor-occupancy", "one", "bad.json"), "{");
            RemoteExecutorOccupancy.Release(root, "one", "a");
            Assert.False(RemoteExecutorOccupancy.IsOccupied(root, "one"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Xunit.Fact]
    public void UnwritableRootAndUnsafeNamesAreBestEffort()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var file = Path.Combine(root, "file");
            File.WriteAllText(file, "blocks directory creation");
            Assert.Null(Xunit.Record.Exception(() => RemoteExecutorOccupancy.Claim(file, "one", "a")));
            Assert.Null(Xunit.Record.Exception(() => RemoteExecutorOccupancy.Release(file, "one", "a")));
            Assert.False(RemoteExecutorOccupancy.IsOccupied(file, "one"));
            RemoteExecutorOccupancy.Claim(root, "..", "a");
            RemoteExecutorOccupancy.Claim(root, "one", "../escape");
            Assert.False(RemoteExecutorOccupancy.IsOccupied(root, "one"));
        }
        finally { Directory.Delete(root, true); }
    }
}
