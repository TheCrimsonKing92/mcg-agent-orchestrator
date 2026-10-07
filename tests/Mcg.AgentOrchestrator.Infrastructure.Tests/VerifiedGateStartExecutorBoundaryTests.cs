// Parallel-safe: reads source only from the explicitly verified repository root.
public sealed class VerifiedGateStartExecutorBoundaryTests
{
    [Fact]
    public void DriverDelegatesGateStartExactlyOnceWithoutInlineCatchTranslation()
    {
        var driver = File.ReadAllText(Path.Combine(OrchestrationDirectory(), "ConductorDriver.AcceptanceLanding.cs"));
        const string call = "VerifiedGateStartExecutor.Execute(";
        var firstCall = driver.IndexOf(call, StringComparison.Ordinal);

        Assert.True(firstCall >= 0, "The driver must delegate verified gate-start execution.");
        Assert.Equal(-1, driver.IndexOf(call, firstCall + call.Length, StringComparison.Ordinal));
        foreach (var forbidden in new[]
        {
            "catch (AcceptanceInfrastructureDeferredException", "catch (DotnetBuildSlotsBusyException",
            "catch (BuildLockBlockedException", "catch (AcceptanceAttemptCancelledException", "GateStartDeferral = \""
        })
        {
            Assert.DoesNotContain(forbidden, driver, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExecutorDoesNotDependOnPolicyDriverOrPresentation()
    {
        var executor = File.ReadAllText(Path.Combine(OrchestrationDirectory(), "VerifiedGateStartExecutor.cs"));

        foreach (var forbidden in new[]
        {
            "VerifiedAdmissionPolicy", "VerifiedAdmissionFacts", "ConductorAdvanceOutcome", "ConductorDriver",
            "FormatSlotsBusy", "FormatBuildLockBlocked", "Console."
        })
        {
            Assert.DoesNotContain(forbidden, executor, StringComparison.Ordinal);
        }
    }

    private static string OrchestrationDirectory() => Path.Combine(
        VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Orchestration");
}
