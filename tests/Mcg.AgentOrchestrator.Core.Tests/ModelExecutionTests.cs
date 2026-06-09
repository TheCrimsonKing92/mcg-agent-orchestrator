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
    Assert.Equal(provider.ProviderName, result.Execution.ProviderName);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskStarted);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskOutputRecorded);
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

    Assert.Equal("gpt-5.4-mini", provider.LastRequest!.Options.ModelName);
    Assert.Equal("medium", provider.LastRequest.Options.ReasoningEffort);
    Assert.Equal(1024, provider.LastRequest.Options.MaxOutputTokens);
    Assert.Equal("gpt-5.4-mini", goal.Tasks[0].LastExecution!.ModelName);

    await runner.RunAsync(goal.Id, goal.Tasks[1].Id);

    Assert.Equal("gpt-5.5", provider.LastRequest!.Options.ModelName);
    Assert.Equal("high", provider.LastRequest.Options.ReasoningEffort);
    Assert.Equal(1200, provider.LastRequest.Options.MaxOutputTokens);
    Assert.Equal("gpt-5.5", goal.Tasks[1].LastExecution!.ModelName);
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
}

