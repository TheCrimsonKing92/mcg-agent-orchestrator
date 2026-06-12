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
    kernel.RetryTask(goal.Id, task.Id, "Developer retry note.");

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id);

    Assert.Equal(goal.Id, brief.GoalId);
    Assert.Equal(task.Id, brief.TaskId);
    Assert.Equal(AgentRole.Developer, brief.Role);
    Assert.Contains(brief.Content, text => text.Contains("Build worker adapter", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains($"Goal id: {goal.Id.Value}", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("do not attempt to reach dashboard APIs or orchestrator state", StringComparison.Ordinal));
    Assert.True(!brief.Content.Contains("Goal work summary:", StringComparison.Ordinal));
    Assert.True(!brief.Content.Contains("/api/goals/", StringComparison.Ordinal));
    Assert.True(!brief.Content.Contains("/api/system/dashboard-host", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains(task.Description, StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("Developer retry note.", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("HUMAN_INPUT:", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("Report only changed files", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("Keep the response concise", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("Model fit: <provider>/<model or launcher> - adequate|overkill|underpowered - <task shape> - <short reason>", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("**/bin/**", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("**/obj/**", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("/api/source-survey?max=8", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("## Verification Plan", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains(task.VerificationPlan!, StringComparison.Ordinal));
    Assert.True(!brief.Content.Contains("Context files:", StringComparison.Ordinal));
    Assert.True(!brief.Content.Contains("Complete this task as the assigned SDLC role", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_prefers_bounded_source_survey_for_complex_tasks")]
    public void BuildTaskBriefPrefersBoundedSourceSurveyForComplexTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Reduce paid model costs",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production architecture for cost-aware model routing across CLI, dashboard, API, workers, and tests.",
                AgentRole.Researcher)
        ]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("/api/source-survey?max=8", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("before broad recursive file reads", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "AgentTaskRunner_prefers_bounded_source_survey_for_research_prompts")]
    public async Task AgentTaskRunnerPrefersBoundedSourceSurveyForResearchPrompts()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Reduce paid model costs",
        [
            new TaskSpec(
                TaskId.New(),
                "Inspect dashboard, API, CLI, worker, and test model-routing behavior; report current cost controls.",
                AgentRole.Researcher)
        ]);
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    var provider = new FakeModelProvider("OpenAI", "research complete");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]));

    await runner.RunAsync(goal.Id, task.Id);

    Assert.True(provider.LastRequest is not null);
    Assert.Contains(provider.LastRequest!.Messages.Single().Content, text => text.Contains("/api/source-survey?max=8", StringComparison.Ordinal));
    Assert.Contains(provider.LastRequest!.Messages.Single().Content, text => text.Contains("Model fit: OpenAI/gpt-5.5 - adequate|overkill|underpowered - <task shape> - <short reason>", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_trims_noisy_goal_and_task_primary_context")]
    public void BuildTaskBriefTrimsNoisyGoalAndTaskPrimaryContext()
{
    var objective = $"goal-start {new string('g', 1700)} goal-middle-omitted {new string('h', 1200)} goal-tail";
    var description = $"task-start {new string('t', 1700)} task-middle-omitted {new string('u', 1200)} task-tail";
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        objective,
        [new TaskSpec(TaskId.New(), description, AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id);

    Assert.Contains(brief.Content, text => text.Contains("goal-start", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("goal-tail", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("task-start", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("task-tail", StringComparison.Ordinal));
    Assert.Contains(brief.Content, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(!brief.Content.Contains("goal-middle-omitted", StringComparison.Ordinal));
    Assert.True(!brief.Content.Contains("task-middle-omitted", StringComparison.Ordinal));
    Assert.True(brief.Title.Length < description.Length);
    Assert.True(!brief.Title.Contains("task-tail", StringComparison.Ordinal));
    Assert.Equal(objective, goal.Objective);
    Assert.Equal(description, task.Description);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_uses_smaller_primary_context_budget_for_simple_tasks")]
    public void BuildTaskBriefUsesSmallerPrimaryContextBudgetForSimpleTasks()
{
    var objective = $"simple-goal-start {new string('g', 900)} simple-goal-middle {new string('h', 500)} simple-goal-tail";
    var description = $"simple-task-start {new string('t', 900)} simple-task-middle {new string('u', 500)} simple-task-tail";
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        objective,
        [new TaskSpec(TaskId.New(), description, AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("simple-goal-start", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("simple-goal-tail", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("simple-task-start", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("simple-task-tail", StringComparison.Ordinal));
    Assert.True(!brief.Contains("simple-goal-middle", StringComparison.Ordinal));
    Assert.True(!brief.Contains("simple-task-middle", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_keeps_larger_primary_context_budget_for_complex_tasks")]
    public void BuildTaskBriefKeepsLargerPrimaryContextBudgetForComplexTasks()
{
    var objective = $"complex-goal-start {new string('g', 900)} complex-goal-middle {new string('h', 500)} complex-goal-tail";
    var description = $"Design and implement production architecture. complex-task-start {new string('t', 900)} complex-task-middle {new string('u', 500)} complex-task-tail";
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        objective,
        [new TaskSpec(TaskId.New(), description, AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("complex-goal-middle", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("complex-task-middle", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_uses_full_role_requirements_for_complex_tasks")]
    public void BuildTaskBriefUsesFullRoleRequirementsForComplexTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Maintain dashboard views",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
                AgentRole.Developer)
        ]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("Developer Requirements", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("dashboard or orchestrator blocks the ideal path", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("Complete this task as the assigned SDLC role", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("Avoid generic status summaries", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("Keep the response evidence-focused", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("omit generic progress and long logs", StringComparison.Ordinal));
    Assert.True(!brief.Contains("Keep the response concise", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "AgentTaskRunner_includes_complex_response_budget_guidance")]
    public async Task AgentTaskRunnerIncludesComplexResponseBudgetGuidance()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Maintain dashboard views",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
                AgentRole.Developer)
        ]);
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    var provider = new FakeModelProvider("OpenAI", "complex work complete");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]));

    await runner.RunAsync(goal.Id, task.Id);

    Assert.True(provider.LastRequest is not null);
    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Contains(prompt, text => text.Contains("Response guidance: Keep the response evidence-focused", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("omit generic progress and long logs", StringComparison.Ordinal));
    Assert.Contains(prompt, text => text.Contains("Model fit: OpenAI/gpt-5.5 - adequate|overkill|underpowered - <task shape> - <short reason>", StringComparison.Ordinal));
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
    [Xunit.Fact(DisplayName = "BuildTaskBrief_skips_current_evidence_events_already_shown_in_sections")]
    public async Task BuildTaskBriefSkipsCurrentEvidenceEventsAlreadyShownInSections()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Avoid duplicate brief evidence");
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var runner = new AgentTaskRunner(
        kernel,
        agents,
        new InMemoryModelProviderRegistry([new FakeModelProvider("OpenAI", "model-output-unique")]),
        clock);

    await runner.RunAsync(goal.Id, task.Id);
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, "stdout-unique", "stderr-unique", clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("## Last Model Output", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("model-output-unique", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("## Last Verification", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("stdout-unique", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("stderr-unique", StringComparison.Ordinal));
    Assert.True(!brief.Contains("TaskOutputRecorded", StringComparison.Ordinal));
    Assert.True(!brief.Contains("TaskVerificationRecorded", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_skips_current_dispatch_event_already_shown_in_section")]
    public void BuildTaskBriefSkipsCurrentDispatchEventAlreadyShownInSection()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Avoid duplicate dispatch evidence");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", "C:\\repo", clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("## Last Dispatch", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("codex-cli", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("codex exec prompt.md", StringComparison.Ordinal));
    Assert.True(!brief.Contains("TaskDispatchRecorded", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_uses_smaller_evidence_budget_for_simple_tasks")]
    public async Task BuildTaskBriefUsesSmallerEvidenceBudgetForSimpleTasks()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Keep simple evidence prompt budget small",
        [new TaskSpec(TaskId.New(), "Update a tooltip label.", AgentRole.Developer)]);
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    var modelOutput = $"simple-model-start {new string('a', 460)} simple-model-middle {new string('b', 260)} simple-model-tail";
    var stdout = $"simple-stdout-start {new string('c', 460)} simple-stdout-middle {new string('d', 260)} simple-stdout-tail";
    var stderr = $"simple-stderr-start {new string('e', 460)} simple-stderr-middle {new string('f', 260)} simple-stderr-tail";
    var runner = new AgentTaskRunner(
        kernel,
        agents,
        new InMemoryModelProviderRegistry([new FakeModelProvider("OpenAI", modelOutput)]),
        clock);

    await runner.RunAsync(goal.Id, task.Id);
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, stdout, stderr, clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("simple-model-start", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("simple-model-tail", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("simple-stdout-start", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("simple-stdout-tail", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("simple-stderr-start", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("simple-stderr-tail", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(!brief.Contains("simple-model-middle", StringComparison.Ordinal));
    Assert.True(!brief.Contains("simple-stdout-middle", StringComparison.Ordinal));
    Assert.True(!brief.Contains("simple-stderr-middle", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_keeps_larger_evidence_budget_for_complex_tasks")]
    public async Task BuildTaskBriefKeepsLargerEvidenceBudgetForComplexTasks()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Keep enough evidence for complex work",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
                AgentRole.Developer)
        ]);
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    var modelOutput = $"complex-model-start {new string('a', 460)} complex-model-middle {new string('b', 260)} complex-model-tail";
    var stdout = $"complex-stdout-start {new string('c', 460)} complex-stdout-middle {new string('d', 260)} complex-stdout-tail";
    var stderr = $"complex-stderr-start {new string('e', 460)} complex-stderr-middle {new string('f', 260)} complex-stderr-tail";
    var runner = new AgentTaskRunner(
        kernel,
        agents,
        new InMemoryModelProviderRegistry([new FakeModelProvider("OpenAI", modelOutput)]),
        clock);

    await runner.RunAsync(goal.Id, task.Id);
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, stdout, stderr, clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("complex-model-middle", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("complex-stdout-middle", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("complex-stderr-middle", StringComparison.Ordinal));
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
    kernel.RetryTask(goal.Id, task.Id, noisyMessage);

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("event-start", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("event-tail", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(!brief.Contains(new string('x', 900), StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_uses_smaller_timeline_message_budget_for_simple_tasks")]
    public void BuildTaskBriefUsesSmallerTimelineMessageBudgetForSimpleTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Keep routine brief timeline entries small",
        [new TaskSpec(TaskId.New(), "Update a tooltip label.", AgentRole.Developer)]);
    var task = goal.Tasks.Single();
    var note = $"simple-brief-event-start {new string('s', 110)} simple-brief-event-middle {new string('m', 50)} simple-brief-event-tail";
    kernel.RetryTask(goal.Id, task.Id, note);

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("simple-brief-event-start", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("simple-brief-event-tail", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(!brief.Contains("simple-brief-event-middle", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_keeps_larger_timeline_message_budget_for_complex_tasks")]
    public void BuildTaskBriefKeepsLargerTimelineMessageBudgetForComplexTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Keep enough brief timeline detail for complex work",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
                AgentRole.Developer)
        ]);
    var task = goal.Tasks.Single();
    var note = $"complex-brief-event-start {new string('c', 110)} complex-brief-event-middle {new string('m', 50)} complex-brief-event-tail";
    kernel.RetryTask(goal.Id, task.Id, note);

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("complex-brief-event-start", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("complex-brief-event-middle", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("complex-brief-event-tail", StringComparison.Ordinal));
    Assert.True(!brief.Contains("[truncated", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_prefers_decision_timeline_events_over_lifecycle_noise")]
    public void BuildTaskBriefPrefersDecisionTimelineEventsOverLifecycleNoise()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Focus brief timeline");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "routine completed lifecycle noise");
    kernel.RetryTask(goal.Id, task.Id, "retry-critical-note");

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("retry-critical-note", StringComparison.Ordinal));
    Assert.True(!brief.Contains("routine completed lifecycle noise", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_omits_lifecycle_only_timeline_for_simple_tasks")]
    public void BuildTaskBriefOmitsLifecycleOnlyTimelineForSimpleTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Keep routine brief lifecycle noise out",
        [new TaskSpec(TaskId.New(), "Update a tooltip label.", AgentRole.Developer)]);
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.True(!brief.Contains("GoalCreated", StringComparison.Ordinal));
    Assert.True(!brief.Contains("TaskDelegated", StringComparison.Ordinal));
    Assert.True(!brief.Contains("Goal created.", StringComparison.Ordinal));
    Assert.True(!brief.Contains("Delegated Developer task", StringComparison.Ordinal));
    Assert.True(!brief.Contains("## Recent Timeline", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_keeps_lifecycle_fallback_timeline_for_complex_tasks")]
    public void BuildTaskBriefKeepsLifecycleFallbackTimelineForComplexTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Keep lifecycle context for complex brief work",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
                AgentRole.Developer)
        ]);
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("GoalCreated", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("TaskDelegated", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_uses_smaller_timeline_budget_for_simple_tasks")]
    public void BuildTaskBriefUsesSmallerTimelineBudgetForSimpleTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Keep routine prompt budget small",
        [new TaskSpec(TaskId.New(), "Update a tooltip label.", AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    AddRetryNotes(kernel, goal.Id, task.Id, "simple-brief-note", 10);

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.True(!brief.Contains("simple-brief-note-02", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("simple-brief-note-03", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("simple-brief-note-10", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("## Recent Timeline", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_keeps_larger_timeline_budget_for_complex_tasks")]
    public void BuildTaskBriefKeepsLargerTimelineBudgetForComplexTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Keep enough context for complex work",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
                AgentRole.Developer)
        ]);
    var task = goal.Tasks.Single();

    AddRetryNotes(kernel, goal.Id, task.Id, "complex-brief-note", 10);

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("complex-brief-note-01", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("complex-brief-note-10", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_trims_noisy_verification_plan")]
    public void BuildTaskBriefTrimsNoisyVerificationPlan()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Trim noisy verification plan");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var plan = $"plan-start {new string('p', 460)} plan-middle {new string('q', 260)} plan-tail";
    kernel.SetTaskVerificationPlan(goal.Id, task.Id, plan);

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("plan-start", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("plan-tail", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(!brief.Contains("plan-middle", StringComparison.Ordinal));
    Assert.Equal(plan, task.VerificationPlan);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_keeps_larger_verification_plan_budget_for_complex_tasks")]
    public void BuildTaskBriefKeepsLargerVerificationPlanBudgetForComplexTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Keep complex verification detail",
        [
            new TaskSpec(
                TaskId.New(),
                "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.",
                AgentRole.Developer)
        ]);
    var task = goal.Tasks.Single();
    var plan = $"complex-plan-start {new string('p', 460)} complex-plan-middle {new string('q', 260)} complex-plan-tail";
    kernel.SetTaskVerificationPlan(goal.Id, task.Id, plan);

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("complex-plan-start", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("complex-plan-middle", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("complex-plan-tail", StringComparison.Ordinal));
    Assert.True(!brief.Contains("[truncated", StringComparison.Ordinal));
    Assert.Equal(plan, task.VerificationPlan);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_trims_noisy_dispatch_command")]
    public void BuildTaskBriefTrimsNoisyDispatchCommand()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Trim noisy dispatch command");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var command = $"cmd-start {new string('d', 1600)} cmd-tail";
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("custom", command, "C:\\repo", clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("cmd-start", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("cmd-tail", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(!brief.Contains(new string('d', 1600), StringComparison.Ordinal));
    Assert.Equal(command, task.LastDispatch!.Command);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_trims_noisy_pending_human_input")]
    public void BuildTaskBriefTrimsNoisyPendingHumanInput()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Trim noisy pending input");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var question = $"question-start {new string('h', 1600)} question-tail";
    var request = kernel.RequestHumanInput(goal.Id, task.Id, question);

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains(brief, text => text.Contains("question-start", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("question-tail", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(!brief.Contains(new string('h', 1600), StringComparison.Ordinal));
    Assert.Equal(question, kernel.GetHumanInputRequest(request.Id).Question);
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_includes_prior_task_evidence_for_completed_earlier_task")]
    public void BuildTaskBriefIncludesPriorTaskEvidenceForCompletedEarlierTask()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var task1Spec = new TaskSpec(TaskId.New(), "Plan the implementation", AgentRole.Planner);
    var task2Spec = new TaskSpec(TaskId.New(), "Implement the plan", AgentRole.Developer);
    var goal = kernel.CreateGoal("Build a feature", [task1Spec, task2Spec]);
    var task1 = goal.Tasks[0];
    kernel.RecordTaskVerification(goal.Id, task1.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "prior-stdout-evidence", "", clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, task2Spec.Id).Content;

    Assert.Contains(brief, text => text.Contains("## Prior Task Evidence", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("Planner", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("prior-stdout-evidence", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_omits_prior_task_evidence_for_single_task_goal")]
    public void BuildTaskBriefOmitsPriorTaskEvidenceForSingleTaskGoal()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Single task goal", [new TaskSpec(TaskId.New(), "Do the work", AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.True(!brief.Contains("## Prior Task Evidence", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "AgentTaskRunner_includes_prior_task_evidence_in_api_run_prompt")]
    public async Task AgentTaskRunnerIncludesPriorTaskEvidenceInApiRunPrompt()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var task1Spec = new TaskSpec(TaskId.New(), "Plan the implementation", AgentRole.Planner);
    var task2Spec = new TaskSpec(TaskId.New(), "Implement the plan", AgentRole.Developer);
    var goal = kernel.CreateGoal("Build a feature", [task1Spec, task2Spec]);
    var agents = DefaultAgents();
    kernel.ActivateGoal(goal.Id, agents);
    kernel.RecordTaskVerification(goal.Id, task1Spec.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "prior-task-verification-stdout", "", clock.UtcNow));
    var provider = new FakeModelProvider("OpenAI", "task 2 complete");
    var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

    await runner.RunAsync(goal.Id, task2Spec.Id);

    Assert.True(provider.LastRequest is not null);
    var prompt = provider.LastRequest!.Messages.Single().Content;
    Assert.Contains(prompt, text => text.Contains("prior-task-verification-stdout", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_collapses_prior_evidence_to_digest_pointer_for_file_context")]
    public void BuildTaskBriefCollapsesPriorEvidenceToDigestPointerForFileContext()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var priorTask = new TaskSpec(TaskId.New(), "Plan the implementation", AgentRole.Planner);
    var currentTask = new TaskSpec(TaskId.New(), "Implement the plan", AgentRole.Developer);
    var goal = kernel.CreateGoal("Build a feature", [priorTask, currentTask]);
    var noisyEvidence = $"prior-evidence-head {new string('x', 1800)} prior-evidence-tail";
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, noisyEvidence, "", clock.UtcNow));

    var brief = kernel.BuildTaskBrief(
        goal.Id,
        currentTask.Id,
        workingDirectory: "C:\\repo",
        contextDirectory: "C:\\repo\\.orchestrator-context\\goal").Content;

    Assert.Contains(brief, text => text.Contains("digest.md", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("Read digest.md first", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("prior-task-evidence.md", StringComparison.Ordinal));
    Assert.Contains(brief, text => text.Contains("verification exit 0", StringComparison.Ordinal));
    Assert.True(!brief.Contains("prior-evidence-head", StringComparison.Ordinal));
    Assert.True(!brief.Contains("prior-evidence-tail", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_keeps_prior_evidence_inline_for_api_no_file_briefs")]
    public void BuildTaskBriefKeepsPriorEvidenceInlineForApiNoFileBriefs()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var priorTask = new TaskSpec(TaskId.New(), "Plan the implementation", AgentRole.Planner);
    var currentTask = new TaskSpec(TaskId.New(), "Implement the plan", AgentRole.Developer);
    var goal = kernel.CreateGoal("Build a feature", [priorTask, currentTask]);
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "api-inline-prior-evidence", "", clock.UtcNow));

    var brief = kernel.BuildTaskBrief(goal.Id, currentTask.Id).Content;

    Assert.Contains(brief, text => text.Contains("api-inline-prior-evidence", StringComparison.Ordinal));
    Assert.True(!brief.Contains("Read digest.md first", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "SdlcRoleRequirements_researcher_brief_includes_no_modify_repository_line")]
    public void SdlcRoleRequirementsResearcherBriefIncludesNoModifyRepositoryLine()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var researcherGoal = kernel.CreateGoal("Inspect configuration behavior", [new TaskSpec(TaskId.New(), "Survey configuration modules and report patterns.", AgentRole.Researcher)]);
    var researcherTask = researcherGoal.Tasks.Single();

    var researcherBrief = kernel.BuildTaskBrief(researcherGoal.Id, researcherTask.Id).Content;

    Assert.Contains(researcherBrief, text => text.Contains("Do not modify repository files", StringComparison.Ordinal));
    Assert.Contains(researcherBrief, text => text.Contains("implementation belongs to the Developer task", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "SdlcRoleRequirements_developer_brief_does_not_include_no_modify_repository_line")]
    public void SdlcRoleRequirementsDeveloperBriefDoesNotIncludeNoModifyRepositoryLine()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var developerGoal = kernel.CreateGoal("Fix a bug", [new TaskSpec(TaskId.New(), "Fix the null reference in the parser.", AgentRole.Developer)]);
    var developerTask = developerGoal.Tasks.Single();

    var developerBrief = kernel.BuildTaskBrief(developerGoal.Id, developerTask.Id).Content;

    Assert.True(!developerBrief.Contains("Do not modify repository files", StringComparison.Ordinal));
}
static void AddRetryNotes(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, string prefix, int count)
{
    for (var index = 1; index <= count; index++)
    {
        kernel.RetryTask(goalId, taskId, $"{prefix}-{index:00}");
    }
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

