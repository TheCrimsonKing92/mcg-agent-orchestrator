// Parallel-safe: reads source from the explicitly verified repository root only.
public sealed class DispatchStartExecutorBoundaryTests
{
    [Xunit.Fact(DisplayName = "The driver delegates dispatch start effects and consumes the policy decision")]
    public void DriverDelegatesStartEffects()
    {
        var driver = File.ReadAllText(Path.Combine(OrchestrationDirectory(), "ConductorDriver.cs"));

        Assert.Contains("DispatchStartExecutor.Execute(", driver, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "retry=sandbox-prep", "retry=spawn-failed",
            "\"dispatch-remediation\"", "_ = DispatchStartPolicy.Evaluate(" })
            Assert.DoesNotContain(forbidden, driver, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "The executor is the sole owner of sandbox prep recovery")]
    public void ExecutorOwnsSandboxRecovery()
    {
        var directory = OrchestrationDirectory();
        var executor = Path.Combine(directory, "DispatchStartExecutor.cs");
        Assert.Contains("private static bool TryRecoverSandboxPrep(", File.ReadAllText(executor), StringComparison.Ordinal);
        var otherFiles = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, executor, StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.NotEmpty(otherFiles);
        foreach (var path in otherFiles)
            Assert.False(File.ReadAllText(path).Contains("TryRecoverSandboxPrep", StringComparison.Ordinal),
                $"Sandbox recovery must be owned by the executor: {path}");
    }

    [Xunit.Fact(DisplayName = "The executor has no policy decisions, advance outcomes or console output")]
    public void ExecutorDoesNotDependOnDecisionOrPresentationOwners()
    {
        var executor = File.ReadAllText(Path.Combine(OrchestrationDirectory(), "DispatchStartExecutor.cs"));

        foreach (var forbidden in new[] { "DispatchStartPolicy", "ConductorAdvanceOutcome", "ConductorDriver", "Console." })
            Assert.DoesNotContain(forbidden, executor, StringComparison.Ordinal);
    }

    private static string OrchestrationDirectory() => Path.Combine(
        VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Orchestration");
}
