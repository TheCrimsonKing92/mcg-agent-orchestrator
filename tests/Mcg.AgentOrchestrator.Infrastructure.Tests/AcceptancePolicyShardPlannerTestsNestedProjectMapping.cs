using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure planner inputs and read-only reflection; no ambient state changes.
public sealed class AcceptancePolicyShardPlannerTestsNestedProjectMapping
{
    private const string AcceptanceProbePath =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Acceptance/AcceptanceProbeTests.cs";

    private static readonly AcceptanceShardPolicySwitches ScopedSwitches =
        new(FullShards: false, ChangeScoped: true);

    public static IEnumerable<object[]> ProjectMappings =>
    [
        [AcceptancePolicyShardPlanner.CoreProject, "Core"],
        [AcceptancePolicyShardPlanner.ProvidersProject, "Infrastructure.Providers"],
        [AcceptancePolicyShardPlanner.OperatorCommsProject, "Infrastructure.OperatorComms"],
        [AcceptancePolicyShardPlanner.ExecutionProject, "Execution"],
        [AcceptancePolicyShardPlanner.InfrastructureProject, "Infrastructure"],
        [AcceptancePolicyShardPlanner.AppProject, "App"],
        [AcceptancePolicyShardPlanner.CoreTestsProject, "Core.Tests"],
        [AcceptancePolicyShardPlanner.TestSupportProject, "TestSupport"],
        [AcceptancePolicyShardPlanner.InfrastructureTestsProject, "Infrastructure.Tests"],
        [AcceptancePolicyShardPlanner.AcceptanceTestsProject, "Infrastructure.Acceptance.Tests"],
        [AcceptancePolicyShardPlanner.ProviderEnvironmentTestsProject, "Infrastructure.ProviderEnvironment.Tests"],
        [AcceptancePolicyShardPlanner.CliTestsProject, "Infrastructure.Cli.Tests"]
    ];

    [Fact]
    public void BuildPolicyShardPlan_AcceptanceFile_IncludesNestedProject()
    {
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
            [AcceptanceProbePath], ScopedSwitches);

        Assert.True(plan.Applies);
        Assert.False(plan.ForceFull);
        Assert.True(plan.IncludesProject(AcceptancePolicyShardPlanner.AcceptanceTestsProject));
        Assert.StartsWith("changed projects: Infrastructure.Acceptance.Tests;", plan.Evidence,
            StringComparison.Ordinal);
    }

    [Fact]
    public void IsSkippedPolicyShardCheck_AcceptanceFile_RunsFullProjectCheck()
    {
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
            [AcceptanceProbePath], ScopedSwitches);
        var check = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "acceptance execution owner tests",
            Type = "dotnet-test",
            Project = AcceptancePolicyShardPlanner.AcceptanceTestsProject
        };

        Assert.True(plan.Applies);
        Assert.False(plan.ForceFull);
        Assert.Empty(check.Arguments);
        Assert.False(AcceptancePolicyShardPlanner.IsSkippedPolicyShardCheck(check, plan));
    }

    [Theory]
    [MemberData(nameof(ProjectMappings))]
    public void BuildPolicyShardPlan_ProjectOwnDirectory_NamesOnlyThatProject(
        string project, string expectedLabel)
    {
        var directory = project[..(project.LastIndexOf('/') + 1)];
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
            [directory + "NestedProjectMappingProbe.cs"], ScopedSwitches);

        Assert.True(plan.Applies);
        Assert.False(plan.ForceFull);
        Assert.True(plan.IncludesProject(project));
        Assert.StartsWith($"changed projects: {expectedLabel}; dependency closure: ",
            plan.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectMappings_DependencyTableKeys_HaveExactlyOneDeclaredRow()
    {
        var field = typeof(AcceptancePolicyShardPlanner).GetField(
            "ReferencingProjectsByProject", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var table = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string[]>>(
            field.GetValue(null));
        Assert.NotEmpty(table);

        var declaredProjects = ProjectMappings.Select(row => (string)row[0]).ToArray();
        var declaredKeys = declaredProjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(declaredProjects.Length, declaredKeys.Count);
        Assert.Empty(table.Keys.Except(declaredKeys, StringComparer.OrdinalIgnoreCase));
        Assert.Empty(declaredKeys.Except(table.Keys, StringComparer.OrdinalIgnoreCase));
    }
}
