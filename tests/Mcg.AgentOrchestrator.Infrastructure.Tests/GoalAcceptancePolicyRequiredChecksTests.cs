using System.Reflection;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: the candidate tree is injected; no filesystem or process state is used.
public sealed class GoalAcceptancePolicyRequiredChecksTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void AppRenderingChangeKeepsRequiredChecksOutsideClosure(bool focusedAbsentCheck)
    {
        const string absentProject = "tests/Removed.Tests/Removed.Tests.csproj";
        string[] changedPaths = ["src/Mcg.AgentOrchestrator.App/Rendering/Renderer.cs"];
        var shardPlan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
            changedPaths, new(FullShards: false, ChangeScoped: true),
            new AbsentTree(absentProject), [absentProject]);
        var requiredCore = TestCheck("required core", AcceptancePolicyShardPlanner.CoreTestsProject);
        var requiredAcceptance = TestCheck("required acceptance", AcceptancePolicyShardPlanner.AcceptanceTestsProject);
        var absentCheck = TestCheck("absent required tests", absentProject,
            focusedAbsentCheck ? ["--filter", "FullyQualifiedName~RemovedTests"] : []);
        var nonTestCheck = new AcceptanceManifestCheck
        {
            Name = "required non-test check", Type = "no-op", Project = absentProject
        };
        var manifestCore = TestCheck("manifest core", AcceptancePolicyShardPlanner.CoreTestsProject);
        var manifestInfrastructure = TestCheck("manifest infrastructure", AcceptancePolicyShardPlanner.InfrastructureTestsProject);

        Assert.True(shardPlan.Applies);
        Assert.False(shardPlan.ForceFull);
        Assert.False(shardPlan.IncludesProject(requiredCore.Project));
        Assert.False(shardPlan.IncludesProject(requiredAcceptance.Project));
        Assert.True(shardPlan.IncludesProject(manifestInfrastructure.Project));
        Assert.True(shardPlan.IsAbsentProject(absentProject));
        Assert.Contains(absentProject, shardPlan.Evidence);

        // Exercise the composition seam with an explicit plan so ambient shard switches cannot
        // force a full plan and mask the distinction between required and manifest checks.
        var compose = typeof(GoalAcceptanceVerifier).GetMethod(
            "BuildPolicyEffectiveChecks", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(compose);
        var result = compose.Invoke(null,
        [
            string.Empty,
            new AcceptanceManifestCheck[] { manifestCore, manifestInfrastructure },
            changedPaths,
            new AcceptanceManifestCheck[] { requiredCore, requiredAcceptance, absentCheck, nonTestCheck },
            shardPlan,
            null
        ]);
        var checks = Assert.IsAssignableFrom<IReadOnlyList<AcceptanceManifestCheck>>(result);

        Assert.Contains(requiredCore, checks);
        Assert.Contains(requiredAcceptance, checks);
        Assert.Contains(manifestInfrastructure, checks);
        Assert.Contains(nonTestCheck, checks);
        Assert.DoesNotContain(absentCheck, checks);
        Assert.DoesNotContain(manifestCore, checks);
    }

    private static AcceptanceManifestCheck TestCheck(string name, string project, string[]? arguments = null) =>
        new() { Name = name, Type = "dotnet-test", Project = project, Arguments = arguments ?? [] };

    private sealed class AbsentTree(string absentProject) : ICandidateTreeProbe
    {
        public bool Exists(string path) => !path.Equals(absentProject, StringComparison.OrdinalIgnoreCase);
    }
}
