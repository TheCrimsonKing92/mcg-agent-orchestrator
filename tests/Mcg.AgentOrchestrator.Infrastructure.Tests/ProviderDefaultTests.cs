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

    [Xunit.Fact(DisplayName = "Provider_smoke_identifies_paid_targets_that_need_confirmation")]
    public void ProviderSmokeIdentifiesPaidTargetsThatNeedConfirmation()
    {
        Assert.False(ProviderSmokeRunner.RequiresPaidConfirmation("ollama"));
        Assert.False(ProviderSmokeRunner.RequiresPaidConfirmation(ProviderSmokeRunner.DefaultTarget, () => true));
        Assert.True(ProviderSmokeRunner.RequiresPaidConfirmation("openai"));
        Assert.True(ProviderSmokeRunner.RequiresPaidConfirmation("anthropic"));
        Assert.True(ProviderSmokeRunner.RequiresPaidConfirmation("all"));
        Assert.True(ProviderSmokeRunner.RequiresPaidConfirmation(ProviderSmokeRunner.DefaultTarget, () => false));
    }

    [Xunit.Fact(DisplayName = "Provider_smoke_evidence_trims_verbose_response_text")]
    public void ProviderSmokeEvidenceTrimsVerboseResponseText()
    {
        var response = "smoke-start " + new string('s', 2000) + " smoke-tail";
        var result = new ProviderSmokeResult("Fake", response, new ModelUsage(1, 2), "stop");

        var preview = ProviderSmokeRunner.FormatProviderSmokeResponseText(response);
        var evidence = ProviderSmokeRunner.FormatProviderSmokeEvidence(result, "fake-model");

        Assert.Contains(preview, text => text.Contains("smoke-start", StringComparison.Ordinal));
        Assert.Contains(preview, text => text.Contains("smoke-tail", StringComparison.Ordinal));
        Assert.Contains(preview, text => text.Contains("[truncated", StringComparison.Ordinal));
        Assert.True(!preview.Contains(new string('s', 2000), StringComparison.Ordinal));
        Assert.Contains(evidence, text => text.Contains("Fake: ok model=fake-model", StringComparison.Ordinal));
        Assert.Contains(evidence, text => text.Contains("Usage: input=1 output=2", StringComparison.Ordinal));
        Assert.Contains(evidence, text => text.Contains("smoke-tail", StringComparison.Ordinal));
        Assert.True(!evidence.Contains(new string('s', 2000), StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "AgentTaskRunner_uses_conservative_paid_output_fallback")]
    public async Task AgentTaskRunnerUsesConservativePaidOutputFallback()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Run paid model with default budget");
        var agent = new AgentDefinition(
            AgentId.New(),
            "OpenAI developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-custom", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        var provider = new FakeSmokeProvider(providerName: "OpenAI");

        await new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]))
            .RunAsync(goal.Id, task.Id);

        Assert.Equal(AgentCatalog.RoutineApiMaxOutputTokens, provider.LastRequest!.Options.MaxOutputTokens);
    }

    [Xunit.Fact(DisplayName = "AgentTaskRunner_uses_complex_paid_output_fallback_for_complex_tasks")]
    public async Task AgentTaskRunnerUsesComplexPaidOutputFallbackForComplexTasks()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(
            TaskId.New(),
            "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
            AgentRole.Developer,
            "Record explicit verification.");
        var goal = kernel.CreateGoal("Run paid complex model with default budget", [task]);
        var agent = new AgentDefinition(
            AgentId.New(),
            "OpenAI developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-custom", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly,
            ComplexModel: new ModelProfile("OpenAI", "gpt-complex", ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        var provider = new FakeSmokeProvider(providerName: "OpenAI");

        await new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]))
            .RunAsync(goal.Id, task.Id);

        Assert.Equal(AgentCatalog.ComplexApiMaxOutputTokens, provider.LastRequest!.Options.MaxOutputTokens);
    }

    [Xunit.Fact(DisplayName = "AgentTaskRunner_uses_larger_local_output_fallback")]
    public async Task AgentTaskRunnerUsesLargerLocalOutputFallback()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Run local model with quality headroom");
        var agent = new AgentDefinition(
            AgentId.New(),
            "Ollama developer",
            AgentRole.Developer,
            new ModelProfile("Ollama", "qwen3:8b", ModelCapability.Text, SubscriptionMode.LocalBridge),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        var provider = new FakeSmokeProvider(providerName: "Ollama");

        await new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]))
            .RunAsync(goal.Id, task.Id);

        Assert.Equal(8192, provider.LastRequest!.Options.MaxOutputTokens);
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
