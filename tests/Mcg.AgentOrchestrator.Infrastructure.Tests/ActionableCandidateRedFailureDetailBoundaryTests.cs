// Parallel-safe: reads source from the explicitly verified repository root only.
public sealed class ActionableCandidateRedFailureDetailBoundaryTests
{
    [Fact]
    public void Driver_FailureDetail_DelegatesRenderingWithoutReaderOrFormatting()
    {
        var driver = ReadSource("ConductorDriver.ActionableCandidateRedFailureDetail.cs");

        Assert.Contains("ActionableCandidateRedFailureDetailRenderer.Append(", driver,
            StringComparison.Ordinal);
        foreach (var forbidden in new[] { "AcceptanceTrxFailureReader", "StackTrace", "FormatCappedTrxFailure" })
            Assert.DoesNotContain(forbidden, driver, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_Source_HasNoDriverPolicyOrConsoleDependency()
    {
        var renderer = ReadSource("ActionableCandidateRedFailureDetailRenderer.cs");

        Assert.Contains("internal static class ActionableCandidateRedFailureDetailRenderer", renderer,
            StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 { "ConductorDriver", "partial", "Console.", "MaxCriterionRetryEvidenceLines", "DescribeTrxReadFailure" })
            Assert.DoesNotContain(forbidden, renderer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ConductorDriver.FindingEvidenceRouting.cs")]
    [InlineData("ConductorDriver.PreTesterDeferredEvidence.cs")]
    [InlineData("ConductorDriver.IncompleteScopeRetryContext.cs")]
    [InlineData("ConductorDriver.DeferredNoChangeEvidence.cs")]
    public void Caller_FailureDetail_RetainsDriverWrapper(string filename)
    {
        var caller = ReadSource(filename);

        Assert.Contains("AppendActionableCandidateRedFailureDetail(", caller, StringComparison.Ordinal);
        Assert.DoesNotContain("ActionableCandidateRedFailureDetailRenderer", caller, StringComparison.Ordinal);
    }

    private static string ReadSource(string filename) => File.ReadAllText(Path.Combine(
        VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Orchestration", filename));
}
