using Mcg.AgentOrchestrator.Core;

// Parallel-safe: in-memory kernels and one uniquely owned temporary context directory.
public sealed class TaskBriefSourceSurveyGuidanceTests
{
    [Theory]
    [InlineData(AgentRole.Developer, "Update the label.", TaskComplexity.Simple)]
    [InlineData(AgentRole.Developer,
        "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
        TaskComplexity.Complex)]
    [InlineData(AgentRole.Researcher, "Inspect the current label.", TaskComplexity.Simple)]
    public void TaskBrief_WorkerRoles_UsesFileSourceSurvey(
        AgentRole role, string description, TaskComplexity complexity)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), description, role);
        var goal = kernel.CreateGoal("Maintain the label.", [task]);
        Assert.Equal(complexity, TaskComplexityEstimator.Estimate(description, goal.Objective, role));

        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

        Assert.Contains("source-survey.md in the context directory", brief, StringComparison.Ordinal);
        Assert.Contains("do not attempt to reach orchestrator state.", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/source-survey", brief, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dashboard", brief, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TaskBrief_PlannerWithDurableResearch_OmitsSourceDiscovery()
    {
        var context = Directory.CreateTempSubdirectory("source-survey-guidance-");
        try
        {
            File.WriteAllText(Path.Combine(context.FullName, "research-notes.md"), "The label is defined in source.");
            var kernel = new AgentOrchestratorKernel();
            var planner = new TaskSpec(TaskId.New(), "Plan the label update.", AgentRole.Planner);
            var goal = kernel.CreateGoal("Maintain the label.", [planner]);

            var brief = kernel.BuildTaskBrief(goal.Id, planner.Id,
                workingDirectory: context.FullName, contextDirectory: context.FullName).Content;

            Assert.Contains("Use the complete Durable Research Notes supplied in the context package", brief, StringComparison.Ordinal);
            Assert.DoesNotContain("source-survey.md in the context directory", brief, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/api/source-survey", brief, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            context.Delete(recursive: true);
        }
    }
}
