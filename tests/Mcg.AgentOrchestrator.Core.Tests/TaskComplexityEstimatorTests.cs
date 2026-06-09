using Mcg.AgentOrchestrator.Core;

public sealed class TaskComplexityEstimatorTests
{
    [Xunit.Fact(DisplayName = "TaskComplexityEstimator_keeps_simple_tasks_simple_under_complex_goal")]
    public void TaskComplexityEstimatorKeepsSimpleTasksSimpleUnderComplexGoal()
    {
        var complexity = TaskComplexityEstimator.Estimate(
            "Update a tooltip label.",
            ComplexGoalObjective,
            AgentRole.Developer);

        Assert.Equal(TaskComplexity.Simple, complexity);
    }

    [Xunit.Fact(DisplayName = "TaskComplexityEstimator_escalates_explicitly_complex_tasks")]
    public void TaskComplexityEstimatorEscalatesExplicitlyComplexTasks()
    {
        var complexity = TaskComplexityEstimator.Estimate(
            "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
            "Maintain dashboard views.",
            AgentRole.Developer);

        Assert.Equal(TaskComplexity.Complex, complexity);
    }

    [Xunit.Fact(DisplayName = "TaskComplexityEstimator_requires_task_text_to_drive_complexity")]
    public void TaskComplexityEstimatorRequiresTaskTextToDriveComplexity()
    {
        var complexity = TaskComplexityEstimator.Estimate(
            "Update the production integration test label.",
            ComplexGoalObjective,
            AgentRole.Tester);

        Assert.Equal(TaskComplexity.Simple, complexity);
    }

    [Xunit.Fact(DisplayName = "TaskComplexityEstimator_uses_complex_model_only_for_complex_task_text")]
    public void TaskComplexityEstimatorUsesComplexModelOnlyForComplexTaskText()
    {
        var agent = new AgentDefinition(
            AgentId.New(),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
            ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"));

        var simpleModel = TaskComplexityEstimator.ResolveModel(agent, TaskComplexity.Auto, "Update a tooltip label.", ComplexGoalObjective);
        var complexModel = TaskComplexityEstimator.ResolveModel(
            agent,
            TaskComplexity.Auto,
            "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
            "Maintain dashboard views.");

        Assert.Equal("gpt-5.4-mini", simpleModel.ModelName);
        Assert.Equal("medium", simpleModel.ReasoningEffort);
        Assert.Equal("gpt-5.5", complexModel.ModelName);
        Assert.Equal("high", complexModel.ReasoningEffort);
    }

    private const string ComplexGoalObjective =
        "Design and implement a production multi-tenant architecture with end-to-end distributed integration, " +
        "horizontal scaling, real-time processing, system design, security, observability, concurrent workflows, " +
        "thread-safe scheduling, and comprehensive operational verification.";
}
