using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: plans and goals are entirely in memory.
public sealed class TestFirstPipelineShapeTests
{
    private const string Objective = "Implement src/Widget.cs with focused tests.";

    [Fact]
    public void Build_TestFirst_DeclaresOrderedScopesAndCriterionReview()
    {
        var plan = GoalObjectivePlanner.Build(Objective, GoalIntakePipeline.TestFirst);

        Assert.Equal([AgentRole.Planner, AgentRole.Developer, AgentRole.Developer, AgentRole.Reviewer],
            plan.TaskBoundaries.Select(boundary => boundary.Role));
        Assert.Equal([1, 2, 3, 4], plan.TaskBoundaries.Select(boundary => boundary.Index));
        Assert.Equal(["read-only", "workspace-write", "workspace-write", "read-only"],
            plan.TaskBoundaries.Select(boundary => boundary.Capability));
        Assert.Equal("test-first", plan.Workflow);
        Assert.Equal("test-first", plan.PipelineDecision.Workflow);
        Assert.Contains("operator override selected test-first", plan.PipelineDecision.Reasons);
        Assert.Equal("Inspect current source and synthesize a criterion-mapped Scout plan. For each Developer-owned criterion, name the test class and the outcome the test-author task asserts.", plan.TaskBoundaries[0].Purpose);
        Assert.Equal("Test author: write one failing test per Developer-owned acceptance criterion that asserts the criterion's named outcome, not implementation details. Change only files under tests/, plus additions-only compile stubs under src/ whose bodies throw NotImplementedException. Do not implement behavior. Report the authored classes in tests: deferred.", plan.TaskBoundaries[1].Purpose);
        Assert.Equal("Implement: make the authored tests pass. Do not edit any file the test-author task changed under tests/. If an authored test contradicts a criterion, stop with blockers: premise-invalid naming the test and the criterion.", plan.TaskBoundaries[2].Purpose);
        Assert.Equal("First check each authored test against its acceptance criterion: it asserts the criterion's named outcome, not implementation details, and one exists for every Developer-owned criterion. Then review the implementation diff, tests, and worker evidence before acceptance.", plan.TaskBoundaries[3].Purpose);
    }

    [Fact]
    public void Parse_TestFirst_IsCaseInsensitiveAndMapsToOverride()
    {
        var request = GoalIntakePipelineRequestParser.Parse("Test-First");
        Assert.Equal(GoalIntakePipelineRequest.TestFirst, request);
        Assert.Equal("test-first", request.ToCanonicalValue());
        Assert.Equal(GoalIntakePipeline.TestFirst, request.ToPipelineOverride());
        Assert.Equal("auto, scout, developer-reviewer, developer-only, test-first", GoalIntakePipelineRequestParser.AllowedValues);
    }

    [Fact]
    public void Build_Automatic_StillUsesScout()
    {
        var plan = GoalObjectivePlanner.Build(Objective);
        Assert.Equal("scout", plan.Workflow);
        Assert.Equal([AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
            plan.TaskBoundaries.Select(boundary => boundary.Role));
    }

    [Fact]
    public void PersistedTestFirst_AcceptsSamePlanAndNamesWorkflowWhenRejectingScout()
    {
        var kernel = new AgentOrchestratorKernel();
        var plan = GoalObjectivePlanner.Build(Objective, GoalIntakePipeline.TestFirst);
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(kernel, AgentCatalog.Default().Agents, plan);
        Assert.True(TestFirstScopeCheck.IsTestFirst(goal));
        GoalLifecycleCommands.EnsureRequestedPipelineMatchesPersistedGoal(plan, goal);

        var error = Assert.Throws<InvalidOperationException>(() =>
            GoalLifecycleCommands.EnsureRequestedPipelineMatchesPersistedGoal(
                GoalObjectivePlanner.Build(Objective, GoalIntakePipeline.Scout), goal));
        Assert.Contains("test-first", error.Message, StringComparison.Ordinal);
    }
}
