namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class RepositoryTestImpactPlannerRemovedEscalationTests
{
    private const string BuildPath = "Directory.Build.props";
    private const string SurvivorPath = "src/Mcg.AgentOrchestrator.App/Cli/Survivor.cs";

    [Xunit.Fact(DisplayName = "Removing build configuration still requires all relevant test projects")]
    public void RemovedBuildConfigurationRequiresFullSuite()
    {
        var plan = Plan([BuildPath], new AbsentTree(BuildPath));

        Assert.True(plan.RequiresBuild);
        Assert.True(plan.RequiresBroadVerification);
        foreach (var project in new[]
                 {
                     RepositoryTestProject.Core, RepositoryTestProject.Infrastructure,
                     RepositoryTestProject.Acceptance, RepositoryTestProject.ProviderEnvironment,
                     RepositoryTestProject.Cli
                 })
        {
            Assert.Contains(plan.Checks, check => check.TestProject == project);
        }
        Assert.Contains("Build configuration changed", plan.Summary);
        Assert.Contains($"skipped removed path: {BuildPath} (absent from candidate tree)", plan.Summary);
    }

    [Xunit.Fact(DisplayName = "Removed build configuration escalates a surviving CLI change")]
    public void RemovedBuildConfigurationEscalatesMixedChange()
    {
        var plan = Plan([BuildPath, SurvivorPath], new AbsentTree(BuildPath));

        Assert.True(plan.RequiresBroadVerification);
        Assert.Contains(plan.Checks, check => check.TestProject == RepositoryTestProject.Core);
    }

    [Xunit.Fact(DisplayName = "Removing a sandbox guard still requires human risk review")]
    public void RemovedSecurityPathRequiresHumanRiskReview()
    {
        const string path = "src/Mcg.AgentOrchestrator.Infrastructure/Workers/RemovedSandboxGuard.cs";
        var plan = Plan([path], new AbsentTree(path));

        Assert.True(plan.RequiresBroadVerification);
        var policy = VerificationPolicyCompiler.Compile(AgentRole.Developer, "", "", null, [path], plan);
        Assert.Contains(policy.Checks, check => check.Required && check.Name == "human risk review");
    }

    [Xunit.Fact(DisplayName = "A removed build file escalates while absent test projects remain skipped")]
    public void RemovedBuildConfigurationStillSkipsAbsentProjects()
    {
        const string coreProject = "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj";
        var plan = Plan([BuildPath], new AbsentTree(BuildPath, coreProject));

        Assert.True(plan.RequiresBuild);
        Assert.True(plan.RequiresBroadVerification);
        Assert.DoesNotContain(plan.Checks, check => check.TestProject == RepositoryTestProject.Core);
        Assert.Contains(plan.Checks, check => check.TestProject == RepositoryTestProject.Infrastructure);
        Assert.Contains($"skipped absent test project: {coreProject} (absent from candidate tree)", plan.Summary);
    }

    private static RepositoryTestImpactPlan Plan(string[] paths, ICandidateTreeProbe tree) =>
        RepositoryTestImpactPlanner.Plan(RepositoryChangeClassifier.Classify(paths),
            UnavailableTestClassDeclarationReader.Instance, tree);

    private sealed class AbsentTree(params string[] absentPaths) : ICandidateTreeProbe
    {
        public bool Exists(string path) => !absentPaths.Contains(path, StringComparer.OrdinalIgnoreCase);
    }
}
