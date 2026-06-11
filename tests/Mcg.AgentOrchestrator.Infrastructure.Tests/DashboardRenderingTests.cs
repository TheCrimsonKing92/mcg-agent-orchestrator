using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Dashboard.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

public sealed class DashboardRenderingTests
{
    [Xunit.Fact(DisplayName = "DashboardRenderer_renders_goal_tasks_and_attention")]
    public void DashboardRendererRendersGoalTasksAndAttention()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Render dashboard");
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "dotnet test", "C:\\repo", DateTimeOffset.UtcNow));

    var goalPrefix = goal.Id.Value[..8];
    var html = DashboardRenderer.Render(kernel, new DashboardRenderOptions(View: DashboardView.Goal, FocusGoalPrefix: goalPrefix));

    Assert.Contains(html, text => text.Contains("Render dashboard", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Running dispatch", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("dotnet test", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Recommended next steps", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("execute-dispatch 3 --confirm-dispatch-start", StringComparison.Ordinal));
    Assert.False(html.Contains("data-next-action=", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Goal completion", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Accepted: False", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Recorded proof", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Dispatch: 1", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Task readiness", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("In progress", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Verification status", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("<th>Gate</th>", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Not ready", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("complete the task before accepting this gate", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Verification to-do", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Complete the task before recording final verification", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("<code>task 3</code>", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_renders_api_model_execution_usage")]
    public async Task DashboardRendererRendersApiModelExecutionUsage()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Render model usage");
    var agent = new AgentDefinition(
        AgentId.New(),
        "API developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-test", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]));

    await runner.RunAsync(goal.Id, task.Id);

    var goalPrefix = goal.Id.Value[..8];
    var html = DashboardRenderer.Render(kernel, new DashboardRenderOptions(EnableOperatorControls: true, View: DashboardView.Goal, FocusGoalPrefix: goalPrefix));
    var taskDto = DashboardResponseMapper.ToTaskDetailDto(goal, task);
    var evidenceDto = DashboardResponseMapper.ToGoalEvidenceSummaryDto(goal, kernel.BuildGoalEvidenceSummary(goal.Id));
    var transcript = GoalTranscriptRenderer.Render(kernel, goal);
    var promptChars = task.LastExecution!.PromptCharacterCount!.Value;

    Assert.Contains(html, text => text.Contains("Model: OpenAI/gpt-test by API developer", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("tokens 1 in / 2 out", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains($"prompt {promptChars} chars", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Tokens: 1 in / 2 out", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Potentially paid: 1 in / 2 out", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains($"Model usage: OpenAI/gpt-test (Simple) [potentially paid]: 1 run, 1 in / 2 out, prompt {promptChars} chars", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("stop reason stop", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("<pre>OK</pre>", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Model fit: OpenAI/gpt-test - adequate|overkill|underpowered - task shape - short reason.", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("Tokens: 1 in / 2 out", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("Potentially paid tokens: 1 in / 2 out", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("Model selection: complexity=Simple", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains($"Prompt size: {promptChars} chars", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("Model usage:", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains($"- OpenAI/gpt-test (Simple) [potentially paid]: 1 run, 1 in / 2 out, prompt {promptChars} chars", StringComparison.Ordinal));
    Assert.Equal(TaskComplexity.Simple, taskDto.LastExecution!.TaskComplexity);
    Assert.Equal(AgentCatalog.RoutineApiMaxOutputTokens, taskDto.LastExecution.MaxOutputTokens);
    Assert.False(taskDto.LastExecution.OutputTokenLimitHit);
    Assert.Equal(promptChars, taskDto.LastExecution.PromptCharacterCount);
    Assert.Equal(1, evidenceDto.InputTokens);
    Assert.Equal(2, evidenceDto.OutputTokens);
    Assert.Equal(1, evidenceDto.PotentiallyPaidInputTokens);
    Assert.Equal(2, evidenceDto.PotentiallyPaidOutputTokens);
    var modelUsage = evidenceDto.ModelUsage.Single();
    Assert.Equal("OpenAI", modelUsage.ProviderName);
    Assert.Equal("gpt-test", modelUsage.ModelName);
    Assert.Equal(1, modelUsage.ExecutionCount);
    Assert.Equal(1, modelUsage.InputTokens);
    Assert.Equal(2, modelUsage.OutputTokens);
    Assert.Equal(TaskComplexity.Simple, modelUsage.TaskComplexity);
    Assert.Equal(promptChars, modelUsage.PromptCharacterCount);
    Assert.True(modelUsage.IsPotentiallyPaidProvider);
}
    [Xunit.Fact(DisplayName = "Dashboard_evidence_surfaces_model_fit_notes")]
    public void DashboardEvidenceSurfacesModelFitNotes()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Surface model fit");
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "manual-verification passed",
        "C:\\repo",
        0,
        "Evidence checked.\nModel fit: OpenAI/gpt-5.3-codex - underpowered - missed required tests.",
        string.Empty,
        DateTimeOffset.UtcNow));

    var evidenceDto = DashboardResponseMapper.ToGoalEvidenceSummaryDto(goal, kernel.BuildGoalEvidenceSummary(goal.Id));
    var transcript = GoalTranscriptRenderer.Render(kernel, goal);
    var html = DashboardRenderer.Render(kernel, new DashboardRenderOptions(View: DashboardView.Goal, FocusGoalPrefix: goal.Id.Value[..8]));
    var taskEvidence = evidenceDto.Tasks.Single(item => item.TaskId == task.Id.Value);
    var modelFit = evidenceDto.ModelFit.Single();

    Assert.Equal("Model fit: OpenAI/gpt-5.3-codex - underpowered - missed required tests.", taskEvidence.ModelFitNote);
    Assert.Equal("OpenAI", modelFit.ProviderName);
    Assert.Equal("gpt-5.3-codex", modelFit.ModelName);
    Assert.Equal(1, modelFit.NoteCount);
    Assert.Equal(0, modelFit.AdequateCount);
    Assert.Equal(0, modelFit.OverkillCount);
    Assert.Equal(1, modelFit.UnderpoweredCount);
    Assert.Equal(0, modelFit.UnknownCount);
    Assert.True(modelFit.TaskShapes!.Contains("missed required tests"));
    Assert.Contains(html, text => text.Contains("Model fit: OpenAI/gpt-5.3-codex: 1 note; underpowered 1; shapes missed required tests", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("- OpenAI/gpt-5.3-codex: 1 note; underpowered 1; shapes missed required tests", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("Model fit: OpenAI/gpt-5.3-codex - underpowered - missed required tests.", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "Dashboard_subscription_cost_preview_surfaces_prior_model_fit")]
    public void DashboardSubscriptionCostPreviewSurfacesPriorModelFit()
{
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Update the old button label.", AgentRole.Developer);
    var nextTask = new TaskSpec(TaskId.New(), "Update the next button label.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Tune model choice from dashboard evidence", [priorTask, nextTask]);
    var agent = new AgentDefinition(
        new AgentId("cost-aware-developer"),
        "Cost-aware Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "manual-verification passed",
        "C:\\repo",
        0,
        "Evidence checked.\nModel fit: OpenAI/gpt-5-mini - overkill - copy-only change.",
        string.Empty,
        DateTimeOffset.UtcNow));

    var html = DashboardRenderer.Render(
        kernel,
        new DashboardRenderOptions(
            EnableOperatorControls: true,
            View: DashboardView.Goal,
            FocusGoalPrefix: goal.Id.Value[..8],
            AgentDefinitions: [agent],
            WorkerProfiles: WorkerProfileCatalog.Default()));

    Assert.Contains(html, text => text.Contains("prior overkill model", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("OpenAI/gpt-5-mini Simple reasoning low", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("prior fit 1: overkill 1", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("shapes copy-only change", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("try local Ollama/qwen3:8b via agent configuration before paid start", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains($"/api/goals/{goal.Id.Value[..8]}/start-subscription-ready?confirmBatchStart=true", StringComparison.Ordinal));
    Assert.False(html.Contains("confirmLargePaidSubscriptionStart=true", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_surfaces_possible_output_token_cap_hits")]
    public async Task DashboardRendererSurfacesPossibleOutputTokenCapHits()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Render cap pressure");
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
        new InMemoryModelProviderRegistry([new FakeSmokeProvider("Done", new ModelUsage(5, 2), "length", "OpenAI")]));

    await runner.RunAsync(goal.Id, task.Id);

    var goalPrefix = goal.Id.Value[..8];
    var html = DashboardRenderer.Render(kernel, new DashboardRenderOptions(View: DashboardView.Goal, FocusGoalPrefix: goalPrefix));
    var evidenceDto = DashboardResponseMapper.ToGoalEvidenceSummaryDto(goal, kernel.BuildGoalEvidenceSummary(goal.Id));
    var taskDto = DashboardResponseMapper.ToTaskDetailDto(goal, task);
    var transcript = GoalTranscriptRenderer.Render(kernel, goal);

    Assert.Contains(html, text => text.Contains("max 2 out", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("possible output cap hit", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("cap hits 1 of 2", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("maxOutput=2", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("Model note: possible output token cap hit.", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("cap hits 1 of 2", StringComparison.Ordinal));
    Assert.Equal(2, taskDto.LastExecution!.MaxOutputTokens);
    Assert.True(taskDto.LastExecution.OutputTokenLimitHit);
    Assert.Contains(evidenceDto.Tasks.Single(item => item.TaskId == task.Id.Value).Message, text => text.Contains("possible output cap hit at 2 tokens", StringComparison.Ordinal));
    var modelUsage = evidenceDto.ModelUsage.Single();
    Assert.Equal(1, modelUsage.OutputTokenLimitHitCount);
    Assert.Equal(2, modelUsage.MaxOutputTokens);
}

    [Xunit.Fact(DisplayName = "DashboardResponseMapper_trims_verbose_execution_output_without_mutating_task_record")]
    public async Task DashboardResponseMapperTrimsVerboseExecutionOutputWithoutMutatingTaskRecord()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Trim execution response");
    var agent = new AgentDefinition(
        AgentId.New(),
        "API developer",
        AgentRole.Developer,
        new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var output = "execution-start " + new string('o', 6000) + " execution-tail";
    var runner = new AgentTaskRunner(kernel, [agent], new InMemoryModelProviderRegistry([new FakeSmokeProvider(output)]));

    await runner.RunAsync(goal.Id, task.Id);

    var dto = DashboardResponseMapper.ToTaskDetailDto(goal, task);
    var goalPrefix = goal.Id.Value[..8];
    var html = DashboardRenderer.Render(kernel, new DashboardRenderOptions(View: DashboardView.Goal, FocusGoalPrefix: goalPrefix));
    var transcript = GoalTranscriptRenderer.Render(kernel, goal);

    Assert.True(dto.LastExecution is not null);
    Assert.True(dto.LastExecution!.OutputTruncated);
    Assert.Equal(output.Length, dto.LastExecution.OutputLength);
    Assert.Contains(dto.LastExecution.Output, text => text.Contains("execution-start", StringComparison.Ordinal));
    Assert.Contains(dto.LastExecution.Output, text => text.Contains("execution-tail", StringComparison.Ordinal));
    Assert.Contains(dto.LastExecution.Output, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(dto.LastExecution.Output.Length < output.Length);
    Assert.Contains(html, text => text.Contains("execution-start", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("execution-tail", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(!html.Contains(new string('o', 6000), StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("execution-start", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("execution-tail", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(!transcript.Contains(new string('o', 6000), StringComparison.Ordinal));
    Assert.Equal(output, task.LastExecution!.Output);
}

    [Xunit.Fact(DisplayName = "DashboardResponseMapper_trims_verbose_verification_output_without_mutating_history")]
    public void DashboardResponseMapperTrimsVerboseVerificationOutputWithoutMutatingHistory()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Trim verification response");
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var stdout = "stdout-start " + new string('s', 6000) + " stdout-tail";
    var stderr = "stderr-start " + new string('e', 6000) + " stderr-tail";
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, stdout, stderr, DateTimeOffset.UtcNow));

    var dto = DashboardResponseMapper.ToVerificationHistoryDto(goal, task);
    var verification = dto.Verifications.Single();

    Assert.True(verification.StandardOutputTruncated);
    Assert.True(verification.StandardErrorTruncated);
    Assert.Equal(stdout.Length, verification.StandardOutputLength);
    Assert.Equal(stderr.Length, verification.StandardErrorLength);
    Assert.Contains(verification.StandardOutput, text => text.Contains("stdout-start", StringComparison.Ordinal));
    Assert.Contains(verification.StandardOutput, text => text.Contains("stdout-tail", StringComparison.Ordinal));
    Assert.Contains(verification.StandardOutput, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.Contains(verification.StandardError, text => text.Contains("stderr-start", StringComparison.Ordinal));
    Assert.Contains(verification.StandardError, text => text.Contains("stderr-tail", StringComparison.Ordinal));
    Assert.Contains(verification.StandardError, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(verification.StandardOutput.Length < stdout.Length);
    Assert.True(verification.StandardError.Length < stderr.Length);
    var transcript = GoalTranscriptRenderer.Render(kernel, goal);
    Assert.Contains(transcript, text => text.Contains("stdout-start", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("stdout-tail", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("stderr-start", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("stderr-tail", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(!transcript.Contains(new string('s', 6000), StringComparison.Ordinal));
    Assert.True(!transcript.Contains(new string('e', 6000), StringComparison.Ordinal));
    Assert.Equal(stdout, task.VerificationHistory.Single().StandardOutput);
    Assert.Equal(stderr, task.VerificationHistory.Single().StandardError);
}

    [Xunit.Fact(DisplayName = "DashboardResponseMapper_trims_verbose_report_fields_without_mutating_records")]
    public void DashboardResponseMapperTrimsVerboseReportFieldsWithoutMutatingRecords()
{
    var objective = "objective-start " + new string('o', 2000) + " objective-tail";
    var description = "description-start " + new string('d', 2000) + " description-tail";
    var question = "question-start " + new string('q', 2000) + " question-tail";
    var message = "message-start " + new string('m', 2000) + " message-tail";
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        objective,
        [new TaskSpec(TaskId.New(), description, AgentRole.Developer)]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    kernel.RequestHumanInput(goal.Id, task.Id, question);
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, message);

    var goalDetail = DashboardResponseMapper.ToGoalDetailDto(kernel, goal);
    var monitor = DashboardResponseMapper.ToMonitorDto(kernel.BuildMonitor(goal.Id));
    var evidence = DashboardResponseMapper.ToGoalEvidenceSummaryDto(goal, kernel.BuildGoalEvidenceSummary(goal.Id));
    var nextActions = DashboardResponseMapper.ToNextActionsDto(goal, kernel.BuildNextActions(goal.Id));
    var humanInput = DashboardResponseMapper.ToHumanInputWorklistDto(goal, kernel.BuildHumanInputWorklist(goal.Id));
    var gate = DashboardResponseMapper.ToVerificationGateDto(goal, kernel.BuildVerificationGate(goal.Id));

    Assert.Contains(goalDetail.Goal.Objective, text => text.Contains("objective-start", StringComparison.Ordinal));
    Assert.Contains(goalDetail.Goal.Objective, text => text.Contains("objective-tail", StringComparison.Ordinal));
    Assert.Contains(goalDetail.Goal.Objective, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.Contains(goalDetail.Tasks.Single().Description, text => text.Contains("description-start", StringComparison.Ordinal));
    Assert.Contains(goalDetail.Tasks.Single().Description, text => text.Contains("description-tail", StringComparison.Ordinal));
    Assert.True(monitor.Attention.Count > 0);
    Assert.Contains(evidence.Tasks.Single().Description, text => text.Contains("description-tail", StringComparison.Ordinal));
    Assert.Contains(nextActions.Objective, text => text.Contains("objective-tail", StringComparison.Ordinal));
    Assert.Contains(humanInput.Items.Single().Question, text => text.Contains("question-tail", StringComparison.Ordinal));
    Assert.Contains(gate.Tasks.Single().Description, text => text.Contains("description-tail", StringComparison.Ordinal));
    Assert.True(!goalDetail.Goal.Objective.Contains(new string('o', 2000), StringComparison.Ordinal));
    Assert.True(!goalDetail.Tasks.Single().Description.Contains(new string('d', 2000), StringComparison.Ordinal));
    Assert.True(!humanInput.Items.Single().Question.Contains(new string('q', 2000), StringComparison.Ordinal));
    Assert.Equal(objective, goal.Objective);
    Assert.Equal(description, task.Description);
    Assert.Equal(question, kernel.HumanInputRequests.Single().Question);
    Assert.Equal(message, goal.Timeline.Single(evt => evt.Message.Contains("message-start", StringComparison.Ordinal)).Message);
}

    [Xunit.Fact(DisplayName = "DashboardResponseMapper_trims_verbose_subscription_plan_detail")]
    public void DashboardResponseMapperTrimsVerboseSubscriptionPlanDetail()
{
    var profileName = "profile-start-" + new string('p', 2000) + "-profile-tail";
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Trim subscription plan detail",
        [new TaskSpec(TaskId.New(), "Prepare missing subscription profile", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("subscription-developer"),
        "Subscription developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-test", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
        Subscription: new SubscriptionLaunchProfile(profileName));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();

    var plan = DashboardResponseMapper.BuildSubscriptionPlan(goal, [agent], WorkerProfileCatalog.Default());
    var item = plan.Items.Single();

    Assert.False(item.CanPrepare);
    Assert.Contains(item.Detail, text => text.Contains("profile-start", StringComparison.Ordinal));
    Assert.Contains(item.Detail, text => text.Contains("profile-tail", StringComparison.Ordinal));
    Assert.Contains(item.Detail, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(!item.Detail.Contains(new string('p', 2000), StringComparison.Ordinal));
    Assert.Equal(profileName, agent.Subscription!.WorkerProfileName);
    Assert.True(task.LastDispatch is null);
}

    [Xunit.Fact(DisplayName = "DashboardResponseMapper_trims_verbose_timeline_messages_without_mutating_events")]
    public void DashboardResponseMapperTrimsVerboseTimelineMessagesWithoutMutatingEvents()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Trim timeline response");
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var message = "timeline-start " + new string('t', 2000) + " timeline-tail";
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, message);

    var detail = DashboardResponseMapper.ToTaskDetailDto(goal, task);
    var timeline = DashboardResponseMapper.ToTaskTimelineDto(goal, task);
    var detailEvent = detail.Timeline.Single(evt => evt.Message.Contains("timeline-start", StringComparison.Ordinal));
    var timelineEvent = timeline.Timeline.Single(evt => evt.Message.Contains("timeline-start", StringComparison.Ordinal));

    Assert.True(detailEvent.MessageTruncated);
    Assert.True(timelineEvent.MessageTruncated);
    Assert.Equal(message.Length, detailEvent.MessageLength);
    Assert.Equal(message.Length, timelineEvent.MessageLength);
    Assert.Contains(detailEvent.Message, text => text.Contains("timeline-tail", StringComparison.Ordinal));
    Assert.Contains(detailEvent.Message, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.Contains(timelineEvent.Message, text => text.Contains("timeline-tail", StringComparison.Ordinal));
    Assert.Contains(timelineEvent.Message, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(detailEvent.Message.Length < message.Length);
    Assert.True(timelineEvent.Message.Length < message.Length);
    Assert.Equal(message, goal.Timeline.Single(evt => evt.Message.Contains("timeline-start", StringComparison.Ordinal)).Message);
}

    [Xunit.Fact(DisplayName = "DashboardResponseMapper_trims_verbose_task_summary_fields_without_mutating_task")]
    public void DashboardResponseMapperTrimsVerboseTaskSummaryFieldsWithoutMutatingTask()
{
    var description = "description-start " + new string('d', 2000) + " description-tail";
    var verificationPlan = "plan-start " + new string('p', 2000) + " plan-tail";
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Trim task summary response",
        [new TaskSpec(TaskId.New(), description, AgentRole.Developer, verificationPlan)]);
    var task = goal.Tasks.Single();

    var summary = DashboardResponseMapper.ToTaskSummaryDto(goal, task);
    var fullPlan = DashboardResponseMapper.ToTaskVerificationPlanDto(goal, task);

    Assert.True(summary.DescriptionTruncated);
    Assert.True(summary.VerificationPlanTruncated);
    Assert.Equal(description.Length, summary.DescriptionLength);
    Assert.Equal(verificationPlan.Length, summary.VerificationPlanLength);
    Assert.Contains(summary.Description, text => text.Contains("description-start", StringComparison.Ordinal));
    Assert.Contains(summary.Description, text => text.Contains("description-tail", StringComparison.Ordinal));
    Assert.Contains(summary.Description, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.Contains(summary.VerificationPlan!, text => text.Contains("plan-start", StringComparison.Ordinal));
    Assert.Contains(summary.VerificationPlan!, text => text.Contains("plan-tail", StringComparison.Ordinal));
    Assert.Contains(summary.VerificationPlan!, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(summary.Description.Length < description.Length);
    Assert.True(summary.VerificationPlan!.Length < verificationPlan.Length);
    Assert.Equal(description, task.Description);
    Assert.Equal(verificationPlan, task.VerificationPlan);
    Assert.Equal(verificationPlan, fullPlan.Plan);
}

    [Xunit.Fact(DisplayName = "DashboardResponseMapper_trims_verbose_process_batch_plan_descriptions_without_mutating_plan")]
    public void DashboardResponseMapperTrimsVerboseProcessBatchPlanDescriptionsWithoutMutatingPlan()
{
    var description = "batch-start " + new string('b', 2000) + " batch-tail";
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Trim batch plan response",
        [new TaskSpec(TaskId.New(), description, AgentRole.Developer)]);
    var plan = kernel.BuildProcessBatchPlan(goal.Id, ProcessBatchActionKind.StartDispatches);

    var dto = DashboardResponseMapper.ToProcessBatchPlanDto(goal, plan);
    var item = dto.Items.Single();

    Assert.True(item.DescriptionTruncated);
    Assert.Equal(description.Length, item.DescriptionLength);
    Assert.Contains(item.Description, text => text.Contains("batch-start", StringComparison.Ordinal));
    Assert.Contains(item.Description, text => text.Contains("batch-tail", StringComparison.Ordinal));
    Assert.Contains(item.Description, text => text.Contains("[truncated", StringComparison.Ordinal));
    Assert.True(item.Description.Length < description.Length);
    Assert.Equal(description, plan.Items.Single().Description);
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_can_emit_auto_refresh_metadata")]
    public void DashboardRendererCanEmitAutoRefreshMetadata()
{
    var kernel = new AgentOrchestratorKernel();
    kernel.CreateGoal("Watch dashboard");

    var staticHtml = DashboardRenderer.Render(kernel);
    var refreshingHtml = DashboardRenderer.Render(kernel, new DashboardRenderOptions(AutoRefreshSeconds: 15));
    var operatorRefreshingHtml = DashboardRenderer.Render(kernel, new DashboardRenderOptions(AutoRefreshSeconds: 15, EnableOperatorControls: true));

    Assert.False(staticHtml.Contains("http-equiv=\"refresh\"", StringComparison.Ordinal));
    Assert.Contains(refreshingHtml, text => text.Contains("<meta http-equiv=\"refresh\" content=\"15\">", StringComparison.Ordinal));
    Assert.Contains(refreshingHtml, text => text.Contains("Auto-refresh every 15 seconds", StringComparison.Ordinal));
    Assert.False(operatorRefreshingHtml.Contains("http-equiv=\"refresh\"", StringComparison.Ordinal));
    Assert.Contains(operatorRefreshingHtml, text => text.Contains("Auto-update every 15 seconds", StringComparison.Ordinal));
    Assert.Contains(operatorRefreshingHtml, text => text.Contains("data-refresh-seconds=\"15\"", StringComparison.Ordinal));
    Assert.Contains(operatorRefreshingHtml, text => text.Contains("href=\"/assets/dashboard.css\"", StringComparison.Ordinal));
    Assert.Contains(operatorRefreshingHtml, text => text.Contains("src=\"/assets/dashboard.js\"", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "DashboardRequestParser_preserves_complex_reasoning_effort")]
    public void DashboardRequestParserPreservesComplexReasoningEffort()
{
    var submission = DashboardRequestParser.ParseAgentSubmission(
        """
        {
          "role": "Developer",
          "providerName": "OpenAI",
          "modelName": "gpt-5.4-mini",
          "reasoningEffort": "medium",
          "executionPolicy": "PreferSubscription",
          "subscriptionProfileName": "codex-cli",
          "subscriptionModelAlias": "gpt-5.3-codex",
          "subscriptionReasoningEffort": "medium",
          "complexProviderName": "OpenAI",
          "complexModelName": "gpt-5.5",
          "complexReasoningEffort": "high"
        }
        """);

    var agent = DashboardRequestParser.CreateAgentDefinition(submission);

    Assert.Equal("gpt-5.5", agent.ComplexModel!.ModelName);
    Assert.Equal("high", agent.ComplexModel.ReasoningEffort);
    Assert.Equal(AgentCatalog.ComplexApiMaxOutputTokens, agent.ComplexModel.MaxOutputTokens);
}
    [Xunit.Fact(DisplayName = "DashboardRequestParser_defaults_paid_agents_to_subscription_preferred")]
    public void DashboardRequestParserDefaultsPaidAgentsToSubscriptionPreferred()
{
    var openAi = DashboardRequestParser.CreateAgentDefinition(new AgentSubmissionDto(
        "Developer",
        "OpenAI",
        "gpt-5.4-mini",
        null));
    var anthropic = DashboardRequestParser.CreateAgentDefinition(new AgentSubmissionDto(
        "Reviewer",
        "Anthropic",
        "claude-sonnet-4-6",
        null));
    var ollama = DashboardRequestParser.CreateAgentDefinition(new AgentSubmissionDto(
        "Tester",
        "Ollama",
        "qwen2.5-coder:7b",
        null));

    Assert.Equal(AgentExecutionPolicy.PreferSubscription, openAi.ExecutionPolicy);
    Assert.Equal("codex-cli", openAi.Subscription!.WorkerProfileName);
    Assert.Equal("gpt-5.3-codex", openAi.Subscription.ModelAlias);
    Assert.Equal(AgentCatalog.RoutineSubscriptionReasoningEffort, openAi.Subscription.ReasoningEffort);
    Assert.Equal(AgentCatalog.RoutineReasoningEffort, openAi.Model.ReasoningEffort);
    Assert.Equal(AgentCatalog.RoutineApiMaxOutputTokens, openAi.Model.MaxOutputTokens);
    Assert.Equal(SubscriptionMode.ApiKey, openAi.Model.SubscriptionMode);
    Assert.Equal("OpenAI", openAi.ComplexModel!.ProviderName);
    Assert.Equal("gpt-5.5", openAi.ComplexModel.ModelName);
    Assert.Equal(AgentCatalog.ComplexReasoningEffort, openAi.ComplexModel.ReasoningEffort);
    Assert.Equal(AgentCatalog.ComplexApiMaxOutputTokens, openAi.ComplexModel.MaxOutputTokens);

    Assert.Equal(AgentExecutionPolicy.PreferSubscription, anthropic.ExecutionPolicy);
    Assert.Equal("claude-cli", anthropic.Subscription!.WorkerProfileName);
    Assert.True(anthropic.Subscription.ModelAlias is null);
    Assert.Equal(AgentCatalog.RoutineApiMaxOutputTokens, anthropic.Model.MaxOutputTokens);
    Assert.Equal(SubscriptionMode.ApiKey, anthropic.Model.SubscriptionMode);
    Assert.True(anthropic.ComplexModel is null);

    Assert.Equal(AgentExecutionPolicy.ApiOnly, ollama.ExecutionPolicy);
    Assert.True(ollama.Subscription is null);
    Assert.True(ollama.ComplexModel is null);
    Assert.True(ollama.Model.MaxOutputTokens is null);
    Assert.Equal(SubscriptionMode.LocalBridge, ollama.Model.SubscriptionMode);
}
    [Xunit.Fact(DisplayName = "DashboardRequestParser_ignores_subscription_fields_for_api_only_agents")]
    public void DashboardRequestParserIgnoresSubscriptionFieldsForApiOnlyAgents()
{
    var agent = DashboardRequestParser.CreateAgentDefinition(new AgentSubmissionDto(
        "Tester",
        "Ollama",
        "qwen2.5-coder:7b",
        null,
        ExecutionPolicy: "ApiOnly",
        SubscriptionProfileName: "codex-cli",
        SubscriptionModelAlias: "gpt-5.3-codex",
        SubscriptionReasoningEffort: "high",
        ComplexProviderName: "Ollama",
        ComplexModelName: "qwen3:8b"));

    Assert.Equal(AgentExecutionPolicy.ApiOnly, agent.ExecutionPolicy);
    Assert.True(agent.Subscription is null);
    Assert.Equal("Ollama", agent.Model.ProviderName);
    Assert.Equal(SubscriptionMode.LocalBridge, agent.Model.SubscriptionMode);
    Assert.Equal("Ollama", agent.ComplexModel!.ProviderName);
    Assert.Equal(SubscriptionMode.LocalBridge, agent.ComplexModel.SubscriptionMode);
}
    [Xunit.Fact(DisplayName = "DashboardRequestParser_respects_explicit_api_only_agent_policy")]
    public void DashboardRequestParserRespectsExplicitApiOnlyAgentPolicy()
{
    var agent = DashboardRequestParser.CreateAgentDefinition(new AgentSubmissionDto(
        "Developer",
        "OpenAI",
        "gpt-5.4-mini",
        null,
        ExecutionPolicy: "ApiOnly"));

    Assert.Equal(AgentExecutionPolicy.ApiOnly, agent.ExecutionPolicy);
    Assert.True(agent.Subscription is null);
}
    [Xunit.Fact(DisplayName = "DashboardRequestParser_defaults_goal_creation_to_manual_handoff")]
    public void DashboardRequestParserDefaultsGoalCreationToManualHandoff()
{
    var omitted = DashboardRequestParser.ParseCreateGoalSubmission(
        "{\"objective\":\"Inspect plan before spending credits\",\"workflow\":\"simple\"}");
    var explicitAutomatic = DashboardRequestParser.ParseCreateGoalSubmission(
        "{\"objective\":\"Start now\",\"workflow\":\"simple\",\"autoHandoff\":true,\"confirmAutoHandoff\":true}");

    Assert.False(omitted.AutoHandoff);
    Assert.True(explicitAutomatic.AutoHandoff);
    Assert.True(explicitAutomatic.ConfirmAutoHandoff);
}
    [Xunit.Fact(DisplayName = "DashboardRequestParser_requires_confirmation_for_auto_handoff")]
    public void DashboardRequestParserRequiresConfirmationForAutoHandoff()
{
    var ex = Assert.Throws<ArgumentException>(() =>
        DashboardRequestParser.ParseCreateGoalSubmission(
            "{\"objective\":\"Start worker processes\",\"workflow\":\"simple\",\"autoHandoff\":true}"));

    Assert.Contains(ex.Message, text => text.Contains("confirmAutoHandoff=true", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "DashboardRequestParser_requires_confirmation_for_paid_provider_smoke")]
    public void DashboardRequestParserRequiresConfirmationForPaidProviderSmoke()
{
    var ex = Assert.Throws<ArgumentException>(() =>
        DashboardRequestParser.ParseProviderSmokeSubmission("{\"target\":\"openai\"}"));

    Xunit.Assert.Contains("confirmPaidSmoke=true", ex.Message);
    Xunit.Assert.Contains("default local Ollama smoke first", ex.Message);
    Assert.Equal("ollama", DashboardRequestParser.ParseProviderSmokeSubmission("ollama"));
    Assert.Equal("openai", DashboardRequestParser.ParseProviderSmokeSubmission("{\"target\":\"openai\",\"confirmPaidSmoke\":true}"));
}
    [Xunit.Fact(DisplayName = "DashboardRequestParser_requires_confirmation_for_broad_provider_smoke")]
    public void DashboardRequestParserRequiresConfirmationForBroadProviderSmoke()
{
    var ex = Assert.Throws<ArgumentException>(() =>
        DashboardRequestParser.ParseProviderSmokeSubmission("{\"target\":\"all\"}"));

    Xunit.Assert.Contains("confirmAll=true", ex.Message);
    Xunit.Assert.Contains("default local Ollama smoke first", ex.Message);
    Assert.Equal("all", DashboardRequestParser.ParseProviderSmokeSubmission("{\"target\":\"all\",\"confirmAll\":true}"));
}
    [Xunit.Fact(DisplayName = "DashboardRequestParser_requires_retry_note")]
    public void DashboardRequestParserRequiresRetryNote()
{
    var empty = Assert.Throws<ArgumentException>(() => DashboardRequestParser.ParseRetrySubmission(""));
    var json = Assert.Throws<ArgumentException>(() => DashboardRequestParser.ParseRetrySubmission("{\"message\":\"\"}"));
    var parsed = DashboardRequestParser.ParseRetrySubmission("{\"message\":\"Fix failed verification\"}");

    Assert.Contains(empty.Message, text => text.Contains("Retry note cannot be empty", StringComparison.Ordinal));
    Assert.Contains(json.Message, text => text.Contains("non-empty 'message'", StringComparison.Ordinal));
    Assert.Equal("Fix failed verification", parsed.Message);
}
    [Xunit.Fact(DisplayName = "DashboardRequestParser_accepts_manual_verification_status_alias")]
    public void DashboardRequestParserAcceptsManualVerificationStatusAlias()
{
    var passed = DashboardRequestParser.ParseManualVerifySubmission("{\"status\":\"passed\",\"note\":\" Looks correct. \"}");
    var failed = DashboardRequestParser.ParseManualVerifySubmission("{\"status\":\"failed\",\"note\":\" Missing evidence. \"}");

    Assert.True(passed.Passed);
    Assert.Equal("Looks correct.", passed.Note);
    Assert.False(failed.Passed);
    Assert.Equal("Missing evidence.", failed.Note);
}
    [Xunit.Fact(DisplayName = "DashboardRequestParser_rejects_ambiguous_manual_verification_outcome")]
    public void DashboardRequestParserRejectsAmbiguousManualVerificationOutcome()
{
    var missing = Assert.Throws<ArgumentException>(() =>
        DashboardRequestParser.ParseManualVerifySubmission("{\"note\":\"No outcome.\"}"));
    var invalid = Assert.Throws<ArgumentException>(() =>
        DashboardRequestParser.ParseManualVerifySubmission("{\"status\":\"completed\",\"note\":\"No outcome.\"}"));
    var conflict = Assert.Throws<ArgumentException>(() =>
        DashboardRequestParser.ParseManualVerifySubmission("{\"passed\":false,\"status\":\"passed\",\"note\":\"Conflict.\"}"));

    Assert.Contains(missing.Message, text => text.Contains("passed' or 'status", StringComparison.Ordinal));
    Assert.Contains(invalid.Message, text => text.Contains("status", StringComparison.Ordinal));
    Assert.Contains(conflict.Message, text => text.Contains("conflict", StringComparison.OrdinalIgnoreCase));
}
    [Xunit.Fact(DisplayName = "DashboardRenderer_can_emit_operator_controls")]
    public void DashboardRendererCanEmitOperatorControls()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Operate from browser",
        [
            new TaskSpec(TaskId.New(), "Ask operator", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Run process", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Assign later", AgentRole.Tester)
        ]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var inputTask = goal.Tasks[0];
    var processTask = goal.Tasks[1];
    var request = kernel.RequestHumanInput(goal.Id, inputTask.Id, "Should we use main?");
    kernel.RecordTaskDispatch(goal.Id, processTask.Id, new TaskDispatchRecord("local", "Write-Output ok", Environment.CurrentDirectory, DateTimeOffset.UtcNow));
    kernel.RecordTaskProcessStarted(
        goal.Id,
        processTask.Id,
        new TaskProcessRecord(
            1234,
            "Write-Output ok",
            Environment.CurrentDirectory,
            "stdout.log",
            "stderr.log",
            "exit.txt",
            DateTimeOffset.UtcNow,
            null,
            null,
            false));

    var health = OrchestratorHealthInspector.Inspect(
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["OPENAI_API_KEY"] = "set" },
        AgentCatalog.Default(),
        WorkerProfileCatalog.Default(),
        _ => true);
    var staticHtml = DashboardRenderer.Render(kernel);
    var workspace = new DashboardWorkspaceContext(
        @"C:\repo\.orchestrator",
        @"C:\repo\.orchestrator\state.json",
        @"C:\repo",
        @"C:\repo\.orchestrator\prompts",
        @"C:\repo\.orchestrator\logs",
        @"C:\repo\.orchestrator\workers.json",
        @"C:\repo\.orchestrator\agents.json",
        12345,
        @".\mcg-orchestrator.cmd prototype-ui http://localhost:5087/ --refresh 5 --no-open",
        new DashboardProcessDiagnostic(
            12345,
            "Mcg.AgentOrchestrator.App",
            @"C:\repo\artifacts\Mcg.AgentOrchestrator.App.exe",
            [5087],
            [
                new DashboardSiblingProcessContext(
                    67890,
                    @"C:\repo\artifacts\Mcg.AgentOrchestrator.App.exe",
                    DateTimeOffset.Parse("2026-06-04T12:00:01Z"),
                    [5098],
                    "Stop-Process -Id 67890",
                    "same process name")
            ],
            "Detected 1 sibling dashboard app process(es). Stop only exact known PIDs after confirming they are stale."),
        BuildTestRuns:
        [
            new DashboardBuildTestRunSummaryDto(
                "20260604-161905",
                DateTimeOffset.Parse("2026-06-04T16:19:05Z"),
                true,
                true,
                true,
                true,
                "Passed",
                @"C:\repo\.orchestrator\logs\dashboard-build-test-cycle-20260604-161905.ps1",
                @"C:\repo\.orchestrator\logs\dashboard-build-test-cycle-20260604-161905.out.log",
                @"C:\repo\.orchestrator\logs\dashboard-build-test-cycle-20260604-161905.err.log",
                "Build succeeded.\nBuildSucceeded        : True\nTestSucceeded         : True",
                string.Empty)
        ]);
    var continuation = new DashboardContinuationStatusDto(
        goal.Id.Value,
        true,
        DateTimeOffset.Parse("2026-06-04T12:00:00Z"),
        DateTimeOffset.Parse("2026-06-04T12:00:05Z"),
        1,
        "Background work is still running for task abcdef12; continue after it exits.",
        null,
        DateTimeOffset.Parse("2026-06-04T12:05:00Z"),
        true);

    var goalPrefix = goal.Id.Value[..8];

    // Ops view
    var opsHtml = DashboardRenderer.Render(
        kernel,
        new DashboardRenderOptions(
            EnableOperatorControls: true,
            HealthReport: health,
            Workspace: workspace,
            ContinuationWatches: [continuation]));

    // Config view
    var configHtml = DashboardRenderer.Render(
        kernel,
        new DashboardRenderOptions(
            EnableOperatorControls: true,
            HealthReport: health,
            Workspace: workspace,
            ContinuationWatches: [continuation],
            View: DashboardView.Config));

    // System view
    var systemHtml = DashboardRenderer.Render(
        kernel,
        new DashboardRenderOptions(
            EnableOperatorControls: true,
            HealthReport: health,
            Workspace: workspace,
            ContinuationWatches: [continuation],
            View: DashboardView.System));

    // Goal detail view
    var goalHtml = DashboardRenderer.Render(
        kernel,
        new DashboardRenderOptions(
            EnableOperatorControls: true,
            HealthReport: health,
            Workspace: workspace,
            ContinuationWatches: [continuation],
            FocusGoalPrefix: goalPrefix,
            View: DashboardView.Goal));

    // Static (no operator controls) should not have forms or setup doctor
    Assert.False(staticHtml.Contains("data-action=\"/api/goals\"", StringComparison.Ordinal));
    Assert.False(staticHtml.Contains("Setup Doctor", StringComparison.Ordinal));

    // Ops view: create goal form, goal summary, attention, next steps
    Assert.Contains(opsHtml, text => text.Contains("data-action=\"/api/goals\"", StringComparison.Ordinal));
    Assert.Contains(opsHtml, text => text.Contains($"href=\"/api/goals/{goalPrefix}\"", StringComparison.Ordinal));
    Assert.Contains(opsHtml, text => text.Contains("Goal JSON", StringComparison.Ordinal));
    Assert.Contains(opsHtml, text => text.Contains("<label for=\"new-goal\">Goal</label>", StringComparison.Ordinal));
    Assert.Contains(opsHtml, text => text.Contains("name=\"workflow\"", StringComparison.Ordinal));
    Assert.Contains(opsHtml, text => text.Contains("<option value=\"simple\">Simple task</option>", StringComparison.Ordinal));
    Assert.Contains(opsHtml, text => text.Contains("name=\"autoHandoff\" value=\"false\"", StringComparison.Ordinal));
    Assert.Contains(opsHtml, text => text.Contains("name=\"confirmAutoHandoff\" value=\"true\"", StringComparison.Ordinal));
    Assert.Contains(opsHtml, text => text.Contains("id=\"new-goal-auto-handoff\" type=\"checkbox\" name=\"autoHandoff\" value=\"true\"", StringComparison.Ordinal));
    Assert.False(opsHtml.Contains("id=\"new-goal-auto-handoff\" type=\"checkbox\" name=\"autoHandoff\" value=\"true\" checked", StringComparison.Ordinal));
    Assert.Contains(opsHtml, text => text.Contains("Automatically start subscription handoff", StringComparison.Ordinal));
    Assert.Contains(opsHtml, text => text.Contains("data-next-action=\"RefreshRunningProcess\"", StringComparison.Ordinal));
    Assert.Contains(opsHtml, text => text.Contains("data-next-action=\"DelegatePendingTask\"", StringComparison.Ordinal));
    Assert.Contains(opsHtml, text => text.Contains("src=\"/assets/dashboard.js\"", StringComparison.Ordinal));

    // Nav bar present on all views
    Assert.Contains(opsHtml, text => text.Contains("class=\"dashboard-nav\"", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("class=\"dashboard-nav\"", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("class=\"dashboard-nav\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("class=\"dashboard-nav\"", StringComparison.Ordinal));

    // System view: workspace, diagnostics, continuations
    Assert.Contains(systemHtml, text => text.Contains("Prototype workspace", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Execution directory", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains(@"C:\repo", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains(@"C:\repo\.orchestrator\workers.json", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Dashboard PID", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("12345", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Stop this known process before full build/test", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Prototype process diagnostic", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Detected 1 sibling dashboard app process", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("67890", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("<code>5087</code>", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("<code>5098</code>", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Stop-Process -Id 67890", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Open process diagnostic", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("href=\"/api/system/processes\"", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Build/test cleanup plan", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Open build/test cleanup plan", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("href=\"/api/system/build-test-cleanup\"", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("data-action-button=\"/api/system/run-build-test-cycle\"", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Run dashboard build/test cycle", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("POST /api/system/run-build-test-cycle", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Build/test run history", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("href=\"/api/system/build-test-runs\"", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("20260604-161905", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("BuildSucceeded        : True", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("TestSucceeded         : True", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("/api/system/build-test-runs/log?path=", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains(@".\scripts\Invoke-DashboardBuildTestCycle.ps1 -DashboardUrl http://localhost:5087/", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Get-Process Mcg.AgentOrchestrator.App -ErrorAction SilentlyContinue", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("dotnet build Mcg.AgentOrchestrator.sln --no-restore", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("dotnet test Mcg.AgentOrchestrator.sln --no-build", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Restart command", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains(@".\mcg-orchestrator.cmd prototype-ui http://localhost:5087/ --refresh 5 --no-open", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("data-action=\"/api/system/stop-dashboard\"", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Stop dashboard for build/test", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Server continuation", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Open continuation summary", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("/api/continuations/summary", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Open continuation status", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("/api/continuations", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("<th>Source</th>", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Restored from durable store", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Next check", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("2026-06-04 12:05:00Z", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Background work is still running", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("Open bounded source survey", StringComparison.Ordinal));
    Assert.Contains(systemHtml, text => text.Contains("href=\"/api/source-survey?max=8\"", StringComparison.Ordinal));

    // Config view: setup doctor, agents, workers
    Assert.Contains(configHtml, text => text.Contains("Setup Doctor", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("OPENAI_API_KEY is set.", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("local-echo", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("codex-cli", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("<td>Optional</td>", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("<th>Patch</th>", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("Patch-capable", StringComparison.Ordinal));
    Assert.False(configHtml.Contains("Smoke default provider", StringComparison.Ordinal));
    Assert.False(configHtml.Contains("/api/provider-smoke?target=openai", StringComparison.Ordinal));
    Assert.False(configHtml.Contains("/api/provider-smoke?target=all", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("data-action=\"/api/provider-smoke\"", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("name=\"target\" value=\"openai\"", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("type=\"checkbox\" name=\"confirmPaidSmoke\" value=\"true\"", StringComparison.Ordinal));
    Assert.False(configHtml.Contains("type=\"hidden\" name=\"confirmPaidSmoke\"", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("<button type=\"submit\">Paid smoke</button>", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains(">Local smoke</a>", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("data-action=\"/api/agents\"", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("data-agent-config=\"true\"", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("name=\"executionPolicy\"", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("name=\"providerName\"", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("<select name=\"modelName\" data-provider-options=\"apiModels\"", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("<select name=\"reasoningEffort\" data-provider-options=\"apiReasoning\"", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("<select name=\"subscriptionProfileName\" data-provider-options=\"subscriptionProfiles\"", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("<select name=\"subscriptionModelAlias\" data-provider-options=\"subscriptionModels\"", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("<option value=\"gpt-5.3-codex\" selected>GPT-5.3-Codex</option>", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("<option value=\"\">Use API model</option>", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("<select name=\"subscriptionReasoningEffort\" data-provider-options=\"subscriptionReasoning\"", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("<select name=\"complexReasoningEffort\" data-provider-options=\"apiReasoning\"", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("name=\"maxOutputTokens\" min=\"1\" placeholder=\"768\"", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("name=\"complexMaxOutputTokens\" min=\"1\" placeholder=\"1200\"", StringComparison.Ordinal));
    AssertOpenAiModelOrderIsCostAware(configHtml);
    AssertOpenAiModelOrderIsCostAware(DashboardAssets.OperatorControlsScript);
    Assert.False(configHtml.Contains("Default CLI model", StringComparison.Ordinal));
    Assert.False(DashboardAssets.OperatorControlsScript.Contains("Default CLI model", StringComparison.Ordinal));
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("defaultSubscriptionModel: 'gpt-5.3-codex'", StringComparison.Ordinal));
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("['sonnet','Claude Sonnet (latest)']", StringComparison.Ordinal));
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("maxTokenPlaceholder(provider)", StringComparison.Ordinal));
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("complexMaxTokenPlaceholder(provider)", StringComparison.Ordinal));
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("complexProviderName", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("data-action=\"/api/worker-profiles\"", StringComparison.Ordinal));
    Assert.Contains(configHtml, text => text.Contains("name=\"commandTemplate\"", StringComparison.Ordinal));

    // Goal detail view: operator controls, pending input, task actions, reports
    Assert.Contains(goalHtml, text => text.Contains("Human decisions", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("<th>Work item</th>", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Task 1: Developer", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/monitor?goal={goalPrefix}", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Source survey", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Low-noise repository map", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("!**/.scratch/**", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("!**/.orchestrator-prototype/**", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"answer {request.Id.Value[..8]} &lt;answer&gt;", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"id=\"input-{request.Id.Value[..8]}\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"href=\"#input-{request.Id.Value[..8]}\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("data-next-action=\"AnswerHumanInput\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("class=\"decision-question\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Recent task activity", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"<textarea id=\"answer-{request.Id.Value[..8]}\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Submit Answer", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("class=\"answer-choice-row\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("name=\"answer\" value=\"Yes\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("name=\"answer\" value=\"No\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"data-toggle-custom-answer=\"answer-{request.Id.Value[..8]}\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("aria-expanded=\"false\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Work summary", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Open compact JSON", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Open monitor JSON", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Open next-action JSON", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Open evidence JSON", StringComparison.Ordinal));
    Assert.False(goalHtml.Contains("Open raw JSON", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/work-summary", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("/api/source-survey?max=8", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/acceptance?goal={goalPrefix}", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/evidence?goal={goalPrefix}", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/stages?goal={goalPrefix}", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/next?goal={goalPrefix}", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/gates?goal={goalPrefix}", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/verification-worklist?goal={goalPrefix}", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/transcript", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/subscription-plan", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/advance", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/advance-subscription?confirmSubscriptionAdvance=true", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/advance-until-blocked", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/advance-subscription-until-blocked?confirmSubscriptionAdvance=true", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Continue subscription handoff", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Continue non-API actions", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/profile-dispatch-ready", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/subscription-dispatch-ready", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/start-subscription-ready?confirmBatchStart=true", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/cancel-dispatches", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"data-action=\"/api/goals/{goalPrefix}/tasks\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"data-action=\"/api/goals/{goalPrefix}/ask\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("<option>Developer</option>", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("name=\"delegate\" value=\"false\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("name=\"verificationPlan\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Goal question", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/start-dispatches?confirmBatchStart=true", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/input/{request.Id.Value[..8]}/answer", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/tasks/3/brief", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/tasks/3/timeline", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/tasks/3/gate", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/tasks/3/verification-plan", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/tasks/3/verifications", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/tasks/3/run?confirmTaskRun=true", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Current next action", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Background process is running: pid 1234", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/tasks/2/refresh", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Advanced task controls", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/tasks/3/retry", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Retry note", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("What changed or what should be tried next?", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/tasks/3/profile-dispatch", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/tasks/3/subscription-dispatch", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("name=\"plan\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("value=\"codex-cli\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/tasks/3/progress", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("name=\"message\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/tasks/3/ask", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("name=\"question\"", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/tasks/3/verify", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("dotnet test --filter", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("AgentCatalog|WorkerProfile", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("PowerShell: quote filters that contain |", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/tasks/3/verify-manual", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("<option value=\"false\">Failed</option>", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/goals/{goalPrefix}/tasks/2/logs", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains($"/api/human-input-worklist?goal={goalPrefix}", StringComparison.Ordinal));

    // Script assertions (view-independent)
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("agentProviderOptions", StringComparison.Ordinal));
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("summarizeResponse", StringComparison.Ordinal));
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("Server continuation is watching", StringComparison.Ordinal));
    Assert.False(DashboardAssets.OperatorControlsScript.Contains("Auto-resume scheduled", StringComparison.Ordinal));
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("Stopped:", StringComparison.Ordinal));
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("Changed:", StringComparison.Ordinal));
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("Started build/test cycle PID", StringComparison.Ordinal));
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("Dashboard stop requested for PID", StringComparison.Ordinal));
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("Siblings:", StringComparison.Ordinal));
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("window.__dashboardSubmitForm", StringComparison.Ordinal));
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("window.__dashboardReady = true", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "DashboardProcessInspector_filters_current_process_and_executable_siblings")]
    public void DashboardProcessInspectorFiltersCurrentProcessAndExecutableSiblings()
{
    DashboardProcessSnapshot[] processes =
    [
        new DashboardProcessSnapshot(10, @"C:\repo\Mcg.AgentOrchestrator.App.exe", DateTimeOffset.Parse("2026-06-04T12:00:00Z"), "current"),
        new DashboardProcessSnapshot(11, @"C:\repo\Mcg.AgentOrchestrator.App.exe", DateTimeOffset.Parse("2026-06-04T12:00:01Z"), "same process name"),
        new DashboardProcessSnapshot(12, @"D:\other\Mcg.AgentOrchestrator.App.exe", DateTimeOffset.Parse("2026-06-04T12:00:02Z"), "different app")
    ];
    var listeningPortsByPid = new Dictionary<int, IReadOnlyList<int>>
    {
        [10] = new List<int> { 5087 },
        [11] = new List<int> { 5098 },
        [12] = new List<int> { 5100 }
    };

    var diagnostic = DashboardProcessInspector.Build(
        "Mcg.AgentOrchestrator.App",
        10,
        @"C:\repo\Mcg.AgentOrchestrator.App.exe",
        processes,
        listeningPortsByPid);

    Assert.Equal(10, diagnostic.CurrentProcessId);
    Assert.Equal(5087, diagnostic.CurrentListeningPorts.Single());
    Assert.Equal(1, diagnostic.SiblingProcesses.Count);
    Assert.Equal(11, diagnostic.SiblingProcesses[0].ProcessId);
    Assert.Equal(5098, diagnostic.SiblingProcesses[0].ListeningPorts.Single());
    Assert.Equal("Stop-Process -Id 11", diagnostic.SiblingProcesses[0].SafeStopCommand);
    Assert.Contains(diagnostic.Message, text => text.Contains("Detected 1 sibling", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "DashboardProcessInspector_parses_netstat_listening_ports_by_pid")]
    public void DashboardProcessInspectorParsesNetstatListeningPortsByPid()
{
    const string netstat = """

      Proto  Local Address          Foreign Address        State           PID
      TCP    0.0.0.0:5087           0.0.0.0:0              LISTENING       12345
      TCP    [::]:5087              [::]:0                 LISTENING       12345
      TCP    127.0.0.1:5098         0.0.0.0:0              LISTENING       67890
      TCP    127.0.0.1:60123        127.0.0.1:443          ESTABLISHED     67890
      UDP    0.0.0.0:5353           *:*                                    111
      """;

    var parsed = DashboardProcessInspector.ParseNetstatListeningPorts(netstat);

    Assert.Equal(2, parsed.Count);
    Assert.Equal(5087, parsed[12345].Single());
    Assert.Equal(5098, parsed[67890].Single());
}
    [Xunit.Fact(DisplayName = "DashboardRenderer_highlights_completed_goals")]
    public void DashboardRendererHighlightsCompletedGoals()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Done from dashboard",
        [new TaskSpec(TaskId.New(), "Finish the simple goal", AgentRole.Developer)]);
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(
        goal.Id,
        task.Id,
        ManualVerificationRecorder.Create(true, "Looks complete.", Environment.CurrentDirectory, DateTimeOffset.UtcNow));

    var goalPrefix = goal.Id.Value[..8];

    // Ops view shows completion banner in goal header
    var opsHtml = DashboardRenderer.Render(kernel, new DashboardRenderOptions(EnableOperatorControls: true));
    Assert.Contains(opsHtml, text => text.Contains("completion-banner", StringComparison.Ordinal));
    Assert.Contains(opsHtml, text => text.Contains("Goal complete", StringComparison.Ordinal));
    Assert.Contains(opsHtml, text => text.Contains("manual-only verification", StringComparison.Ordinal));
    Assert.Contains(opsHtml, text => text.Contains("No execution, dispatch, or process proof is recorded", StringComparison.Ordinal));
    Assert.False(opsHtml.Contains("No operator action is required.", StringComparison.Ordinal));

    // Goal detail view shows task action status
    var goalHtml = DashboardRenderer.Render(kernel, new DashboardRenderOptions(
        EnableOperatorControls: true,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix));
    Assert.Contains(goalHtml, text => text.Contains("This task is complete and has passing verification evidence.", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "DashboardNextActionControls_builds_direct_controls_for_safe_actions")]
    public void DashboardNextActionControlsBuildsDirectControlsForSafeActions()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Map direct next actions");
    var task = goal.Tasks[2];
    var goalPrefix = goal.Id.Value[..8];

    AssertControl(
        goal,
        new NextActionItem(NextActionKind.RunAssignedTask, task.Id, null, "Run it"),
        "Run task",
        "POST",
        $"/api/goals/{goalPrefix}/tasks/3/run?confirmTaskRun=true");
    AssertControl(
        goal,
        new NextActionItem(NextActionKind.RunAssignedTask, task.Id, null, "Run it"),
        "Prepare subscription handoff",
        "POST",
        $"/api/goals/{goalPrefix}/tasks/3/run?confirmTaskRun=true",
        [Validation(task.RequiredRole, AgentExecutionPolicy.PreferSubscription)],
        "paid subscription handoff");
    AssertControl(
        goal,
        new NextActionItem(NextActionKind.RunAssignedTask, task.Id, null, "Run it"),
        "Prepare subscription handoff",
        "POST",
        $"/api/goals/{goalPrefix}/tasks/3/run?confirmTaskRun=true",
        [Validation(task.RequiredRole, AgentExecutionPolicy.AnyAvailable)],
        "paid subscription handoff");
    AssertControl(
        goal,
        new NextActionItem(NextActionKind.RunAssignedTask, task.Id, null, "Run it"),
        "Run paid API task",
        "POST",
        $"/api/goals/{goalPrefix}/tasks/3/run?confirmTaskRun=true&confirmPaidApiRun=true",
        [Validation(task.RequiredRole, AgentExecutionPolicy.ApiOnly)],
        "paid API");
    AssertControl(
        goal,
        new NextActionItem(NextActionKind.RefreshRunningProcess, task.Id, null, "Refresh it"),
        "Refresh process",
        "POST",
        $"/api/goals/{goalPrefix}/tasks/3/refresh");
    AssertControl(
        goal,
        new NextActionItem(NextActionKind.ExecuteRecordedDispatch, task.Id, null, "Start it"),
        "Start prepared work",
        "POST",
        $"/api/goals/{goalPrefix}/tasks/3/start?confirmDispatchStart=true");
    AssertControl(
        goal,
        new NextActionItem(NextActionKind.DelegatePendingTask, null, null, "Delegate"),
        "Assign tasks",
        "POST",
        $"/api/goals/{goalPrefix}/delegate");
    AssertControl(
        goal,
        new NextActionItem(NextActionKind.InspectFailedTask, task.Id, null, "Inspect"),
        "Inspect task",
        "GET",
        $"/api/task/3?goal={goalPrefix}");
    AssertControl(
        goal,
        new NextActionItem(NextActionKind.FixFailedVerification, task.Id, null, "Fix verification"),
        "Verification records",
        "GET",
        $"/api/goals/{goalPrefix}/tasks/3/verifications");
    AssertControl(
        goal,
        new NextActionItem(NextActionKind.MonitorGoal, null, null, "Monitor"),
        "Monitor goal",
        "GET",
        $"/api/monitor?goal={goalPrefix}");

    Assert.Equal(null, DashboardNextActionControls.Build(goal, new NextActionItem(NextActionKind.VerifyCompletedTask, task.Id, null, "Verify")));
}

    [Xunit.Fact(DisplayName = "DashboardNextActionControls_surface_prior_subscription_model_fit_before_handoff")]
    public void DashboardNextActionControlsSurfacePriorSubscriptionModelFitBeforeHandoff()
{
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Update the old label.", AgentRole.Developer);
    var nextTask = new TaskSpec(TaskId.New(), "Update the next label.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Avoid repeating overkill paid subscription handoff", [priorTask, nextTask]);
    var agent = new AgentDefinition(
        new AgentId("cost-aware-developer"),
        "Cost-aware Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "manual-verification passed",
        "C:\\repo",
        0,
        "Evidence checked.\nModel fit: OpenAI/gpt-5-mini - overkill - label-only change.",
        string.Empty,
        DateTimeOffset.UtcNow));
    var action = kernel.BuildNextActions(goal.Id).Items.Single();

    var control = DashboardNextActionControls.Build(goal, action, agentDefinitions: [agent]);
    var nextDto = DashboardResponseMapper.ToNextActionsDto(goal, kernel.BuildNextActions(goal.Id), [agent]).Items.Single();

    Assert.Equal(NextActionKind.RunAssignedTask, action.Kind);
    Assert.Equal(nextTask.Id.Value, action.TaskId!.Value);
    Assert.Equal("prior overkill subscription model", control!.CostRisk);
    Assert.True(control.CostRecommendation?.Contains("try local Ollama/qwen3:8b", StringComparison.Ordinal) == true);
    Assert.Equal("prior overkill subscription model", nextDto.Control!.CostRisk);
    Assert.True(nextDto.Control.CostRecommendation?.Contains("try local Ollama/qwen3:8b", StringComparison.Ordinal) == true);
}

    [Xunit.Fact(DisplayName = "DashboardNextActionControls_confirm_large_paid_prepared_dispatch")]
    public void DashboardNextActionControlsConfirmLargePaidPreparedDispatch()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Start costly prepared dispatch",
        [new TaskSpec(TaskId.New(), "Run prepared paid work", AgentRole.Developer)]);
    var agent = Agent(AgentRole.Developer, AgentExecutionPolicy.PreferSubscription);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec prompt.md",
        "C:\\repo",
        DateTimeOffset.UtcNow,
        "OpenAI",
        "gpt-5.3-codex",
        "medium",
        TaskComplexity.Complex,
        12001));
    var action = kernel.BuildNextActions(goal.Id).Items.Single();
    var goalPrefix = goal.Id.Value[..8];

    var control = DashboardNextActionControls.Build(goal, action);
    var nextDto = DashboardResponseMapper.ToNextActionsDto(goal, kernel.BuildNextActions(goal.Id)).Items.Single();
    var workSummary = DashboardResponseMapper.ToGoalWorkSummaryDto(kernel, goal);
    var html = DashboardRenderer.Render(kernel, new DashboardRenderOptions(
        EnableOperatorControls: true,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix));
    var transcript = GoalTranscriptRenderer.Render(kernel, goal);

    Assert.Equal(NextActionKind.ExecuteRecordedDispatch, action.Kind);
    Assert.Equal($"/api/goals/{goalPrefix}/tasks/1/start?confirmDispatchStart=true", control!.Url);
    Assert.Equal("large paid subscription start", control.CostRisk);
    Assert.True(control.CostRecommendation?.Contains("Inspect the generated prompt", StringComparison.Ordinal) == true);
    Assert.True(nextDto.SuggestedCommand.Contains("--confirm-large-paid-subscription-start", StringComparison.Ordinal));
    Assert.Equal("large paid subscription start", nextDto.Control!.CostRisk);
    Assert.True(nextDto.Control.CostRecommendation?.Contains("Inspect the generated prompt", StringComparison.Ordinal) == true);
    Assert.True(workSummary.NextAction!.SuggestedCommand.Contains("--confirm-large-paid-subscription-start", StringComparison.Ordinal));
    Assert.Equal("large paid subscription start", workSummary.NextAction.Control!.CostRisk);
    Assert.True(workSummary.NextAction.Control.CostRecommendation?.Contains("Inspect the generated prompt", StringComparison.Ordinal) == true);
    Assert.Contains(html, text => text.Contains($"data-next-action=\"ExecuteRecordedDispatch\" data-action-button=\"/api/goals/{goalPrefix}/tasks/1/start?confirmDispatchStart=true\"", StringComparison.Ordinal));
    Assert.False(html.Contains("confirmLargePaidSubscriptionStart=true", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("execute-dispatch 1 --confirm-dispatch-start --confirm-large-paid-subscription-start", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("Suggested command: execute-dispatch 1 --confirm-dispatch-start --confirm-large-paid-subscription-start", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("Cost: large paid subscription start. Inspect the generated prompt", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "DashboardNextActionControls_label_complex_paid_prepared_dispatch")]
    public void DashboardNextActionControlsLabelComplexPaidPreparedDispatch()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Start complex prepared dispatch",
        [new TaskSpec(TaskId.New(), "Run prepared complex paid work", AgentRole.Developer)]);
    var agent = Agent(AgentRole.Developer, AgentExecutionPolicy.PreferSubscription);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec prompt.md",
        "C:\\repo",
        DateTimeOffset.UtcNow,
        "OpenAI",
        "gpt-5.5",
        "high",
        TaskComplexity.Complex,
        500));
    var action = kernel.BuildNextActions(goal.Id).Items.Single();
    var goalPrefix = goal.Id.Value[..8];

    var control = DashboardNextActionControls.Build(goal, action);
    var nextDto = DashboardResponseMapper.ToNextActionsDto(goal, kernel.BuildNextActions(goal.Id)).Items.Single();
    var workSummary = DashboardResponseMapper.ToGoalWorkSummaryDto(kernel, goal);

    Assert.Equal(NextActionKind.ExecuteRecordedDispatch, action.Kind);
    Assert.Equal($"/api/goals/{goalPrefix}/tasks/1/start?confirmDispatchStart=true", control!.Url);
    Assert.Equal("complex paid subscription model", control.CostRisk);
    Assert.True(control.CostRecommendation?.Contains("Confirm this task needs the complex paid subscription model", StringComparison.Ordinal) == true);
    Assert.Equal("complex paid subscription model", nextDto.Control!.CostRisk);
    Assert.True(nextDto.Control.CostRecommendation?.Contains("Confirm this task needs the complex paid subscription model", StringComparison.Ordinal) == true);
    Assert.Equal("complex paid subscription model", workSummary.NextAction!.Control!.CostRisk);
    Assert.True(workSummary.NextAction.Control.CostRecommendation?.Contains("Confirm this task needs the complex paid subscription model", StringComparison.Ordinal) == true);
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_labels_run_controls_by_execution_policy")]
    public void DashboardRendererLabelsRunControlsByExecutionPolicy()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Render policy-aware run labels",
        [
            new TaskSpec(TaskId.New(), "Subscription preferred", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Flexible execution", AgentRole.Tester),
            new TaskSpec(TaskId.New(), "API execution", AgentRole.Reviewer),
            new TaskSpec(TaskId.New(), "Flexible execution with dispatch evidence", AgentRole.Researcher)
        ]);
    var agents = new[]
    {
        Agent(AgentRole.Developer, AgentExecutionPolicy.PreferSubscription),
        Agent(AgentRole.Tester, AgentExecutionPolicy.AnyAvailable),
        Agent(AgentRole.Reviewer, AgentExecutionPolicy.ApiOnly),
        Agent(AgentRole.Researcher, AgentExecutionPolicy.AnyAvailable)
    };
    kernel.ActivateGoal(goal.Id, agents);
    var dispatchedTask = goal.Tasks[3];
    kernel.RecordTaskDispatch(goal.Id, dispatchedTask.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", "C:\\repo", DateTimeOffset.UtcNow));

    var goalPrefix = goal.Id.Value[..8];
    var health = new OrchestratorHealthReport(
        [],
        [
            Validation(AgentRole.Developer, AgentExecutionPolicy.PreferSubscription),
            Validation(AgentRole.Tester, AgentExecutionPolicy.AnyAvailable),
            Validation(AgentRole.Reviewer, AgentExecutionPolicy.ApiOnly),
            Validation(AgentRole.Researcher, AgentExecutionPolicy.AnyAvailable)
        ],
        []);
    var html = DashboardRenderer.Render(kernel, new DashboardRenderOptions(
        EnableOperatorControls: true,
        HealthReport: health,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix,
        AgentDefinitions: agents));
    var flexiblePromptChars = AgentTaskRunner.PreviewRun(goal, goal.Tasks[1], agents).PromptCharacterCount;
    var apiPromptChars = AgentTaskRunner.PreviewRun(goal, goal.Tasks[2], agents).PromptCharacterCount;
    var stages = DashboardResponseMapper.ToGoalStageReadinessReportDto(goal, kernel.BuildStageReadinessReport(goal.Id), agents);

    Assert.Contains(html, text => text.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/1/run?confirmTaskRun=true\">Prepare subscription handoff</button>", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/2/run?confirmTaskRun=true\">Prepare subscription handoff</button>", StringComparison.Ordinal));
    Assert.False(html.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/1/api-run", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/2/api-run?confirmTaskRun=true&amp;confirmPaidApiRun=true\">Explicit paid API run</button>", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/3/run?confirmTaskRun=true&amp;confirmPaidApiRun=true\">Run paid API task</button>", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("confirmPaidApiRun=true", StringComparison.Ordinal));
    Assert.False(html.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/3/api-run", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/4/run?confirmTaskRun=true\">Prepare subscription handoff</button>", StringComparison.Ordinal));
    Assert.False(html.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/4/api-run", StringComparison.Ordinal));
    Assert.False(ExtractTaskControls(html, 1).Contains("API plan:", StringComparison.Ordinal));
    Assert.Contains(ExtractTaskControls(html, 2), text => text.Contains($"API plan: OpenAI/test Simple reasoning medium prompt {flexiblePromptChars} chars max 768 out [potentially paid]", StringComparison.Ordinal));
    Assert.Contains(ExtractTaskControls(html, 3), text => text.Contains($"API plan: OpenAI/test Simple reasoning medium prompt {apiPromptChars} chars max 768 out [potentially paid]", StringComparison.Ordinal));
    Assert.False(ExtractTaskControls(html, 4).Contains("API plan:", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("<code>subscription-dispatch 1</code>", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("<code>subscription-dispatch 2</code>", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("<code>run 3 --confirm-paid-api-run</code>", StringComparison.Ordinal));
    Assert.Equal("subscription-dispatch 1", stages.Stages.Single(stage => stage.TaskNumber == 1).SuggestedCommand);
    Assert.Equal("subscription-dispatch 2", stages.Stages.Single(stage => stage.TaskNumber == 2).SuggestedCommand);
    Assert.Equal("run 3 --confirm-paid-api-run", stages.Stages.Single(stage => stage.TaskNumber == 3).SuggestedCommand);
    Assert.False(html.Contains($"subscription-dispatch 1 | api-run 1", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_confirms_large_paid_api_prompt_from_exact_preview")]
    public void DashboardRendererConfirmsLargePaidApiPromptFromExactPreview()
{
    var objective = "production architecture api cli dashboard provider subscription worker persistence state tests docs " + new string('o', 5000);
    var description = "Design and implement complete integration with authentication migration rollback state persistence and dashboard api tests. " + new string('d', 5000);
    var verificationPlan = "Run end-to-end integration tests, dashboard smoke tests, api tests, cli tests, and rollback checks. " + new string('v', 5000);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        objective,
        [new TaskSpec(TaskId.New(), description, AgentRole.Developer, verificationPlan)]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey, ReasoningEffort: "medium"),
        ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
    var agents = new[] { agent };
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    var preview = AgentTaskRunner.PreviewRun(goal, task, agents);
    var risk = ApiPromptCostGuard.Evaluate(preview);
    var goalPrefix = goal.Id.Value[..8];
    var health = new OrchestratorHealthReport(
        [],
        [Validation(AgentRole.Developer, AgentExecutionPolicy.ApiOnly)],
        []);

    var html = DashboardRenderer.Render(kernel, new DashboardRenderOptions(
        EnableOperatorControls: true,
        HealthReport: health,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix,
        AgentDefinitions: agents));
    var controls = ExtractTaskControls(html, 1);
    var nextDto = DashboardResponseMapper
        .ToNextActionsDto(goal, kernel.BuildNextActions(goal.Id), agents)
        .Items
        .Single(item => item.TaskId == task.Id.Value);
    var workSummary = DashboardResponseMapper.ToGoalWorkSummaryDto(kernel, goal, agents);
    var stageDto = DashboardResponseMapper.ToGoalStageReadinessReportDto(goal, kernel.BuildStageReadinessReport(goal.Id), agents).Stages.Single();

    Assert.Equal(TaskComplexity.Complex, preview.TaskComplexity);
    Assert.True(risk is not null);
    Assert.Equal("run 1 --confirm-paid-api-run --confirm-large-paid-api-prompt", nextDto.SuggestedCommand);
    Assert.Equal(ApiPromptCostGuard.BuildInlineLabel(risk!), nextDto.Control!.CostRisk);
    Assert.Equal("run 1 --confirm-paid-api-run --confirm-large-paid-api-prompt", workSummary.NextAction!.SuggestedCommand);
    Assert.Equal(ApiPromptCostGuard.BuildInlineLabel(risk!), workSummary.NextAction.Control!.CostRisk);
    Assert.Equal("run 1 --confirm-paid-api-run --confirm-large-paid-api-prompt", stageDto.SuggestedCommand);
    Assert.Contains(html, text => text.Contains("<code>run 1 --confirm-paid-api-run --confirm-large-paid-api-prompt</code>", StringComparison.Ordinal));
    Assert.Contains(controls, text => text.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/1/run?confirmTaskRun=true&amp;confirmPaidApiRun=true\">Run paid API task</button>", StringComparison.Ordinal));
    Assert.Contains(controls, text => text.Contains("confirmPaidApiRun=true", StringComparison.Ordinal));
    Assert.False(controls.Contains("confirmLargePaidApiPrompt=true", StringComparison.Ordinal));
    Assert.Contains(controls, text => text.Contains($"API plan: OpenAI/test Complex reasoning medium prompt {preview.PromptCharacterCount} chars max 1200 out [potentially paid] [large paid prompt: exceeds {risk!.PromptThreshold}]", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_confirms_prior_overkill_paid_api_model")]
    public void DashboardRendererConfirmsPriorOverkillPaidApiModel()
{
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Update the old label.", AgentRole.Developer);
    var nextTask = new TaskSpec(TaskId.New(), "Update the next label.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Avoid repeating overkill API model", [priorTask, nextTask]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey, "medium", 768),
        ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
    var agents = new[] { agent };
    kernel.ActivateGoal(goal.Id, agents);
    kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "manual-verification passed",
        "C:\\repo",
        0,
        "Evidence checked.\nModel fit: OpenAI/gpt-5-codex - overkill - copy-only change.",
        string.Empty,
        DateTimeOffset.UtcNow));
    var preview = AgentTaskRunner.PreviewRun(goal, nextTask, agents);
    var risk = ApiPromptCostGuard.Evaluate(preview, goal);
    var goalPrefix = goal.Id.Value[..8];
    var health = new OrchestratorHealthReport(
        [],
        [Validation(AgentRole.Developer, AgentExecutionPolicy.ApiOnly)],
        []);

    var html = DashboardRenderer.Render(kernel, new DashboardRenderOptions(
        EnableOperatorControls: true,
        HealthReport: health,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix,
        AgentDefinitions: agents));
    var controls = ExtractTaskControls(html, 2);
    var nextDto = DashboardResponseMapper
        .ToNextActionsDto(goal, kernel.BuildNextActions(goal.Id), agents)
        .Items
        .Single(item => item.TaskId == nextTask.Id.Value);

    Assert.Equal(TaskComplexity.Simple, preview.TaskComplexity);
    Assert.True(preview.PromptCharacterCount <= risk!.PromptThreshold);
    Assert.True(risk.HasPriorOverkillFit);
    Assert.False(risk.PromptExceedsThreshold);
    Assert.Equal("prior overkill API model", nextDto.Control!.CostRisk);
    Assert.True(nextDto.Control.CostRecommendation?.Contains("try local Ollama/qwen3:8b via agent configuration before paid API run", StringComparison.Ordinal) == true);
    Assert.Equal("run 2 --confirm-paid-api-run --confirm-large-paid-api-prompt", nextDto.SuggestedCommand);
    Assert.Contains(controls, text => text.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/2/run?confirmTaskRun=true&amp;confirmPaidApiRun=true\">Run paid API task</button>", StringComparison.Ordinal));
    Assert.Contains(controls, text => text.Contains("confirmPaidApiRun=true", StringComparison.Ordinal));
    Assert.False(controls.Contains("confirmLargePaidApiPrompt=true", StringComparison.Ordinal));
    Assert.True(risk!.PriorTaskShapes?.Contains("copy-only change") == true);
    Assert.Contains(controls, text => text.Contains($"API plan: OpenAI/gpt-5-codex Simple reasoning medium prompt {preview.PromptCharacterCount} chars max 768 out [potentially paid] [prior overkill API model]", StringComparison.Ordinal));
    Assert.Contains(controls, text => text.Contains("try local Ollama/qwen3:8b via agent configuration before paid API run", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "ApiPromptCostGuard_retains_earlier_model_fit_attempts")]
    public void ApiPromptCostGuardRetainsEarlierModelFitAttempts()
{
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Update the old label.", AgentRole.Developer);
    var nextTask = new TaskSpec(TaskId.New(), "Update the next label.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Avoid repeated overkill API model from full history", [priorTask, nextTask]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey, "medium", 768),
        ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
    var agents = new[] { agent };
    kernel.ActivateGoal(goal.Id, agents);
    kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "manual-verification passed",
        "C:\\repo",
        0,
        "Evidence checked.\nModel fit: OpenAI/gpt-5-codex - overkill - label-only change.",
        string.Empty,
        DateTimeOffset.UtcNow));
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "manual-verification passed",
        "C:\\repo",
        0,
        "Evidence checked.\nModel fit: OpenAI/gpt-5.4-mini - adequate - focused parser fix.",
        string.Empty,
        DateTimeOffset.UtcNow.AddMinutes(1)));

    var preview = AgentTaskRunner.PreviewRun(goal, nextTask, agents);
    var risk = ApiPromptCostGuard.Evaluate(preview, goal);
    var requiredRisk = risk ?? throw new InvalidOperationException("Expected paid API prompt risk.");

    Assert.Equal("prior overkill API model", ApiPromptCostGuard.BuildInlineLabel(requiredRisk));
    Assert.Equal(1, requiredRisk.PriorOverkillCount);
    Assert.True(requiredRisk.PriorTaskShapes?.Contains("label-only change") == true);
    Assert.True(ApiPromptCostGuard.BuildRecommendation(requiredRisk)?.Contains("try local Ollama/qwen3:8b", StringComparison.Ordinal) == true);
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_confirms_complex_paid_api_model_from_exact_preview")]
    public void DashboardRendererConfirmsComplexPaidApiModelFromExactPreview()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Plan architecture work",
        [new TaskSpec(TaskId.New(), "Design and implement a production multi-tenant architecture.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium", 768),
        ExecutionPolicy: AgentExecutionPolicy.ApiOnly,
        ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high", 1200));
    var agents = new[] { agent };
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    var preview = AgentTaskRunner.PreviewRun(goal, task, agents);
    var risk = ApiPromptCostGuard.Evaluate(preview);
    var goalPrefix = goal.Id.Value[..8];
    var health = new OrchestratorHealthReport(
        [],
        [Validation(AgentRole.Developer, AgentExecutionPolicy.ApiOnly)],
        []);

    var html = DashboardRenderer.Render(kernel, new DashboardRenderOptions(
        EnableOperatorControls: true,
        HealthReport: health,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix,
        AgentDefinitions: agents));
    var controls = ExtractTaskControls(html, 1);

    Assert.Equal(TaskComplexity.Complex, preview.TaskComplexity);
    Assert.True(preview.PromptCharacterCount <= risk!.PromptThreshold);
    Assert.True(risk.UsesComplexPaidModel);
    Assert.False(risk.PromptExceedsThreshold);
    Assert.Contains(controls, text => text.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/1/run?confirmTaskRun=true&amp;confirmPaidApiRun=true\">Run paid API task</button>", StringComparison.Ordinal));
    Assert.Contains(controls, text => text.Contains("confirmPaidApiRun=true", StringComparison.Ordinal));
    Assert.False(controls.Contains("confirmLargePaidApiPrompt=true", StringComparison.Ordinal));
    Assert.Contains(controls, text => text.Contains($"API plan: OpenAI/gpt-5.5 Complex reasoning high prompt {preview.PromptCharacterCount} chars max 1200 out [potentially paid] [complex paid API model]", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "ApiPromptCostGuard_uses_complex_threshold_for_evidence_escalated_model")]
    public void ApiPromptCostGuardUsesComplexThresholdForEvidenceEscalatedModel()
{
    var preview = new AgentTaskRunPreview(
        AgentId.New(),
        "Developer",
        "OpenAI",
        "gpt-5.5",
        TaskComplexity.Simple,
        MaxOutputTokens: 1200,
        ReasoningEffort: "high",
        PromptCharacterCount: 5000,
        UsesComplexModel: true);

    var risk = ApiPromptCostGuard.Evaluate(preview);

    Assert.True(risk is not null);
    Assert.Equal(6000, risk!.PromptThreshold);
    Assert.False(risk.PromptExceedsThreshold);
    Assert.True(risk.UsesComplexPaidModel);
    Assert.Equal("complex paid API model", ApiPromptCostGuard.BuildInlineLabel(risk));
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_confirms_evidence_escalated_paid_api_model")]
    public void DashboardRendererConfirmsEvidenceEscalatedPaidApiModel()
{
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Fix old parser behavior.", AgentRole.Developer);
    var nextTask = new TaskSpec(TaskId.New(), "Fix another parser behavior.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Avoid repeating underpowered API model", [priorTask, nextTask]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low", 768),
        ExecutionPolicy: AgentExecutionPolicy.ApiOnly,
        ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high", 1200));
    var agents = new[] { agent };
    kernel.ActivateGoal(goal.Id, agents);
    kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "manual-verification failed",
        "C:\\repo",
        1,
        "Evidence checked.\nModel fit: OpenAI/gpt-5-mini - underpowered - missed regression path.",
        string.Empty,
        DateTimeOffset.UtcNow));
    var preview = AgentTaskRunner.PreviewRun(goal, nextTask, agents);
    var risk = ApiPromptCostGuard.Evaluate(preview, goal);
    var goalPrefix = goal.Id.Value[..8];
    var health = new OrchestratorHealthReport(
        [],
        [Validation(AgentRole.Developer, AgentExecutionPolicy.ApiOnly)],
        []);

    var html = DashboardRenderer.Render(kernel, new DashboardRenderOptions(
        EnableOperatorControls: true,
        HealthReport: health,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix,
        AgentDefinitions: agents));
    var controls = ExtractTaskControls(html, 2);
    var nextDto = DashboardResponseMapper
        .ToNextActionsDto(goal, kernel.BuildNextActions(goal.Id), agents)
        .Items
        .Single(item => item.TaskId == nextTask.Id.Value);

    Assert.Equal(TaskComplexity.Simple, preview.TaskComplexity);
    Assert.True(preview.UsesComplexModel);
    Assert.Equal("gpt-5.5", preview.ModelName);
    Assert.True(risk!.UsesComplexPaidModel);
    Assert.Equal("complex paid API model", nextDto.Control!.CostRisk);
    Assert.Equal("run 2 --confirm-paid-api-run --confirm-large-paid-api-prompt", nextDto.SuggestedCommand);
    Assert.Contains(controls, text => text.Contains($"API plan: OpenAI/gpt-5.5 Simple reasoning high prompt {preview.PromptCharacterCount} chars max 1200 out [potentially paid] [complex paid API model]", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_confirms_large_paid_subscription_prompt_from_plan")]
    public void DashboardRendererConfirmsLargePaidSubscriptionPromptFromPlan()
{
    var objective = "production architecture api cli dashboard provider subscription worker persistence state tests docs " + new string('o', 5000);
    var description = "Design and implement complete integration with authentication migration rollback state persistence and dashboard api tests. " + new string('d', 5000);
    var verificationPlan = "Run end-to-end integration tests, dashboard smoke tests, api tests, cli tests, and rollback checks. " + new string('v', 5000);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        objective,
        [new TaskSpec(TaskId.New(), description, AgentRole.Developer, verificationPlan)]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey, ReasoningEffort: "medium"),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.3-codex", "medium"));
    var agents = new[] { agent };
    var profiles = WorkerProfileCatalog.Default();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    var risk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
        goal,
        agents,
        profiles,
        item => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(kernel, goal, item, agents),
        task);
    var goalPrefix = goal.Id.Value[..8];

    var html = DashboardRenderer.Render(kernel, new DashboardRenderOptions(
        EnableOperatorControls: true,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix,
        AgentDefinitions: agents,
        WorkerProfiles: profiles));

    Assert.True(risk is not null);
    Assert.Contains(html, text => text.Contains($"/api/goals/{goalPrefix}/advance-subscription?confirmSubscriptionAdvance=true", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains($"/api/goals/{goalPrefix}/advance-subscription-until-blocked?confirmSubscriptionAdvance=true", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains($"/api/goals/{goalPrefix}/start-subscription-ready?confirmBatchStart=true", StringComparison.Ordinal));
    Assert.False(html.Contains("confirmLargePaidSubscriptionStart=true", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Run next subscription action (cost gate: large paid subscription start)", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Continue subscription handoff (cost gate: large paid subscription start)", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Start subscription work (cost gate: large paid subscription start)", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains(SubscriptionPromptCostGuard.BuildInlineLabel(risk!), StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains($"{risk!.PromptCharacterCount} prompt chars across 1 task(s), thresholds {risk.BatchPromptThreshold} chars or {risk.BatchTaskThreshold} task(s).", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("OpenAI/gpt-5.3-codex Complex reasoning medium", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_confirms_large_paid_prepared_dispatch_start")]
    public void DashboardRendererConfirmsLargePaidPreparedDispatchStart()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Start large prepared subscription work",
        [new TaskSpec(TaskId.New(), "Run a prepared paid subscription prompt.", AgentRole.Developer)]);
    var agent = Agent(AgentRole.Developer, AgentExecutionPolicy.PreferSubscription);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec prompt.md",
        "C:\\repo",
        DateTimeOffset.UtcNow,
        "OpenAI",
        "gpt-5.3-codex",
        "medium",
        TaskComplexity.Complex,
        12001));
    var risk = SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(kernel, goal, task);
    var goalPrefix = goal.Id.Value[..8];

    var html = DashboardRenderer.Render(kernel, new DashboardRenderOptions(
        EnableOperatorControls: true,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix,
        AgentDefinitions: [agent],
        WorkerProfiles: WorkerProfileCatalog.Default()));
    var controls = ExtractTaskControls(html, 1);

    Assert.True(risk is not null);
    Assert.Contains(html, text => text.Contains($"/api/goals/{goalPrefix}/start-dispatches?confirmBatchStart=true", StringComparison.Ordinal));
    Assert.Contains(controls, text => text.Contains($"/api/goals/{goalPrefix}/tasks/1/start?confirmDispatchStart=true", StringComparison.Ordinal));
    Assert.False(html.Contains("confirmLargePaidSubscriptionStart=true", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Continue subscription handoff (cost gate: large paid subscription start)", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Start prepared work (cost gate: large paid subscription start)", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Prepared starts: 12001 paid prompt chars.", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_surfaces_prepared_dispatch_as_primary_task_action")]
    public void DashboardRendererSurfacesPreparedDispatchAsPrimaryTaskAction()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Operate a prepared dispatch");
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec prompt.md",
        "C:\\repo",
        DateTimeOffset.UtcNow,
        "OpenAI",
        "gpt-5.3-codex",
        "medium",
        TaskComplexity.Simple,
        321));

    var goalPrefix = goal.Id.Value[..8];
    var html = DashboardRenderer.Render(kernel, new DashboardRenderOptions(
        EnableOperatorControls: true,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix));
    var taskDto = DashboardResponseMapper.ToTaskDetailDto(goal, task);
    var workSummary = DashboardResponseMapper.ToGoalWorkSummaryDto(kernel, goal);
    var evidenceDto = DashboardResponseMapper.ToGoalEvidenceSummaryDto(goal, kernel.BuildGoalEvidenceSummary(goal.Id));
    var transcript = GoalTranscriptRenderer.Render(kernel, goal);
    var taskNumber = goal.Tasks.Select((candidate, index) => (candidate, index))
        .Single(item => item.candidate.Id == task.Id)
        .index + 1;

    Assert.Contains(html, text => text.Contains("Prepared handoff for codex-cli.", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("OpenAI/gpt-5.3-codex Simple reasoning medium prompt 321 chars", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Paid subscription handoff prepared", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("try local Ollama/qwen3:8b via agent configuration when the task is routine", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Prompt: 321 chars", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Dispatch models: OpenAI/gpt-5.3-codex (Simple) reasoning medium [potentially paid]: 1 dispatch, prompt 321 chars", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Model fit: OpenAI/gpt-5.3-codex - adequate|overkill|underpowered - task shape - short reason.", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("<code>codex exec prompt.md</code>", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/{taskNumber}/start?confirmDispatchStart=true\"", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("Start prepared work", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains($"href=\"/api/goals/{goalPrefix}/tasks/{taskNumber}/brief\"", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("Dispatch model: OpenAI/gpt-5.3-codex complexity=Simple reasoning=medium", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("Prompt size: 321 chars", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("Dispatch models:", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("- OpenAI/gpt-5.3-codex (Simple) reasoning medium [potentially paid]: 1 dispatch, prompt 321 chars", StringComparison.Ordinal));
    Assert.Contains(evidenceDto.Tasks.Single(item => item.TaskId == task.Id.Value).Message, text => text.Contains("using OpenAI/gpt-5.3-codex Simple reasoning medium", StringComparison.Ordinal));
    var dispatchModel = evidenceDto.DispatchModelUsage.Single();
    Assert.Equal("OpenAI", dispatchModel.ProviderName);
    Assert.Equal("gpt-5.3-codex", dispatchModel.ModelName);
    Assert.Equal(1, dispatchModel.DispatchCount);
    Assert.Equal(TaskComplexity.Simple, dispatchModel.TaskComplexity);
    Assert.Equal("medium", dispatchModel.ReasoningEffort);
    Assert.Equal(321, dispatchModel.PromptCharacterCount);
    Assert.True(dispatchModel.IsPotentiallyPaidProvider);
    Assert.Equal("OpenAI", taskDto.LastDispatch!.ProviderName);
    Assert.Equal("gpt-5.3-codex", taskDto.LastDispatch.ModelName);
    Assert.Equal("medium", taskDto.LastDispatch.ReasoningEffort);
    Assert.Equal(TaskComplexity.Simple, taskDto.LastDispatch.TaskComplexity);
    Assert.Equal(321, taskDto.LastDispatch.PromptCharacterCount);
    var summaryDispatch = workSummary.Tasks.Single(item => item.TaskId == task.Id.Value).LastDispatch!;
    Assert.Equal("OpenAI", summaryDispatch.ProviderName);
    Assert.Equal("gpt-5.3-codex", summaryDispatch.ModelName);
    Assert.Equal(321, summaryDispatch.PromptCharacterCount);
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_keeps_ollama_agent_configuration_local")]
    public void DashboardRendererKeepsOllamaAgentConfigurationLocal()
{
    var kernel = new AgentOrchestratorKernel();
    var health = new OrchestratorHealthReport(
        [new ProviderConfigurationStatus("Ollama", true, "LocalBridge", "Ollama is running.")],
        [
            new AgentConfigurationValidation(
                AgentRole.Tester,
                "Ollama tester",
                "Ollama",
                "qwen2.5-coder:7b",
                null,
                null,
                AgentExecutionPolicy.ApiOnly,
                null,
                null,
                null,
                true,
                "API provider 'Ollama' is registered; subscription execution disabled.",
                "Ollama",
                "qwen3:8b",
                8192,
                null)
        ],
        WorkerProfileCatalog.Default().Profiles.Select(profile => new WorkerProfileValidation(
            profile.Name,
            profile.CommandTemplate,
            profile.Name,
            true,
            false,
            true,
            true,
            "ok")).ToList());

    var html = DashboardRenderer.Render(kernel, new DashboardRenderOptions(
        EnableOperatorControls: true,
        HealthReport: health,
        View: DashboardView.Config));

    Assert.Contains(html, text => text.Contains("<option value=\"Ollama\" selected>Ollama</option>", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("<option value=\"qwen2.5-coder:7b\" selected>Qwen2.5 Coder 7B</option>", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("<option value=\"\" selected>None</option>", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("max tokens: 8192", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("name=\"maxOutputTokens\" min=\"1\" placeholder=\"8192\" value=\"\"", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("name=\"complexMaxOutputTokens\" min=\"1\" placeholder=\"8192\" value=\"8192\"", StringComparison.Ordinal));
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("Ollama: {", StringComparison.Ordinal));
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("qwen2.5-coder:7b", StringComparison.Ordinal));
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("preferredProfile: ''", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_defaults_missing_agent_configuration_to_configured_ollama")]
    public void DashboardRendererDefaultsMissingAgentConfigurationToConfiguredOllama()
{
    var kernel = new AgentOrchestratorKernel();
    var health = new OrchestratorHealthReport(
        [
            new ProviderConfigurationStatus("OpenAI", false, "Offline", "OPENAI_API_KEY is not set."),
            new ProviderConfigurationStatus("Ollama", true, "LocalBridge", "Ollama is running.")
        ],
        [
            new AgentConfigurationValidation(
                AgentRole.Developer,
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                null,
                AgentExecutionPolicy.ApiOnly,
                null,
                null,
                null,
                false,
                "No agent is configured for this role.")
        ],
        WorkerProfileCatalog.Default().Profiles.Select(profile => new WorkerProfileValidation(
            profile.Name,
            profile.CommandTemplate,
            profile.Name,
            true,
            false,
            true,
            true,
            "ok")).ToList());

    var html = DashboardRenderer.Render(kernel, new DashboardRenderOptions(
        EnableOperatorControls: true,
        HealthReport: health,
        View: DashboardView.Config));

    Assert.Contains(html, text => text.Contains("data-default-provider=\"Ollama\"", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("name=\"name\" value=\"Ollama developer\"", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("<option value=\"Ollama\" selected>Ollama</option>", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("<option value=\"qwen2.5-coder:7b\" selected>Qwen2.5 Coder 7B</option>", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains("<option value=\"\" selected>None</option>", StringComparison.Ordinal));
    Assert.Contains(DashboardAssets.OperatorControlsScript, text => text.Contains("form.dataset.defaultProvider || 'OpenAI'", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_makes_advanced_process_controls_state_aware")]
    public void DashboardRendererMakesAdvancedProcessControlsStateAware()
{
    var now = DateTimeOffset.UtcNow;
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Render state-aware process controls",
        [
            new TaskSpec(TaskId.New(), "Prepared dispatch", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Running process", AgentRole.Tester),
            new TaskSpec(TaskId.New(), "No process", AgentRole.Reviewer)
        ]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var prepared = goal.Tasks[0];
    var running = goal.Tasks[1];
    var noProcess = goal.Tasks[2];
    kernel.RecordTaskDispatch(goal.Id, prepared.Id, new TaskDispatchRecord("local", "echo prepared", "C:\\repo", now));
    kernel.RecordTaskDispatch(goal.Id, running.Id, new TaskDispatchRecord("local", "echo running", "C:\\repo", now));
    kernel.RecordTaskProcessStarted(goal.Id, running.Id, new TaskProcessRecord(1234, "echo running", "C:\\repo", "out.log", "err.log", "exit.txt", now, null, null));
    kernel.ReportTaskProgress(goal.Id, noProcess.Id, WorkTaskStatus.Running, "No process yet.");

    var goalPrefix = goal.Id.Value[..8];
    var html = DashboardRenderer.Render(kernel, new DashboardRenderOptions(
        EnableOperatorControls: true,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix));
    var preparedControls = ExtractTaskControls(html, 1);
    var runningControls = ExtractTaskControls(html, 2);
    var noProcessControls = ExtractTaskControls(html, 3);

    Assert.Contains(preparedControls, text => text.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/1/start?confirmDispatchStart=true\"", StringComparison.Ordinal));
    Assert.False(preparedControls.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/1/refresh\"", StringComparison.Ordinal));
    Assert.False(preparedControls.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/1/cancel\"", StringComparison.Ordinal));
    Assert.Contains(preparedControls, text => text.Contains("Task has no background process to refresh.", StringComparison.Ordinal));
    Assert.Contains(preparedControls, text => text.Contains("Task has no background process to cancel.", StringComparison.Ordinal));

    Assert.False(runningControls.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/2/start\"", StringComparison.Ordinal));
    Assert.Contains(runningControls, text => text.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/2/refresh\"", StringComparison.Ordinal));
    Assert.Contains(runningControls, text => text.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/2/cancel\"", StringComparison.Ordinal));
    Assert.Contains(runningControls, text => text.Contains("Task already has a running process pid=1234.", StringComparison.Ordinal));

    Assert.False(noProcessControls.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/3/start\"", StringComparison.Ordinal));
    Assert.False(noProcessControls.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/3/refresh\"", StringComparison.Ordinal));
    Assert.False(noProcessControls.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/3/cancel\"", StringComparison.Ordinal));
    Assert.Contains(noProcessControls, text => text.Contains("Task has no recorded dispatch.", StringComparison.Ordinal));
    Assert.Contains(noProcessControls, text => text.Contains("Task has no background process to refresh.", StringComparison.Ordinal));
    Assert.Contains(noProcessControls, text => text.Contains("Task has no background process to cancel.", StringComparison.Ordinal));
}

static void AssertControl(
    Goal goal,
    NextActionItem item,
    string label,
    string method,
    string url,
    IReadOnlyList<AgentConfigurationValidation>? agents = null,
    string? costRisk = null)
{
    var control = DashboardNextActionControls.Build(goal, item, agents);

    Assert.True(control is not null);
    Assert.Equal(label, control!.Label);
    Assert.Equal(method, control.Method);
    Assert.Equal(url, control.Url);
    Assert.Equal(costRisk, control.CostRisk);
}

static AgentDefinition Agent(AgentRole role, AgentExecutionPolicy policy)
{
    return new AgentDefinition(
        AgentId.New(),
        role.ToString(),
        role,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey, ReasoningEffort: "medium"),
        ExecutionPolicy: policy);
}

static AgentConfigurationValidation Validation(AgentRole role, AgentExecutionPolicy policy)
{
    return new AgentConfigurationValidation(
        role,
        role.ToString(),
        "OpenAI",
        "test",
        "medium",
        AgentCatalog.RoutineApiMaxOutputTokens,
        policy,
        null,
        null,
        null,
        true,
        "valid");
}

static string ExtractTaskControls(string html, int taskNumber)
{
    var start = html.IndexOf($"<strong>Task {taskNumber} controls</strong>", StringComparison.Ordinal);
    Assert.True(start >= 0);
    var next = html.IndexOf($"<strong>Task {taskNumber + 1} controls</strong>", start + 1, StringComparison.Ordinal);
    return next < 0 ? html[start..] : html[start..next];
}
    [Xunit.Fact(DisplayName = "DashboardRenderer_can_focus_an_older_goal_outside_recent_window")]
    public void DashboardRendererCanFocusAnOlderGoalOutsideRecentWindow()
{
    var kernel = new AgentOrchestratorKernel();
    var focused = kernel.CreateGoal("Focused older goal");
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(focused.Id, [agent]);

    for (var index = 0; index < 12; index++)
    {
        kernel.CreateGoal($"Recent goal {index}");
    }

    var focusedPrefix = focused.Id.Value[..8];
    var defaultHtml = DashboardRenderer.Render(kernel, new DashboardRenderOptions(EnableOperatorControls: true));

    // Goal detail view for focused goal shows task actions
    var goalDetailHtml = DashboardRenderer.Render(
        kernel,
        new DashboardRenderOptions(
            EnableOperatorControls: true,
            FocusGoalPrefix: focusedPrefix,
            View: DashboardView.Goal));

    // Ops view with focus query param shows focused goal in ops list
    var focusedOpsHtml = DashboardRenderer.Render(
        kernel,
        new DashboardRenderOptions(
            EnableOperatorControls: true,
            FocusGoalPrefix: focusedPrefix));

    Assert.Contains(defaultHtml, text => text.Contains("Goal Archive", StringComparison.Ordinal));
    Assert.Contains(defaultHtml, text => text.Contains("id=\"goal-archive-focus\"", StringComparison.Ordinal));
    Assert.Contains(defaultHtml, text => text.Contains("list=\"goal-archive-options\"", StringComparison.Ordinal));
    Assert.Contains(defaultHtml, text => text.Contains($"<option value=\"{focusedPrefix}\">Focused older goal</option>", StringComparison.Ordinal));
    Assert.Contains(defaultHtml, text => text.Contains($"href=\"/?goal={focusedPrefix}\"", StringComparison.Ordinal));
    Assert.Contains(focusedOpsHtml, text => text.Contains("Focused older goal", StringComparison.Ordinal));
    Assert.Contains(focusedOpsHtml, text => text.Contains($"Focused goal filter: <code>{focusedPrefix}</code>", StringComparison.Ordinal));
    Assert.Contains(focusedOpsHtml, text => text.Contains($"value=\"{focusedPrefix}\"", StringComparison.Ordinal));
    Assert.Contains(goalDetailHtml, text => text.Contains($"data-action=\"/api/goals/{focusedPrefix}/tasks/1/complete-verify\"", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_prioritizes_active_goals_over_completed_recent_goals")]
    public void DashboardRendererPrioritizesActiveGoalsOverCompletedRecentGoals()
{
    var kernel = new AgentOrchestratorKernel();
    var active = kernel.CreateGoal("Active older goal");
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(active.Id, [agent]);

    string? oldestCompletedPrefix = null;
    for (var index = 0; index < 12; index++)
    {
        var completed = kernel.CreateGoal(
            $"Completed recent goal {index}",
            [new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)]);
        kernel.ActivateGoal(completed.Id, [agent]);
        var task = completed.Tasks.Single();
        kernel.ReportTaskProgress(completed.Id, task.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(completed.Id, task.Id, new TaskVerificationRecord("manual", "C:\\repo", 0, "ok", string.Empty, DateTimeOffset.UtcNow));
        oldestCompletedPrefix ??= completed.Id.Value[..8];
    }

    var activePrefix = active.Id.Value[..8];
    var html = DashboardRenderer.Render(kernel, new DashboardRenderOptions(EnableOperatorControls: true));

    Assert.Contains(html, text => text.Contains($"<h2><a href=\"/goal/{activePrefix}\">Active older goal</a></h2>", StringComparison.Ordinal));
    Assert.False(html.Contains($"<h2><a href=\"/goal/{oldestCompletedPrefix}\">Completed recent goal 0</a></h2>", StringComparison.Ordinal));
    Assert.Contains(html, text => text.Contains($"<option value=\"{oldestCompletedPrefix}\">Completed recent goal 0</option>", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "DashboardRenderer_surfaces_recoverable_subscription_limit_evidence")]
    public void DashboardRendererSurfacesRecoverableSubscriptionLimitEvidence()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Show retry later");
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var now = DateTimeOffset.UtcNow;
    var retryTime = now.AddHours(1);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", "C:\\repo", now));
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "codex exec",
        "C:\\repo",
        1,
        string.Empty,
        $"ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at {retryTime:h:mm tt}.",
        now));

    var goalPrefix = goal.Id.Value[..8];

    // Goal detail view shows evidence and retry queue
    var goalHtml = DashboardRenderer.Render(kernel, new DashboardRenderOptions(
        EnableOperatorControls: true,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix));

    Assert.Contains(goalHtml, text => text.Contains("Retry later", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Recoverable subscription usage limit", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("task Assigned", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Subscription retry queue", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Subscription handoff is paused", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Retry after", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("1 limit failure", StringComparison.Ordinal));
    Assert.Contains(goalHtml, text => text.Contains("Developer", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "DashboardRenderer_html_encodes_dynamic_content")]
    public void DashboardRendererHtmlEncodesDynamicContent()
{
    var kernel = new AgentOrchestratorKernel();
    kernel.CreateGoal("<script>alert(1)</script>");

    var html = DashboardRenderer.Render(kernel);

    Assert.Contains(html, text => text.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", StringComparison.Ordinal));
    Assert.False(html.Contains("<script>alert(1)</script>", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "GoalTranscriptRenderer_renders_status_actions_and_evidence")]
    public void GoalTranscriptRendererRendersStatusActionsAndEvidence()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Export transcript");
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var inputTask = goal.Tasks.First(task => task.RequiredRole == AgentRole.Planner);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var request = kernel.RequestHumanInput(goal.Id, inputTask.Id, "Which branch?");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "dotnet test", "C:\\repo", DateTimeOffset.UtcNow));
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("provider-smoke openai", "C:\\repo", 0, "OpenAI: ok", "", DateTimeOffset.UtcNow));
    var testerTask = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
    kernel.RecordTaskVerification(goal.Id, testerTask.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, "", "failed", DateTimeOffset.UtcNow));

    var transcript = GoalTranscriptRenderer.Render(kernel, goal);

    Assert.Contains(transcript, text => text.Contains("# Goal", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("Objective: Export transcript", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("## Recommended Next Steps", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("## Goal Completion", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("Accepted: False", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("## Recorded Proof", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("dispatch: 1", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("## Task Readiness", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("Ready for acceptance: False", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("## Verification Status", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("## Human Decisions", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("Which branch?", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains($"Suggested command: answer {request.Id.Value[..8]} <answer>", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("## Verification To-Do", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("Suggested command: task 1", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("retry 4 <note>", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("provider-smoke openai", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains($"Verification plan: {task.VerificationPlan}", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("OpenAI: ok", StringComparison.Ordinal));
    Assert.Contains(transcript, text => text.Contains("Task timeline", StringComparison.Ordinal));

    var readyKernel = new AgentOrchestratorKernel();
    var readyGoal = readyKernel.CreateGoal("Ready transcript");
    var readyAgents = AgentCatalog.Default().Agents;
    readyKernel.ActivateGoal(readyGoal.Id, readyAgents);
    var readyTranscript = GoalTranscriptRenderer.Render(readyKernel, readyGoal);
    Assert.Contains(readyTranscript, text => text.Contains("Suggested command: run", StringComparison.Ordinal));
    Assert.False(readyTranscript.Contains("subscription-dispatch 1 | api-run 1", StringComparison.Ordinal));
    var costAwareTranscript = GoalTranscriptRenderer.Render(readyKernel, readyGoal, readyAgents);
    var nextSteps = costAwareTranscript[..costAwareTranscript.IndexOf("## Needs Attention", StringComparison.Ordinal)];
    Assert.Contains(nextSteps, text => text.Contains("Suggested command: subscription-dispatch 1", StringComparison.Ordinal));
    Assert.False(nextSteps.Contains("Suggested command: run 1", StringComparison.Ordinal));
}

private static void AssertOpenAiModelOrderIsCostAware(string text)
{
    var miniIndex = text.IndexOf("gpt-5.4-mini", StringComparison.Ordinal);
    var expensiveIndex = text.IndexOf("gpt-5.5", StringComparison.Ordinal);

    Assert.True(miniIndex >= 0);
    Assert.True(expensiveIndex >= 0);
    Assert.True(miniIndex < expensiveIndex);
}
}
