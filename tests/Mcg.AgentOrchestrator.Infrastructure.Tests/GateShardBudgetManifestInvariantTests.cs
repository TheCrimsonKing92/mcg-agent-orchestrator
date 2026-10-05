using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GateShardBudgetManifestInvariantTests
{
    [Fact]
    public void DefaultPermitBudgetExceedsCheckedInManifestShardConcurrency()
    {
        var settings = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot());

        Assert.True(
            GateShardPermitPool.DefaultBudget > settings.MaxConcurrentShards,
            $"Default permit budget {GateShardPermitPool.DefaultBudget} must exceed manifest shard concurrency {settings.MaxConcurrentShards}.");
    }
}
