namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class FailedGoalRecoveryExtractionTests
{
    [Fact]
    public void DriverDelegatesFailedSelectionToPolicy()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var driver = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Mcg.AgentOrchestrator.App",
            "Orchestration",
            "ConductorDriver.cs"));
        var interpreter = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Mcg.AgentOrchestrator.App",
            "Orchestration",
            "ConductorDriver.FailedGoalRecovery.cs"));

        Assert.Contains("ExecuteFailedGoalRecovery", driver, StringComparison.Ordinal);
        Assert.Contains("FailedGoalRecoveryPolicy.Evaluate", interpreter, StringComparison.Ordinal);
        Assert.DoesNotContain("var runningSibling = goal.Tasks.FirstOrDefault", driver, StringComparison.Ordinal);
    }
}
