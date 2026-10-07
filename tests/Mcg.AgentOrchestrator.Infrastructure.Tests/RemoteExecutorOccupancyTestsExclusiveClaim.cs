using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteExecutorOccupancyTestsExclusiveClaim
{
    [Xunit.Fact]
    public void ClaimsExcludeTheSameExecutorUntilDisposed()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            using (var first = RemoteExecutorOccupancy.TryClaimExclusive(root, "one"))
            {
                Assert.NotNull(first);
                Assert.Null(RemoteExecutorOccupancy.TryClaimExclusive(root, "one"));
                using var other = RemoteExecutorOccupancy.TryClaimExclusive(root, "two");
                Assert.NotNull(other);
            }
            using var next = RemoteExecutorOccupancy.TryClaimExclusive(root, "one");
            Assert.NotNull(next);
        }
        finally { Directory.Delete(root, true); }
    }

    [Xunit.Fact]
    public void BlockedOccupancyDirectoryReturnsNullWithoutThrowing()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".orchestrator"), "blocks occupancy folder");
            IDisposable? claim = null;
            Assert.Null(Xunit.Record.Exception(() => claim = RemoteExecutorOccupancy.TryClaimExclusive(root, "one")));
            using (claim) Assert.Null(claim);
        }
        finally { Directory.Delete(root, true); }
    }
}
