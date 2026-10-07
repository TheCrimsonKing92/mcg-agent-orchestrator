// Parallel-safe: reads source only from the explicitly verified repository root.
public sealed class VerifyingAttemptExecutorBoundaryTests
{
    [Fact]
    public void Driver_DelegatesAttemptEffects_ExactlyOnce()
    {
        var driver = File.ReadAllText(Path.Combine(OrchestrationDirectory(), "ConductorDriver.AcceptanceLanding.cs"));
        const string call = "VerifyingAttemptExecutor.Execute(";
        var firstCall = driver.IndexOf(call, StringComparison.Ordinal);

        Assert.True(firstCall >= 0, "The driver must delegate verifying attempt execution.");
        Assert.Equal(-1, driver.IndexOf(call, firstCall + call.Length, StringComparison.Ordinal));
        foreach (var forbidden in new[]
        {
            "\"deadline-elapsed\"", "\"reconciliation-ownership-changed\"", "\"ownership-changed\"",
            "_noTickAcceptancePollDelay(", "NoTickWaitOutcome = \"", "AcceptanceArtifactWriterLeaseBusyException"
        })
        {
            Assert.DoesNotContain(forbidden, driver, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Executor_DoesNotDependOnPolicyDriverCoordinatorOrPresentation()
    {
        var executor = File.ReadAllText(Path.Combine(OrchestrationDirectory(), "VerifyingAttemptExecutor.cs"));

        foreach (var forbidden in new[]
        {
            "VerifyingStagePolicy", "VerifyingStageFacts", "ConductorAdvanceOutcome", "ConductorDriver",
            "ConductorParallelAcceptanceAttemptCoordinator", "Console."
        })
        {
            Assert.DoesNotContain(forbidden, executor, StringComparison.Ordinal);
        }
    }

    private static string OrchestrationDirectory() => Path.Combine(
        VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Orchestration");
}
