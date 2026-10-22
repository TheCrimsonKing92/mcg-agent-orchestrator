using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: reads one static constant.
public sealed class WorkerBuildBudgetTests
{
    [Fact]
    public void DefaultTimeoutCoversMeasuredFullRebuild()
    {
        // Goal e888e2fb: full rebuild measured about 6m11s under light load, about 9 to 10 minutes under acceptance-gate load.
        Assert.True(OrchestratorBuildEvidenceCheck.DefaultTimeout >= TimeSpan.FromMinutes(10));
    }
}
