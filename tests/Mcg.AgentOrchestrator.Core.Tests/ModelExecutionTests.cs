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
    Assert.Contains(provider.LastRequest!.SystemPrompt, text => text.Contains("Developer", StringComparison.Ordinal));
    Assert.Contains(provider.LastRequest.SystemPrompt, text => text.Contains("HUMAN_INPUT:", StringComparison.Ordinal));
    Assert.Contains(provider.LastRequest.SystemPrompt, text => text.Contains("Avoid generic status summaries", StringComparison.Ordinal));
    Assert.Contains(provider.LastRequest.Messages, message => message.Content.Contains(goal.Objective, StringComparison.Ordinal));
    Assert.Contains(provider.LastRequest.Messages, message => message.Content.Contains(task.Description, StringComparison.Ordinal));
    Assert.Contains(provider.LastRequest.Messages, message => message.Content.Contains(task.VerificationPlan!, StringComparison.Ordinal));
    Assert.Contains(provider.LastRequest.Messages, message => message.Content.Contains("Developer Requirements", StringComparison.Ordinal));
    Assert.Contains(provider.LastRequest.Messages, message => message.Content.Contains("changed files", StringComparison.Ordinal));
    Assert.Equal("medium", provider.LastRequest.Options.ReasoningEffort);
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal("Implemented requested change.", task.LastExecution!.Output);
    Assert.Equal(TaskComplexity.Simple, task.LastExecution.TaskComplexity);
    Assert.Equal(provider.ProviderName, result.Execution.ProviderName);
    Assert.Equal(TaskComplexity.Simple, result.Execution.TaskComplexity);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskStarted);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskOutputRecorded);
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
    Assert.Contains(prompt, text => text.Contains("goal-start", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("goal-tail", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("task-start", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("task-tail", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("[truncated", StringComparison.Ordinal));
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
    Assert.Contains(prompt, text => text.Contains("simple-api-goal-start", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("simple-api-goal-tail", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("simple-api-task-start", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("simple-api-task-tail", StringComparison.Ordinal));
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
    Assert.Contains(prompt, text => text.Contains("complex-api-goal-middle", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("complex-api-task-middle", StringComparison.Ordinal));
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

    Assert.Contains(ex.Message, text => text.Contains("subscription execution only", StringComparison.Ordinal));
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

    Assert.Contains(ex.Message, text => text.Contains("already has passing verification", StringComparison.Ordinal));
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

        Assert.Contains(ex.Message, text => text.Contains("already has model output", StringComparison.Ordinal));
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.True(task.LastExecution is not null);

        kernel.RetryTask(goal.Id, task.Id, "Retry with new instructions.");
        Assert.True(task.LastExecution is null);

        await runner.RunAsync(goal.Id, task.Id);

        Assert.Equal(2, provider.CallCount);
        Assert.True(task.LastExecution is not null);
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

    Assert.Contains(ex.Message, text => text.Contains("status is Running", StringComparison.Ordinal));
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
    Assert.Contains(prompt, text => text.Contains("api-event-start", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("api-event-tail", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(!prompt.Contains(new string('z', 900), StringComparison.Ordinal));
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
    Assert.Contains(prompt, text => text.Contains("retry-critical-note", StringComparison.Ordinal));
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
    var plan = $"api-plan-start {new string('v', 1600)} api-plan-tail";
    kernel.SetTaskVerificationPlan(goal.Id, task.Id, plan);
    var provider = new FakeModelProvider("OpenAI", "Implemented requested change.");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, task.Id);

    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Contains(prompt, text => text.Contains("api-plan-start", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("api-plan-tail", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(!prompt.Contains(new string('v', 1600), StringComparison.Ordinal));
    Assert.Equal(plan, task.VerificationPlan);
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
    Assert.Contains(prompt, text => text.Contains("developer-specific-plan", StringComparison.Ordinal));
    Assert.True(!prompt.Contains("unrelated-tester-noise", StringComparison.Ordinal));
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
    Assert.Contains(prompt, text => text.Contains("simple-api-note-05", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("simple-api-note-10", StringComparison.Ordinal));
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
    Assert.Contains(prompt, text => text.Contains("complex-api-note-01", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("complex-api-note-10", StringComparison.Ordinal));
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
    Assert.Contains(simplePrompt, text => text.Contains("Call out blockers or follow-up work explicitly", StringComparison.Ordinal));
    Assert.True(!simplePrompt.Contains("dashboard or orchestrator blocks the ideal path", StringComparison.Ordinal));

    await runner.RunAsync(goal.Id, goal.Tasks[1].Id);

    var complexPrompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Equal("gpt-5.5", provider.LastRequest!.Options.ModelName);
    Assert.Equal("high", provider.LastRequest.Options.ReasoningEffort);
    Assert.Equal(1200, provider.LastRequest.Options.MaxOutputTokens);
    Assert.Equal("gpt-5.5", goal.Tasks[1].LastExecution!.ModelName);
    Assert.Equal(TaskComplexity.Complex, goal.Tasks[1].LastExecution!.TaskComplexity);
    Assert.Contains(complexPrompt, text => text.Contains("dashboard or orchestrator blocks the ideal path", StringComparison.Ordinal));
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
    var provider = new FakeModelProvider("OpenAI", "HUMAN_INPUT: Which branch should I modify?");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

    var result = await runner.RunAsync(goal.Id, task.Id);

    var request = kernel.GetPendingHumanInput(goal.Id).Single();
    Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
    Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
    Assert.Equal(task.Id, request.TaskId);
    Assert.Equal("Which branch should I modify?", request.Question);
    Assert.Equal("HUMAN_INPUT: Which branch should I modify?", result.Execution.Output);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskOutputRecorded);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.HumanInputRequested);
    Assert.False(goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted));
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

    Assert.Contains(ex.Message, text => text.Contains("provider unavailable", StringComparison.Ordinal));
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
}

