using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class GoalObjectivePlannerPipelineShapeTests
{
    [Xunit.Fact]
    public void Build_WholeGoalReview_CreatesOneReadOnlyReviewer()
    {
        var plan = GoalObjectivePlanner.Build(
            "Implement src/Feature.cs with focused tests.",
            GoalIntakePipeline.WholeGoalReview);

        Xunit.Assert.Equal([AgentRole.Reviewer], plan.TaskBoundaries.Select(boundary => boundary.Role));
        var reviewer = Xunit.Assert.Single(plan.TaskBoundaries);
        Xunit.Assert.Equal(1, reviewer.Index);
        Xunit.Assert.Equal("read-only", reviewer.Capability);
        Xunit.Assert.Contains("composed whole goal against all acceptance criteria", reviewer.Purpose, StringComparison.Ordinal);
        Xunit.Assert.Equal("whole-goal-review", plan.Workflow);
        Xunit.Assert.Equal("whole-goal-review", plan.PipelineDecision.Workflow);
        Xunit.Assert.Contains("operator override selected Whole-Goal Reviewer", plan.PipelineDecision.Reasons);
    }

    [Xunit.Fact]
    public void Parse_WholeGoalReview_RejectsInternalValueAndKeepsExistingValues()
    {
        Xunit.Assert.Equal("auto, scout, five-role, developer-reviewer, developer-only",
            GoalIntakePipelineRequestParser.AllowedValues);
        Xunit.Assert.Throws<ArgumentException>(() => GoalIntakePipelineRequestParser.Parse("whole-goal-review"));
    }

    [Xunit.Fact]
    public void Build_StreamPipeline_CreatesDeveloperThenStreamReviewer()
    {
        var plan = GoalObjectivePlanner.Build(
            "Implement src/Feature.cs with focused tests.",
            GoalIntakePipeline.DeveloperStreamReviewer);

        Xunit.Assert.Equal([AgentRole.Developer, AgentRole.Reviewer],
            plan.TaskBoundaries.Select(boundary => boundary.Role));
        Xunit.Assert.Equal([1, 2], plan.TaskBoundaries.Select(boundary => boundary.Index));
        Xunit.Assert.Equal("developer-stream-reviewer", plan.Workflow);
        Xunit.Assert.Equal("developer-stream-reviewer", plan.PipelineDecision.Workflow);
        var reviewer = plan.TaskBoundaries[1];
        Xunit.Assert.Contains("single stream's diff", reviewer.Purpose, StringComparison.Ordinal);
        Xunit.Assert.Contains("evidence", reviewer.Purpose, StringComparison.Ordinal);
        Xunit.Assert.Equal("read-only", reviewer.Capability);
        Xunit.Assert.Contains(plan.PipelineDecision.Reasons,
            reason => reason.Contains("Developer+stream-Reviewer", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void Parse_StreamPipeline_RejectsInternalValueAndKeepsExistingValues()
    {
        string[] expected = ["auto", "scout", "five-role", "developer-reviewer", "developer-only"];
        Xunit.Assert.Equal(expected, GoalIntakePipelineRequestParser.AllowedValues.Split(", "));
        Xunit.Assert.Equal(expected.Order(), Enum.GetValues<GoalIntakePipelineRequest>()
            .Select(request => request.ToCanonicalValue()).Order());
        foreach (var value in expected)
        {
            Xunit.Assert.Equal(value, GoalIntakePipelineRequestParser.Parse(value).ToCanonicalValue());
        }

        Xunit.Assert.Throws<ArgumentException>(() =>
            GoalIntakePipelineRequestParser.Parse("developer-stream-reviewer"));
    }
}
