// Parallel-safe: reads source from the explicitly verified repository root only.
public sealed class FailedGoalRetryApplicationBoundaryTests
{
    [Xunit.Fact]
    public void Driver_RetryApplication_DelegatesEffectsAndRetainsDecisionsAndReconciliation()
    {
        var driver = Read("ConductorDriver.FailedGoalRecovery.cs");

        Assert.Contains("FailedGoalRetryApplicationExecutor.Apply(", driver, StringComparison.Ordinal);
        Assert.DoesNotContain("_retryTask(", driver, StringComparison.Ordinal);
        Assert.DoesNotContain("_recordCriterionRetryFeedback(", driver, StringComparison.Ordinal);
        Assert.Contains("_reconcileExitedDispatch(", driver, StringComparison.Ordinal);
        Assert.Contains("Escalate(", driver, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Executor_Source_HasNoDriverDecisionOrPresentationDependency()
    {
        var executor = Read("FailedGoalRetryApplicationExecutor.cs");

        foreach (var forbidden in new[]
                 { "ConductorDriver", "ConductorAdvanceOutcome", "Escalate", "Held", "Console.", "partial" })
            Assert.DoesNotContain(forbidden, executor, StringComparison.Ordinal);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(
        VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Orchestration", file));
}
