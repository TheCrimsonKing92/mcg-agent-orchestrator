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
        Assert.Contains("FailedGoalRecoveryAction.ObserveReviewContract", interpreter, StringComparison.Ordinal);
        Assert.Contains("FailedGoalRecoveryAction.ObserveVerifyingFinding", interpreter, StringComparison.Ordinal);
        Assert.Contains("WithReviewContractObservation", interpreter, StringComparison.Ordinal);
        Assert.Contains("WithVerifyingFindingObservation", interpreter, StringComparison.Ordinal);
        Assert.DoesNotContain("TryBuildReviewContractRepairRetry", interpreter, StringComparison.Ordinal);
        Assert.DoesNotContain("TryBuildVerifyingFindingAutoRetry", interpreter, StringComparison.Ordinal);
        Assert.DoesNotContain("var runningSibling = goal.Tasks.FirstOrDefault", driver, StringComparison.Ordinal);
    }

    [Fact]
    public void DriverDoesNotOwnRungEightSelection()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var driver = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Mcg.AgentOrchestrator.App",
            "Orchestration",
            "ConductorDriver.cs"));
        var policy = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Mcg.AgentOrchestrator.Core",
            "Application",
            "FailedGoalRecoveryPolicy.cs"));

        Assert.Contains("SelectReviewContractCandidate", policy, StringComparison.Ordinal);
        Assert.Contains("SelectReviewContractObservation", policy, StringComparison.Ordinal);
        Assert.Contains("SelectVerifyingFindingRoute", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("targetTask = trigger.TargetTask", driver, StringComparison.Ordinal);
        Assert.DoesNotContain("round >= policy.ReviewAutoRetryStopRound", driver, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomaticWorkerRetryCause.Resolve(triggeringTask) ??", driver, StringComparison.Ordinal);
        Assert.DoesNotContain("TryBuildMissingFindingResultRetry", driver, StringComparison.Ordinal);
    }
}
