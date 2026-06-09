using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ProviderDefaultTests
{
    [Xunit.Fact(DisplayName = "Provider_defaults_keep_openai_fallback_aligned_with_base_agent_model")]
    public void ProviderDefaultsKeepOpenAiFallbackAlignedWithBaseAgentModel()
    {
        var developer = AgentCatalog.Default().GetRequired(AgentRole.Developer);

        Assert.Equal(developer.Model.ModelName, ProviderModelDefaults.OpenAi);
        Assert.False(developer.ComplexModel!.ModelName.Equals(ProviderModelDefaults.OpenAi, StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Provider_smoke_uses_cost_aware_openai_default_when_model_env_is_unset")]
    public void ProviderSmokeUsesCostAwareOpenAiDefaultWhenModelEnvIsUnset()
    {
        var previousKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var previousModel = Environment.GetEnvironmentVariable("OPENAI_MODEL");

        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
            Environment.SetEnvironmentVariable("OPENAI_MODEL", null);

            var created = ProviderSmokeRunner.TryCreateLiveProvider("openai", out _, out var modelName, out _);

            Assert.True(created);
            Assert.Equal(ProviderModelDefaults.OpenAi, modelName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousKey);
            Environment.SetEnvironmentVariable("OPENAI_MODEL", previousModel);
        }
    }

    [Xunit.Fact(DisplayName = "Provider_smoke_default_targets_one_provider")]
    public void ProviderSmokeDefaultTargetsOneProvider()
    {
        var localTargets = ProviderSmokeRunner.ResolveProviderSmokeTargets(ProviderSmokeRunner.DefaultTarget, () => true);
        var paidFallbackTargets = ProviderSmokeRunner.ResolveProviderSmokeTargets(ProviderSmokeRunner.DefaultTarget, () => false);

        Assert.Equal("default", ProviderSmokeRunner.DefaultTarget);
        Assert.Equal("Ollama", localTargets.Single());
        Assert.Equal("OpenAI", paidFallbackTargets.Single());
    }

    [Xunit.Fact(DisplayName = "Scripted_provider_requests_configuration_instead_of_fake_completion")]
    public async Task ScriptedProviderRequestsConfigurationInsteadOfFakeCompletion()
    {
        var provider = new ScriptedModelProvider("OpenAI");
        var request = new ModelRequest(
            "system",
            [new ModelMessage("user", "expensive prompt text should not be echoed")],
            new ModelOptions(ModelName: "gpt-5.4-mini"));

        var response = await provider.CompleteAsync(request, CancellationToken.None);

        Assert.Equal("offline-scripted", response.StopReason);
        Assert.Contains(response.Text, text => text.Contains("HUMAN_INPUT:", StringComparison.Ordinal));
        Assert.Contains(response.Text, text => text.Contains("offline adapter", StringComparison.Ordinal));
        Assert.False(response.Text.Contains("expensive prompt text", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Scripted_provider_pauses_task_instead_of_completing_it")]
    public async Task ScriptedProviderPausesTaskInsteadOfCompletingIt()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Avoid false offline completion");
        var agent = new AgentDefinition(
            AgentId.New(),
            "OpenAI developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium", 1024));
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        var runner = new AgentTaskRunner(
            kernel,
            [agent],
            new InMemoryModelProviderRegistry([new ScriptedModelProvider("OpenAI")]));

        await runner.RunAsync(goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
        Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
        Assert.True(task.LastExecution is not null);
        Assert.Contains(task.LastExecution!.Output, text => text.Contains("HUMAN_INPUT:", StringComparison.Ordinal));
        var request = kernel.GetPendingHumanInput(goal.Id).Single();
        Assert.Equal(task.Id, request.TaskId);
        Assert.Contains(request.Question, text => text.Contains("Configure a live provider", StringComparison.Ordinal));
        Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted));
    }
}
