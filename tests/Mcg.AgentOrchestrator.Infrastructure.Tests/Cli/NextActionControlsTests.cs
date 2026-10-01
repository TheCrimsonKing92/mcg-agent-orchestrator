using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each fixture uses its own in-memory kernel and starts no processes.
public sealed class NextActionControlsTests
{
    [Fact]
    public void PaidSubscriptionHandoffPreservesControlAndCostValues()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Map direct next actions");
        var task = goal.Tasks[2];
        var validation = new AgentConfigurationValidation(
            task.RequiredRole, task.RequiredRole.ToString(), "OpenAI", "test", "medium",
            AgentCatalog.RoutineApiMaxOutputTokens, AgentExecutionPolicy.PreferSubscription,
            null, null, null, false, "none", true, "valid");

        var control = NextActionControls.Build(goal,
            new NextActionItem(NextActionKind.RunAssignedTask, task.Id, null, "Run it"),
            WorkerProfileCatalog.Default(), [validation]);

        Assert.Equal(new NextActionControl(
            "Prepare subscription handoff", "POST",
            $"/api/goals/{goal.Id.Value[..8]}/tasks/3/run?confirmTaskRun=true",
            "paid subscription handoff",
            $"Review subscription-plan first; {CostRecommendationText.LocalModelSwitchAction} before paid subscription handoff when the task is routine."), control);
    }

    [Fact]
    public void PreparedPaidDispatchPreservesControlAndCostValues()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Start costly prepared dispatch",
            [new TaskSpec(TaskId.New(), "Run prepared paid work", AgentRole.Developer)]);
        var agent = new AgentDefinition(AgentId.New(), AgentRole.Developer.ToString(), AgentRole.Developer,
            new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey, ReasoningEffort: "medium"),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription);
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec prompt.md", "C:\\repo", DateTimeOffset.UtcNow,
            "OpenAI", AgentCatalog.StaleOpenAiCodexSubscriptionModelAlias, "medium", TaskComplexity.Complex, 20000));
        var action = kernel.BuildNextActions(goal.Id).Items.Single();

        Assert.Equal(NextActionKind.ExecuteRecordedDispatch, action.Kind);
        var control = NextActionControls.Build(goal, action, WorkerProfileCatalog.Default());

        Assert.Equal(new NextActionControl(
            "Start prepared work", "POST",
            $"/api/goals/{goal.Id.Value[..8]}/tasks/1/start?confirmDispatchStart=true",
            "large paid subscription start",
            "Inspect the generated prompt before paid subscription start; it exceeds the paid prompt threshold."), control);
    }

    [Fact]
    public void InspectPreservesControlAndHasNoCostRecommendation()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Map direct next actions");

        var control = NextActionControls.Build(goal,
            new NextActionItem(NextActionKind.InspectFailedTask, goal.Tasks[2].Id, null, "Inspect"),
            WorkerProfileCatalog.Default());

        Assert.Equal(new NextActionControl("Inspect task", "GET",
            $"/api/task/3?goal={goal.Id.Value[..8]}", null, null), control);
    }
}
