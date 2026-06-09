using Mcg.AgentOrchestrator.Core;

public sealed class TaskBriefTests
{
    [Xunit.Fact(DisplayName = "BuildTaskBrief_includes_goal_task_role_and_timeline")]
    public void BuildTaskBriefIncludesGoalTaskRoleAndTimeline()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Build worker adapter");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Developer started.");

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id);

    Assert.Equal(goal.Id, brief.GoalId);
    Assert.Equal(task.Id, brief.TaskId);
    Assert.Equal(AgentRole.Developer, brief.Role);
    Assert.Contains(brief.Content, text => text.Contains("Build worker adapter", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains(task.Description, StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("Developer started.", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("HUMAN_INPUT:", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("**/bin/**", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("**/obj/**", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("/api/source-survey", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("## Verification Plan", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains(task.VerificationPlan!, StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_includes_pending_human_input_and_verification")]
    public void BuildTaskBriefIncludesPendingHumanInputAndVerification()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Brief with context");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
    kernel.RequestHumanInput(goal.Id, task.Id, "Which test command?");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, "", "failed", clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id);

    Assert.Contains(brief.Content, text => text.Contains("Which test command?", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("dotnet test", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("failed", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_trims_noisy_model_and_verification_evidence")]
    public async Task BuildTaskBriefTrimsNoisyModelAndVerificationEvidence()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Trim noisy brief evidence");
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var modelOutput = $"model-start {new string('a', 1600)} model-tail";
    var stdout = $"stdout-start {new string('b', 1600)} stdout-tail";
    var stderr = $"stderr-start {new string('c', 1600)} stderr-tail";
    var runner = new AgentTaskRunner(
        kernel,
        agents,
        new InMemoryModelProviderRegistry([new FakeModelProvider("OpenAI", modelOutput)]),
        clock);

    await runner.RunAsync(goal.Id, task.Id);
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, stdout, stderr, clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("model-start", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("model-tail", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("stdout-start", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("stdout-tail", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("stderr-start", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("stderr-tail", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(!brief.Contains(new string('a', 1600), StringComparison.Ordinal));
    Assert.True(!brief.Contains(new string('b', 1600), StringComparison.Ordinal));
    Assert.True(!brief.Contains(new string('c', 1600), StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_trims_noisy_timeline_messages")]
    public void BuildTaskBriefTrimsNoisyTimelineMessages()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Trim noisy timeline");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var noisyMessage = $"event-start {new string('x', 900)} event-tail";
    kernel.SetTaskVerificationPlan(goal.Id, task.Id, noisyMessage);
    kernel.SetTaskVerificationPlan(goal.Id, task.Id, "Run focused tests.");

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("event-start", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("event-tail", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(!brief.Contains(new string('x', 900), StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_trims_noisy_verification_plan")]
    public void BuildTaskBriefTrimsNoisyVerificationPlan()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Trim noisy verification plan");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var plan = $"plan-start {new string('p', 1600)} plan-tail";
    kernel.SetTaskVerificationPlan(goal.Id, task.Id, plan);

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("plan-start", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("plan-tail", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(!brief.Contains(new string('p', 1600), StringComparison.Ordinal));
    Assert.Equal(plan, task.VerificationPlan);
}

static IReadOnlyList<AgentDefinition> DefaultAgents()
{
    static ModelProfile OpenAi(string reasoningEffort) =>
        new("OpenAI", "gpt-5.5", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey, reasoningEffort);

    return
    [
        new(AgentId.New(), "Planner", AgentRole.Planner, OpenAi("high")),
        new(AgentId.New(), "Researcher", AgentRole.Researcher, OpenAi("high")),
        new(AgentId.New(), "Developer", AgentRole.Developer, OpenAi("medium")),
        new(AgentId.New(), "Tester", AgentRole.Tester, OpenAi("high")),
        new(AgentId.New(), "Reviewer", AgentRole.Reviewer, OpenAi("high"))
    ];
}
}

