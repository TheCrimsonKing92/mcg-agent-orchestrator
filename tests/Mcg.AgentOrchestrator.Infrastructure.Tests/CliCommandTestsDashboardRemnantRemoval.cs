using Mcg.AgentOrchestrator.Core;

// Parallel-safe: ownership classification is pure; CLI fixtures own separate workspace roots
// and use the base fixture's async-local console capture.
public sealed class CliCommandTestsDashboardRemnantRemoval : CliCommandTestBase
{
    [Theory]
    [InlineData("src/Mcg.AgentOrchestrator.App/Dashboard/Api/X.cs")]
    [InlineData("src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/X.cs")]
    public void Ownership_FormerDashboardPaths_UsesAppSourceRules(string path)
    {
        var owned = RepositoryOwnershipMap.Classify(path);
        Assert.Equal(RepositoryOwnershipArea.Source, owned.Area);
        Assert.False(owned.IsHighRisk);
        Assert.Equal("source:Mcg.AgentOrchestrator.App", owned.ReservationKey);

        var relaunch = RepositoryChangeClassifier.DecideConductorRelaunch([path]);
        Assert.True(relaunch.Required);
        Assert.Equal("conductor-source", relaunch.Classification);
    }

    [Fact]
    public void Architecture_CliReport_OmitsRemovedHttpSurfaces()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var result = ExecuteCliAndCaptureResult(["architecture"], new AgentOrchestratorKernel(), workspace);

        Assert.False(result.Changed);
        Assert.Contains("Architecture:", result.Output, StringComparison.Ordinal);
        Assert.Contains("state stores:", result.Output, StringComparison.Ordinal);
        Assert.Contains("safety gates:", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("api surfaces:", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/api/", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dashboard", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IntentTemplate_RemovedDashboardName_ReportsSixRemainingTemplates()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var error = Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(
            ["intent-template", "dashboard", "Update the label."], new AgentOrchestratorKernel(), workspace));

        Assert.Equal("Unknown intent template 'dashboard'. Available: feature, bugfix, refactor, " +
            "test-hardening, skill-authoring, release-prep.", error.Message);
    }
}
