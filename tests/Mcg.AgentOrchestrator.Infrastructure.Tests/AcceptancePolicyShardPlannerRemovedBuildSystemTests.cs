using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptancePolicyShardPlannerRemovedBuildSystemTests
{
    private const string BuildPath = "Directory.Build.props";
    private const string SurvivorPath = "src/Mcg.AgentOrchestrator.App/Cli/Survivor.cs";

    [Xunit.Theory(DisplayName = "Removed build configuration forces full shards with or without a survivor")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void RemovedBuildConfigurationForcesFullShards(bool withSurvivor)
    {
        string[] paths = withSurvivor ? [BuildPath, SurvivorPath] : [BuildPath];
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan(paths,
            new(FullShards: false, ChangeScoped: true), new AbsentTree(BuildPath));

        Assert.True(plan.ForceFull);
        Assert.Contains($"build-system file changed: {BuildPath}", plan.Evidence);
        Assert.Contains($"skipped removed path: {BuildPath} (absent from candidate tree)", plan.Evidence);
        var presentPlan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
            withSurvivor ? [SurvivorPath] : [], new(FullShards: false, ChangeScoped: true),
            CandidateTreeProbe.AssumeAllPresent);
        Assert.True(plan.DependencyClosure.SetEquals(presentPlan.DependencyClosure));
    }

    [Xunit.Theory(DisplayName = "Removed build-system and script paths keep their existing full-shard reasons")]
    [Xunit.InlineData("src/Removed/Removed.csproj", "build-system file changed")]
    [Xunit.InlineData("Directory.Build.targets", "build-system file changed")]
    [Xunit.InlineData("Removed.sln", "build-system file changed")]
    [Xunit.InlineData("global.json", "build-system file changed")]
    [Xunit.InlineData("src/Removed/AssemblyInfo.cs", "build-system file changed")]
    [Xunit.InlineData("scripts/Removed.ps1", "script changed")]
    public void RemovedPathClausesForceFullShards(string path, string reason)
    {
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan([path],
            new(FullShards: false, ChangeScoped: true), new AbsentTree(path));

        Assert.True(plan.ForceFull);
        Assert.Contains($"{reason}: {path}", plan.Evidence);
        Assert.Contains($"skipped removed path: {path} (absent from candidate tree)", plan.Evidence);
        Assert.Empty(plan.DependencyClosure);
    }

    [Xunit.Theory(DisplayName = "Removed ordinary and security paths do not add shard escalation")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Cli/Removed.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure/Workers/RemovedSandboxGuard.cs")]
    public void RemovedNonBuildPathsKeepNoSelection(string path)
    {
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan([path],
            new(FullShards: false, ChangeScoped: true), new AbsentTree(path));

        Assert.False(plan.ForceFull);
        Assert.False(plan.Applies);
        Assert.Empty(plan.DependencyClosure);
        Assert.Contains($"skipped removed path: {path} (absent from candidate tree)", plan.Evidence);
    }

    [Xunit.Fact(DisplayName = "Full shards from a removed build file still skip absent projects")]
    public void RemovedBuildConfigurationStillSkipsAbsentProjects()
    {
        var project = AcceptancePolicyShardPlanner.CliTestsProject;
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan([BuildPath, SurvivorPath],
            new(FullShards: false, ChangeScoped: true), new AbsentTree(BuildPath, project));

        Assert.True(plan.ForceFull);
        Assert.Contains(project, plan.AbsentProjects);
        Assert.DoesNotContain(project, plan.DependencyClosure);
        Assert.False(plan.IncludesProject(project));
        Assert.Contains($"skipped absent test project: {project} (absent from candidate tree)", plan.Evidence);
    }

    private sealed class AbsentTree(params string[] absentPaths) : ICandidateTreeProbe
    {
        public bool Exists(string path) => !absentPaths.Contains(path, StringComparer.OrdinalIgnoreCase);
    }
}
