using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

public sealed class CandidateTreeAbsenceSkipTests
{
    private const string RemovedPath = "src/Mcg.AgentOrchestrator.App/Cli/Removed.cs";
    private const string SurvivorPath = "src/Mcg.AgentOrchestrator.Core/Domain/Survivor.cs";

    [Xunit.Fact(DisplayName = "Both planners skip removed paths and absent projects in the candidate root")]
    public void CandidateRootAbsencesRemoveChecksAndSelections()
    {
        var root = CreateRoot();
        try
        {
            Write(root, ".git", "gitdir: isolated-fixture");
            Write(root, "Mcg.AgentOrchestrator.sln", "");
            Write(root, SurvivorPath, "namespace Fixture; public class Survivor { }");
            Write(root, AcceptancePolicyShardPlanner.InfrastructureTestsProject, "<Project />");

            var paths = new[] { RemovedPath, SurvivorPath };
            var impact = RepositoryTestImpactPlanner.Plan(paths, root);
            Assert.DoesNotContain(impact.Checks, check => check.TestProject == RepositoryTestProject.Core);
            Assert.DoesNotContain(impact.Checks, check => check.Name == "focused CLI infrastructure tests");
            Assert.Contains(RemovedPath, impact.Summary);
            Assert.Contains(AcceptancePolicyShardPlanner.CoreTestsProject, impact.Summary);
            Assert.All(impact.Checks, check =>
            {
                Assert.Contains(RemovedPath, check.Reason);
                Assert.Contains(AcceptancePolicyShardPlanner.CoreTestsProject, check.Reason);
            });

            var shards = AcceptancePolicyShardPlanner.BuildPolicyShardPlan(paths,
                new(FullShards: false, ChangeScoped: true), CandidateTreeProbe.ForRepositoryRoot(root));
            Assert.DoesNotContain(AcceptancePolicyShardPlanner.CoreTestsProject, shards.DependencyClosure);
            Assert.DoesNotContain("tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj", shards.DependencyClosure);
            Assert.StartsWith("changed projects: Core;", shards.Evidence);
            Assert.Contains(RemovedPath, shards.Evidence);
            Assert.Contains(AcceptancePolicyShardPlanner.CoreTestsProject, shards.Evidence);
            Assert.True(AcceptancePolicyShardPlanner.IsSkippedPolicyShardCheck(
                TestCheck(AcceptancePolicyShardPlanner.CoreTestsProject), shards));
            Assert.False(AcceptancePolicyShardPlanner.IsRunnablePolicyShardCheck(
                TestCheck(AcceptancePolicyShardPlanner.CoreTestsProject), shards));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Theory(DisplayName = "Missing projects are skipped for full, scoped and empty shard plans")]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, true)]
    public void AllShardModesSkipAbsentManifestProjects(bool forceFull, bool noChanges)
    {
        const string project = "tests/Custom.Tests/Custom.Tests.csproj";
        var tree = new AbsentTree(project);
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
            noChanges ? [] : [SurvivorPath], new(FullShards: forceFull, ChangeScoped: true), tree, [project]);
        var focused = new AcceptanceManifestCheck
        {
            Name = "fixture focused tests", Type = "dotnet-test", Project = project,
            Arguments = ["--filter", "FullyQualifiedName~CustomTests"]
        };

        Assert.True(AcceptancePolicyShardPlanner.IsSkippedPolicyShardCheck(focused, plan));
        Assert.True(AcceptancePolicyShardPlanner.IsSkippedPolicyShardCheck(TestCheck(project), plan));
        Assert.False(plan.IncludesProject(project));
        Assert.False(AcceptancePolicyShardPlanner.IsRunnablePolicyShardCheck(focused, plan));
        Assert.Contains($"skipped absent test project: {project} (absent from candidate tree)", plan.Evidence);
        Assert.DoesNotContain(project, plan.DependencyClosure);
    }

    [Xunit.Fact(DisplayName = "Dashboard test paths no longer route to a project")]
    public void DashboardTestPathsDoNotMapToDashboardProject()
    {
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
            ["tests/Mcg.AgentOrchestrator.Dashboard.Tests/DashboardRenderingTests.cs"],
            new(FullShards: false, ChangeScoped: true), CandidateTreeProbe.AssumeAllPresent);

        Assert.Empty(plan.DependencyClosure);
        Assert.True(plan.Applies);
        Assert.False(plan.ForceFull);
        Assert.False(plan.IncludesProject("tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj"));
        Assert.Equal("changed projects: (none); dependency closure: (none)", plan.Evidence);
    }

    [Xunit.Theory(DisplayName = "Effective acceptance plans never schedule an absent manifest project")]
    [Xunit.InlineData((string?)null)]
    [Xunit.InlineData(RemovedPath)]
    public void EffectivePlanOmitsAbsentManifestChecks(string? changedPath)
    {
        var root = CreateRoot();
        try
        {
            Write(root, ".git", "gitdir: isolated-fixture");
            Write(root, "Mcg.AgentOrchestrator.sln", "");
            Write(root, "config/acceptance-manifest.json", """
                {
                  "engine": { "enforceStructuralCoverage": false },
                  "checks": [
                    { "name": "absent tests", "type": "dotnet-test",
                      "project": "tests/Custom.Tests/Custom.Tests.csproj" },
                    { "name": "surviving check", "type": "no-op" }
                  ]
                }
                """);
            var checks = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(
                root, changedPath is null ? null : [changedPath]);

            Assert.DoesNotContain(checks, check => check.Type == "dotnet-test");
            Assert.Contains(checks, check => check.Name == "surviving check");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Theory(DisplayName = "Absence detection requires both repository markers")]
    [Xunit.InlineData(false, false, true)]
    [Xunit.InlineData(true, false, true)]
    [Xunit.InlineData(false, true, true)]
    [Xunit.InlineData(true, true, false)]
    public void CandidateProbeRequiresCompleteRoot(bool git, bool solution, bool assumedPresent)
    {
        var root = CreateRoot();
        try
        {
            if (git) Write(root, ".git", "gitdir: isolated-fixture");
            if (solution) Write(root, "Mcg.AgentOrchestrator.sln", "");
            Assert.Equal(assumedPresent, CandidateTreeProbe.ForRepositoryRoot(root).Exists(RemovedPath));
            Write(root, RemovedPath, "survivor");
            Assert.True(CandidateTreeProbe.ForRepositoryRoot(root).Exists(RemovedPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static AcceptanceManifestCheck TestCheck(string project) =>
        new() { Name = "fixture tests", Type = "dotnet-test", Project = project };

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "candidate-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Write(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private sealed class AbsentTree(params string[] absentPaths) : ICandidateTreeProbe
    {
        public bool Exists(string path) => !absentPaths.Contains(path, StringComparer.OrdinalIgnoreCase);
    }
}
