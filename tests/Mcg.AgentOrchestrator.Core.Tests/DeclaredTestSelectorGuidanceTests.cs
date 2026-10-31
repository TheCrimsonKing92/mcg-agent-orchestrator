using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: brief construction uses an in-memory kernel.
public sealed class DeclaredTestSelectorGuidanceTests
{
    [Fact]
    public void StackGuidance_OwnsDeclaredTestSelectors()
    {
        var guidance = Assert.Single(DeclaredTestSelectorGuidance.For(ProjectStack.Declared));
        Assert.Equal(new[] { "Existing requirement", guidance },
            RoleRequirementStackGuidance.Apply(["Existing requirement"], ProjectStack.Declared));
        foreach (var stack in new ProjectStack?[] { ProjectStack.Dotnet, ProjectStack.Go, ProjectStack.Node, null })
            Assert.Equal(new[] { "Existing requirement" },
                RoleRequirementStackGuidance.Apply(["Existing requirement"], stack));
    }

    [Theory]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Developer)]
    public void DeclaredProjectBrief_UsesReportIdentityGuidance(AgentRole role)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Select test evidence", role);
        var goal = kernel.CreateGoal("Use declared project test results", [task]);
        var declared = kernel.BuildTaskBriefSource(goal.Id, task.Id, roleRequirementStack: ProjectStack.Declared)
            .ProjectLegacyMarkedTextV1(emitTypedSourceBoundaries: false).Content;
        Assert.Contains(Assert.Single(DeclaredTestSelectorGuidance.For(ProjectStack.Declared)), declared);
        Assert.Contains("Never construct selectors by joining suite and name with a dot", declared);
        foreach (var stack in new ProjectStack?[] { ProjectStack.Dotnet, ProjectStack.Go, ProjectStack.Node, null })
        {
            var other = kernel.BuildTaskBriefSource(goal.Id, task.Id, roleRequirementStack: stack)
                .ProjectLegacyMarkedTextV1(emitTypedSourceBoundaries: false).Content;
            Assert.DoesNotContain("Declared-test evidence selectors:", other);
        }
    }
}
