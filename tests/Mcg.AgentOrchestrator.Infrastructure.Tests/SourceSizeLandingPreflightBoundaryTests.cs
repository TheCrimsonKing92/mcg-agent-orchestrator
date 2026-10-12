// Parallel-safe: reads source from the explicitly verified repository root only.
public sealed class SourceSizeLandingPreflightBoundaryTests
{
    [Xunit.Fact]
    public void Driver_AllLandingPaths_DelegateToExecutor()
    {
        var driver = ReadSource("ConductorDriver.SourceSizePreflight.cs");

        Assert.True(driver.Split("SourceSizeLandingPreflightExecutor.", StringSplitOptions.None).Length - 1 >= 4);
        Assert.DoesNotContain("SourceSizeRatchetPreflight.", driver, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Executor_Source_HasNoDriverOrPresentationDependency()
    {
        var executor = ReadSource("SourceSizeLandingPreflightExecutor.cs");

        foreach (var forbidden in new[]
                 { "ConductorDriver", "partial", "Console.", "_executionDirectory", "_utcNow" })
            Assert.DoesNotContain(forbidden, executor, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("ConductorDriver.ParallelAcceptance.cs", "RunParallelLandingSourceSizePreflight(")]
    [Xunit.InlineData("ConductorDriver.AcceptanceLanding.cs", "RunInlineLandingSourceSizePreflight(")]
    [Xunit.InlineData("ConductorDriver.AcceptanceCohortExecution.cs", "RunAcceptanceCohortSourceSizePreflight(")]
    [Xunit.InlineData("ConductorDriver.MergeTrains.cs", "RunMergeTrainSourceSizePreflight(")]
    public void CallSite_LandingPath_RetainsDriverEntryPoint(string file, string method)
    {
        Assert.Contains(method, ReadSource(file), StringComparison.Ordinal);
    }

    private static string ReadSource(string file) => File.ReadAllText(Path.Combine(
        VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Orchestration", file));
}
