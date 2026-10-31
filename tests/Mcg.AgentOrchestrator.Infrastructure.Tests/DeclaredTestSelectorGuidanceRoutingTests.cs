using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its worktree, project home and in-memory kernel.
public sealed class DeclaredTestSelectorGuidanceRoutingTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("declared-guidance-");

    [Theory]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Developer)]
    public void GuidanceStackFor_DeclaredHomeReachesBrief(AgentRole role)
    {
        var worktree = Directory.CreateDirectory(Path.Combine(_root.FullName, "worktree")).FullName;
        var home = Directory.CreateDirectory(Path.Combine(_root.FullName, "project-home")).FullName;
        // An explicit declared stack overrides incidental markers without inventing a Rust toolchain.
        File.WriteAllText(Path.Combine(worktree, "go.mod"), "module example.test/declared\n");
        File.WriteAllText(Path.Combine(home, "project-model.json"), """{"stack":"declared"}""");
        var targetHome = new WorkerTargetHome(false, projectHomeDirectory: home);
        Assert.Equal(Toolchain.Unknown, ProjectStackResolver.Resolve(worktree, home));

        var stack = ProjectStackResolver.GuidanceStackFor(worktree, targetHome);
        Assert.Equal(ProjectStack.Declared, stack);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Select test evidence", role);
        var goal = kernel.CreateGoal("Use declared project test results", [task]);
        var brief = kernel.BuildTaskBriefSource(goal.Id, task.Id, roleRequirementStack: stack)
            .ProjectLegacyMarkedTextV1(emitTypedSourceBoundaries: false).Content;

        Assert.Contains("Declared-test evidence selectors:", brief);
        Assert.Contains("Never construct selectors by joining suite and name with a dot", brief);
        Assert.Contains("Preserve :: paths", brief);
    }

    [Theory]
    [InlineData("dotnet", ProjectStack.Dotnet)]
    [InlineData("go", ProjectStack.Go)]
    [InlineData("crosswalk", null)]
    [InlineData("node", null)]
    [InlineData("rust", null)]
    public void GuidanceStackFor_OtherStacksKeepExistingRouting(string declaration, ProjectStack? expected)
    {
        var worktree = Directory.CreateDirectory(Path.Combine(_root.FullName, "worktree")).FullName;
        var home = Directory.CreateDirectory(Path.Combine(_root.FullName, "project-home")).FullName;
        File.WriteAllText(Path.Combine(home, "project-model.json"), $"{{\"stack\":\"{declaration}\"}}");

        Assert.Equal(expected, ProjectStackResolver.GuidanceStackFor(worktree,
            new WorkerTargetHome(false, projectHomeDirectory: home)));
    }

    public void Dispose() => _root.Delete(recursive: true);
}
