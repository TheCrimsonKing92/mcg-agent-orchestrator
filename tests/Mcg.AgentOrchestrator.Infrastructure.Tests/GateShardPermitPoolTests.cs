using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GateShardPermitPoolTests
{
    [Fact]
    public void PermitFilesBoundConcurrentHoldersAndReleaseOnDispose()
    {
        var root = Path.Combine(Path.GetTempPath(), "gate-shard-pool-" + Guid.NewGuid().ToString("N"));
        try
        {
            var firstPool = GateShardPermitPool.ForRoot(root, 2);
            var secondPool = GateShardPermitPool.ForRoot(root, 2);
            Assert.True(firstPool.TryAcquire(out var first));
            Assert.True(secondPool.TryAcquire(out var second));
            Assert.False(firstPool.TryAcquire(out var unavailable));
            Assert.Null(unavailable);
            first!.Dispose();
            first.Dispose();
            Assert.True(secondPool.TryAcquire(out var replacement));
            replacement!.Dispose();
            second!.Dispose();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData("", 5)]
    [InlineData("bad", 5)]
    [InlineData("0", 5)]
    [InlineData("-1", 5)]
    [InlineData("6", 6)]
    public void BudgetUsesPositiveEnvironmentValueOrCodeDefault(string? raw, int expected) =>
        Assert.Equal(expected, GateShardPermitPool.ResolveBudget(raw));
}
