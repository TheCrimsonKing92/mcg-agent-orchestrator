using Mcg.AgentOrchestrator.Core;

public sealed class DeveloperDeferredDottedIdentityParseTests
{
    [Fact]
    public void MethodIdentityYieldsDeclaringClass()
    {
        Assert.Equal(["AlphaTests", "CanaryEngineSurfaceCoverageTests"],
            DeveloperDeferredTestClassNames.Parse("deferred - AlphaTests, " +
                "CanaryEngineSurfaceCoverageTests.Verifier_dependencies_are_covered_by_canary_surface, " +
                "CanaryEngineSurfaceCoverageTests.Missing_settings_prefix_reports_only_settings_source; conductor executes tests"));
        Assert.Equal(["BetaTests"],
            DeveloperDeferredTestClassNames.Parse("deferred - Suite.BetaTests.Second(case)"));
    }

    [Fact]
    public void GoAndFileEntriesYieldNoClass()
    {
        Assert.Empty(DeveloperDeferredTestClassNames.Parse(
            "deferred - main.TestStdoutIdempotency, main.TestStderrSeparation; conductor executes go test -count=1 ./..."));
        Assert.Empty(DeveloperDeferredTestClassNames.Parse("deferred - Alpha.cs"));
        Assert.Equal(["AlphaTests"], DeveloperDeferredTestClassNames.Parse(
            "deferred - AlphaTests, main.TestStdoutIdempotency, BetaTests"));
    }

    [Theory]
    [InlineData("namespace_1.Suite.BetaTests.Second(case)", "BetaTests")]
    [InlineData("_suite.BetaTests.Second()", "BetaTests")]
    [InlineData("1suite.BetaTests.Second", null)]
    [InlineData("Suite..BetaTests.Second", null)]
    [InlineData("Suite.betaTests.Second", null)]
    [InlineData("Suite.BetaTests.second", null)]
    [InlineData("Suite.BetaTests.Second(nested(case))", null)]
    [InlineData("Suite.BetaTests.Second(case)(other)", null)]
    public void OnlySupportedIdentitySegmentsExtendThePrefix(string identity, string? expected)
    {
        var names = DeveloperDeferredTestClassNames.Parse($"deferred - {identity}, AfterTests");
        Assert.Equal(expected is null ? Array.Empty<string>() : [expected, "AfterTests"], names);
    }
}
