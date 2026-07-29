using Mcg.AgentOrchestrator.Core;

public sealed class ModelExecutionTests
{
    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_calls_assigned_model_provider")]
    public async Task ExecuteAssignedTaskCallsAssignedModelProvider()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Implement model-backed task execution");
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

    var result = await runner.RunAsync(goal.Id, task.Id);

    Assert.Equal(1, provider.CallCount);
    Assert.Contains("Developer", provider.LastRequest!.SystemPrompt, StringComparison.Ordinal);
    Assert.Contains("HUMAN_INPUT:", provider.LastRequest.SystemPrompt, StringComparison.Ordinal);
    Assert.Contains("Report only changed files", provider.LastRequest.SystemPrompt, StringComparison.Ordinal);
    Assert.True(!provider.LastRequest.SystemPrompt.Contains("Avoid generic status summaries", StringComparison.Ordinal));
    Assert.Contains(provider.LastRequest.Messages, message => message.Content.Contains(goal.Objective, StringComparison.Ordinal));
    Assert.Contains(provider.LastRequest.Messages, message => message.Content.Contains(task.Description, StringComparison.Ordinal));
    Assert.Contains(provider.LastRequest.Messages, message => message.Content.Contains(task.VerificationPlan!, StringComparison.Ordinal));
    Assert.Contains(provider.LastRequest.Messages, message => message.Content.Contains("Developer Requirements", StringComparison.Ordinal));
    Assert.Contains(provider.LastRequest.Messages, message => message.Content.Contains("changed files", StringComparison.Ordinal));
    Assert.Contains(provider.LastRequest.Messages, message => message.Content.Contains("Keep the response concise", StringComparison.Ordinal));
    Assert.Equal("medium", provider.LastRequest.Options.ReasoningEffort);
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal("Implemented requested change.", task.LastExecution!.Output);
    Assert.Equal(TaskComplexity.Simple, task.LastExecution.TaskComplexity);
    Assert.Equal(
        provider.LastRequest.SystemPrompt.Length + provider.LastRequest.Messages.Sum(message => message.Content.Length),
        task.LastExecution.PromptCharacterCount);
    Assert.Equal(provider.ProviderName, result.Execution.ProviderName);
    Assert.Equal(TaskComplexity.Simple, result.Execution.TaskComplexity);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskStarted);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskOutputRecorded);
}
    [Xunit.Fact(DisplayName = "PreviewRun_matches_executed_prompt_size")]
    public async Task PreviewRunMatchesExecutedPromptSize()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Preview API prompt size");
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

    var preview = AgentTaskRunner.PreviewRun(goal, task, agents);
    await runner.RunAsync(goal.Id, task.Id);

    Assert.Equal("OpenAI", preview.ProviderName);
    Assert.Equal(task.LastExecution!.ProviderName, preview.ProviderName);
    Assert.Equal(task.LastExecution.ModelName, preview.ModelName);
    Assert.Equal(task.LastExecution.TaskComplexity, preview.TaskComplexity);
    Assert.Equal(task.LastExecution.MaxOutputTokens, preview.MaxOutputTokens);
    Assert.Equal(task.LastExecution.PromptCharacterCount, preview.PromptCharacterCount);
}
    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_does_not_complete_when_output_cap_is_hit")]
    public async Task ExecuteAssignedTaskDoesNotCompleteWhenOutputCapIsHit()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Avoid accepting truncated output");
    var agent = new AgentDefinition(
        AgentId.New(),
        "API developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-small", ModelCapability.Text, SubscriptionMode.ApiKey, MaxOutputTokens: 2),
        ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var runner = new AgentTaskRunner(
        kernel,
        [agent],
        new InMemoryModelProviderRegistry([new FakeModelProvider("OpenAI", "Partial output", usage: new ModelUsage(5, 2), stopReason: "length")]),
        clock);

    await runner.RunAsync(goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.True(task.LastExecution is not null);
    Assert.Equal(2, task.LastExecution!.MaxOutputTokens);
    Assert.Equal(2, task.LastExecution.Usage!.OutputTokens);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskOutputRecorded);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskFailed && evt.Message.Contains("output may be truncated", StringComparison.Ordinal));
    Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted));
}

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_completes_on_normal_stop_at_exact_output_cap")]
    public async Task ExecuteAssignedTaskCompletesOnNormalStopAtExactOutputCap()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Accept complete output that lands on the cap");
    var agent = new AgentDefinition(
        AgentId.New(),
        "API developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-small", ModelCapability.Text, SubscriptionMode.ApiKey, MaxOutputTokens: 2),
        ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var runner = new AgentTaskRunner(
        kernel,
        [agent],
        new InMemoryModelProviderRegistry([new FakeModelProvider("OpenAI", "Complete output", usage: new ModelUsage(5, 2), stopReason: "end_turn")]),
        clock);

    await runner.RunAsync(goal.Id, task.Id);

    Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskFailed));
}
    [Xunit.Fact(DisplayName = "Verification_gate_blocks_output_cap_hits_even_after_manual_pass")]
    public async Task VerificationGateBlocksOutputCapHitsEvenAfterManualPass()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Avoid accepting manually approved truncated output");
    var agent = new AgentDefinition(
        AgentId.New(),
        "API developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-small", ModelCapability.Text, SubscriptionMode.ApiKey, MaxOutputTokens: 2),
        ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var runner = new AgentTaskRunner(
        kernel,
        [agent],
        new InMemoryModelProviderRegistry([new FakeModelProvider("OpenAI", "Partial output", usage: new ModelUsage(5, 2), stopReason: "length")]),
        clock);

    await runner.RunAsync(goal.Id, task.Id);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Manual override.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "manual",
        "C:\\repo",
        0,
        "looks ok",
        string.Empty,
        clock.UtcNow));

    var gate = kernel.BuildVerificationGate(goal.Id);
    var taskGate = gate.Tasks.Single(item => item.TaskId == task.Id);

    Assert.False(gate.IsSatisfied);
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(VerificationGateStatus.FailedVerification, taskGate.GateStatus);
    Assert.Contains("output may be truncated", taskGate.Message, StringComparison.Ordinal);

    var worklistItem = kernel.BuildVerificationWorklist(goal.Id).Items.Single(item => item.TaskId == task.Id);
    Assert.Contains("stronger model", worklistItem.SuggestedAction, StringComparison.Ordinal);
    Assert.Contains("record model fit", worklistItem.SuggestedAction, StringComparison.Ordinal);

    var blocker = kernel.BuildGoalAcceptanceSummary(goal.Id).Blockers.Single(item => item.TaskId == task.Id);
    Assert.Contains("narrower scope", blocker.SuggestedAction, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_trims_noisy_goal_and_task_primary_context_in_prompt")]
    public async Task ExecuteAssignedTaskTrimsNoisyGoalAndTaskPrimaryContextInPrompt()
{
    var clock = new FakeClock();
    var objective = $"goal-start {new string('g', 1700)} goal-middle-omitted {new string('h', 1200)} goal-tail";
    var description = $"task-start {new string('t', 1700)} task-middle-omitted {new string('u', 1200)} task-tail";
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        objective,
        [new TaskSpec(TaskId.New(), description, AgentRole.Developer)]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium", 1024));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
    var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, task.Id);

    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Contains("goal-start", prompt, StringComparison.Ordinal);
    Assert.Contains("goal-tail", prompt, StringComparison.Ordinal);
    Assert.Contains("task-start", prompt, StringComparison.Ordinal);
    Assert.Contains("task-tail", prompt, StringComparison.Ordinal);
    Assert.Contains("[truncated", prompt, StringComparison.Ordinal);
    Assert.True(!prompt.Contains("goal-middle-omitted", StringComparison.Ordinal));
    Assert.True(!prompt.Contains("task-middle-omitted", StringComparison.Ordinal));
    Assert.Equal(objective, goal.Objective);
    Assert.Equal(description, task.Description);
    Assert.Equal("gpt-5.4-mini", provider.LastRequest.Options.ModelName);
}
    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_uses_smaller_primary_context_budget_for_simple_tasks")]
    public async Task ExecuteAssignedTaskUsesSmallerPrimaryContextBudgetForSimpleTasks()
{
    var clock = new FakeClock();
    var objective = $"simple-api-goal-start {new string('g', 900)} simple-api-goal-middle {new string('h', 500)} simple-api-goal-tail";
    var description = $"simple-api-task-start {new string('t', 900)} simple-api-task-middle {new string('u', 500)} simple-api-task-tail";
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        objective,
        [new TaskSpec(TaskId.New(), description, AgentRole.Developer)]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium", 1024));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
    var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, task.Id);

    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Contains("simple-api-goal-start", prompt, StringComparison.Ordinal);
    Assert.Contains("simple-api-goal-tail", prompt, StringComparison.Ordinal);
    Assert.Contains("simple-api-task-start", prompt, StringComparison.Ordinal);
    Assert.Contains("simple-api-task-tail", prompt, StringComparison.Ordinal);
    Assert.True(!prompt.Contains("simple-api-goal-middle", StringComparison.Ordinal));
    Assert.True(!prompt.Contains("simple-api-task-middle", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_keeps_larger_primary_context_budget_for_complex_tasks")]
    public async Task ExecuteAssignedTaskKeepsLargerPrimaryContextBudgetForComplexTasks()
{
    var clock = new FakeClock();
    var objective = $"complex-api-goal-start {new string('g', 900)} complex-api-goal-middle {new string('h', 500)} complex-api-goal-tail";
    var description = $"Design and implement production architecture. complex-api-task-start {new string('t', 900)} complex-api-task-middle {new string('u', 500)} complex-api-task-tail";
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        objective,
        [new TaskSpec(TaskId.New(), description, AgentRole.Developer)]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium", 1024),
        ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high", 1200));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
    var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, task.Id);

    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Contains("complex-api-goal-middle", prompt, StringComparison.Ordinal);
    Assert.Contains("complex-api-task-middle", prompt, StringComparison.Ordinal);
    Assert.Contains("software-development orchestrator", provider.LastRequest.SystemPrompt, StringComparison.Ordinal);
    Assert.Contains("Avoid generic status summaries", provider.LastRequest.SystemPrompt, StringComparison.Ordinal);
    Assert.Equal("gpt-5.5", provider.LastRequest.Options.ModelName);
}
    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_avoids_repeating_task_description_in_lifecycle_events")]
    public async Task ExecuteAssignedTaskAvoidsRepeatingTaskDescriptionInLifecycleEvents()
{
    var clock = new FakeClock();
    var description = "unique-task-start " + new string('u', 900) + " unique-task-tail";
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Avoid repeated task text",
        [new TaskSpec(TaskId.New(), description, AgentRole.Developer)]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium", 1024));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
    var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, task.Id);

    var prompt = provider.LastRequest!.Messages.Single().Content;
    var firstDescription = prompt.IndexOf(description, StringComparison.Ordinal);
    Assert.True(firstDescription >= 0);
    Assert.Equal(firstDescription, prompt.LastIndexOf(description, StringComparison.Ordinal));
    Assert.Equal(description, task.Description);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskStarted && evt.Message.Contains("started task", StringComparison.Ordinal));
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted && evt.Message.Contains("completed task", StringComparison.Ordinal));
    Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Message.Contains(description, StringComparison.Ordinal)));
}
    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_rejects_subscription_only_agent_without_calling_provider")]
    public async Task ExecuteAssignedTaskRejectsSubscriptionOnlyAgentWithoutCallingProvider()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Reject API execution for subscription-only agent");
    var agent = new AgentDefinition(
        AgentId.New(),
        "Subscription Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var provider = new FakeModelProvider("OpenAI", "unused");
    var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]));

    var ex = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.RunAsync(goal.Id, task.Id));

    Assert.Contains("subscription execution only", ex.Message, StringComparison.Ordinal);
    Assert.Equal(0, provider.CallCount);
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
}

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_rejects_verified_task_without_calling_provider")]
    public async Task ExecuteAssignedTaskRejectsVerifiedTaskWithoutCallingProvider()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Avoid repeated model spending");
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "ok", string.Empty, clock.UtcNow));
    var provider = new FakeModelProvider("OpenAI", "unused");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]));

    var ex = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.RunAsync(goal.Id, task.Id));

    Assert.Contains("already has passing verification", ex.Message, StringComparison.Ordinal);
    Assert.Equal(0, provider.CallCount);
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.True(task.LastVerification?.Succeeded is true);
}

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_rejects_existing_model_output_without_retry")]
    public async Task ExecuteAssignedTaskRejectsExistingModelOutputWithoutRetry()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Avoid duplicate model output");
        var agents = DefaultAgents();
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
        var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

        await runner.RunAsync(goal.Id, task.Id);
        var ex = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.RunAsync(goal.Id, task.Id));

        Assert.Contains("already has model output", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.True(task.LastExecution is not null);

        kernel.RetryTask(goal.Id, task.Id, "Retry with new instructions.");
        Assert.True(task.LastExecution is null);

        await runner.RunAsync(goal.Id, task.Id);

        Assert.Equal(2, provider.CallCount);
        Assert.True(task.LastExecution is not null);
    }

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_rejects_subscription_dispatch_evidence_without_calling_provider")]
    public async Task ExecuteAssignedTaskRejectsSubscriptionDispatchEvidenceWithoutCallingProvider()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Avoid API fallback after subscription work");
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec",
        "C:\\repo",
        clock.UtcNow,
        WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "codex exec",
        "C:\\repo",
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Visit settings to purchase more credits or try again at 4:58 PM.",
        clock.UtcNow));
    var provider = new FakeModelProvider("OpenAI", "unused");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

    var ex = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.RunAsync(goal.Id, task.Id));

    Assert.Contains("already has dispatch evidence", ex.Message, StringComparison.Ordinal);
    Assert.Equal(0, provider.CallCount);
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.True(task.LastDispatch is not null);
    Assert.True(task.LastVerification is null);
    Assert.True(task.SubscriptionRetryAfter is not null);
}

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_rejects_non_assigned_task_without_calling_provider")]
    public async Task ExecuteAssignedTaskRejectsNonAssignedTaskWithoutCallingProvider()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Avoid direct rerun of active task");
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Already running elsewhere.");
    var provider = new FakeModelProvider("OpenAI", "unused");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]));

    var ex = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.RunAsync(goal.Id, task.Id));

    Assert.Contains("status is Running", ex.Message, StringComparison.Ordinal);
    Assert.Equal(0, provider.CallCount);
}

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_uses_provider_from_assigned_agent_profile")]
    public async Task ExecuteAssignedTaskUsesProviderFromAssignedAgentProfile()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Review provider routing");
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Reviewer);
    var openAi = new FakeModelProvider("OpenAI", "reviewed");
    var anthropic = new FakeModelProvider("Anthropic", "wrong provider");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([openAi, anthropic]));

    await runner.RunAsync(goal.Id, task.Id);

    Assert.Equal(1, openAi.CallCount);
    Assert.Equal(0, anthropic.CallCount);
    Assert.Equal("OpenAI", task.LastExecution!.ProviderName);
    Assert.Contains(openAi.LastRequest!.Messages, message => message.Content.Contains("Reviewer Requirements", StringComparison.Ordinal));
    Assert.Contains(openAi.LastRequest.Messages, message => message.Content.Contains("findings first", StringComparison.Ordinal));
    Assert.Contains(openAi.LastRequest.Messages, message => message.Content.Contains("Challenge generic summaries", StringComparison.Ordinal));
    Assert.Equal("high", openAi.LastRequest.Options.ReasoningEffort);
}
    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_trims_noisy_timeline_messages_in_prompt")]
    public async Task ExecuteAssignedTaskTrimsNoisyTimelineMessagesInPrompt()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Trim noisy API prompt timeline");
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var noisyMessage = $"api-event-start {new string('z', 900)} api-event-tail";
    kernel.RetryTask(goal.Id, task.Id, noisyMessage);
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, task.Id);

    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Contains("api-event-start", prompt, StringComparison.Ordinal);
    Assert.Contains("api-event-tail", prompt, StringComparison.Ordinal);
    Assert.Contains("[truncated", prompt, StringComparison.Ordinal);
    Assert.True(!prompt.Contains(new string('z', 900), StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_uses_smaller_timeline_message_budget_for_simple_tasks")]
    public async Task ExecuteAssignedTaskUsesSmallerTimelineMessageBudgetForSimpleTasks()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Keep routine timeline prompt entries small",
        [new TaskSpec(TaskId.New(), "Update a tooltip label.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium", 1024));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var note = $"simple-event-start {new string('s', 110)} simple-event-middle {new string('m', 50)} simple-event-tail";
    kernel.RetryTask(goal.Id, task.Id, note);
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
    var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, task.Id);

    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Contains("simple-event-start", prompt, StringComparison.Ordinal);
    Assert.Contains("simple-event-tail", prompt, StringComparison.Ordinal);
    Assert.Contains("[truncated", prompt, StringComparison.Ordinal);
    Assert.True(!prompt.Contains("simple-event-middle", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_keeps_larger_timeline_message_budget_for_complex_tasks")]
    public async Task ExecuteAssignedTaskKeepsLargerTimelineMessageBudgetForComplexTasks()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Keep enough timeline detail for complex work",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
                AgentRole.Developer)
        ]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium", 1024),
        ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high", 1200));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var note = $"complex-event-start {new string('c', 110)} complex-event-middle {new string('m', 50)} complex-event-tail";
    kernel.RetryTask(goal.Id, task.Id, note);
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
    var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, task.Id);

    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Contains("complex-event-start", prompt, StringComparison.Ordinal);
    Assert.Contains("complex-event-middle", prompt, StringComparison.Ordinal);
    Assert.Contains("complex-event-tail", prompt, StringComparison.Ordinal);
    Assert.True(!prompt.Contains("[truncated", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_prefers_decision_timeline_events_over_lifecycle_noise")]
    public async Task ExecuteAssignedTaskPrefersDecisionTimelineEventsOverLifecycleNoise()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Focus API prompt timeline");
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "routine completed lifecycle noise");
    kernel.RetryTask(goal.Id, task.Id, "retry-critical-note");
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, task.Id);

    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Contains("retry-critical-note", prompt, StringComparison.Ordinal);
    Assert.True(!prompt.Contains("routine completed lifecycle noise", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_trims_noisy_verification_plan_in_prompt")]
    public async Task ExecuteAssignedTaskTrimsNoisyVerificationPlanInPrompt()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Trim noisy API verification plan");
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var plan = $"api-plan-start {new string('v', 460)} api-plan-middle {new string('w', 260)} api-plan-tail";
    kernel.SetTaskVerificationPlan(goal.Id, task.Id, plan);
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, task.Id);

    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Contains("api-plan-start", prompt, StringComparison.Ordinal);
    Assert.Contains("api-plan-tail", prompt, StringComparison.Ordinal);
    Assert.Contains("[truncated", prompt, StringComparison.Ordinal);
    Assert.True(!prompt.Contains("api-plan-middle", StringComparison.Ordinal));
    Assert.Equal(plan, task.VerificationPlan);
}

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_keeps_larger_verification_plan_budget_for_complex_tasks")]
    public async Task ExecuteAssignedTaskKeepsLargerVerificationPlanBudgetForComplexTasks()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Keep complex API verification detail",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
                AgentRole.Developer)
        ]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium", 768),
        ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high", 1200));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var plan = $"complex-api-plan-start {new string('v', 460)} complex-api-plan-middle {new string('w', 260)} complex-api-plan-tail";
    kernel.SetTaskVerificationPlan(goal.Id, task.Id, plan);
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
    var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, task.Id);

    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Contains("complex-api-plan-start", prompt, StringComparison.Ordinal);
    Assert.Contains("complex-api-plan-middle", prompt, StringComparison.Ordinal);
    Assert.Contains("complex-api-plan-tail", prompt, StringComparison.Ordinal);
    Assert.True(!prompt.Contains("[truncated", StringComparison.Ordinal));
    Assert.Equal("gpt-5.5", provider.LastRequest.Options.ModelName);
}

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_excludes_unrelated_task_timeline_from_prompt")]
    public async Task ExecuteAssignedTaskExcludesUnrelatedTaskTimelineFromPrompt()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Keep API prompt focused");
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var developer = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var tester = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
    kernel.SetTaskVerificationPlan(goal.Id, tester.Id, $"unrelated-tester-noise {new string('q', 400)}");
    kernel.SetTaskVerificationPlan(goal.Id, developer.Id, "developer-specific-plan");
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, developer.Id);

    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Contains("developer-specific-plan", prompt, StringComparison.Ordinal);
    Assert.True(!prompt.Contains("unrelated-tester-noise", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_omits_lifecycle_only_timeline_for_simple_tasks")]
    public async Task ExecuteAssignedTaskOmitsLifecycleOnlyTimelineForSimpleTasks()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Keep routine API prompt lifecycle noise out",
        [new TaskSpec(TaskId.New(), "Update a tooltip label.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium", 768));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
    var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, task.Id);

    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.True(!prompt.Contains("GoalCreated", StringComparison.Ordinal));
    Assert.True(!prompt.Contains("TaskDelegated", StringComparison.Ordinal));
    Assert.True(!prompt.Contains("TaskStarted", StringComparison.Ordinal));
    Assert.True(!prompt.Contains("started task", StringComparison.Ordinal));
    Assert.True(!prompt.Contains("Recent timeline:", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_keeps_lifecycle_fallback_timeline_for_complex_tasks")]
    public async Task ExecuteAssignedTaskKeepsLifecycleFallbackTimelineForComplexTasks()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Keep lifecycle context for complex API work",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
                AgentRole.Developer)
        ]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium", 768),
        ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high", 1200));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
    var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, task.Id);

    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Contains("GoalCreated", prompt, StringComparison.Ordinal);
    Assert.Contains("TaskDelegated", prompt, StringComparison.Ordinal);
    Assert.Contains("TaskStarted", prompt, StringComparison.Ordinal);
    Assert.Equal("gpt-5.5", provider.LastRequest.Options.ModelName);
}

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_uses_smaller_timeline_budget_for_simple_tasks")]
    public async Task ExecuteAssignedTaskUsesSmallerTimelineBudgetForSimpleTasks()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Keep routine API prompt budget small",
        [new TaskSpec(TaskId.New(), "Update a tooltip label.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium", 1024));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    AddRetryNotes(kernel, goal.Id, task.Id, "simple-api-note", 10);
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
    var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, task.Id);

    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.True(!prompt.Contains("simple-api-note-04", StringComparison.Ordinal));
    Assert.Contains("simple-api-note-05", prompt, StringComparison.Ordinal);
    Assert.Contains("simple-api-note-10", prompt, StringComparison.Ordinal);
    Assert.Contains("Recent timeline:", prompt, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_keeps_larger_timeline_budget_for_complex_tasks")]
    public async Task ExecuteAssignedTaskKeepsLargerTimelineBudgetForComplexTasks()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Keep enough API context for complex work",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
                AgentRole.Developer)
        ]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium", 1024),
        ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high", 1200));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    AddRetryNotes(kernel, goal.Id, task.Id, "complex-api-note", 10);
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
    var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, task.Id);

    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Contains("complex-api-note-01", prompt, StringComparison.Ordinal);
    Assert.Contains("complex-api-note-10", prompt, StringComparison.Ordinal);
    Assert.Equal("gpt-5.5", provider.LastRequest.Options.ModelName);
}

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_uses_complex_model_only_for_complex_tasks")]
    public async Task ExecuteAssignedTaskUsesComplexModelOnlyForComplexTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Design and implement a production multi-tenant architecture with end-to-end distributed integration, horizontal scaling, real-time processing, system design, security, observability, and concurrent workflows.",
        [
            new TaskSpec(TaskId.New(), "Update a tooltip label.", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.", AgentRole.Developer)
        ]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Cost-aware Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium", 1024),
        ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high", 1200));
    kernel.ActivateGoal(goal.Id, [agent]);
    var provider = new FakeModelProvider("OpenAI", "done");
    var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]));

    await runner.RunAsync(goal.Id, goal.Tasks[0].Id);

    var simplePrompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Equal("gpt-5.4-mini", provider.LastRequest!.Options.ModelName);
    Assert.Equal("medium", provider.LastRequest.Options.ReasoningEffort);
    Assert.Equal(1024, provider.LastRequest.Options.MaxOutputTokens);
    Assert.Equal("gpt-5.4-mini", goal.Tasks[0].LastExecution!.ModelName);
    Assert.Equal(TaskComplexity.Simple, goal.Tasks[0].LastExecution!.TaskComplexity);
    Assert.Contains("Call out blockers or follow-up work explicitly", simplePrompt, StringComparison.Ordinal);
    Assert.True(!simplePrompt.Contains("dashboard or orchestrator blocks the ideal path", StringComparison.Ordinal));

    await runner.RunAsync(goal.Id, goal.Tasks[1].Id);

    var complexPrompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Equal("gpt-5.5", provider.LastRequest!.Options.ModelName);
    Assert.Equal("high", provider.LastRequest.Options.ReasoningEffort);
    Assert.Equal(1200, provider.LastRequest.Options.MaxOutputTokens);
    Assert.Equal("gpt-5.5", goal.Tasks[1].LastExecution!.ModelName);
    Assert.Equal(TaskComplexity.Complex, goal.Tasks[1].LastExecution!.TaskComplexity);
    Assert.Contains("dashboard or orchestrator blocks the ideal path", complexPrompt, StringComparison.Ordinal);
    Assert.True(!complexPrompt.Contains("Keep the response concise", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_pauses_for_agent_requested_human_input")]
    public async Task ExecuteAssignedTaskPausesForAgentRequestedHumanInput()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Clarify during model execution");
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var output = new string('A', VerificationTextBounds.PreviewHeadChars) +
        "\n" +
        new string('M', 2_000) +
        "\nHUMAN_INPUT: Which branch should I modify?\n" +
        new string('N', 2_000) +
        "\n" +
        new string('Z', VerificationTextBounds.PreviewTailChars);
    var provider = new FakeModelProvider("OpenAI", output);
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

    var result = await runner.RunAsync(goal.Id, task.Id);

    var request = kernel.GetPendingHumanInput(goal.Id).Single();
    Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
    Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
    Assert.Equal(task.Id, request.TaskId);
    Assert.Equal("Which branch should I modify?", request.Question);
    Assert.True(result.Execution.Output.Length <= VerificationTextBounds.MaxRetainedChars);
    Assert.DoesNotContain("HUMAN_INPUT:", result.Execution.Output, StringComparison.Ordinal);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskOutputRecorded);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.HumanInputRequested);
    Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted));
}

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_ignores_explicit_no_human_input_summary")]
    public async Task ExecuteAssignedTaskIgnoresExplicitNoHumanInputSummary()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Complete without operator input");
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.\nHuman input: none");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, task.Id);

    Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
    Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.HumanInputRequested));
}

    [Xunit.Theory(DisplayName = "ExecuteAssignedTask_routes_structured_planning_premise_invalid_to_human_input")]
    [Xunit.InlineData(AgentRole.Planner)]
    [Xunit.InlineData(AgentRole.Researcher)]
    public async Task ExecuteAssignedTaskRoutesStructuredPlanningPremiseInvalidToHumanInput(AgentRole role)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal(
            "Do work based on a disputed premise",
            [new TaskSpec(TaskId.New(), "Inspect the premise", role)]);
        var agent = DefaultAgents().Single(candidate => candidate.Role == role);
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        var output = """
            WORKER_RESULT:
            files: none
            commands: inspected source
            tests: not-run - read-only task
            blockers: premise-invalid - required API does not exist; see src/Api.cs
            model_fit: OpenAI/test - adequate - inspection - sufficient
            skills: none
            confidence: high
            END_WORKER_RESULT
            """;
        var provider = new FakeModelProvider("OpenAI", output);
        var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]), clock);

        await runner.RunAsync(goal.Id, task.Id);

        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.Equal(task.Id, request.TaskId);
        Assert.Contains("required API does not exist", request.Question, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
        Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
        Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
    }

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_prioritizes_human_input_and_displays_accompanying_blocker")]
    public async Task ExecuteAssignedTaskPrioritizesHumanInputAndDisplaysAccompanyingBlocker()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal(
            "Plan work that needs an operator decision",
            [new TaskSpec(TaskId.New(), "Inspect the premise", AgentRole.Planner)]);
        var agent = DefaultAgents().Single(candidate => candidate.Role == AgentRole.Planner);
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        var output = """
            HUMAN_INPUT: Should this goal be superseded?
            WORKER_RESULT:
            files: none
            commands: inspected source
            tests: not-run - read-only task
            blockers: premise-invalid - required API does not exist; see src/Api.cs
            model_fit: OpenAI/test - adequate - inspection - sufficient
            skills: none
            confidence: high
            END_WORKER_RESULT
            """;
        var provider = new FakeModelProvider("OpenAI", output);
        var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]), clock);

        await runner.RunAsync(goal.Id, task.Id);

        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.StartsWith("Should this goal be superseded?", request.Question, StringComparison.Ordinal);
        Assert.Contains("required API does not exist", request.Question, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
        Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
    }

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_does_not_complete_structured_Tester_inconclusive")]
    public async Task ExecuteAssignedTaskDoesNotCompleteStructuredTesterInconclusive()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal(
            "Verify an implementation",
            [new TaskSpec(TaskId.New(), "Run focused verification", AgentRole.Tester)]);
        var agent = DefaultAgents().Single(candidate => candidate.Role == AgentRole.Tester);
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        var output = """
            WORKER_RESULT:
            files: none
            commands: dotnet test --no-build --filter Focused
            tests: inconclusive - command timed out; no TRX
            blockers: none
            model_fit: OpenAI/test - adequate - verification - sufficient
            skills: none
            confidence: high
            END_WORKER_RESULT
            """;
        var provider = new FakeModelProvider("OpenAI", output);
        var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]), clock);

        await runner.RunAsync(goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.NotNull(task.LastVerification);
        Assert.True(task.LastVerification.WorkerResultPresent);
        Assert.Equal(1, task.EmptyOutputRetryCount);
        Assert.Equal(
            DispatchOutcomeKind.VerificationInconclusive,
            DispatchFailureClassifier.Classify(task, task.LastVerification).Kind);
        Assert.Contains(
            goal.Timeline,
            evt => evt.TaskId == task.Id &&
                evt.Kind == ProgressKind.TaskFailed &&
                evt.Message.Contains("command timed out; no TRX", StringComparison.Ordinal));
        Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
    }

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_fails_malformed_evidence_bound_outcome")]
    public async Task ExecuteAssignedTaskFailsMalformedEvidenceBoundOutcome()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal(
            "Verify an implementation",
            [new TaskSpec(TaskId.New(), "Run focused verification", AgentRole.Tester)]);
        var agent = DefaultAgents().Single(candidate => candidate.Role == AgentRole.Tester);
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        var output = """
            WORKER_RESULT:
            files: none
            commands: dotnet test --no-build --filter Focused
            tests: inconclusive
            blockers: none
            model_fit: OpenAI/test - adequate - verification - sufficient
            skills: none
            confidence: high
            END_WORKER_RESULT
            """;
        var provider = new FakeModelProvider("OpenAI", output);
        var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([provider]), clock);

        await runner.RunAsync(goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Contains(
            goal.Timeline,
            evt => evt.TaskId == task.Id &&
                evt.Kind == ProgressKind.TaskFailed &&
                evt.Message.Contains("requires", StringComparison.Ordinal));
        Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
    }

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_marks_task_failed_when_provider_throws")]
    public async Task ExecuteAssignedTaskMarksTaskFailedWhenProviderThrows()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Handle provider failure");
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var provider = new FakeModelProvider("OpenAI", "unused", new InvalidOperationException("provider unavailable"));
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]));

    var ex = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.RunAsync(goal.Id, task.Id));

    Assert.Contains("provider unavailable", ex.Message, StringComparison.Ordinal);
    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskStarted);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskFailed);
}
    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_rejects_unassigned_task_without_calling_provider")]
    public async Task ExecuteAssignedTaskRejectsUnassignedTaskWithoutCallingProvider()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Reject unassigned execution");
    var task = goal.Tasks.First();
    var provider = new FakeModelProvider("OpenAI", "unused");
    var runner = new AgentTaskRunner(kernel, DefaultAgents(), new InMemoryModelProviderRegistry([provider]));

    await Xunit.Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.RunAsync(goal.Id, task.Id));

    Assert.Equal(0, provider.CallCount);
    Assert.Equal(WorkTaskStatus.Pending, task.Status);
}

