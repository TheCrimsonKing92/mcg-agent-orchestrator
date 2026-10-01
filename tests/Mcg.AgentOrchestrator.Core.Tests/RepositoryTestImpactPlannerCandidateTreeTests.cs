namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class RepositoryTestImpactPlannerCandidateTreeTests
{
    private const string RemovedCliPath = "src/Mcg.AgentOrchestrator.App/Cli/Removed.cs";
    private const string CoreTestPath = "tests/Mcg.AgentOrchestrator.Core.Tests/SurvivingTests.cs";
    private const string CoreTestsProject = "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj";

    [Xunit.Fact(DisplayName = "An absent changed path cannot select CLI tests")]
    public void RemovedPathProducesNoTestSelection()
    {
        var plan = Plan([RemovedCliPath], new AbsentTree(RemovedCliPath));

        Assert.False(plan.RequiresBuild);
        Assert.All(plan.Checks, check => Assert.Empty(check.Command));
        Assert.All(plan.Checks, check => Assert.Null(check.TestClassSelections));
        Assert.Contains($"skipped removed path: {RemovedCliPath} (absent from candidate tree)", plan.Summary);
        Assert.All(plan.Checks, check => Assert.Contains(RemovedCliPath, check.Reason));
    }

    [Xunit.Fact(DisplayName = "An absent project removes its focused class selection")]
    public void AbsentProjectProducesNoFocusedSelection()
    {
        var plan = Plan([CoreTestPath], new AbsentTree(CoreTestsProject));

        Assert.False(plan.RequiresBuild);
        Assert.All(plan.Checks, check => Assert.Empty(check.Command));
        Assert.All(plan.Checks, check => Assert.Null(check.TestClassSelections));
        Assert.Contains($"skipped absent test project: {CoreTestsProject} (absent from candidate tree)", plan.Summary);
        Assert.All(plan.Checks, check => Assert.Contains(CoreTestsProject, check.Reason));
    }

    [Xunit.Fact(DisplayName = "Surviving checks retain their reason and record every absence")]
    public void SurvivingChecksKeepOriginalReasonAndBothSkips()
    {
        const string survivor = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SurvivorTests.cs";
        var baseline = Plan([survivor], CandidateTreeProbe.AssumeAllPresent);
        var plan = Plan([RemovedCliPath, CoreTestPath, survivor], new AbsentTree(RemovedCliPath, CoreTestsProject));

        var check = Assert.Single(plan.Checks);
        Assert.Equal(Assert.Single(baseline.Checks).CommandLine, check.CommandLine);
        Assert.StartsWith(Assert.Single(baseline.Checks).Reason, check.Reason);
        Assert.Contains(RemovedCliPath, check.Reason);
        Assert.Contains(CoreTestsProject, check.Reason);
        Assert.Contains(RemovedCliPath, plan.Summary);
        Assert.Contains(CoreTestsProject, plan.Summary);
        Assert.Equal(["SurvivorTests"], check.TestClassSelections);
    }

    [Xunit.Fact(DisplayName = "Broad verification also skips absent test projects")]
    public void FullSuiteChecksSkipAbsentProjects()
    {
        var plan = Plan(["Directory.Build.props"], new AbsentTree(CoreTestsProject));

        Assert.True(plan.RequiresBuild);
        Assert.True(plan.RequiresBroadVerification);
        Assert.DoesNotContain(plan.Checks, check => check.TestProject == RepositoryTestProject.Core);
        Assert.Contains(plan.Checks, check => check.TestProject == RepositoryTestProject.Infrastructure);
        Assert.Contains(CoreTestsProject, plan.Summary);
        Assert.All(plan.Checks, check => Assert.Contains(CoreTestsProject, check.Reason));
    }

    private static RepositoryTestImpactPlan Plan(string[] paths, ICandidateTreeProbe tree) =>
        RepositoryTestImpactPlanner.Plan(RepositoryChangeClassifier.Classify(paths),
            UnavailableTestClassDeclarationReader.Instance, tree);

    private sealed class AbsentTree(params string[] absentPaths) : ICandidateTreeProbe
    {
        public bool Exists(string path) => !absentPaths.Contains(path, StringComparer.OrdinalIgnoreCase);
    }
}
