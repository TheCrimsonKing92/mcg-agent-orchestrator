using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.Core;

public sealed class ScriptedProviderTests
{
    [Xunit.Fact(DisplayName = "ScriptedModelProvider_fails_task_without_creating_human_input_request")]
    public async Task ScriptedModelProviderFailsTaskWithoutCreatingHumanInputRequest()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Run task against offline scripted provider");
        var agent = new AgentDefinition(
            AgentId.New(),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("Scripted", "offline", ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        var provider = new ScriptedModelProvider("Scripted");
        var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]));

        var ex = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(
            async () => await runner.RunAsync(goal.Id, task.Id));

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        AssertEx.Contains(ex.Message, text => text.Contains("offline adapter", StringComparison.Ordinal));
        Assert.False(goal.Timeline.Any(evt => evt.Kind == ProgressKind.HumanInputRequested));
    }
}
