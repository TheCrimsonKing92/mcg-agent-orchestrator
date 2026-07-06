using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("ProviderEnvironment")]
public sealed class ProviderDefaultTests
{
    [Xunit.Fact(DisplayName = "Ollama_default_base_url_avoids_localhost_ipv6_fallback")]
    public void OllamaDefaultBaseUrlAvoidsLocalhostIpv6Fallback()
    {
        Assert.Equal("http://127.0.0.1:11434", OllamaDefaults.BaseUrl);
        Assert.Equal(OllamaDefaults.BaseUrl, OllamaDefaults.ResolveBaseUrl(null));
        Assert.Equal(OllamaDefaults.BaseUrl, OllamaDefaults.ResolveBaseUrl("  "));
        Assert.Equal("http://ollama-host:11434", OllamaDefaults.ResolveBaseUrl(" http://ollama-host:11434 "));
    }

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
        var defaultTargets = ProviderSmokeRunner.ResolveProviderSmokeTargets(ProviderSmokeRunner.DefaultTarget);

        Assert.Equal("default", ProviderSmokeRunner.DefaultTarget);
        Assert.Equal("Ollama", defaultTargets.Single());
    }

    [Xunit.Fact(DisplayName = "Provider_smoke_identifies_paid_targets_that_need_confirmation")]
    public void ProviderSmokeIdentifiesPaidTargetsThatNeedConfirmation()
    {
        Assert.False(ProviderSmokeRunner.RequiresPaidConfirmation("ollama"));
        Assert.False(ProviderSmokeRunner.RequiresPaidConfirmation(ProviderSmokeRunner.DefaultTarget));
        Assert.True(ProviderSmokeRunner.RequiresPaidConfirmation("openai"));
        Assert.True(ProviderSmokeRunner.RequiresPaidConfirmation("anthropic"));
        Assert.True(ProviderSmokeRunner.RequiresPaidConfirmation("all"));
    }

    [Xunit.Fact(DisplayName = "Provider_smoke_evidence_trims_verbose_response_text")]
    public void ProviderSmokeEvidenceTrimsVerboseResponseText()
    {
        var response = "smoke-start " + new string('s', 2000) + " smoke-tail";
        var result = new ProviderSmokeResult("Fake", response, new ModelUsage(1, 2), "stop");

        var preview = ProviderSmokeRunner.FormatProviderSmokeResponseText(response);
        var evidence = ProviderSmokeRunner.FormatProviderSmokeEvidence(result, "fake-model");

        AssertEx.Contains(preview, text => text.Contains("smoke-start", StringComparison.Ordinal));
        AssertEx.Contains(preview, text => text.Contains("smoke-tail", StringComparison.Ordinal));
        AssertEx.Contains(preview, text => text.Contains("[truncated", StringComparison.Ordinal));
        Assert.True(!preview.Contains(new string('s', 2000), StringComparison.Ordinal));
        AssertEx.Contains(evidence, text => text.Contains("Fake: ok model=fake-model", StringComparison.Ordinal));
        AssertEx.Contains(evidence, text => text.Contains("Usage: input=1 output=2", StringComparison.Ordinal));
        AssertEx.Contains(evidence, text => text.Contains("smoke-tail", StringComparison.Ordinal));
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

    [Xunit.Fact(DisplayName = "AgentTaskRunner_uses_routine_local_output_fallback_for_simple_tasks")]
    public async Task AgentTaskRunnerUsesRoutineLocalOutputFallbackForSimpleTasks()
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

        Assert.Equal(2048, provider.LastRequest!.Options.MaxOutputTokens);
    }

    [Xunit.Fact(DisplayName = "AgentTaskRunner_uses_larger_local_output_fallback_for_complex_tasks")]
    public async Task AgentTaskRunnerUsesLargerLocalOutputFallbackForComplexTasks()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(
            TaskId.New(),
            "Design and implement production dashboard integration across API, CLI, provider, worker, persistence, state, tests, and docs.",
            AgentRole.Developer,
            "Record explicit verification.");
        var goal = kernel.CreateGoal("Run complex local model with quality headroom", [task]);
        var agent = new AgentDefinition(
            AgentId.New(),
            "Ollama developer",
            AgentRole.Developer,
            new ModelProfile("Ollama", "qwen3:8b", ModelCapability.Text, SubscriptionMode.LocalBridge),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        kernel.ActivateGoal(goal.Id, [agent]);
        var provider = new FakeSmokeProvider(providerName: "Ollama");

        await new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]))
            .RunAsync(goal.Id, task.Id);

        Assert.Equal(8192, provider.LastRequest!.Options.MaxOutputTokens);
    }

    [Xunit.Fact(DisplayName = "AgentTaskRunner_treats_any_local_bridge_provider_as_local")]
    public async Task AgentTaskRunnerTreatsAnyLocalBridgeProviderAsLocal()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Run local bridge model with local prompt tuning");
        var agent = new AgentDefinition(
            AgentId.New(),
            "Local bridge developer",
            AgentRole.Developer,
            new ModelProfile("LocalAI", "local-coder", ModelCapability.Text, SubscriptionMode.LocalBridge),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        var provider = new FakeSmokeProvider(providerName: "LocalAI");

        await new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]))
            .RunAsync(goal.Id, task.Id);

        Assert.Equal(2048, provider.LastRequest!.Options.MaxOutputTokens);
        AssertEx.Contains(provider.LastRequest.SystemPrompt, text => text.Contains("## Implementation", StringComparison.Ordinal));
        AssertEx.Contains(provider.LastRequest.Messages.Single().Content, text => text.Contains("/no_think", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Scripted_provider_throws_configuration_error_instead_of_completing")]
    public async Task ScriptedProviderThrowsConfigurationErrorInsteadOfCompleting()
    {
        var provider = new ScriptedModelProvider("OpenAI");
        var request = new ModelRequest(
            "system",
            [new ModelMessage("user", "expensive prompt text should not be echoed")],
            new ModelOptions(ModelName: "gpt-5.4-mini"));

        var ex = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(
            async () => await provider.CompleteAsync(request, CancellationToken.None));

        AssertEx.Contains(ex.Message, text => text.Contains("offline adapter", StringComparison.Ordinal));
        AssertEx.Contains(ex.Message, text => text.Contains("Configure a live provider", StringComparison.Ordinal));
        Assert.False(ex.Message.Contains("expensive prompt text", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Scripted_provider_fails_task_instead_of_waiting_for_human")]
    public async Task ScriptedProviderFailsTaskInsteadOfWaitingForHuman()
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

        await Xunit.Assert.ThrowsAsync<InvalidOperationException>(
            async () => await runner.RunAsync(goal.Id, task.Id));

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.HumanInputRequested));
        Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted));
    }
}
