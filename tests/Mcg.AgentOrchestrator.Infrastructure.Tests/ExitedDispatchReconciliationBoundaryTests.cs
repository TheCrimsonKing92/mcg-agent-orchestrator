// Parallel-safe: reads source from the explicitly verified repository root only.
public sealed class ExitedDispatchReconciliationBoundaryTests
{
    [Xunit.Fact]
    public void Driver_ExitedReconciliation_DelegatesToExecutor()
    {
        var driver = File.ReadAllText(Path.Combine(OrchestrationDirectory(), "ConductorDriver.cs"));

        Assert.Contains("ExitedDispatchReconciliationExecutor.Execute(", driver, StringComparison.Ordinal);
        Assert.DoesNotContain("_reconcileExitedDispatch(", driver, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Executor_Source_HasNoDriverDecisionOrPresentationDependency()
    {
        var executor = File.ReadAllText(Path.Combine(
            OrchestrationDirectory(), "ExitedDispatchReconciliationExecutor.cs"));

        foreach (var forbidden in new[]
                 { "ConductorDriver", "ConductorAdvanceOutcome", "DispatchStartPolicy", "Console.", "partial" })
            Assert.DoesNotContain(forbidden, executor, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void FailedGoalRecovery_SeparateLoop_RetainsDirectReconciliation()
    {
        var recovery = File.ReadAllText(Path.Combine(
            OrchestrationDirectory(), "ConductorDriver.FailedGoalRecovery.cs"));

        Assert.Contains("_reconcileExitedDispatch(", recovery, StringComparison.Ordinal);
    }

    private static string OrchestrationDirectory() => Path.Combine(
        VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Orchestration");
}
