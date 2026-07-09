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
    Assert.Contains("Build worker adapter", brief.Content, StringComparison.Ordinal);
    Assert.Contains($"Goal id: {goal.Id.Value}", brief.Content, StringComparison.Ordinal);
    Assert.Contains("do not attempt to reach dashboard APIs or orchestrator state", brief.Content, StringComparison.Ordinal);
    Assert.True(!brief.Content.Contains("Goal work summary:", StringComparison.Ordinal));
    Assert.True(!brief.Content.Contains("/api/goals/", StringComparison.Ordinal));
    Assert.True(!brief.Content.Contains("/api/system/dashboard-host", StringComparison.Ordinal));
    Assert.Contains(task.Description, brief.Content, StringComparison.Ordinal);
    Assert.Contains("Developer retry note.", brief.Content, StringComparison.Ordinal);
    Assert.Contains("HUMAN_INPUT:", brief.Content, StringComparison.Ordinal);
    Assert.Contains("Report only changed files", brief.Content, StringComparison.Ordinal);
    Assert.Contains("Keep the response concise", brief.Content, StringComparison.Ordinal);
    Assert.Contains("Model fit: <provider>/<model or launcher> - adequate|overkill|underpowered - <task shape> - <short reason>", brief.Content, StringComparison.Ordinal);
    Assert.Contains("**/bin/**", brief.Content, StringComparison.Ordinal);
    Assert.Contains("**/obj/**", brief.Content, StringComparison.Ordinal);
    Assert.Contains("/api/source-survey?max=8", brief.Content, StringComparison.Ordinal);
    Assert.Contains("## Verification Plan", brief.Content, StringComparison.Ordinal);
    Assert.Contains(task.VerificationPlan!, brief.Content, StringComparison.Ordinal);
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

    Assert.Contains("/api/source-survey?max=8", brief, StringComparison.Ordinal);
    Assert.Contains("before broad recursive file reads", brief, StringComparison.Ordinal);
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
    Assert.Contains("/api/source-survey?max=8", provider.LastRequest!.Messages.Single().Content, StringComparison.Ordinal);
    Assert.Contains("Model fit: OpenAI/gpt-5.5 - adequate|overkill|underpowered - <task shape> - <short reason>", provider.LastRequest!.Messages.Single().Content, StringComparison.Ordinal);
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

    Assert.Contains("goal-start", brief.Content, StringComparison.Ordinal);
    Assert.Contains("goal-tail", brief.Content, StringComparison.Ordinal);
    Assert.Contains("task-start", brief.Content, StringComparison.Ordinal);
    Assert.Contains("task-tail", brief.Content, StringComparison.Ordinal);
    Assert.Contains("[truncated", brief.Content, StringComparison.Ordinal);
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

    Assert.Contains("simple-goal-start", brief, StringComparison.Ordinal);
    Assert.Contains("simple-goal-tail", brief, StringComparison.Ordinal);
    Assert.Contains("simple-task-start", brief, StringComparison.Ordinal);
    Assert.Contains("simple-task-tail", brief, StringComparison.Ordinal);
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

    Assert.Contains("complex-goal-middle", brief, StringComparison.Ordinal);
    Assert.Contains("complex-task-middle", brief, StringComparison.Ordinal);
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

    Assert.Contains("Developer Requirements", brief, StringComparison.Ordinal);
    Assert.Contains("dashboard or orchestrator blocks the ideal path", brief, StringComparison.Ordinal);
    Assert.Contains("Complete this task as the assigned SDLC role", brief, StringComparison.Ordinal);
    Assert.Contains("Avoid generic status summaries", brief, StringComparison.Ordinal);
    Assert.Contains("Keep the response evidence-focused", brief, StringComparison.Ordinal);
    Assert.Contains("omit generic progress and long logs", brief, StringComparison.Ordinal);
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
    Assert.Contains("Response guidance: Keep the response evidence-focused", prompt, StringComparison.Ordinal);
    Assert.Contains("omit generic progress and long logs", prompt, StringComparison.Ordinal);
    Assert.Contains("Model fit: OpenAI/gpt-5.5 - adequate|overkill|underpowered - <task shape> - <short reason>", prompt, StringComparison.Ordinal);
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

    Assert.Contains("Which test command?", brief.Content, StringComparison.Ordinal);
    Assert.Contains("dotnet test", brief.Content, StringComparison.Ordinal);
    Assert.Contains("failed", brief.Content, StringComparison.Ordinal);
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

    Assert.Contains("model-start", brief, StringComparison.Ordinal);
    Assert.Contains("model-tail", brief, StringComparison.Ordinal);
    Assert.Contains("stdout-start", brief, StringComparison.Ordinal);
    Assert.Contains("stdout-tail", brief, StringComparison.Ordinal);
    Assert.Contains("stderr-start", brief, StringComparison.Ordinal);
    Assert.Contains("stderr-tail", brief, StringComparison.Ordinal);
    Assert.Contains("[truncated", brief, StringComparison.Ordinal);
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

    Assert.Contains("## Last Model Output", brief, StringComparison.Ordinal);
    Assert.Contains("model-output-unique", brief, StringComparison.Ordinal);
    Assert.Contains("## Last Verification", brief, StringComparison.Ordinal);
    Assert.Contains("stdout-unique", brief, StringComparison.Ordinal);
    Assert.Contains("stderr-unique", brief, StringComparison.Ordinal);
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

    Assert.Contains("## Last Dispatch", brief, StringComparison.Ordinal);
    Assert.Contains("codex-cli", brief, StringComparison.Ordinal);
    Assert.Contains("codex exec prompt.md", brief, StringComparison.Ordinal);
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

    Assert.Contains("simple-model-start", brief, StringComparison.Ordinal);
    Assert.Contains("simple-model-tail", brief, StringComparison.Ordinal);
    Assert.Contains("simple-stdout-start", brief, StringComparison.Ordinal);
    Assert.Contains("simple-stdout-tail", brief, StringComparison.Ordinal);
    Assert.Contains("simple-stderr-start", brief, StringComparison.Ordinal);
    Assert.Contains("simple-stderr-tail", brief, StringComparison.Ordinal);
    Assert.Contains("[truncated", brief, StringComparison.Ordinal);
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

    Assert.Contains("complex-model-middle", brief, StringComparison.Ordinal);
    Assert.Contains("complex-stdout-middle", brief, StringComparison.Ordinal);
    Assert.Contains("complex-stderr-middle", brief, StringComparison.Ordinal);
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

    Assert.Contains("event-start", brief, StringComparison.Ordinal);
    Assert.Contains("event-tail", brief, StringComparison.Ordinal);
    var timelineLine = brief.Split(Environment.NewLine).Single(text =>
        text.Contains("TaskRetried", StringComparison.Ordinal) &&
        text.Contains("event-start", StringComparison.Ordinal));
    Assert.True(timelineLine.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(!timelineLine.Contains(new string('x', 900), StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_uses_smaller_timeline_message_budget_for_simple_tasks")]
    public void BuildTaskBriefUsesSmallerTimelineMessageBudgetForSimpleTasks()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal(
        "Keep routine brief timeline entries small",
        [new TaskSpec(TaskId.New(), "Update a tooltip label.", AgentRole.Planner)]);
    var task = goal.Tasks.Single();
    var note = $"simple-brief-event-start {new string('s', 110)} simple-brief-event-middle {new string('m', 50)} simple-brief-event-tail";
    kernel.RetryTask(goal.Id, task.Id, note);

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("simple-brief-event-start", brief, StringComparison.Ordinal);
    Assert.Contains("simple-brief-event-tail", brief, StringComparison.Ordinal);
    Assert.Contains("[truncated", brief, StringComparison.Ordinal);
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

    Assert.Contains("complex-brief-event-start", brief, StringComparison.Ordinal);
    Assert.Contains("complex-brief-event-middle", brief, StringComparison.Ordinal);
    Assert.Contains("complex-brief-event-tail", brief, StringComparison.Ordinal);
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

    Assert.Contains("retry-critical-note", brief, StringComparison.Ordinal);
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

    Assert.Contains("GoalCreated", brief, StringComparison.Ordinal);
    Assert.Contains("TaskDelegated", brief, StringComparison.Ordinal);
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
    Assert.Contains("simple-brief-note-03", brief, StringComparison.Ordinal);
    Assert.Contains("simple-brief-note-10", brief, StringComparison.Ordinal);
    Assert.Contains("## Recent Timeline", brief, StringComparison.Ordinal);
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

    Assert.Contains("complex-brief-note-01", brief, StringComparison.Ordinal);
    Assert.Contains("complex-brief-note-10", brief, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_includes_latest_developer_retry_feedback_for_tester")]
    public void BuildTaskBriefIncludesLatestDeveloperRetryFeedbackForTester()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Implement retry prompt regeneration.", AgentRole.Developer);
    var tester = new TaskSpec(TaskId.New(), "Test retry prompt regeneration.", AgentRole.Tester);
    var goal = kernel.CreateGoal("Fix retry prompt regeneration", [developer, tester]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());

    kernel.RetryTask(goal.Id, developer.Id, "stale duplicate retry feedback");
    clock.Advance();
    kernel.RetryTask(goal.Id, developer.Id, "stale duplicate retry feedback");
    clock.Advance();
    kernel.ReportTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Failed, "prior developer outcome for tester redispatch");
    clock.Advance();
    kernel.RetryTask(goal.Id, developer.Id, "latest developer retry feedback");
    var latestRetryAt = clock.UtcNow;
    clock.Advance();
    kernel.RecordTaskNote(goal.Id, developer.Id, "operator recovery note for redispatch");

    var testerBrief = kernel.BuildTaskBrief(goal.Id, tester.Id).Content;
    var developerBrief = kernel.BuildTaskBrief(goal.Id, developer.Id).Content;

    Assert.Contains("## Recent retry/recovery feedback", testerBrief, StringComparison.Ordinal);
    Assert.Contains("Most recent retry: Retry 3 of 3", testerBrief, StringComparison.Ordinal);
    Assert.Contains(latestRetryAt.ToString("u"), testerBrief, StringComparison.Ordinal);
    Assert.Contains("Task 1 Developer", testerBrief, StringComparison.Ordinal);
    Assert.Contains("Prior outcome:", testerBrief, StringComparison.Ordinal);
    Assert.Contains("TaskFailed: prior developer outcome for tester redispatch", testerBrief, StringComparison.Ordinal);
    Assert.Contains("latest developer retry feedback", testerBrief, StringComparison.Ordinal);
    Assert.Contains("operator recovery note for redispatch", testerBrief, StringComparison.Ordinal);
    Assert.Equal(1, CountOccurrences(testerBrief, "## Recent retry/recovery feedback"));
    Assert.True(!testerBrief.Contains("stale duplicate retry feedback", StringComparison.Ordinal));

    Assert.True(!developerBrief.Contains("## Recent retry/recovery feedback", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildTaskBrief_includes_latest_developer_retry_feedback_for_reviewer")]
    public void BuildTaskBriefIncludesLatestDeveloperRetryFeedbackForReviewer()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var developer = new TaskSpec(TaskId.New(), "Implement retry prompt regeneration.", AgentRole.Developer);
    var reviewer = new TaskSpec(TaskId.New(), "Review retry prompt regeneration.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Fix retry prompt regeneration", [developer, reviewer]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());

    kernel.RetryTask(goal.Id, developer.Id, "first stale retry feedback");
    clock.Advance();
    kernel.ReportTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Failed, "prior developer outcome for reviewer redispatch");
    clock.Advance();
    kernel.RetryTask(goal.Id, developer.Id, "latest developer retry feedback for review");
    var latestRetryAt = clock.UtcNow;
    clock.Advance();
    kernel.RecordTaskNote(goal.Id, developer.Id, "operator recovery note for reviewer redispatch");

    var reviewerBrief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;
    var developerBrief = kernel.BuildTaskBrief(goal.Id, developer.Id).Content;

    Assert.Contains("## Recent retry/recovery feedback", reviewerBrief, StringComparison.Ordinal);
    Assert.Contains("Most recent retry: Retry 2 of 2", reviewerBrief, StringComparison.Ordinal);
    Assert.Contains(latestRetryAt.ToString("u"), reviewerBrief, StringComparison.Ordinal);
    Assert.Contains("Task 1 Developer", reviewerBrief, StringComparison.Ordinal);
    Assert.Contains("Prior outcome:", reviewerBrief, StringComparison.Ordinal);
    Assert.Contains("TaskFailed: prior developer outcome for reviewer redispatch", reviewerBrief, StringComparison.Ordinal);
    Assert.Contains("latest developer retry feedback for review", reviewerBrief, StringComparison.Ordinal);
    Assert.Contains("operator recovery note for reviewer redispatch", reviewerBrief, StringComparison.Ordinal);
    Assert.Equal(1, CountOccurrences(reviewerBrief, "## Recent retry/recovery feedback"));
    Assert.True(!reviewerBrief.Contains("first stale retry feedback", StringComparison.Ordinal));
    Assert.True(!developerBrief.Contains("## Recent retry/recovery feedback", StringComparison.Ordinal));
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

    Assert.Contains("plan-start", brief, StringComparison.Ordinal);
    Assert.Contains("plan-tail", brief, StringComparison.Ordinal);
    Assert.Contains("[truncated", brief, StringComparison.Ordinal);
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

    Assert.Contains("complex-plan-start", brief, StringComparison.Ordinal);
    Assert.Contains("complex-plan-middle", brief, StringComparison.Ordinal);
    Assert.Contains("complex-plan-tail", brief, StringComparison.Ordinal);
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

    Assert.Contains("cmd-start", brief, StringComparison.Ordinal);
    Assert.Contains("cmd-tail", brief, StringComparison.Ordinal);
    Assert.Contains("[truncated", brief, StringComparison.Ordinal);
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

    Assert.Contains("question-start", brief, StringComparison.Ordinal);
    Assert.Contains("question-tail", brief, StringComparison.Ordinal);
    Assert.Contains("[truncated", brief, StringComparison.Ordinal);
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

    Assert.Contains("## Prior Task Evidence", brief, StringComparison.Ordinal);
    Assert.Contains("Planner", brief, StringComparison.Ordinal);
    Assert.Contains("prior-stdout-evidence", brief, StringComparison.Ordinal);
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
    Assert.Contains("prior-task-verification-stdout", prompt, StringComparison.Ordinal);
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

    Assert.Contains("digest.md", brief, StringComparison.Ordinal);
    Assert.Contains("prior-task-summaries.md", brief, StringComparison.Ordinal);
    Assert.Contains("Read prior-task-summaries.md first", brief, StringComparison.Ordinal);
    Assert.Contains("prior-task-evidence.md", brief, StringComparison.Ordinal);
    Assert.True(brief.IndexOf("prior-task-summaries.md", StringComparison.Ordinal) < brief.IndexOf("prior-task-evidence.md", StringComparison.Ordinal));
    Assert.Contains("verification exit 0", brief, StringComparison.Ordinal);
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

    Assert.Contains("api-inline-prior-evidence", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains("Read digest.md first", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_budgets_late_pipeline_file_context_without_dropping_api_evidence")]
    public void BuildTaskBriefBudgetsLatePipelineFileContextWithoutDroppingApiEvidence()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var priorPlanner = new TaskSpec(TaskId.New(), "Plan the implementation sequence.", AgentRole.Planner);
    var priorResearcher = new TaskSpec(TaskId.New(), "Inspect the prompt construction path.", AgentRole.Researcher);
    var priorTester = new TaskSpec(TaskId.New(), "Verify previous prompt behavior.", AgentRole.Tester);
    var priorReviewer = new TaskSpec(TaskId.New(), "Review prompt evidence retention.", AgentRole.Reviewer);
    var currentTask = new TaskSpec(
        TaskId.New(),
        $"Design and implement production prompt budget controls. task-start {new string('t', 1800)} task-tail",
        AgentRole.Developer,
        $"verification-plan-start {new string('v', 1400)} verification-plan-tail");
    var goal = kernel.CreateGoal(
        $"Reduce subscription prompt bloat for late pipeline work. goal-start {new string('g', 1800)} goal-tail",
        [priorPlanner, priorResearcher, priorTester, priorReviewer, currentTask]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    CompleteWithEvidence(kernel, goal.Id, priorPlanner.Id, "planner evidence");
    CompleteWithEvidence(kernel, goal.Id, priorResearcher.Id, "researcher evidence");
    CompleteWithEvidence(kernel, goal.Id, priorTester.Id, "tester evidence");
    CompleteWithEvidence(kernel, goal.Id, priorReviewer.Id, $"api-inline-evidence-token {new string('p', 1600)} reviewer evidence tail");
    kernel.RecordTaskDispatch(goal.Id, currentTask.Id, new TaskDispatchRecord(
        "codex-cli",
        $"codex exec prompt-start {new string('c', 1800)} prompt-tail",
        "C:\\repo",
        clock.UtcNow));
    kernel.RecordTaskVerification(goal.Id, currentTask.Id, new TaskVerificationRecord(
        "dotnet test",
        "C:\\repo",
        1,
        $"current-verification-stdout-start {new string('s', 1800)} current-verification-stdout-tail",
        $"current-verification-stderr-start {new string('e', 1800)} current-verification-stderr-tail",
        clock.UtcNow));
    for (var index = 1; index <= 24; index++)
    {
        kernel.RecordTaskNote(goal.Id, currentTask.Id, $"timeline-overflow-note-{index:00} {new string('n', 420)}");
    }

    var fileAccessBrief = kernel.BuildTaskBrief(
        goal.Id,
        currentTask.Id,
        workingDirectory: "C:\\repo",
        contextDirectory: "C:\\repo\\.orchestrator-context\\goal").Content;
    var apiBrief = kernel.BuildTaskBrief(goal.Id, currentTask.Id).Content;

    Assert.True(fileAccessBrief.Length <= 9000);
    Assert.Contains("current-task.md in the context directory", fileAccessBrief, StringComparison.Ordinal);
    Assert.Contains("inline verification output collapsed", fileAccessBrief, StringComparison.Ordinal);
    Assert.Contains("prior-task-summaries.md", fileAccessBrief, StringComparison.Ordinal);
    Assert.True(!fileAccessBrief.Contains("api-inline-evidence-token", StringComparison.Ordinal));
    Assert.Contains("api-inline-evidence-token", apiBrief, StringComparison.Ordinal);
    Assert.Contains("reviewer evidence tail", apiBrief, StringComparison.Ordinal);
    Assert.True(apiBrief.Length > fileAccessBrief.Length);
}

    [Xunit.Fact(DisplayName = "SdlcRoleRequirements_researcher_brief_includes_no_modify_repository_line")]
    public void SdlcRoleRequirementsResearcherBriefIncludesNoModifyRepositoryLine()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var researcherGoal = kernel.CreateGoal("Inspect configuration behavior", [new TaskSpec(TaskId.New(), "Survey configuration modules and report patterns.", AgentRole.Researcher)]);
    var researcherTask = researcherGoal.Tasks.Single();

    var researcherBrief = kernel.BuildTaskBrief(researcherGoal.Id, researcherTask.Id).Content;

    Assert.Contains("Do not modify repository files", researcherBrief, StringComparison.Ordinal);
    Assert.Contains("implementation belongs to the Developer task", researcherBrief, StringComparison.Ordinal);
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

    [Xunit.Fact(DisplayName = "BuildTaskBrief_includes_worker_result_block_fields_and_no_self_commit_instruction")]
    public void BuildTaskBriefIncludesWorkerResultBlockFieldsAndNoSelfCommitInstruction()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Ship a feature", [new TaskSpec(TaskId.New(), "Implement the feature and write tests.", AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    Assert.Contains("WORKER_RESULT:", brief, StringComparison.Ordinal);
    Assert.Contains("files:", brief, StringComparison.Ordinal);
    Assert.Contains("commit:", brief, StringComparison.Ordinal);
    Assert.Contains("model_fit:", brief, StringComparison.Ordinal);
    Assert.Contains("END_WORKER_RESULT", brief, StringComparison.Ordinal);
    Assert.Contains("Do not stage or commit changes; the orchestrator commits verified Developer/Tester diffs.", brief, StringComparison.Ordinal);
    Assert.True(!brief.Contains("Git commit all changes in the working directory before reporting results.", StringComparison.Ordinal));
    Assert.True(!brief.Contains("Final: WORKER_RESULT.", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BuildTaskBrief_worker_result_block_matches_shared_template")]
    public void BuildTaskBriefWorkerResultBlockMatchesSharedTemplate()
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Verify shared template", [new TaskSpec(TaskId.New(), "Apply the shared output contract.", AgentRole.Developer)]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    foreach (var templateLine in AgentOutputDirectives.WorkerResultTemplateLines)
    {
        Assert.Contains(templateLine, brief, StringComparison.Ordinal);
    }
}

    [Xunit.Theory(DisplayName = "BuildTaskBrief_role_worker_result_contract_contains_guardrail_fields")]
    [Xunit.InlineData(AgentRole.Researcher)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void BuildTaskBriefRoleWorkerResultContractContainsGuardrailFields(AgentRole role)
{
    var kernel = new AgentOrchestratorKernel(new FakeClock());
    var goal = kernel.CreateGoal("Verify role output contract", [new TaskSpec(TaskId.New(), "Report role-specific evidence.", role)]);
    var task = goal.Tasks.Single();

    var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

    foreach (var field in AgentOutputDirectives.RequiredWorkerResultFieldNamesForRole(role))
    {
        Assert.Contains($"{field}:", brief, StringComparison.Ordinal);
    }
}

static void AddRetryNotes(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, string prefix, int count)
{
    for (var index = 1; index <= count; index++)
    {
        kernel.RetryTask(goalId, taskId, $"{prefix}-{index:00}");
    }
}

static void CompleteWithEvidence(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, string evidence)
{
    kernel.RecordTaskVerification(goalId, taskId, new TaskVerificationRecord(
        "dotnet test",
        "C:\\repo",
        0,
        evidence,
        string.Empty,
        DateTimeOffset.UtcNow));
}

static int CountOccurrences(string value, string needle)
{
    var count = 0;
    var index = 0;
    while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
    {
        count++;
        index += needle.Length;
    }

    return count;
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

