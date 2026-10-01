namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class RepositoryTestImpactPlannerDashboardRouteTests
{
    [Xunit.Fact(DisplayName = "Dashboard source and test changes emit no dashboard checks or filters")]
    public void DashboardChangesEmitNoDashboardChecks()
    {
        var plan = RepositoryTestImpactPlanner.Plan(
        [
            "src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.cs",
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests/DashboardRenderingTests.cs"
        ]);

        Assert.NotEmpty(plan.Checks);
        Assert.DoesNotContain(plan.Checks, check =>
            check.CommandLine.Contains("Mcg.AgentOrchestrator.Dashboard.Tests", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Checks, check =>
            check.Name.Contains("dashboard", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Checks.SelectMany(check => check.TestClassSelections ?? []),
            name => name.StartsWith("Dashboard", StringComparison.Ordinal));
    }

    [Xunit.Theory(DisplayName = "Dashboard App paths receive the same plan as sibling App paths")]
    [Xunit.InlineData("Rendering/DashboardRenderer.cs", false)]
    [Xunit.InlineData("Api/DashboardEndpoints.cs", false)]
    [Xunit.InlineData("Rendering/DashboardRenderer.cs", true)]
    [Xunit.InlineData("Api/DashboardEndpoints.cs", true)]
    public void DashboardAppPathsMatchSiblingAppPaths(string suffix, bool includeCli)
    {
        var dashboardPaths = new List<string> { $"src/Mcg.AgentOrchestrator.App/Dashboard/{suffix}" };
        var siblingPaths = new List<string> { $"src/Mcg.AgentOrchestrator.App/{suffix}" };
        if (includeCli)
        {
            dashboardPaths.Add("src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.cs");
            siblingPaths.Add("src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.cs");
        }
        var actual = RepositoryTestImpactPlanner.Plan(dashboardPaths);
        var expected = RepositoryTestImpactPlanner.Plan(siblingPaths);

        Assert.Equal(expected.RequiresBuild, actual.RequiresBuild);
        Assert.Equal(expected.RequiresBroadVerification, actual.RequiresBroadVerification);
        Assert.Equal(expected.Summary, actual.Summary);
        Assert.Equal(expected.Checks.Select(check => check.Name), actual.Checks.Select(check => check.Name));
        Assert.Equal(expected.Checks.Select(check => check.CommandLine), actual.Checks.Select(check => check.CommandLine));
        Assert.Equal(expected.Checks.Select(check => check.TestProject), actual.Checks.Select(check => check.TestProject));
        Assert.Equal(expected.Checks.Select(check => check.Reason), actual.Checks.Select(check => check.Reason));
        Assert.Equal(expected.Checks.SelectMany(check => check.TestClassSelections ?? []),
            actual.Checks.SelectMany(check => check.TestClassSelections ?? []));
    }
}