static void AddRetryNotes(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, string prefix, int count)
{
    for (var index = 1; index <= count; index++)
    {
        kernel.RetryTask(goalId, taskId, $"{prefix}-{index:00}");
    }
}

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_includes_workspace_diff_section_when_diff_provider_returns_text")]
    public async Task ExecuteAssignedTaskIncludesWorkspaceDiffSectionWhenDiffProviderReturnsText()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Add workspace diff to API prompts");
        var agents = DefaultAgents();
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        var provider = new FakeModelProvider("OpenAI", "Reviewed branch changes.");
        var diffText = "diff --git a/Foo.cs b/Foo.cs\n+++ b/Foo.cs\n+public void Bar() {}";
        var runner = new AgentTaskRunner(
            kernel,
            agents,
            new InMemoryModelProviderRegistry([provider]),
            clock,
            goalDiffProvider: _ => diffText);

        await runner.RunAsync(goal.Id, task.Id);

        var prompt = provider.LastRequest!.Messages.Single().Content;
        Assert.Contains("## Workspace Diff", prompt, StringComparison.Ordinal);
        Assert.Contains("Foo.cs", prompt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_omits_workspace_diff_section_when_diff_provider_returns_null")]
    public async Task ExecuteAssignedTaskOmitsWorkspaceDiffSectionWhenDiffProviderReturnsNull()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Omit diff section for goals without worktree");
        var agents = DefaultAgents();
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        var provider = new FakeModelProvider("OpenAI", "Completed.");
        var runner = new AgentTaskRunner(
            kernel,
            agents,
            new InMemoryModelProviderRegistry([provider]),
            clock,
            goalDiffProvider: _ => null);

        await runner.RunAsync(goal.Id, task.Id);

        var prompt = provider.LastRequest!.Messages.Single().Content;
        Assert.True(!prompt.Contains("## Workspace Diff", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_omits_workspace_diff_section_when_no_diff_provider")]
    public async Task ExecuteAssignedTaskOmitsWorkspaceDiffSectionWhenNoDiffProvider()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Omit diff section when no provider is wired");
        var agents = DefaultAgents();
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        var provider = new FakeModelProvider("OpenAI", "Completed.");
        var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

        await runner.RunAsync(goal.Id, task.Id);

        var prompt = provider.LastRequest!.Messages.Single().Content;
        Assert.True(!prompt.Contains("## Workspace Diff", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ExecuteAssignedTask_truncates_oversized_workspace_diff_with_budget_marker")]
    public async Task ExecuteAssignedTaskTruncatesOversizedWorkspaceDiffWithBudgetMarker()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Bound large branch diffs in API prompts");
        var agents = DefaultAgents();
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        var provider = new FakeModelProvider("OpenAI", "Reviewed changes.");
        var diffText = $"diff-head-marker {new string('d', 2200)} diff-middle-omitted {new string('e', 1000)} diff-tail-marker";
        var runner = new AgentTaskRunner(
            kernel,
            agents,
            new InMemoryModelProviderRegistry([provider]),
            clock,
            goalDiffProvider: _ => diffText);

        await runner.RunAsync(goal.Id, task.Id);

        var prompt = provider.LastRequest!.Messages.Single().Content;
        Assert.Contains("## Workspace Diff", prompt, StringComparison.Ordinal);
        Assert.Contains("diff-head-marker", prompt, StringComparison.Ordinal);
        Assert.Contains("diff-tail-marker", prompt, StringComparison.Ordinal);
        Assert.Contains("[truncated", prompt, StringComparison.Ordinal);
        Assert.True(!prompt.Contains("diff-middle-omitted", StringComparison.Ordinal));
    }
}

