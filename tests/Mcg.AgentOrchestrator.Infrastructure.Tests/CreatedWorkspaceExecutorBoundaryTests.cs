// Parallel-safe: reads source only from the explicitly verified repository root.
public sealed class CreatedWorkspaceExecutorBoundaryTests
{
    [Fact]
    public void Driver_DelegatesWorkspaceEffect_ExactlyOnce()
    {
        var driver = File.ReadAllText(Path.Combine(OrchestrationDirectory(), "ConductorDriver.LifecycleEntryDecisions.cs"));
        const string call = "CreatedWorkspaceExecutor.Execute(";
        var firstCall = driver.IndexOf(call, StringComparison.Ordinal);

        Assert.True(firstCall >= 0, "The driver must delegate Created workspace execution.");
        Assert.Equal(-1, driver.IndexOf(call, firstCall + call.Length, StringComparison.Ordinal));
    }

    [Fact]
    public void Driver_DoesNotTranslateWorkspaceExceptions()
    {
        var driver = File.ReadAllText(Path.Combine(OrchestrationDirectory(), "ConductorDriver.LifecycleEntryDecisions.cs"));

        Assert.DoesNotContain("catch (", driver, StringComparison.Ordinal);
    }

    [Fact]
    public void Executor_DoesNotDependOnPolicyFactsOutcomeOrPresentation()
    {
        var executor = File.ReadAllText(Path.Combine(OrchestrationDirectory(), "CreatedWorkspaceExecutor.cs"));

        foreach (var forbidden in new[]
        {
            "CreatedStagePolicy", "CreatedStageFacts", "ConductorAdvanceOutcome", "MakeResult(", "Console."
        })
        {
            Assert.DoesNotContain(forbidden, executor, StringComparison.Ordinal);
        }
    }

    private static string OrchestrationDirectory() => Path.Combine(
        VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Orchestration");
}
