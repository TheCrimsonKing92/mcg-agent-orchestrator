// Parallel-safe: reads source from the explicitly verified repository root only.
public sealed class IncompleteScopeRetryEvidenceBoundaryTests
{
    [Fact]
    public void RetryContext_DelegatesEvidence_RetainsDriverRendering()
    {
        var driver = ReadSource("ConductorDriver.IncompleteScopeRetryContext.cs");

        Assert.Contains("IncompleteScopeRetryEvidence.BuildPreviousRoundAccount(", driver,
            StringComparison.Ordinal);
        Assert.Contains("IncompleteScopeRetryEvidence.SelectNewestCandidateRed(", driver,
            StringComparison.Ordinal);
        Assert.Contains("AppendActionableCandidateRedFailureDetail(", driver, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "Regex", "WORKER_RESULT", "OrderByDescending" })
            Assert.DoesNotContain(forbidden, driver, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_Source_HasNoDriverOrRenderingDependency()
    {
        var evidence = ReadSource("IncompleteScopeRetryEvidence.cs");

        Assert.Contains("internal static class IncompleteScopeRetryEvidence", evidence, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "ConductorDriver", "partial", "Console.",
            "_getPreReviewEvidenceContext", "AppendActionableCandidateRedFailureDetail",
            "ActionableCandidateRedFailureDetailRenderer" })
            Assert.DoesNotContain(forbidden, evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedGoalRecovery_Source_RetainsRetryFeedbackBoundary()
    {
        var recovery = ReadSource("ConductorDriver.FailedGoalRecovery.cs");

        Assert.Contains("BuildCriterionRetryFeedback(", recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("IncompleteScopeRetryEvidence", recovery, StringComparison.Ordinal);
    }

    private static string ReadSource(string filename) => File.ReadAllText(Path.Combine(
        VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Orchestration", filename));
}
