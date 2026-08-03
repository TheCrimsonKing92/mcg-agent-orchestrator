using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Dashboard.Rendering;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class DashboardRenderingTests
{
    [Xunit.Fact(DisplayName = "DashboardRenderer_emits_mobile_responsive_shell")]
    public void DashboardRendererEmitsMobileResponsiveShell()
    {
        var html = DashboardRenderer.Render(new AgentOrchestratorKernel());

        Assert.True(html.Contains("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">", StringComparison.Ordinal));
        Assert.True(html.Contains("@media (max-width:900px)", StringComparison.Ordinal));
        Assert.True(html.Contains(".dashboard-nav{flex-wrap:wrap;padding:0}", StringComparison.Ordinal));
        Assert.True(html.Contains("min-height:44px", StringComparison.Ordinal));
        Assert.True(html.Contains("section{overflow-x:auto}", StringComparison.Ordinal));
        Assert.True(html.Contains("<nav class=\"dashboard-nav\">", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "DashboardMonitoringEvents_renders_SSE_tick_payload_from_run_event_store")]
    public async Task DashboardMonitoringEventsRendersSseTickPayloadFromRunEventStore()
{
    var root = CreateTempDirectory();
    var db = Path.Combine(root, "run-events.db");
    ConductorTickPusher.TryRecord(
        db,
        new BatchTickSummary(4, Advanced: 1, Held: 0, Escalated: 0, Retried: 0, Done: 1, WatchSleeping: false)
        {
            ProgressLines = ["GOAL goal=abc12345 result=done state=Complete"],
            OperatorDispositions =
            [
                new ConductorOperatorDispositionSnapshot(
                    "abc12345",
                    OperatorDispositionState.Wait,
                    OperatorDispositionConfidence.High,
                    "conductor-owned wait",
                    "wait",
                    DateTimeOffset.Parse("2026-07-03T12:00:00Z"),
                    [],
                    [new ConductorOperatorEvidenceSnapshot("heartbeat", "logs/worker.heartbeat.json", "running")],
                    [])
            ]
        });

    var records = await new SqliteRunEventStore(db).ReadSinceAsync();
    var tick = Assert.Single(DashboardMonitoringEvents.BuildConductorTickEvents(records));
    Assert.Null(tick.OperatorDispositions);

    using var stream = new MemoryStream();
    await DashboardMonitoringEvents.WriteServerSentEventAsync(
        stream,
        DashboardMonitoringEvents.ConductorTickEventName,
        tick,
        $"run-{tick.Seq}",
        CancellationToken.None);
    await DashboardMonitoringEvents.WriteServerSentEventAsync(
        stream,
        DashboardMonitoringEvents.ConductorProgressEventName,
        new ConductorProgressLineEventDto("GOAL goal=abc12345 result=done state=Complete"),
        id: null,
        CancellationToken.None);
    var sse = Encoding.UTF8.GetString(stream.ToArray());

    Assert.True(sse.Contains("id: run-", StringComparison.Ordinal));
    Assert.True(sse.Contains("event: conductor.tick", StringComparison.Ordinal));
    Assert.True(sse.Contains("\"Tick\": 4", StringComparison.Ordinal));
    Assert.DoesNotContain("conductor-owned wait", sse, StringComparison.Ordinal);
    Assert.True(sse.Contains("event: conductor.progress", StringComparison.Ordinal));
    Assert.True(sse.Contains("\"Line\": \"GOAL goal=abc12345 result=done state=Complete\"", StringComparison.Ordinal));
    Assert.True(sse.Contains("GOAL goal=abc12345 result=done state=Complete", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_and_work_summary_surface_operator_intent_audit_outcome")]
    public async Task DashboardRendererAndWorkSummarySurfaceOperatorIntentAuditOutcome()
    {
        var root = CreateTempDirectory();
        var orchestratorDirectory = Path.Combine(root, ".orchestrator");
        var logDirectory = Path.Combine(orchestratorDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Render operator intent evidence");
        var task = goal.Tasks.Single();
        var store = SqliteOperatorIntentStore.ForDirectories(orchestratorDirectory, logDirectory);
        var intent = new OperatorIntentRecord(
            "intent-dashboard",
            "intent-dashboard-key",
            OperatorIntentVerbs.Retry,
            goal.Id.Value,
            task.Id.Value,
            JsonSerializer.Serialize(
                new RetryOperatorIntentPayload("dashboard retry", null),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            [],
            "miles",
            "dashboard",
            "dashboard-operator-control",
            DateTimeOffset.UtcNow);
        await store.EnqueueAsync(intent);
        await store.ClaimNextAsync(goal.Id.Value, "test-conductor");
        await store.CompleteAsync(
            intent.Id,
            "test-conductor",
            OperatorIntentStatus.Applied,
            "Applied retry in tick 4.",
            DateTimeOffset.UtcNow);
        var persisted = await store.ListForGoalAsync(goal.Id.Value);
        var workspace = new DashboardWorkspaceContext(
            root,
            Path.Combine(orchestratorDirectory, "state.db"),
            root,
            Path.Combine(orchestratorDirectory, "prompts"),
            logDirectory,
            Path.Combine(orchestratorDirectory, "workers.json"),
            Path.Combine(orchestratorDirectory, "agents.json"),
            12345);

        var html = DashboardRenderer.Render(
            kernel,
            RenderOptions(
                View: DashboardView.Goal,
                FocusGoalPrefix: goal.Id.Value[..8],
                Workspace: workspace));
        var dto = DashboardResponseMapper.ToGoalWorkSummaryDto(
            kernel,
            goal,
            WorkerProfileCatalog.Default(),
            operatorIntents: persisted);

        Assert.Contains("Operator intent evidence", html, StringComparison.Ordinal);
        Assert.Contains("intent-dashboard", html, StringComparison.Ordinal);
        Assert.Contains("actor=miles; channel=dashboard; auth=dashboard-operator-control", html, StringComparison.Ordinal);
        Assert.Contains("Applied retry in tick 4.", html, StringComparison.Ordinal);
        var dtoIntent = Assert.Single(dto.OperatorIntents!);
        Assert.Equal(OperatorIntentStatus.Applied, dtoIntent.Status);
        Assert.Equal("Applied retry in tick 4.", dtoIntent.Outcome);
    }

    [Xunit.Theory(DisplayName = "Dashboard_recovery_actions_route_outside_state_mutation_transaction")]
    [Xunit.InlineData(OperatorIntentVerbs.Retry, true)]
    [Xunit.InlineData(OperatorIntentVerbs.Progress, true)]
    [Xunit.InlineData(OperatorIntentVerbs.VerifyManual, true)]
    [Xunit.InlineData("complete-verify", false)]
    public void DashboardRecoveryActionsRouteOutsideStateMutationTransaction(string operation, bool expected)
    {
        Assert.Equal(expected, GoalManagementCommandService.IsInboxBackedTaskAction(operation));
    }

    [Xunit.Fact(DisplayName = "Dashboard_recovery_actions_always_use_inbox")]
    public async Task DashboardRecoveryActionsAlwaysUseInbox()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var agents = (IReadOnlyList<AgentDefinition>)AgentCatalog.Default().Agents;
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, agents, "Dashboard recovery routing");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
            var providers = new InMemoryModelProviderRegistry([]);

            var queuedRetry = await GoalManagementCommandService.ApplyTaskActionAsync(
                kernel,
                agents,
                providers,
                workspace,
                goal,
                task,
                "retry",
                "retry while loop is down");

            var retryDto = Assert.IsType<OperatorIntentDto>(queuedRetry);
            Assert.Equal(OperatorIntentStatus.Pending, retryDto.Status);
            Assert.Equal(
                "WARNING: intent queued but NO conduct loop is running - it will not apply until a loop starts.",
                retryDto.Warning);
            Assert.Equal(WorkTaskStatus.Failed, task.Status);
            Assert.True(File.Exists(Path.Combine(
                workspace.OrchestratorDirectory,
                SqliteOperatorIntentStore.DatabaseFileName)));

            object? queuedProgress;
            using (ConductorLoopLease.Acquire(workspace.OrchestratorDirectory))
            {
                queuedProgress = await GoalManagementCommandService.ApplyTaskActionAsync(
                    kernel,
                    agents,
                    providers,
                    workspace,
                    goal,
                    task,
                    "progress",
                    """{"status":"completed","message":"verified while loop runs"}""");
            }

            var progressDto = Assert.IsType<OperatorIntentDto>(queuedProgress);
            Assert.Equal(OperatorIntentStatus.Pending, progressDto.Status);
            Assert.Null(progressDto.Warning);
            Assert.Equal(WorkTaskStatus.Failed, task.Status);
            var intents = await SqliteOperatorIntentStore
                .OpenExisting(workspace.OrchestratorDirectory, workspace.LogDirectory)
                .ListForGoalAsync(goal.Id.Value);
            Assert.Equal(2, intents.Count);
            Assert.Contains(intents, intent => intent.Verb == OperatorIntentVerbs.Retry);
            Assert.Contains(intents, intent => intent.Verb == OperatorIntentVerbs.Progress);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

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
    var html = DashboardRenderer.Render(kernel, RenderOptions(View: DashboardView.Goal, FocusGoalPrefix: goalPrefix));

    Assert.Contains("Render dashboard", html, StringComparison.Ordinal);
    Assert.Contains("Running dispatch", html, StringComparison.Ordinal);
    Assert.Contains("dotnet test", html, StringComparison.Ordinal);
    Assert.Contains("Recommended next steps", html, StringComparison.Ordinal);
    Assert.Contains("execute-dispatch 3 --confirm-dispatch-start", html, StringComparison.Ordinal);
    Assert.False(html.Contains("data-next-action=", StringComparison.Ordinal));
    Assert.Contains("Goal completion", html, StringComparison.Ordinal);
    Assert.Contains("Accepted: False", html, StringComparison.Ordinal);
    Assert.Contains("Recorded proof", html, StringComparison.Ordinal);
    Assert.Contains("Dispatch: 1", html, StringComparison.Ordinal);
    Assert.Contains("Task readiness", html, StringComparison.Ordinal);
    Assert.Contains("In progress", html, StringComparison.Ordinal);
    Assert.Contains("Verification status", html, StringComparison.Ordinal);
    Assert.Contains("<th>Gate</th>", html, StringComparison.Ordinal);
    Assert.Contains("Not ready", html, StringComparison.Ordinal);
    Assert.Contains("complete the task before accepting this gate", html, StringComparison.Ordinal);
    Assert.Contains("Verification to-do", html, StringComparison.Ordinal);
    Assert.Contains("Complete the task before recording final verification", html, StringComparison.Ordinal);
    Assert.Contains("<code>task 3</code>", html, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_renders_operator_inbox_with_ack_control")]
    public void DashboardRendererRendersOperatorInboxWithAckControl()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Render operator inbox", [new TaskSpec(TaskId.New(), "Fix blocked task", AgentRole.Developer)]);
    var goalPrefix = goal.Id.Value[..8];
    var report = new OperatorInboxReportDto(
        goalPrefix,
        TotalCount: 1,
        OpenCount: 1,
        AcknowledgedCount: 0,
        [
            new OperatorInboxItemDto(
                "inbox-test123",
                OperatorInboxKind.FailedTask,
                OperatorInboxSeverity.Blocker,
                goal.Id.Value,
                goalPrefix,
                goal.Objective,
                goal.Tasks.Single().Id.Value,
                1,
                "Failed task on task 1",
                "Worker failed before producing evidence.",
                "monitor: failed task",
                "Inspect the failure.",
                $"next {goalPrefix}",
                "test",
                Acknowledged: false,
                AcknowledgedAt: null,
                AcknowledgementNote: null)
        ]);

    var html = DashboardRenderer.Render(kernel, RenderOptions(
        EnableOperatorControls: true,
        OperatorInbox: report));

    Assert.Contains("Operator inbox", html, StringComparison.Ordinal);
    Assert.Contains("inbox-test123", html, StringComparison.Ordinal);
    Assert.Contains($"next {goalPrefix}", html, StringComparison.Ordinal);
    Assert.Contains("/api/operator-inbox/ack?itemId=inbox-test123", html, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_renders_worker_result_skill_usage")]
    public void DashboardRendererRendersWorkerResultSkillUsage()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Render skill evidence", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.Single();
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "worker refresh",
        "C:\\repo",
        0,
        """
        WORKER_RESULT:
        files: src/Feature.cs
        commands: dotnet test
        tests: Passed: 1
        commit: abc123
        blockers: none
        model_fit: OpenAI/gpt-5.5 - adequate - test worker fixture.
        skills: dotnet-windows-build-hygiene, orchestrator-dogfood
        confidence: high
        END_WORKER_RESULT
        """,
        string.Empty,
        DateTimeOffset.UtcNow));

    var html = DashboardRenderer.Render(kernel, RenderOptions(View: DashboardView.Goal, FocusGoalPrefix: goal.Id.Value[..8]));

    Assert.Contains("Skills: dotnet-windows-build-hygiene, orchestrator-dogfood", html, StringComparison.Ordinal);
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
    var html = DashboardRenderer.Render(kernel, RenderOptions(EnableOperatorControls: true, View: DashboardView.Goal, FocusGoalPrefix: goalPrefix));
    var taskDto = DashboardResponseMapper.ToTaskDetailDto(goal, task);
    var evidenceDto = DashboardResponseMapper.ToGoalEvidenceSummaryDto(goal, kernel.BuildGoalEvidenceSummary(goal.Id));
    var transcript = GoalTranscriptRenderer.Render(kernel, goal, WorkerProfileCatalog.Default());
    var promptChars = task.LastExecution!.PromptCharacterCount!.Value;

    Assert.Contains("Model: OpenAI/gpt-test by API developer", html, StringComparison.Ordinal);
    Assert.Contains("tokens 1 in / 2 out", html, StringComparison.Ordinal);
    Assert.Contains($"prompt {promptChars} chars", html, StringComparison.Ordinal);
    Assert.Contains("Tokens: 1 in / 2 out", html, StringComparison.Ordinal);
    Assert.Contains("Potentially paid: 1 in / 2 out", html, StringComparison.Ordinal);
    Assert.Contains($"Model usage: OpenAI/gpt-test (Simple) [potentially paid]: 1 run, 1 in / 2 out, prompt {promptChars} chars", html, StringComparison.Ordinal);
    Assert.Contains("stop reason stop", html, StringComparison.Ordinal);
    Assert.Contains("<pre>OK</pre>", html, StringComparison.Ordinal);
    Assert.Contains("Model fit: OpenAI/gpt-test - adequate|overkill|underpowered - task shape - short reason.", html, StringComparison.Ordinal);
    Assert.Contains("Tokens: 1 in / 2 out", transcript, StringComparison.Ordinal);
    Assert.Contains("Potentially paid tokens: 1 in / 2 out", transcript, StringComparison.Ordinal);
    Assert.Contains("Model selection: complexity=Simple", transcript, StringComparison.Ordinal);
    Assert.Contains($"Prompt size: {promptChars} chars", transcript, StringComparison.Ordinal);
    Assert.Contains("Model usage:", transcript, StringComparison.Ordinal);
    Assert.Contains($"- OpenAI/gpt-test (Simple) [potentially paid]: 1 run, 1 in / 2 out, prompt {promptChars} chars", transcript, StringComparison.Ordinal);
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
        "Evidence checked.\nModel fit: OpenAI/gpt-5.3-codex - adequate - focused implementation.",
        string.Empty,
        DateTimeOffset.UtcNow));
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
        "manual-verification passed",
        "C:\\repo",
        0,
        "Evidence checked.\nModel fit: openai/GPT-5.3-CODEX - underpowered - missed required tests.",
        string.Empty,
        DateTimeOffset.UtcNow));

    var evidenceDto = DashboardResponseMapper.ToGoalEvidenceSummaryDto(goal, kernel.BuildGoalEvidenceSummary(goal.Id));
    var transcript = GoalTranscriptRenderer.Render(kernel, goal, WorkerProfileCatalog.Default());
    var html = DashboardRenderer.Render(kernel, RenderOptions(View: DashboardView.Goal, FocusGoalPrefix: goal.Id.Value[..8]));
    var taskEvidence = evidenceDto.Tasks.Single(item => item.TaskId == task.Id.Value);
    var modelFit = evidenceDto.ModelFit.Single();

    Assert.Equal("Model fit: openai/GPT-5.3-CODEX - underpowered - missed required tests.", taskEvidence.ModelFitNote);
    Assert.Equal("OpenAI", modelFit.ProviderName);
    Assert.Equal("gpt-5.3-codex", modelFit.ModelName);
    Assert.Equal(2, modelFit.NoteCount);
    Assert.Equal(1, modelFit.AdequateCount);
    Assert.Equal(0, modelFit.OverkillCount);
    Assert.Equal(1, modelFit.UnderpoweredCount);
    Assert.Equal(0, modelFit.UnknownCount);
    Assert.True(modelFit.TaskShapes!.Contains("focused implementation"));
    Assert.True(modelFit.TaskShapes!.Contains("missed required tests"));
    Assert.Contains("Model fit: OpenAI/gpt-5.3-codex: 2 notes; adequate 1, underpowered 1; shapes focused implementation, missed required tests", html, StringComparison.Ordinal);
    Assert.Contains("- OpenAI/gpt-5.3-codex: 2 notes; adequate 1, underpowered 1; shapes focused implementation, missed required tests", transcript, StringComparison.Ordinal);
    Assert.Contains("Model fit: openai/GPT-5.3-CODEX - underpowered - missed required tests.", transcript, StringComparison.Ordinal);
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
        RenderOptions(
            EnableOperatorControls: true,
            View: DashboardView.Goal,
            FocusGoalPrefix: goal.Id.Value[..8],
            AgentDefinitions: [agent],
            WorkerProfiles: WorkerProfileCatalog.Default()));

    Assert.Contains("prior overkill model", html, StringComparison.Ordinal);
    Assert.Contains("OpenAI/gpt-5-mini Simple reasoning low", html, StringComparison.Ordinal);
    Assert.Contains("prior fit 1: overkill 1", html, StringComparison.Ordinal);
    Assert.Contains("shapes copy-only change", html, StringComparison.Ordinal);
    Assert.Contains("try local Ollama/qwen3:8b via agent configuration before paid start", html, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goal.Id.Value[..8]}/start-subscription-ready?confirmBatchStart=true", html, StringComparison.Ordinal);
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
    var html = DashboardRenderer.Render(kernel, RenderOptions(View: DashboardView.Goal, FocusGoalPrefix: goalPrefix));
    var evidenceDto = DashboardResponseMapper.ToGoalEvidenceSummaryDto(goal, kernel.BuildGoalEvidenceSummary(goal.Id));
    var taskDto = DashboardResponseMapper.ToTaskDetailDto(goal, task);
    var transcript = GoalTranscriptRenderer.Render(kernel, goal, WorkerProfileCatalog.Default());

    Assert.Contains("max 2 out", html, StringComparison.Ordinal);
    Assert.Contains("possible output cap hit", html, StringComparison.Ordinal);
    Assert.Contains("cap hits 1 of 2", html, StringComparison.Ordinal);
    Assert.Contains("maxOutput=2", transcript, StringComparison.Ordinal);
    Assert.Contains("Model note: possible output token cap hit.", transcript, StringComparison.Ordinal);
    Assert.Contains("cap hits 1 of 2", transcript, StringComparison.Ordinal);
    Assert.Equal(2, taskDto.LastExecution!.MaxOutputTokens);
    Assert.True(taskDto.LastExecution.OutputTokenLimitHit);
    Assert.Contains("possible output cap hit at 2 tokens", evidenceDto.Tasks.Single(item => item.TaskId == task.Id.Value).Message, StringComparison.Ordinal);
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
    var html = DashboardRenderer.Render(kernel, RenderOptions(View: DashboardView.Goal, FocusGoalPrefix: goalPrefix));
    var transcript = GoalTranscriptRenderer.Render(kernel, goal, WorkerProfileCatalog.Default());

    Assert.True(dto.LastExecution is not null);
    Assert.True(dto.LastExecution!.OutputTruncated);
    Assert.Equal(output.Length, dto.LastExecution.OutputLength);
    Assert.Contains("execution-start", dto.LastExecution.Output, StringComparison.Ordinal);
    Assert.Contains("execution-tail", dto.LastExecution.Output, StringComparison.Ordinal);
    Assert.Contains("[truncated", dto.LastExecution.Output, StringComparison.Ordinal);
    Assert.True(dto.LastExecution.Output.Length < output.Length);
    Assert.Contains("execution-start", html, StringComparison.Ordinal);
    Assert.Contains("execution-tail", html, StringComparison.Ordinal);
    Assert.Contains("[truncated", html, StringComparison.Ordinal);
    Assert.True(!html.Contains(new string('o', 6000), StringComparison.Ordinal));
    Assert.Contains("execution-start", transcript, StringComparison.Ordinal);
    Assert.Contains("execution-tail", transcript, StringComparison.Ordinal);
    Assert.Contains("[truncated", transcript, StringComparison.Ordinal);
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
    Assert.Contains("stdout-start", verification.StandardOutput, StringComparison.Ordinal);
    Assert.Contains("stdout-tail", verification.StandardOutput, StringComparison.Ordinal);
    Assert.Contains("[truncated", verification.StandardOutput, StringComparison.Ordinal);
    Assert.Contains("stderr-start", verification.StandardError, StringComparison.Ordinal);
    Assert.Contains("stderr-tail", verification.StandardError, StringComparison.Ordinal);
    Assert.Contains("[truncated", verification.StandardError, StringComparison.Ordinal);
    Assert.True(verification.StandardOutput.Length < stdout.Length);
    Assert.True(verification.StandardError.Length < stderr.Length);
    var transcript = GoalTranscriptRenderer.Render(kernel, goal, WorkerProfileCatalog.Default());
    Assert.Contains("stdout-start", transcript, StringComparison.Ordinal);
    Assert.Contains("stdout-tail", transcript, StringComparison.Ordinal);
    Assert.Contains("stderr-start", transcript, StringComparison.Ordinal);
    Assert.Contains("stderr-tail", transcript, StringComparison.Ordinal);
    Assert.Contains("[truncated", transcript, StringComparison.Ordinal);
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
    var nextActions = DashboardResponseMapper.ToNextActionsDto(goal, kernel.BuildNextActions(goal.Id), WorkerProfileCatalog.Default());
    var humanInput = DashboardResponseMapper.ToHumanInputWorklistDto(goal, kernel.BuildHumanInputWorklist(goal.Id));
    var gate = DashboardResponseMapper.ToVerificationGateDto(goal, kernel.BuildVerificationGate(goal.Id));

    Assert.Contains("objective-start", goalDetail.Goal.Objective, StringComparison.Ordinal);
    Assert.Contains("objective-tail", goalDetail.Goal.Objective, StringComparison.Ordinal);
    Assert.Contains("[truncated", goalDetail.Goal.Objective, StringComparison.Ordinal);
    Assert.Contains("description-start", goalDetail.Tasks.Single().Description, StringComparison.Ordinal);
    Assert.Contains("description-tail", goalDetail.Tasks.Single().Description, StringComparison.Ordinal);
    Assert.True(monitor.Attention.Count > 0);
    Assert.Contains("description-tail", evidence.Tasks.Single().Description, StringComparison.Ordinal);
    Assert.Contains("objective-tail", nextActions.Objective, StringComparison.Ordinal);
    Assert.Contains("question-tail", humanInput.Items.Single().Question, StringComparison.Ordinal);
    Assert.Contains("description-tail", gate.Tasks.Single().Description, StringComparison.Ordinal);
    Assert.True(!goalDetail.Goal.Objective.Contains(new string('o', 2000), StringComparison.Ordinal));
    Assert.True(!goalDetail.Tasks.Single().Description.Contains(new string('d', 2000), StringComparison.Ordinal));
    Assert.True(!humanInput.Items.Single().Question.Contains(new string('q', 2000), StringComparison.Ordinal));
    Assert.Equal(objective, goal.Objective);
    Assert.Equal(description, task.Description);
    Assert.Equal(question, kernel.HumanInputRequests.Single().Question);
    Assert.Equal(message, goal.Timeline.Single(evt => evt.Message.Contains("message-start", StringComparison.Ordinal)).Message);
}

    [Xunit.Fact(DisplayName = "DashboardResponseMapper_projects_unintegrated_completed_goal_as_verified")]
    public void DashboardResponseMapperProjectsUnintegratedCompletedGoalAsVerified()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Legacy completed before cleanup facts", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, [Agent(AgentRole.Developer, AgentExecutionPolicy.ApiOnly)]);
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "passed", "", DateTimeOffset.UtcNow));
    kernel.CompleteGoal(goal.Id, "Legacy completion.");

    var monitor = DashboardResponseMapper.ToMonitorDto(kernel.BuildMonitor(goal.Id));
    var acceptance = DashboardResponseMapper.ToGoalAcceptanceSummaryDto(goal, kernel.BuildGoalAcceptanceSummary(goal.Id));
    var workSummary = DashboardResponseMapper.ToGoalWorkSummaryDto(kernel, goal, WorkerProfileCatalog.Default());
    var detail = DashboardResponseMapper.ToGoalDetailDto(kernel, goal);
    var monitoringBatch = DashboardMonitoringEvents.BuildBatch(kernel, goal, sinceEventId: 0);

    Assert.Equal(GoalStatus.Completed, goal.Status);
    Assert.Equal(GoalStatus.Verified, monitor.Status);
    Assert.Equal(GoalStatus.Verified, acceptance.Status);
    Assert.Equal(GoalStatus.Verified, workSummary.Status);
    Assert.Equal(GoalStatus.Verified, detail.Goal.Status);
    Assert.Equal(GoalStatus.Verified, monitoringBatch.Snapshot.Monitor.Status);
    Assert.Equal(OperatorDispositionState.Accept, workSummary.OperatorDisposition.State);
    Assert.Equal($"acceptance {goal.Id.Value[..8]}", workSummary.OperatorDisposition.NextSafeCommand);
}

    [Xunit.Fact(DisplayName = "DashboardResponseMapper_surfaces_cleaned_goal_status_text")]
    public void DashboardResponseMapperSurfacesCleanedGoalStatusText()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Manual acceptance cleaned", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, [Agent(AgentRole.Developer, AgentExecutionPolicy.ApiOnly)]);
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("manual", root, 0, "passed", "", DateTimeOffset.UtcNow));
    GoalOperationJournal.Completed(root, goal, "acceptance", "Acceptance passed and merge completed.");
    GoalOperationJournal.Completed(root, goal, "workspace:remove", "Workspace removed.");
    kernel.CompleteGoal(goal.Id, "Manual acceptance completed after cleanup evidence.");
    var workspace = OrchestratorWorkspace.ForDirectory(root);

    var workSummary = DashboardResponseMapper.ToGoalWorkSummaryDto(
        kernel,
        goal,
        WorkerProfileCatalog.Default(),
        executionDirectory: root);
    var summary = DashboardResponseMapper.ToGoalSummary(goal, root);
    var detail = DashboardResponseMapper.ToGoalDetailDto(kernel, goal, root);
    var streamBatch = GoalMonitoringStream.BuildBatch(
        kernel,
        goal,
        sinceEventId: 0,
        [Agent(AgentRole.Developer, AgentExecutionPolicy.ApiOnly)],
        WorkerProfileCatalog.Default(),
        workspace);

    Assert.Equal(GoalStatus.Completed, goal.Status);
    Assert.Equal(GoalStatus.Completed, workSummary.Status);
    Assert.Equal(GoalStatus.Completed, summary.Status);
    Assert.Equal(GoalStatus.Completed, detail.Goal.Status);
    Assert.Equal(GoalStatus.Completed, streamBatch.Snapshot.Monitor.Status);
    Assert.Equal("CleanedUp", workSummary.StatusText);
    Assert.Equal("CleanedUp", summary.StatusText);
    Assert.Equal("CleanedUp", detail.Goal.StatusText);
    Assert.Equal("CleanedUp", streamBatch.Snapshot.Monitor.StatusText);
}

    [Xunit.Fact(DisplayName = "DashboardResponseMapper_next_action_includes_dispatch_recovery_policy_action")]
    public void DashboardResponseMapperNextActionIncludesDispatchRecoveryPolicyAction()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Next recovery policy",
            [new TaskSpec(TaskId.New(), "Refresh interrupted worker", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        var stdout = Path.Combine(root, "out.log");
        var stderr = Path.Combine(root, "err.log");
        var exit = Path.Combine(root, "exit.txt");
        File.WriteAllText(stdout, string.Empty);
        File.WriteAllText(stderr, string.Empty);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", root, DateTimeOffset.UtcNow));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(999999, "codex exec prompt.md", root, stdout, stderr, exit, DateTimeOffset.UtcNow, null, null));

        var dto = DashboardResponseMapper.ToNextActionsDto(goal, kernel.BuildNextActions(goal.Id), WorkerProfileCatalog.Default());
        var recovery = dto.Items.Single().Recovery;
        var dispatchState = dto.Items.Single().DispatchState;

        Assert.NotNull(recovery);
        Assert.Equal(DispatchRecoveryAction.MarkStale, recovery!.Action);
        Assert.Equal("mark-stale", recovery.ActionName);
        Assert.Equal("heartbeat-absent", recovery.EvidencePath);
        Assert.Equal(OperatorDispositionState.Recover, dto.OperatorDisposition.State);
        Assert.Equal("goal-recovery apply 1 --action mark-stale", dto.OperatorDisposition.NextSafeCommand);
        Assert.Contains(dto.OperatorDisposition.Evidence, pointer => pointer.Kind == "exit-code");
        Assert.NotNull(dispatchState);
        Assert.Equal(DispatchStateKind.StaleCleanup, dispatchState!.Kind);
        Assert.Equal("mark-stale", dispatchState.RecommendedAction);
        Assert.Equal(999999, dispatchState.ProcessTree.WrapperProcessId);
        Assert.False(dispatchState.Artifacts.ExitCodeExists);
        Assert.Equal(DispatchRecoveryAction.MarkStale, dispatchState.RecoveryDecision.Action);
        Assert.True(dispatchState.StaleThresholds.LiveIdleTimeoutSeconds > 0);
    }

    [Xunit.Fact(DisplayName = "DashboardResponseMapper_uses_conductor_disposition_snapshot_for_next_actions")]
    public void DashboardResponseMapperUsesConductorDispositionSnapshotForNextActions()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Use conductor disposition",
            [new TaskSpec(TaskId.New(), "Await real human request", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        goal = kernel.GetGoal(goal.Id);

        var conductorDisposition = new GoalOperatorDisposition(
            goal.Id,
            OperatorDispositionState.Wait,
            OperatorDispositionConfidence.High,
            "conductor emitted wait from run-event state",
            "wait",
            DateTimeOffset.Parse("2026-07-03T12:05:00Z"),
            ["owned-by-conductor"],
            [new OperatorEvidencePointer("run-event", "run-events.db", "conductor.tick")],
            []);

        var dto = DashboardResponseMapper.ToNextActionsDto(
            goal,
            kernel.BuildNextActions(goal.Id),
            WorkerProfileCatalog.Default(),
            conductorDisposition: conductorDisposition);

        Assert.Equal(OperatorDispositionState.Wait, dto.OperatorDisposition.State);
        Assert.Equal("conductor emitted wait from run-event state", dto.OperatorDisposition.Reason);
        Assert.Contains(dto.OperatorDisposition.Blockers, blocker => blocker == "owned-by-conductor");
    }

    [Xunit.Fact(DisplayName = "Dashboard_human_wait_dto_and_rendering_include_operator_evidence")]
    public void DashboardHumanWaitDtoAndRenderingIncludeOperatorEvidence()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Expose typed wait",
            [new TaskSpec(TaskId.New(), "Authenticate provider", AgentRole.Developer)]);
        var agent = new AgentDefinition(
            AgentId.New(),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        var request = kernel.RequestHumanInput(
            goal.Id,
            task.Id,
            "Complete OAuth.",
            HumanWaitKind.ProviderAuth,
            resumeCommand: "provider auth resume");

        var dto = DashboardResponseMapper.ToHumanInputDto(kernel, request);
        var worklist = DashboardResponseMapper.ToHumanInputWorklistDto(goal, kernel.BuildHumanInputWorklist(goal.Id));
        var html = DashboardRenderer.Render(
            kernel,
            RenderOptions(EnableOperatorControls: true, FocusGoalPrefix: goal.Id.Value[..8], View: DashboardView.Goal));

        Assert.Equal(request.Id.Value, dto.WaitId);
        Assert.Equal(HumanWaitKind.ProviderAuth, dto.Kind);
        Assert.True(dto.IsExternallyBlocked);
        Assert.False(dto.IsAutoDefaultable);
        Assert.False(dto.IsDismissible);
        Assert.Equal("provider auth resume", dto.ResumeCommand);
        Assert.Equal(1, dto.TotalRequestCount);
        Assert.Equal(1, dto.OpenRequestCount);
        var item = worklist.Items.Single();
        Assert.Equal(request.Id.Value, item.WaitId);
        Assert.Equal(goal.Id.Value, item.GoalId);
        Assert.Equal("provider auth resume", item.ResumeCommand);
        Assert.Equal(1, item.TotalRequestCount);
        Assert.Equal(1, item.OpenRequestCount);
        Assert.Contains("Requests: 1 open / 1 total", item.SuggestedAction, StringComparison.Ordinal);
        Assert.Contains("ProviderAuth", html, StringComparison.Ordinal);
        Assert.Contains("externally-blocked=True", html, StringComparison.Ordinal);
        Assert.Contains("provider auth resume", html, StringComparison.Ordinal);
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

    var plan = DashboardResponseMapper.ToSubscriptionPlanDto(
        SubscriptionPlanBuilder.Build(goal, [agent], WorkerProfileCatalog.Default()));
    var item = plan.Items.Single();

    Assert.False(item.CanPrepare);
    Assert.Contains("profile-start", item.Detail, StringComparison.Ordinal);
    Assert.Contains("profile-tail", item.Detail, StringComparison.Ordinal);
    Assert.Contains("[truncated", item.Detail, StringComparison.Ordinal);
    Assert.True(!item.Detail.Contains(new string('p', 2000), StringComparison.Ordinal));
    Assert.Equal(profileName, agent.Subscription!.WorkerProfileName);
    Assert.True(task.LastDispatch is null);
}

    [Xunit.Fact(DisplayName = "DashboardResponseMapper_exposes_subscription_plan_worker_route")]
    public void DashboardResponseMapperExposesSubscriptionPlanWorkerRoute()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Expose route evidence",
        [new TaskSpec(TaskId.New(), "Update a dashboard label.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("subscription-developer"),
        "Subscription developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);

    var plan = DashboardResponseMapper.ToSubscriptionPlanDto(
        SubscriptionPlanBuilder.Build(goal, [agent], WorkerProfileCatalog.Default(), _ => 1200));
    var item = plan.Items.Single();

    Xunit.Assert.NotNull(item.Route);
    Assert.Equal(WorkerRouteDisposition.Selected, item.Route!.Disposition);
    Xunit.Assert.Contains(item.Route.Reasons, text => text.Contains("provider=OpenAI", StringComparison.Ordinal));
    Xunit.Assert.Contains(item.Route.Reasons, text => text.Contains("estimated cost-guard prompt chars=1200", StringComparison.Ordinal));
    Xunit.Assert.Contains(item.Route.Alternatives, text => text.Contains("local Ollama/qwen", StringComparison.Ordinal));
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
    Assert.Contains("timeline-tail", detailEvent.Message, StringComparison.Ordinal);
    Assert.Contains("[truncated", detailEvent.Message, StringComparison.Ordinal);
    Assert.Contains("timeline-tail", timelineEvent.Message, StringComparison.Ordinal);
    Assert.Contains("[truncated", timelineEvent.Message, StringComparison.Ordinal);
    Assert.True(detailEvent.Message.Length < message.Length);
    Assert.True(timelineEvent.Message.Length < message.Length);
    Assert.Equal(message, goal.Timeline.Single(evt => evt.Message.Contains("timeline-start", StringComparison.Ordinal)).Message);
}

    [Xunit.Fact(DisplayName = "DashboardMonitoringEvents_builds_resumable_batches_and_sse_events")]
    public async Task DashboardMonitoringEventsBuildsResumableBatchesAndSseEvents()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Monitor goal state");
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var goalPrefix = goal.Id.Value[..8];
    var dispatchRoot = CreateTempDirectory();
    var stdout = Path.Combine(dispatchRoot, "developer.out.log");
    var stderr = Path.Combine(dispatchRoot, "developer.err.log");
    var exit = Path.Combine(dispatchRoot, "developer.exit.txt");
    File.WriteAllText(stdout, "worker output");
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", dispatchRoot, DateTimeOffset.UtcNow));
    var process = new TaskProcessRecord(333333, "codex exec prompt", dispatchRoot, stdout, stderr, exit, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    WriteHeartbeat(process, DateTimeOffset.UtcNow.AddMinutes(-45), DateTimeOffset.UtcNow.AddMinutes(-45), childPid: 444444, ownedPids: [333333, 444444]);
    var initialCursor = DashboardMonitoringEvents.BuildBatch(kernel, goal, 0).LastEventId;

    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Started monitoring work.");
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Finished monitoring work.");

    var inbox = new OperatorInboxReportDto(
        goalPrefix,
        TotalCount: 1,
        OpenCount: 1,
        AcknowledgedCount: 0,
        [
            new OperatorInboxItemDto(
                "inbox-monitor",
                OperatorInboxKind.MissingVerification,
                OperatorInboxSeverity.Blocker,
                goal.Id.Value,
                goalPrefix,
                goal.Objective,
                task.Id.Value,
                1,
                "Verify task 1",
                "Verification is missing.",
                "monitor snapshot",
                "Run verification.",
                "verify-needed " + goalPrefix,
                "test",
                Acknowledged: false,
                AcknowledgedAt: null,
                AcknowledgementNote: null)
        ]);
    var capacity = new ProviderCapacityScheduleDto(
        ProviderCapacityDisposition.Ready,
        "Start ready subscription work.",
        ReadyNowCount: 1,
        DeferredCount: 0,
        NextRetryAfter: null,
        HasCostRisk: false,
        [
            new ProviderCapacityActionDto(
                1,
                task.Id.Value,
                "OpenAI",
                ProviderCapacityDisposition.Ready,
                RetryAfter: null,
                "Ready to start.",
                [])
        ]);
    var batch = DashboardMonitoringEvents.BuildBatch(kernel, goal, initialCursor, inbox, capacity);
    var replay = DashboardMonitoringEvents.BuildBatch(kernel, goal, initialCursor + 1);
    var noNewEvents = DashboardMonitoringEvents.BuildBatch(kernel, goal, batch.LastEventId);

    Assert.Equal(goal.Id.Value, batch.GoalId);
    Assert.Equal(DashboardMonitoringEvents.StreamPath(goal.Id.Value), batch.StreamPath);
    Assert.Equal(batch.LastEventId, batch.Snapshot.LastEventId);
    var snapshotInbox = Xunit.Assert.IsType<OperatorInboxReportDto>(batch.Snapshot.OperatorInbox);
    Assert.Equal(1, snapshotInbox.OpenCount);
    Assert.Equal("inbox-monitor", snapshotInbox.Items.Single().Id);
    var snapshotCapacity = Xunit.Assert.IsType<ProviderCapacityScheduleDto>(batch.Snapshot.ProviderCapacity);
    Assert.Equal(ProviderCapacityDisposition.Ready, snapshotCapacity.Disposition);
    Assert.Equal(1, snapshotCapacity.ReadyNowCount);
    Assert.Equal(2, batch.Events.Count);
    Assert.Equal("timeline", batch.Events[0].Event);
    Assert.Equal("Started monitoring work.", batch.Events[0].Message);
    Assert.Equal(task.Id.Value, batch.Events[0].TaskId);
    Assert.Equal(AgentRole.Developer, batch.Events[0].Role);
    Assert.Equal(WorkTaskStatus.Completed, batch.Snapshot.Tasks.Single(item => item.TaskId == task.Id.Value).Status);
    Xunit.Assert.Single(replay.Events);
    Assert.Equal("Finished monitoring work.", replay.Events.Single().Message);
    Xunit.Assert.Empty(noNewEvents.Events);

    using var stream = new MemoryStream();
    await DashboardMonitoringEvents.WriteServerSentEventAsync(
        stream,
        batch.Events[0].Event,
        batch.Events[0],
        DashboardMonitoringEvents.FormatEventId(batch.Events[0].Id),
        CancellationToken.None);
    var text = Encoding.UTF8.GetString(stream.ToArray());

    Xunit.Assert.Contains($"id: {batch.Events[0].Id}", text);
    Xunit.Assert.Contains("event: timeline", text);
    Xunit.Assert.Contains("data:", text);
    Xunit.Assert.Contains("Started monitoring work.", text);

    var taskStatusEvents = DashboardMonitoringEvents.BuildTaskStatusEvents(batch);
    Assert.True(taskStatusEvents.Count >= 2);
    var taskStatus = taskStatusEvents.First(evt => evt.TaskId == task.Id.Value);
    var sourceEvent = batch.Events.First(evt => evt.TaskId == task.Id.Value);
    Assert.Equal(goal.Id.Value, taskStatus.GoalId);
    Assert.Equal(task.Id.Value, taskStatus.TaskId);
    Assert.Equal(sourceEvent.TaskNumber, taskStatus.TaskNumber);
    Assert.Equal(AgentRole.Developer, taskStatus.Role);
    Assert.Equal(WorkTaskStatus.Completed, taskStatus.Status);
    using var taskStatusStream = new MemoryStream();
    await DashboardMonitoringEvents.WriteServerSentEventAsync(
        taskStatusStream,
        DashboardMonitoringEvents.TaskStatusEventName,
        taskStatus,
        $"task-{taskStatus.TimelineEventId}",
        CancellationToken.None);
    var taskStatusText = Encoding.UTF8.GetString(taskStatusStream.ToArray());
    Xunit.Assert.Contains("event: task.status", taskStatusText);
    Xunit.Assert.Contains("\"Status\": \"Completed\"", taskStatusText);

    using var snapshotStream = new MemoryStream();
    await DashboardMonitoringEvents.WriteServerSentEventAsync(
        snapshotStream,
        DashboardMonitoringEvents.SnapshotEventName,
        batch.Snapshot,
        id: null,
        CancellationToken.None);
    var snapshotText = Encoding.UTF8.GetString(snapshotStream.ToArray());
    Xunit.Assert.Contains("event: goal.snapshot", snapshotText);
    Xunit.Assert.Contains("inbox-monitor", snapshotText);
    Xunit.Assert.Contains("ProviderCapacity", snapshotText);
    Xunit.Assert.Contains("ReadyNowCount", snapshotText);
    Xunit.Assert.Contains("Verify task 1", snapshotText);
    Xunit.Assert.Contains("\"DispatchState\"", snapshotText);
    Xunit.Assert.Contains("\"OperatorDisposition\"", snapshotText);
    Xunit.Assert.Contains("\"NextSafeCommand\"", snapshotText);
    Xunit.Assert.Contains("\"RecommendedAction\": \"mark-stale\"", snapshotText);
    Xunit.Assert.Contains("\"ProcessTree\"", snapshotText);
    Xunit.Assert.Contains("\"WrapperProcessId\": 333333", snapshotText);
    Xunit.Assert.Contains("\"ChildProcessId\": 444444", snapshotText);
    Xunit.Assert.Contains("\"Artifacts\"", snapshotText);
    Xunit.Assert.Contains("\"StandardOutputExists\": true", snapshotText);
    Xunit.Assert.Contains("\"Worktree\"", snapshotText);
    Xunit.Assert.Contains("\"WorkingDirectory\"", snapshotText);
    Xunit.Assert.Contains("\"StaleThresholds\"", snapshotText);
    Xunit.Assert.Contains("\"StaleRetryBudgetRemaining\"", snapshotText);
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
    Assert.Contains("description-start", summary.Description, StringComparison.Ordinal);
    Assert.Contains("description-tail", summary.Description, StringComparison.Ordinal);
    Assert.Contains("[truncated", summary.Description, StringComparison.Ordinal);
    Assert.Contains("plan-start", summary.VerificationPlan!, StringComparison.Ordinal);
    Assert.Contains("plan-tail", summary.VerificationPlan!, StringComparison.Ordinal);
    Assert.Contains("[truncated", summary.VerificationPlan!, StringComparison.Ordinal);
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
    Assert.Contains("batch-start", item.Description, StringComparison.Ordinal);
    Assert.Contains("batch-tail", item.Description, StringComparison.Ordinal);
    Assert.Contains("[truncated", item.Description, StringComparison.Ordinal);
    Assert.True(item.Description.Length < description.Length);
    Assert.Equal(description, plan.Items.Single().Description);
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_can_emit_auto_refresh_metadata")]
    public void DashboardRendererCanEmitAutoRefreshMetadata()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Watch dashboard");
    var goalPrefix = goal.Id.Value[..8];

    var staticHtml = DashboardRenderer.Render(kernel);
    var refreshingHtml = DashboardRenderer.Render(kernel, RenderOptions(AutoRefreshSeconds: 15));
    var operatorRefreshingHtml = DashboardRenderer.Render(kernel, RenderOptions(AutoRefreshSeconds: 15, EnableOperatorControls: true));
    var focusedOperatorHtml = DashboardRenderer.Render(kernel, RenderOptions(AutoRefreshSeconds: 15, EnableOperatorControls: true, View: DashboardView.Goal, FocusGoalPrefix: goalPrefix));

    Assert.False(staticHtml.Contains("http-equiv=\"refresh\"", StringComparison.Ordinal));
    Assert.Contains("<meta http-equiv=\"refresh\" content=\"15\">", refreshingHtml, StringComparison.Ordinal);
    Assert.Contains("Auto-refresh every 15 seconds", refreshingHtml, StringComparison.Ordinal);
    Assert.False(operatorRefreshingHtml.Contains("http-equiv=\"refresh\"", StringComparison.Ordinal));
    Assert.Contains("Auto-update every 15 seconds", operatorRefreshingHtml, StringComparison.Ordinal);
    Assert.Contains("data-refresh-seconds=\"15\"", operatorRefreshingHtml, StringComparison.Ordinal);
    Assert.Contains($"data-monitor-stream=\"/api/goals/{goalPrefix}/events/stream\"", focusedOperatorHtml, StringComparison.Ordinal);
    Assert.Contains("data-live-monitor", focusedOperatorHtml, StringComparison.Ordinal);
    Assert.Contains("data-live-capacity", focusedOperatorHtml, StringComparison.Ordinal);
    Assert.Contains("href=\"/assets/dashboard.css\"", operatorRefreshingHtml, StringComparison.Ordinal);
    Assert.Contains("src=\"/assets/dashboard.js\"", operatorRefreshingHtml, StringComparison.Ordinal);
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
    Assert.Equal(AgentCatalog.OpenAiSubscriptionModelAlias, openAi.Subscription.ModelAlias);
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
    Assert.Equal("Anthropic", anthropic.ComplexModel!.ProviderName);
    Assert.Equal("claude-sonnet-4-6", anthropic.ComplexModel.ModelName);
    Assert.Equal(AgentCatalog.ComplexApiMaxOutputTokens, anthropic.ComplexModel.MaxOutputTokens);

    Assert.Equal(AgentExecutionPolicy.ApiOnly, ollama.ExecutionPolicy);
    Assert.True(ollama.Subscription is null);
    Assert.True(ollama.ComplexModel is null);
    Assert.True(ollama.Model.MaxOutputTokens is null);
    Assert.Equal(SubscriptionMode.LocalBridge, ollama.Model.SubscriptionMode);
}
    [Xunit.Fact(DisplayName = "DashboardRequestParser_applies_first_class_subscription_reasoning_defaults")]
    public void DashboardRequestParserAppliesFirstClassSubscriptionReasoningDefaults()
{
    var sol = DashboardRequestParser.CreateAgentDefinition(new AgentSubmissionDto(
        "Developer",
        "OpenAI",
        "gpt-5.4-mini",
        null,
        SubscriptionModelAlias: AgentCatalog.OpenAiSolSubscriptionModelAlias));
    var terra = DashboardRequestParser.CreateAgentDefinition(new AgentSubmissionDto(
        "Tester",
        "OpenAI",
        "gpt-5.4-mini",
        null,
        SubscriptionModelAlias: AgentCatalog.OpenAiTerraSubscriptionModelAlias));
    var luna = DashboardRequestParser.CreateAgentDefinition(new AgentSubmissionDto(
        "Reviewer",
        "OpenAI",
        "gpt-5.4-mini",
        null,
        SubscriptionModelAlias: AgentCatalog.OpenAiLunaSubscriptionModelAlias));
    var fable = DashboardRequestParser.CreateAgentDefinition(new AgentSubmissionDto(
        "Planner",
        "Anthropic",
        "claude-haiku-4-5",
        null,
        SubscriptionModelAlias: "fable"));

    Assert.Equal(AgentCatalog.OpenAiSolSubscriptionModelAlias, sol.Subscription!.ModelAlias);
    Assert.Equal("low", sol.Subscription.ReasoningEffort);
    Assert.Equal(AgentCatalog.OpenAiTerraSubscriptionModelAlias, terra.Subscription!.ModelAlias);
    Assert.Equal("medium", terra.Subscription.ReasoningEffort);
    Assert.Equal(AgentCatalog.OpenAiLunaSubscriptionModelAlias, luna.Subscription!.ModelAlias);
    Assert.Equal("medium", luna.Subscription.ReasoningEffort);
    Assert.Equal("fable", fable.Subscription!.ModelAlias);
    Assert.True(fable.Subscription.ReasoningEffort is null);
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
    var ex = Assert.ThrowsAny<ArgumentException>(() =>
        DashboardRequestParser.ParseCreateGoalSubmission(
            "{\"objective\":\"Start worker processes\",\"workflow\":\"simple\",\"autoHandoff\":true}"));

    Assert.Contains("confirmAutoHandoff=true", ex.Message, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "DashboardRequestParser_requires_confirmation_for_paid_provider_smoke")]
    public void DashboardRequestParserRequiresConfirmationForPaidProviderSmoke()
{
    var ex = Assert.ThrowsAny<ArgumentException>(() =>
        DashboardRequestParser.ParseProviderSmokeSubmission("{\"target\":\"openai\"}"));

    Xunit.Assert.Contains("confirmPaidSmoke=true", ex.Message);
    Xunit.Assert.Contains("default local Ollama smoke first", ex.Message);
    Assert.Equal("ollama", DashboardRequestParser.ParseProviderSmokeSubmission("ollama"));
    Assert.Equal("openai", DashboardRequestParser.ParseProviderSmokeSubmission("{\"target\":\"openai\",\"confirmPaidSmoke\":true}"));
}
    [Xunit.Fact(DisplayName = "DashboardRequestParser_requires_confirmation_for_broad_provider_smoke")]
    public void DashboardRequestParserRequiresConfirmationForBroadProviderSmoke()
{
    var ex = Assert.ThrowsAny<ArgumentException>(() =>
        DashboardRequestParser.ParseProviderSmokeSubmission("{\"target\":\"all\"}"));

    Xunit.Assert.Contains("confirmAll=true", ex.Message);
    Xunit.Assert.Contains("default local Ollama smoke first", ex.Message);
    Assert.Equal("all", DashboardRequestParser.ParseProviderSmokeSubmission("{\"target\":\"all\",\"confirmAll\":true}"));
}
    [Xunit.Fact(DisplayName = "DashboardRequestParser_requires_retry_note")]
    public void DashboardRequestParserRequiresRetryNote()
{
    var empty = Assert.ThrowsAny<ArgumentException>(() => DashboardRequestParser.ParseRetrySubmission(""));
    var json = Assert.ThrowsAny<ArgumentException>(() => DashboardRequestParser.ParseRetrySubmission("{\"message\":\"\"}"));
    var parsed = DashboardRequestParser.ParseRetrySubmission(
        "{\"message\":\"Fix failed verification\",\"idempotencyKey\":\"dashboard-submit-1\"}");

    Assert.Contains("Retry note cannot be empty", empty.Message, StringComparison.Ordinal);
    Assert.Contains("non-empty 'message'", json.Message, StringComparison.Ordinal);
    Assert.Equal("Fix failed verification", parsed.Message);
    Assert.Equal("dashboard-submit-1", parsed.IdempotencyKey);
}
    [Xunit.Fact(DisplayName = "DashboardRequestParser_requires_limit_review_confirmation_note")]
    public void DashboardRequestParserRequiresLimitReviewConfirmationNote()
{
    Xunit.Assert.Null(DashboardRequestParser.ParseLimitReviewSubmission(""));
    var missingConfirm = Assert.ThrowsAny<ArgumentException>(() => DashboardRequestParser.ParseLimitReviewSubmission("{\"note\":\"Reviewed\"}"));
    var missingNote = Assert.ThrowsAny<ArgumentException>(() => DashboardRequestParser.ParseLimitReviewSubmission("{\"confirmLimitReview\":true,\"note\":\"\"}"));
    var parsed = DashboardRequestParser.ParseLimitReviewSubmission("{\"confirmLimitReview\":true,\"note\":\" Reviewed model timing. \"}");

    Assert.Contains("confirmLimitReview=true", missingConfirm.Message, StringComparison.Ordinal);
    Assert.Contains("non-empty 'note'", missingNote.Message, StringComparison.Ordinal);
    Xunit.Assert.NotNull(parsed);
    Assert.Equal("Reviewed model timing.", parsed!.Note);
}

    [Xunit.Fact(DisplayName = "Dashboard_api_subscription_dispatch_acknowledges_limit_review")]
    public async Task DashboardApiSubscriptionDispatchAcknowledgesLimitReview()
{
    var root = CreateTempDirectory();
    var workspace = CreateRefinedWorkspace(root);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Review dashboard subscription limits", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
    IReadOnlyList<AgentDefinition> agents =
    [
        new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli"))
    ];
    var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]);
    kernel.ActivateGoal(goal.Id, agents);
    var worktreePath = GoalWorktrees.WorktreePath(root, goal.Id);
    Directory.CreateDirectory(worktreePath);
    File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: ..");
    var task = goal.Tasks.Single();

    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec attempt 1", root, DateTimeOffset.UtcNow, WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "codex exec attempt 1",
        root,
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Visit settings to purchase more credits or try again later.",
        DateTimeOffset.UtcNow));
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec attempt 2", root, DateTimeOffset.UtcNow, WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "codex exec attempt 2",
        root,
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Visit settings to purchase more credits or try again later.",
        DateTimeOffset.UtcNow));

    var blocked = await Xunit.Assert.ThrowsAsync<ArgumentException>(async () => await GoalManagementCommandService.ApplyTaskActionAsync(
        kernel,
        agents,
        providers,
        workspace,
        goal,
        task,
        "subscription-dispatch",
        string.Empty));

    var result = await GoalManagementCommandService.ApplyTaskActionAsync(
        kernel,
        agents,
        providers,
        workspace,
        goal,
        task,
        "subscription-dispatch",
        "{\"confirmLimitReview\":true,\"note\":\"Reviewed profile from dashboard.\"}");

    Assert.Contains("confirmLimitReview=true", blocked.Message, StringComparison.Ordinal);
    Xunit.Assert.NotNull(result);
    Assert.False(DispatchFailureClassifier.RequiresSubscriptionLimitReview(task));
    Assert.Equal("Reviewed profile from dashboard.", task.SubscriptionLimitReviewNote);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Xunit.Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskSubscriptionLimitReviewAcknowledged &&
        evt.Message.Contains("Reviewed profile from dashboard", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_shows_limit_review_acknowledgement_form")]
    public void DashboardRendererShowsLimitReviewAcknowledgementForm()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Render subscription limit review", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
    IReadOnlyList<AgentDefinition> agents =
    [
        new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli"))
    ];
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec attempt 1", "C:\\repo", DateTimeOffset.UtcNow, WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "codex exec attempt 1",
        "C:\\repo",
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Visit settings to purchase more credits or try again later.",
        DateTimeOffset.UtcNow));
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec attempt 2", "C:\\repo", DateTimeOffset.UtcNow, WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "codex exec attempt 2",
        "C:\\repo",
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Visit settings to purchase more credits or try again later.",
        DateTimeOffset.UtcNow));

    var html = DashboardRenderer.Render(
        kernel,
        RenderOptions(
            EnableOperatorControls: true,
            View: DashboardView.Goal,
            FocusGoalPrefix: goal.Id.Value[..8],
            AgentDefinitions: agents));

    Assert.Contains("name=\"confirmLimitReview\" value=\"true\"", html, StringComparison.Ordinal);
    Assert.Contains("Limit review note", html, StringComparison.Ordinal);
    Assert.Contains("Acknowledge and prepare", html, StringComparison.Ordinal);
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
    var missing = Assert.ThrowsAny<ArgumentException>(() =>
        DashboardRequestParser.ParseManualVerifySubmission("{\"note\":\"No outcome.\"}"));
    var invalid = Assert.ThrowsAny<ArgumentException>(() =>
        DashboardRequestParser.ParseManualVerifySubmission("{\"status\":\"completed\",\"note\":\"No outcome.\"}"));
    var conflict = Assert.ThrowsAny<ArgumentException>(() =>
        DashboardRequestParser.ParseManualVerifySubmission("{\"passed\":false,\"status\":\"passed\",\"note\":\"Conflict.\"}"));

    Assert.Contains("passed' or 'status", missing.Message, StringComparison.Ordinal);
    Assert.Contains("status", invalid.Message, StringComparison.Ordinal);
    Assert.Contains("conflict", conflict.Message, StringComparison.OrdinalIgnoreCase);
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
        @"C:\repo\.orchestrator\state.db",
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
        RenderOptions(
            EnableOperatorControls: true,
            HealthReport: health,
            Workspace: workspace,
            ContinuationWatches: [continuation]));

    // Config view
    var configHtml = DashboardRenderer.Render(
        kernel,
        RenderOptions(
            EnableOperatorControls: true,
            HealthReport: health,
            Workspace: workspace,
            ContinuationWatches: [continuation],
            View: DashboardView.Config));

    // System view
    var systemHtml = DashboardRenderer.Render(
        kernel,
        RenderOptions(
            EnableOperatorControls: true,
            HealthReport: health,
            Workspace: workspace,
            ContinuationWatches: [continuation],
            View: DashboardView.System));

    // Goal detail view
    var goalHtml = DashboardRenderer.Render(
        kernel,
        RenderOptions(
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
    Assert.Contains("data-action=\"/api/goals\"", opsHtml, StringComparison.Ordinal);
    Assert.Contains($"href=\"/api/goals/{goalPrefix}\"", opsHtml, StringComparison.Ordinal);
    Assert.Contains("Goal JSON", opsHtml, StringComparison.Ordinal);
    Assert.Contains("<label for=\"new-goal\">Goal</label>", opsHtml, StringComparison.Ordinal);
    Assert.Contains("name=\"workflow\"", opsHtml, StringComparison.Ordinal);
    Assert.Contains("<option value=\"sdlc\">Automatic intake</option>", opsHtml, StringComparison.Ordinal);
    Assert.Contains("<option value=\"simple\">Developer-only override</option>", opsHtml, StringComparison.Ordinal);
    Assert.Contains("name=\"autoHandoff\" value=\"false\"", opsHtml, StringComparison.Ordinal);
    Assert.Contains("name=\"confirmAutoHandoff\" value=\"true\"", opsHtml, StringComparison.Ordinal);
    Assert.Contains("id=\"new-goal-auto-handoff\" type=\"checkbox\" name=\"autoHandoff\" value=\"true\"", opsHtml, StringComparison.Ordinal);
    Assert.False(opsHtml.Contains("id=\"new-goal-auto-handoff\" type=\"checkbox\" name=\"autoHandoff\" value=\"true\" checked", StringComparison.Ordinal));
    Assert.Contains("Automatically start subscription handoff", opsHtml, StringComparison.Ordinal);
    Assert.Contains("data-next-action=\"RefreshRunningProcess\"", opsHtml, StringComparison.Ordinal);
    Assert.Contains("data-next-action=\"DelegatePendingTask\"", opsHtml, StringComparison.Ordinal);
    Assert.Contains("src=\"/assets/dashboard.js\"", opsHtml, StringComparison.Ordinal);

    // Nav bar present on all views
    Assert.Contains("class=\"dashboard-nav\"", opsHtml, StringComparison.Ordinal);
    Assert.Contains("class=\"dashboard-nav\"", configHtml, StringComparison.Ordinal);
    Assert.Contains("class=\"dashboard-nav\"", systemHtml, StringComparison.Ordinal);
    Assert.Contains("class=\"dashboard-nav\"", goalHtml, StringComparison.Ordinal);

    // System view: workspace, diagnostics, continuations
    Assert.Contains("Prototype workspace", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Execution directory", systemHtml, StringComparison.Ordinal);
    Assert.Contains(@"C:\repo", systemHtml, StringComparison.Ordinal);
    Assert.Contains(@"C:\repo\.orchestrator\workers.json", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Dashboard PID", systemHtml, StringComparison.Ordinal);
    Assert.Contains("12345", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Stop this known process before full build/test", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Prototype process diagnostic", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Detected 1 sibling dashboard app process", systemHtml, StringComparison.Ordinal);
    Assert.Contains("67890", systemHtml, StringComparison.Ordinal);
    Assert.Contains("<code>5087</code>", systemHtml, StringComparison.Ordinal);
    Assert.Contains("<code>5098</code>", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Stop-Process -Id 67890", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Open process diagnostic", systemHtml, StringComparison.Ordinal);
    Assert.Contains("href=\"/api/system/processes\"", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Build/test cleanup plan", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Open build/test cleanup plan", systemHtml, StringComparison.Ordinal);
    Assert.Contains("href=\"/api/system/build-test-cleanup\"", systemHtml, StringComparison.Ordinal);
    Assert.Contains("data-action-button=\"/api/system/run-build-test-cycle\"", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Run dashboard build/test cycle", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Task Duration Estimates", systemHtml, StringComparison.Ordinal);
    Assert.Contains("href=\"/api/system/task-durations\"", systemHtml, StringComparison.Ordinal);
    Assert.Contains("POST /api/system/run-build-test-cycle", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Build/test run history", systemHtml, StringComparison.Ordinal);
    Assert.Contains("href=\"/api/system/build-test-runs\"", systemHtml, StringComparison.Ordinal);
    Assert.Contains("20260604-161905", systemHtml, StringComparison.Ordinal);
    Assert.Contains("BuildSucceeded        : True", systemHtml, StringComparison.Ordinal);
    Assert.Contains("TestSucceeded         : True", systemHtml, StringComparison.Ordinal);
    Assert.Contains("/api/system/build-test-runs/log?path=", systemHtml, StringComparison.Ordinal);
    Assert.Contains(@".\scripts\Invoke-DashboardBuildTestCycle.ps1 -DashboardUrl http://localhost:5087/", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Get-Process Mcg.AgentOrchestrator.App -ErrorAction SilentlyContinue", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Invoke-IsolatedDotnet.ps1 test Mcg.AgentOrchestrator.sln --verbosity minimal", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Restart command", systemHtml, StringComparison.Ordinal);
    Assert.Contains(@".\mcg-orchestrator.cmd prototype-ui http://localhost:5087/ --refresh 5 --no-open", systemHtml, StringComparison.Ordinal);
    Assert.Contains("data-action=\"/api/system/stop-dashboard\"", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Stop dashboard for build/test", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Server continuation", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Open continuation summary", systemHtml, StringComparison.Ordinal);
    Assert.Contains("/api/continuations/summary", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Open continuation status", systemHtml, StringComparison.Ordinal);
    Assert.Contains("/api/continuations", systemHtml, StringComparison.Ordinal);
    Assert.Contains("<th>Source</th>", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Restored from durable store", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Next check", systemHtml, StringComparison.Ordinal);
    Assert.Contains("2026-06-04 12:05:00Z", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Background work is still running", systemHtml, StringComparison.Ordinal);
    Assert.Contains("Open bounded source survey", systemHtml, StringComparison.Ordinal);
    Assert.Contains("href=\"/api/source-survey?max=8\"", systemHtml, StringComparison.Ordinal);

    // Config view: setup doctor, agents, workers
    Assert.Contains("Setup Doctor", configHtml, StringComparison.Ordinal);
    Assert.Contains("OPENAI_API_KEY is set.", configHtml, StringComparison.Ordinal);
    Assert.Contains("local-echo", configHtml, StringComparison.Ordinal);
    Assert.Contains("codex-cli", configHtml, StringComparison.Ordinal);
    Assert.Contains("<td>Optional</td>", configHtml, StringComparison.Ordinal);
    Assert.Contains("<th>Patch</th>", configHtml, StringComparison.Ordinal);
    Assert.Contains("Patch-capable", configHtml, StringComparison.Ordinal);
    Assert.False(configHtml.Contains("Smoke default provider", StringComparison.Ordinal));
    Assert.False(configHtml.Contains("/api/provider-smoke?target=openai", StringComparison.Ordinal));
    Assert.False(configHtml.Contains("/api/provider-smoke?target=all", StringComparison.Ordinal));
    Assert.Contains("data-action=\"/api/provider-smoke\"", configHtml, StringComparison.Ordinal);
    Assert.Contains("name=\"target\" value=\"openai\"", configHtml, StringComparison.Ordinal);
    Assert.Contains("type=\"checkbox\" name=\"confirmPaidSmoke\" value=\"true\"", configHtml, StringComparison.Ordinal);
    Assert.False(configHtml.Contains("type=\"hidden\" name=\"confirmPaidSmoke\"", StringComparison.Ordinal));
    Assert.Contains("<button type=\"submit\">Paid smoke</button>", configHtml, StringComparison.Ordinal);
    Assert.Contains(">Local smoke</a>", configHtml, StringComparison.Ordinal);
    Assert.Contains("data-action=\"/api/agents\"", configHtml, StringComparison.Ordinal);
    Assert.Contains("data-agent-config=\"true\"", configHtml, StringComparison.Ordinal);
    Assert.Contains("name=\"executionPolicy\"", configHtml, StringComparison.Ordinal);
    Assert.Contains("name=\"providerName\"", configHtml, StringComparison.Ordinal);
    Assert.Contains("<select name=\"modelName\" data-provider-options=\"apiModels\"", configHtml, StringComparison.Ordinal);
    Assert.Contains("<select name=\"reasoningEffort\" data-provider-options=\"apiReasoning\"", configHtml, StringComparison.Ordinal);
    Assert.Contains("<select name=\"subscriptionProfileName\" data-provider-options=\"subscriptionProfiles\"", configHtml, StringComparison.Ordinal);
    Assert.Contains("<select name=\"subscriptionModelAlias\" data-provider-options=\"subscriptionModels\"", configHtml, StringComparison.Ordinal);
    Assert.Contains("<option value=\"gpt-5.6-sol\">GPT-5.6 Sol</option>", configHtml, StringComparison.Ordinal);
    Assert.Contains("<option value=\"gpt-5.6-terra\">GPT-5.6 Terra</option>", configHtml, StringComparison.Ordinal);
    Assert.Contains("<option value=\"gpt-5.6-luna\">GPT-5.6 Luna</option>", configHtml, StringComparison.Ordinal);
    Assert.Contains("<option value=\"gpt-5.5\" selected>GPT-5.5</option>", configHtml, StringComparison.Ordinal);
    Assert.Contains("<option value=\"\">Use API model</option>", configHtml, StringComparison.Ordinal);
    Assert.Contains("<select name=\"subscriptionReasoningEffort\" data-provider-options=\"subscriptionReasoning\"", configHtml, StringComparison.Ordinal);
    Assert.Contains("<select name=\"complexReasoningEffort\" data-provider-options=\"apiReasoning\"", configHtml, StringComparison.Ordinal);
    Assert.Contains("name=\"maxOutputTokens\" min=\"1\" placeholder=\"2048\"", configHtml, StringComparison.Ordinal);
    Assert.Contains("name=\"complexMaxOutputTokens\" min=\"1\" placeholder=\"4096\"", configHtml, StringComparison.Ordinal);
    AssertOpenAiModelOrderIsCostAware(configHtml);
    AssertOpenAiModelOrderIsCostAware(DashboardAssets.OperatorControlsScript);
    Assert.False(configHtml.Contains("Default CLI model", StringComparison.Ordinal));
    Assert.False(DashboardAssets.OperatorControlsScript.Contains("Default CLI model", StringComparison.Ordinal));
    Assert.Contains("defaultSubscriptionModel: 'gpt-5.5'", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.False(DashboardAssets.OperatorControlsScript.Contains("GPT-5.3-Codex", StringComparison.Ordinal));
    Assert.Contains("['gpt-5.6-sol','GPT-5.6 Sol']", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("'gpt-5.6-luna': [['','Default'], ['low','Low'], ['medium','Medium'], ['high','High'], ['xhigh','Extra high'], ['max','Max']]", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("'gpt-5.6-terra': 'medium'", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("['sonnet','Claude Sonnet (latest)']", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("['opus-5','Claude Opus 5 (pinned)']", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.DoesNotContain("['fable',", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("maxTokenPlaceholder(provider)", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("complexMaxTokenPlaceholder(provider)", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("complexProviderName", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("data-action=\"/api/worker-profiles\"", configHtml, StringComparison.Ordinal);
    Assert.Contains("name=\"commandTemplate\"", configHtml, StringComparison.Ordinal);

    // Goal detail view: operator controls, pending input, task actions, reports
    Assert.Contains("Human decisions", goalHtml, StringComparison.Ordinal);
    Assert.Contains("<th>Work item</th>", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Task 1: Developer", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/monitor?goal={goalPrefix}", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Source survey", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Low-noise repository map", goalHtml, StringComparison.Ordinal);
    Assert.Contains("!**/.scratch/**", goalHtml, StringComparison.Ordinal);
    Assert.Contains("!**/.orchestrator-prototype/**", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"answer {request.Id.Value[..8]} &lt;answer&gt;", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"id=\"input-{request.Id.Value[..8]}\"", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"href=\"#input-{request.Id.Value[..8]}\"", goalHtml, StringComparison.Ordinal);
    Assert.Contains("data-next-action=\"AnswerHumanInput\"", goalHtml, StringComparison.Ordinal);
    Assert.Contains("class=\"decision-question\"", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Recent task activity", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"<textarea id=\"answer-{request.Id.Value[..8]}\"", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Submit Answer", goalHtml, StringComparison.Ordinal);
    Assert.Contains("class=\"answer-choice-row\"", goalHtml, StringComparison.Ordinal);
    Assert.Contains("name=\"answer\" value=\"Yes\"", goalHtml, StringComparison.Ordinal);
    Assert.Contains("name=\"answer\" value=\"No\"", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"data-toggle-custom-answer=\"answer-{request.Id.Value[..8]}\"", goalHtml, StringComparison.Ordinal);
    Assert.Contains("aria-expanded=\"false\"", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Operator disposition", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Next safe command:", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Work summary", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Action recommendation", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Open recommendation JSON", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Open compact JSON", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Open triage JSON", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Open monitor JSON", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Open next-action JSON", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Open evidence JSON", goalHtml, StringComparison.Ordinal);
    Assert.False(goalHtml.Contains("Open raw JSON", StringComparison.Ordinal));
    Assert.Contains($"/api/goals/{goalPrefix}/work-summary", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/action-recommendations", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/failure-triage", goalHtml, StringComparison.Ordinal);
    Assert.Contains("/api/source-survey?max=8", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/acceptance?goal={goalPrefix}", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/evidence?goal={goalPrefix}", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/stages?goal={goalPrefix}", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/next?goal={goalPrefix}", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/gates?goal={goalPrefix}", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/verification-worklist?goal={goalPrefix}", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/transcript", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/subscription-plan", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/advance", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/advance-subscription?confirmSubscriptionAdvance=true", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/advance-until-blocked", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/advance-subscription-until-blocked?confirmSubscriptionAdvance=true", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Continue subscription handoff", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Continue non-API actions", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/profile-dispatch-ready", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/subscription-dispatch-ready", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/start-subscription-ready?confirmBatchStart=true", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/cancel-dispatches", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"data-action=\"/api/goals/{goalPrefix}/tasks\"", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"data-action=\"/api/goals/{goalPrefix}/ask\"", goalHtml, StringComparison.Ordinal);
    Assert.Contains("<option>Developer</option>", goalHtml, StringComparison.Ordinal);
    Assert.Contains("name=\"delegate\" value=\"false\"", goalHtml, StringComparison.Ordinal);
    Assert.Contains("name=\"verificationPlan\"", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Goal question", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/start-dispatches?confirmBatchStart=true", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/input/{request.Id.Value[..8]}/answer", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/tasks/3/brief", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/tasks/3/timeline", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/tasks/3/gate", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/tasks/3/verification-plan", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/tasks/3/verifications", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/tasks/3/run?confirmTaskRun=true", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Current next action", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Background process is running: pid 1234", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/tasks/2/refresh", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Advanced task controls", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/tasks/3/retry", goalHtml, StringComparison.Ordinal);
    Assert.Contains("name=\"idempotencyKey\" value=\"dashboard-retry-", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Retry note", goalHtml, StringComparison.Ordinal);
    Assert.Contains("What changed or what should be tried next?", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/tasks/3/profile-dispatch", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/tasks/3/subscription-dispatch", goalHtml, StringComparison.Ordinal);
    Assert.Contains("name=\"plan\"", goalHtml, StringComparison.Ordinal);
    Assert.Contains("value=\"codex-cli\"", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/tasks/3/progress", goalHtml, StringComparison.Ordinal);
    Assert.Contains("name=\"message\"", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/tasks/3/ask", goalHtml, StringComparison.Ordinal);
    Assert.Contains("name=\"question\"", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/tasks/3/verify", goalHtml, StringComparison.Ordinal);
    Assert.Contains("dotnet test --filter", goalHtml, StringComparison.Ordinal);
    Assert.Contains("AgentCatalog|WorkerProfile", goalHtml, StringComparison.Ordinal);
    Assert.Contains("PowerShell: quote filters that contain |", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/tasks/3/verify-manual", goalHtml, StringComparison.Ordinal);
    Assert.Contains("name=\"idempotencyKey\" value=\"dashboard-verify-manual-", goalHtml, StringComparison.Ordinal);
    Assert.Contains("<option value=\"false\">Failed</option>", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/tasks/2/logs", goalHtml, StringComparison.Ordinal);
    Assert.Contains($"/api/human-input-worklist?goal={goalPrefix}", goalHtml, StringComparison.Ordinal);

    // Script assertions (view-independent)
    Assert.Contains("agentProviderOptions", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("summarizeResponse", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("if(warning) return warning", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("Server continuation is watching", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.False(DashboardAssets.OperatorControlsScript.Contains("Auto-resume scheduled", StringComparison.Ordinal));
    Assert.Contains("Stopped:", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("Changed:", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("Started build/test cycle PID", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("Dashboard stop requested for PID", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("Siblings:", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("window.__dashboardSubmitForm", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("applyLiveSnapshot", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("providerCapacity", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("source.addEventListener('goal.snapshot', handleSnapshotEvent)", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("startMonitorStaleTimer", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("Live monitor stale; timed refresh remains active.", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("window.__dashboardReady = true", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_system_view_presents_task_duration_stats")]
    public void DashboardRendererSystemViewPresentsTaskDurationStats()
{
    var html = DashboardRenderer.Render(
        new AgentOrchestratorKernel(),
        RenderOptions(
            EnableOperatorControls: true,
            View: DashboardView.System,
            Workspace: new DashboardWorkspaceContext(
                @"C:\repo\.orchestrator",
                @"C:\repo\.orchestrator\state.db",
                @"C:\repo",
                @"C:\repo\.orchestrator\prompts",
                @"C:\repo\.orchestrator\logs",
                @"C:\repo\.orchestrator\workers.json",
                @"C:\repo\.orchestrator\agents.json",
                12345),
            TaskDurationStats:
            [
                new TaskDurationStatsDto(
                    "Developer/Complex",
                    AgentRole.Developer,
                    TaskComplexity.Complex,
                    null,
                    null,
                    TaskCount: 3,
                    AttemptCount: 4,
                    FailedAttemptCount: 1,
                    MinimumSampleCount: TaskDurationReport.MinSamplesForPublishedStats,
                    HasPublishedStats: true,
                    MedianLegitimateRuntime: "10m",
                    P90LegitimateRuntime: "12m",
                    MedianFailureInterventionOverhead: "20m",
                    FailureRate: 0.25,
                    RealFailureAttemptCount: 1,
                    EnvironmentalFailureAttemptCount: 0,
                    ManufacturedFixedFailureAttemptCount: 0,
                    UnknownEraFailureAttemptCount: 0,
                    RealFailureRate: 0.25,
                    EnvironmentalFailureRate: 0,
                    ManufacturedFixedFailureRate: 0,
                    UnknownEraFailureRate: 0),
                new TaskDurationStatsDto(
                    "Tester/Simple",
                    AgentRole.Tester,
                    TaskComplexity.Simple,
                    null,
                    null,
                    TaskCount: 1,
                    AttemptCount: 1,
                    FailedAttemptCount: 0,
                    MinimumSampleCount: TaskDurationReport.MinSamplesForPublishedStats,
                    HasPublishedStats: false,
                    MedianLegitimateRuntime: "n/a (n=1)",
                    P90LegitimateRuntime: "n/a (n=1)",
                    MedianFailureInterventionOverhead: "n/a (n=1)",
                    FailureRate: 0,
                    RealFailureAttemptCount: 0,
                    EnvironmentalFailureAttemptCount: 0,
                    ManufacturedFixedFailureAttemptCount: 0,
                    UnknownEraFailureAttemptCount: 0,
                    RealFailureRate: 0,
                    EnvironmentalFailureRate: 0,
                    ManufacturedFixedFailureRate: 0,
                    UnknownEraFailureRate: 0)
            ]));

    Assert.Contains("Task Duration Estimates", html, StringComparison.Ordinal);
    Assert.Contains("Developer/Complex", html, StringComparison.Ordinal);
    Assert.Contains("<td>10m</td>", html, StringComparison.Ordinal);
    Assert.Contains("<td>20m</td>", html, StringComparison.Ordinal);
    Assert.Contains("<td>25%</td>", html, StringComparison.Ordinal);
    Assert.Contains("Tester/Simple", html, StringComparison.Ordinal);
    Assert.Contains("n/a (n=1)", html, StringComparison.Ordinal);
    Assert.Contains("Buckets need at least 3 samples", html, StringComparison.Ordinal);
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
    Assert.Contains("Detected 1 sibling", diagnostic.Message, StringComparison.Ordinal);
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
    var opsHtml = DashboardRenderer.Render(kernel, RenderOptions(EnableOperatorControls: true));
    Assert.Contains("completion-banner", opsHtml, StringComparison.Ordinal);
    Assert.Contains("Goal verified", opsHtml, StringComparison.Ordinal);
    Assert.Contains("manual-only verification", opsHtml, StringComparison.Ordinal);
    Assert.Contains("No execution, dispatch, or process proof is recorded", opsHtml, StringComparison.Ordinal);
    Assert.False(opsHtml.Contains("No operator action is required.", StringComparison.Ordinal));

    // Goal detail view shows task action status
    var goalHtml = DashboardRenderer.Render(kernel, RenderOptions(
        EnableOperatorControls: true,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix));
    Assert.Contains("This task is complete and has passing verification evidence.", goalHtml, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_verified_goal_banner_points_to_acceptance_landing_and_cleanup")]
    public void DashboardRendererVerifiedGoalBannerPointsToAcceptanceLandingAndCleanup()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Ready for acceptance",
        [new TaskSpec(TaskId.New(), "Finish the simple goal", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, [Agent(AgentRole.Developer, AgentExecutionPolicy.ApiOnly)]);
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "dotnet test", Environment.CurrentDirectory, DateTimeOffset.UtcNow));
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(
        goal.Id,
        task.Id,
        new TaskVerificationRecord("dotnet test", Environment.CurrentDirectory, 0, "passed", string.Empty, DateTimeOffset.UtcNow));

    var gate = kernel.BuildVerificationGate(goal.Id);
    var html = DashboardRenderer.Render(kernel, RenderOptions(EnableOperatorControls: true));

    Assert.Equal(GoalStatus.Verified, goal.Status);
    Assert.True(gate.IsSatisfied, gate.Tasks.Single().Reason.ToString());
    Assert.True(html.Contains("completion-banner", StringComparison.Ordinal));
    Assert.True(html.Contains("Goal verified", StringComparison.Ordinal));
    Assert.True(html.Contains("Next action: run acceptance, merge the goal branch, record landing evidence, and clean up the worktree", StringComparison.Ordinal));
    Assert.False(html.Contains("No operator action is required.", StringComparison.Ordinal));
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

    Assert.Equal(null, DashboardNextActionControls.Build(goal, new NextActionItem(NextActionKind.VerifyCompletedTask, task.Id, null, "Verify"), WorkerProfileCatalog.Default()));
}

    [Xunit.Fact(DisplayName = "Dashboard_action_recommendations_aggregate_next_triage_recovery_capacity_and_policy")]
    public void DashboardActionRecommendationsAggregateNextTriageRecoveryCapacityAndPolicy()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Implement dashboard action recommendation panel.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Recommend action", [task]);
    var agent = new AgentDefinition(
        AgentId.New(),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
    kernel.ActivateGoal(goal.Id, [agent]);

    var report = DashboardActionRecommendationPlanner.Build(
        kernel,
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        root,
        AutonomyPolicy.Observe);
    var dto = DashboardResponseMapper.ToDashboardActionRecommendationReportDto(report);

    Xunit.Assert.True(dto.Primary is not null);
    var primary = dto.Primary!;
    Xunit.Assert.Equal(DashboardActionRecommendationSource.NextAction, primary.Source);
    Xunit.Assert.Equal("run 1 --confirm-paid-api-run", primary.SuggestedCommand);
    Xunit.Assert.Equal("POST", primary.ApiMethod);
    Xunit.Assert.True(primary.ApiPath?.Contains($"/api/goals/{goal.Id.Value[..8]}/tasks/1/run?confirmTaskRun=true&confirmPaidApiRun=true", StringComparison.Ordinal) == true);
    Xunit.Assert.True(primary.RequiresOperatorGate);
    Xunit.Assert.Contains(dto.Secondary, item => item.Source == DashboardActionRecommendationSource.GoalHealth);
    Xunit.Assert.Contains(dto.SourceSummaries, summary => summary.StartsWith("goal health: Active score=70", StringComparison.Ordinal));
    Xunit.Assert.Contains(dto.SourceSummaries, summary => summary.StartsWith("failure triage:", StringComparison.Ordinal));
    Xunit.Assert.Contains(dto.SourceSummaries, summary => summary.StartsWith("recovery:", StringComparison.Ordinal));
    Xunit.Assert.Contains(dto.SourceSummaries, summary => summary.StartsWith("acceptance queue:", StringComparison.Ordinal));
    Xunit.Assert.Contains(dto.SourceSummaries, summary => summary.StartsWith("capacity:", StringComparison.Ordinal));
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

    var control = DashboardNextActionControls.Build(goal, action, WorkerProfileCatalog.Default(), agentDefinitions: [agent]);
    var nextDto = DashboardResponseMapper.ToNextActionsDto(goal, kernel.BuildNextActions(goal.Id), WorkerProfileCatalog.Default(), [agent]).Items.Single();

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
        20000));
    var action = kernel.BuildNextActions(goal.Id).Items.Single();
    var goalPrefix = goal.Id.Value[..8];

    var control = DashboardNextActionControls.Build(goal, action, WorkerProfileCatalog.Default());
    var nextDto = DashboardResponseMapper.ToNextActionsDto(goal, kernel.BuildNextActions(goal.Id), WorkerProfileCatalog.Default()).Items.Single();
    var workSummary = DashboardResponseMapper.ToGoalWorkSummaryDto(
        kernel,
        goal,
        WorkerProfileCatalog.Default(),
        changedFiles: ["src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.Tasks.cs"]);
    var html = DashboardRenderer.Render(kernel, RenderOptions(
        EnableOperatorControls: true,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix,
        FocusGoalChangedFiles: ["src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.Tasks.cs"]));
    var transcript = GoalTranscriptRenderer.Render(kernel, goal, WorkerProfileCatalog.Default());

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
    Assert.Equal($"goal-{goalPrefix}", workSummary.BuildEnvironment.LeaseId);
    Assert.True(workSummary.BuildEnvironment.ArtifactsPath.Contains(Path.Combine("goals", goalPrefix, "artifacts"), StringComparison.OrdinalIgnoreCase));
    Assert.True(workSummary.BuildEnvironment.LeaseMetadataPath.Contains(Path.Combine("goals", goalPrefix, "lease", "lease.json"), StringComparison.OrdinalIgnoreCase));
    Assert.False(workSummary.BuildEnvironment.LeaseExists);
    Assert.Equal("focused CLI infrastructure tests", Assert.Single(workSummary.TestImpact!.Checks).Name);
    Assert.True(workSummary.TestImpact.Checks[0].CommandLine.Contains("FullyQualifiedName~CliHelpTests", StringComparison.Ordinal));
    Assert.False(workSummary.TestImpact.Checks[0].CommandLine.Contains("FullyQualifiedName~FundamentalAliasTests", StringComparison.Ordinal));
    Assert.True(workSummary.TestImpact.Checks[0].CommandLine.Contains("FullyQualifiedName~CliCommandTests", StringComparison.Ordinal));
    Assert.True(html.Contains("Test impact: Selected focused CLI infrastructure tests from changed file scope.", StringComparison.Ordinal));
    Assert.Contains($"data-next-action=\"ExecuteRecordedDispatch\" data-action-button=\"/api/goals/{goalPrefix}/tasks/1/start?confirmDispatchStart=true\"", html, StringComparison.Ordinal);
    Assert.False(html.Contains("confirmLargePaidSubscriptionStart=true", StringComparison.Ordinal));
    Assert.Contains("execute-dispatch 1 --confirm-dispatch-start --confirm-large-paid-subscription-start", html, StringComparison.Ordinal);
    Assert.Contains("Suggested command: execute-dispatch 1 --confirm-dispatch-start --confirm-large-paid-subscription-start", transcript, StringComparison.Ordinal);
    Assert.Contains("Cost: large paid subscription start. Inspect the generated prompt", transcript, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "Dashboard_preview_resolves_test_impact_from_workspace_when_focus_changed_files_are_null")]
    public void DashboardPreviewResolvesTestImpactFromWorkspaceWhenFocusChangedFilesAreNull()
{
    var root = CreateTempDirectory();
    RunGit(root, "init", "-b", "main");
    RunGit(root, "config", "user.email", "dashboard-tests@example.com");
    RunGit(root, "config", "user.name", "Dashboard Tests");
    File.WriteAllText(Path.Combine(root, "README.md"), "seed");
    RunGit(root, "add", "-A");
    RunGit(root, "commit", "-m", "Seed");

    var workspace = CreateRefinedWorkspace(root);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Render workspace-backed test impact", [new TaskSpec(TaskId.New(), "Change dashboard rendering", AgentRole.Developer)]);
    var worktree = GoalWorktrees.Ensure(workspace.ExecutionDirectory, goal.Id);
    var changedFile = Path.Combine(
        worktree,
        "src",
        "Mcg.AgentOrchestrator.App",
        "Dashboard",
        "Preview.cs");
    Directory.CreateDirectory(Path.GetDirectoryName(changedFile)!);
    File.WriteAllText(changedFile, "// dashboard preview change");
    RunGit(worktree, "add", "-A");
    RunGit(worktree, "commit", "-m", "Dashboard preview change");
    var dashboardWorkspace = new DashboardWorkspaceContext(
        workspace.RootDirectory,
        workspace.SqliteStatePath,
        workspace.ExecutionDirectory,
        workspace.PromptDirectory,
        workspace.LogDirectory,
        workspace.WorkerProfilePath,
        workspace.AgentCatalogPath,
        0);

    var workSummary = DashboardResponseMapper.ToGoalWorkSummaryDto(
        kernel,
        goal,
        WorkerProfileCatalog.Default(),
        executionDirectory: workspace.ExecutionDirectory);
    var html = DashboardRenderer.Render(kernel, RenderOptions(
        EnableOperatorControls: true,
        View: DashboardView.Goal,
        FocusGoalPrefix: goal.Id.Value[..8],
        Workspace: dashboardWorkspace));

    Assert.Equal("focused dashboard infrastructure tests", Assert.Single(workSummary.TestImpact!.Checks).Name);
    Assert.True(workSummary.TestImpact.Checks[0].CommandLine.Contains("FullyQualifiedName~DashboardHostTests", StringComparison.Ordinal));
    Assert.True(workSummary.TestImpact.Checks[0].CommandLine.Contains("Category!=HostIntegration", StringComparison.Ordinal));
    Assert.True(html.Contains("Test impact: Selected focused dashboard infrastructure tests from changed file scope.", StringComparison.Ordinal));
    Assert.False(html.Contains("Test impact: No changed files detected; no build verification required.", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "DashboardNextActionControls_allow_complex_paid_prepared_dispatch_under_size_threshold")]
    public void DashboardNextActionControlsAllowComplexPaidPreparedDispatchUnderSizeThreshold()
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

    var control = DashboardNextActionControls.Build(goal, action, WorkerProfileCatalog.Default());
    var nextDto = DashboardResponseMapper.ToNextActionsDto(goal, kernel.BuildNextActions(goal.Id), WorkerProfileCatalog.Default()).Items.Single();
    var workSummary = DashboardResponseMapper.ToGoalWorkSummaryDto(kernel, goal, WorkerProfileCatalog.Default());

    Assert.Equal(NextActionKind.ExecuteRecordedDispatch, action.Kind);
    Assert.Equal($"/api/goals/{goalPrefix}/tasks/1/start?confirmDispatchStart=true", control!.Url);
    Xunit.Assert.Null(control.CostRisk);
    Xunit.Assert.Null(control.CostRecommendation);
    Xunit.Assert.Null(nextDto.Control!.CostRisk);
    Xunit.Assert.Null(nextDto.Control.CostRecommendation);
    Xunit.Assert.Null(workSummary.NextAction!.Control!.CostRisk);
    Xunit.Assert.Null(workSummary.NextAction.Control.CostRecommendation);
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
    var html = DashboardRenderer.Render(kernel, RenderOptions(
        EnableOperatorControls: true,
        HealthReport: health,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix,
        AgentDefinitions: agents));
    var flexiblePromptChars = AgentTaskRunner.PreviewRun(goal, goal.Tasks[1], agents).PromptCharacterCount;
    var apiPromptChars = AgentTaskRunner.PreviewRun(goal, goal.Tasks[2], agents).PromptCharacterCount;
    var stages = DashboardResponseMapper.ToGoalStageReadinessReportDto(goal, kernel.BuildStageReadinessReport(goal.Id), agents);

    Assert.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/1/run?confirmTaskRun=true\">Prepare subscription handoff</button>", html, StringComparison.Ordinal);
    Assert.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/2/run?confirmTaskRun=true\">Prepare subscription handoff</button>", html, StringComparison.Ordinal);
    Assert.False(html.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/1/api-run", StringComparison.Ordinal));
    Assert.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/2/api-run?confirmTaskRun=true&amp;confirmPaidApiRun=true\">Explicit paid API run</button>", html, StringComparison.Ordinal);
    Assert.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/3/run?confirmTaskRun=true&amp;confirmPaidApiRun=true\">Run paid API task</button>", html, StringComparison.Ordinal);
    Assert.Contains("confirmPaidApiRun=true", html, StringComparison.Ordinal);
    Assert.False(html.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/3/api-run", StringComparison.Ordinal));
    Assert.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/4/run?confirmTaskRun=true\">Prepare subscription handoff</button>", html, StringComparison.Ordinal);
    Assert.False(html.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/4/api-run", StringComparison.Ordinal));
    Assert.False(ExtractTaskControls(html, 1).Contains("API plan:", StringComparison.Ordinal));
    Assert.Contains($"API plan: OpenAI/test Simple reasoning medium prompt {flexiblePromptChars} chars max 2048 out [potentially paid]", ExtractTaskControls(html, 2), StringComparison.Ordinal);
    Assert.Contains($"API plan: OpenAI/test Simple reasoning medium prompt {apiPromptChars} chars max 2048 out [potentially paid]", ExtractTaskControls(html, 3), StringComparison.Ordinal);
    Assert.False(ExtractTaskControls(html, 4).Contains("API plan:", StringComparison.Ordinal));
    Assert.Contains("<code>subscription-dispatch 1</code>", html, StringComparison.Ordinal);
    Assert.Contains("<code>subscription-dispatch 2</code>", html, StringComparison.Ordinal);
    Assert.Contains("<code>run 3 --confirm-paid-api-run</code>", html, StringComparison.Ordinal);
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

    var html = DashboardRenderer.Render(kernel, RenderOptions(
        EnableOperatorControls: true,
        HealthReport: health,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix,
        AgentDefinitions: agents));
    var controls = ExtractTaskControls(html, 1);
    var nextDto = DashboardResponseMapper
        .ToNextActionsDto(goal, kernel.BuildNextActions(goal.Id), WorkerProfileCatalog.Default(), agents)
        .Items
        .Single(item => item.TaskId == task.Id.Value);
    var workSummary = DashboardResponseMapper.ToGoalWorkSummaryDto(kernel, goal, WorkerProfileCatalog.Default(), agents);
    var stageDto = DashboardResponseMapper.ToGoalStageReadinessReportDto(goal, kernel.BuildStageReadinessReport(goal.Id), agents).Stages.Single();

    Assert.Equal(TaskComplexity.Complex, preview.TaskComplexity);
    Assert.True(risk is not null);
    Assert.Equal("run 1 --confirm-paid-api-run --confirm-large-paid-api-prompt", nextDto.SuggestedCommand);
    Assert.Equal(ApiPromptCostGuard.BuildInlineLabel(risk!), nextDto.Control!.CostRisk);
    Assert.Equal("run 1 --confirm-paid-api-run --confirm-large-paid-api-prompt", workSummary.NextAction!.SuggestedCommand);
    Assert.Equal(ApiPromptCostGuard.BuildInlineLabel(risk!), workSummary.NextAction.Control!.CostRisk);
    Assert.Equal("run 1 --confirm-paid-api-run --confirm-large-paid-api-prompt", stageDto.SuggestedCommand);
    Assert.Contains("<code>run 1 --confirm-paid-api-run --confirm-large-paid-api-prompt</code>", html, StringComparison.Ordinal);
    Assert.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/1/run?confirmTaskRun=true&amp;confirmPaidApiRun=true\">Run paid API task</button>", controls, StringComparison.Ordinal);
    Assert.Contains("confirmPaidApiRun=true", controls, StringComparison.Ordinal);
    Assert.False(controls.Contains("confirmLargePaidApiPrompt=true", StringComparison.Ordinal));
    Assert.Contains($"API plan: OpenAI/test Complex reasoning medium prompt {preview.PromptCharacterCount} chars max 4096 out [potentially paid] [large paid prompt: exceeds {risk!.PromptThreshold}]", controls, StringComparison.Ordinal);
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

    var html = DashboardRenderer.Render(kernel, RenderOptions(
        EnableOperatorControls: true,
        HealthReport: health,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix,
        AgentDefinitions: agents));
    var controls = ExtractTaskControls(html, 2);
    var nextDto = DashboardResponseMapper
        .ToNextActionsDto(goal, kernel.BuildNextActions(goal.Id), WorkerProfileCatalog.Default(), agents)
        .Items
        .Single(item => item.TaskId == nextTask.Id.Value);

    Assert.Equal(TaskComplexity.Simple, preview.TaskComplexity);
    Assert.True(preview.PromptCharacterCount <= risk!.PromptThreshold);
    Assert.True(risk.HasPriorOverkillFit);
    Assert.False(risk.PromptExceedsThreshold);
    Assert.Equal("prior overkill API model", nextDto.Control!.CostRisk);
    Assert.True(nextDto.Control.CostRecommendation?.Contains("try local Ollama/qwen3:8b via agent configuration before paid API run", StringComparison.Ordinal) == true);
    Assert.Equal("run 2 --confirm-paid-api-run --confirm-large-paid-api-prompt", nextDto.SuggestedCommand);
    Assert.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/2/run?confirmTaskRun=true&amp;confirmPaidApiRun=true\">Run paid API task</button>", controls, StringComparison.Ordinal);
    Assert.Contains("confirmPaidApiRun=true", controls, StringComparison.Ordinal);
    Assert.False(controls.Contains("confirmLargePaidApiPrompt=true", StringComparison.Ordinal));
    Assert.True(risk!.PriorTaskShapes?.Contains("copy-only change") == true);
    Assert.Contains($"API plan: OpenAI/gpt-5-codex Simple reasoning medium prompt {preview.PromptCharacterCount} chars max 768 out [potentially paid] [prior overkill API model]", controls, StringComparison.Ordinal);
    Assert.Contains("try local Ollama/qwen3:8b via agent configuration before paid API run", controls, StringComparison.Ordinal);
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

    var html = DashboardRenderer.Render(kernel, RenderOptions(
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
    Assert.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/1/run?confirmTaskRun=true&amp;confirmPaidApiRun=true\">Run paid API task</button>", controls, StringComparison.Ordinal);
    Assert.Contains("confirmPaidApiRun=true", controls, StringComparison.Ordinal);
    Assert.False(controls.Contains("confirmLargePaidApiPrompt=true", StringComparison.Ordinal));
    Assert.Contains($"API plan: OpenAI/gpt-5.5 Complex reasoning high prompt {preview.PromptCharacterCount} chars max 1200 out [potentially paid] [complex paid API model]", controls, StringComparison.Ordinal);
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

    var html = DashboardRenderer.Render(kernel, RenderOptions(
        EnableOperatorControls: true,
        HealthReport: health,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix,
        AgentDefinitions: agents));
    var controls = ExtractTaskControls(html, 2);
    var nextDto = DashboardResponseMapper
        .ToNextActionsDto(goal, kernel.BuildNextActions(goal.Id), WorkerProfileCatalog.Default(), agents)
        .Items
        .Single(item => item.TaskId == nextTask.Id.Value);

    Assert.Equal(TaskComplexity.Simple, preview.TaskComplexity);
    Assert.True(preview.UsesComplexModel);
    Assert.Equal("gpt-5.5", preview.ModelName);
    Assert.True(risk!.UsesComplexPaidModel);
    Assert.Equal("complex paid API model", nextDto.Control!.CostRisk);
    Assert.Equal("run 2 --confirm-paid-api-run --confirm-large-paid-api-prompt", nextDto.SuggestedCommand);
    Assert.Contains($"API plan: OpenAI/gpt-5.5 Simple reasoning high prompt {preview.PromptCharacterCount} chars max 1200 out [potentially paid] [complex paid API model]", controls, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "DashboardRenderer_allows_subscription_plan_prompt_under_new_threshold")]
    public void DashboardRendererAllowsSubscriptionPlanPromptUnderNewThreshold()
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
    var failedCommand = "codex exec " + new string('c', 5000);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        failedCommand,
        "C:\\repo",
        DateTimeOffset.UtcNow,
        "OpenAI",
        "gpt-5.3-codex",
        "medium",
        TaskComplexity.Complex,
        9500));
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        failedCommand,
        "C:\\repo",
        1,
        new string('s', 5000),
        new string('e', 5000),
        DateTimeOffset.UtcNow));
    kernel.RetryTask(goal.Id, task.Id, "Retry after failed verification.");
    var risk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
        goal,
        agents,
        profiles,
        item => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(kernel, goal, item, agents),
        task);
    var goalPrefix = goal.Id.Value[..8];

    var html = DashboardRenderer.Render(kernel, RenderOptions(
        EnableOperatorControls: true,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix,
        AgentDefinitions: agents,
        WorkerProfiles: profiles));

    Xunit.Assert.NotNull(risk);
    Xunit.Assert.False(risk.IsAnomalous);
    Assert.Contains($"/api/goals/{goalPrefix}/advance-subscription?confirmSubscriptionAdvance=true", html, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/advance-subscription-until-blocked?confirmSubscriptionAdvance=true", html, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/start-subscription-ready?confirmBatchStart=true", html, StringComparison.Ordinal);
    Assert.False(html.Contains("confirmLargePaidSubscriptionStart=true", StringComparison.Ordinal));
    Assert.False(html.Contains("Paid subscription start requires explicit confirmation", StringComparison.Ordinal));
    Assert.Contains("OpenAI/gpt-5.3-codex Complex reasoning high", html, StringComparison.Ordinal);
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

    var html = DashboardRenderer.Render(kernel, RenderOptions(
        EnableOperatorControls: true,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix,
        AgentDefinitions: [agent],
        WorkerProfiles: WorkerProfileCatalog.Default()));
    var controls = ExtractTaskControls(html, 1);

    Assert.True(risk is not null);
    Assert.Contains($"/api/goals/{goalPrefix}/start-dispatches?confirmBatchStart=true", html, StringComparison.Ordinal);
    Assert.Contains($"/api/goals/{goalPrefix}/tasks/1/start?confirmDispatchStart=true", controls, StringComparison.Ordinal);
    Assert.False(html.Contains("confirmLargePaidSubscriptionStart=true", StringComparison.Ordinal));
    Assert.Contains("Continue subscription handoff (cost gate: large paid subscription start)", html, StringComparison.Ordinal);
    Assert.Contains("Start prepared work (cost gate: large paid subscription start)", html, StringComparison.Ordinal);
    Assert.Contains("Prepared starts: 12001 paid prompt chars.", html, StringComparison.Ordinal);
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
    var html = DashboardRenderer.Render(kernel, RenderOptions(
        EnableOperatorControls: true,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix));
    var taskDto = DashboardResponseMapper.ToTaskDetailDto(goal, task);
    var workSummary = DashboardResponseMapper.ToGoalWorkSummaryDto(kernel, goal, WorkerProfileCatalog.Default());
    var evidenceDto = DashboardResponseMapper.ToGoalEvidenceSummaryDto(goal, kernel.BuildGoalEvidenceSummary(goal.Id));
    var transcript = GoalTranscriptRenderer.Render(kernel, goal, WorkerProfileCatalog.Default());
    var taskNumber = goal.Tasks.Select((candidate, index) => (candidate, index))
        .Single(item => item.candidate.Id == task.Id)
        .index + 1;

    Assert.Contains("Prepared handoff for codex-cli.", html, StringComparison.Ordinal);
    Assert.Contains("OpenAI/gpt-5.3-codex Simple reasoning medium prompt 321 chars", html, StringComparison.Ordinal);
    Assert.Contains("Paid subscription handoff prepared", html, StringComparison.Ordinal);
    Assert.Contains("try local Ollama/qwen3:8b via agent configuration when the task is routine", html, StringComparison.Ordinal);
    Assert.Contains("Prompt: 321 chars", html, StringComparison.Ordinal);
    Assert.Contains("Dispatch models: OpenAI/gpt-5.3-codex (Simple) reasoning medium [potentially paid]: 1 dispatch, prompt 321 chars", html, StringComparison.Ordinal);
    Assert.Contains("Model fit: OpenAI/gpt-5.3-codex - adequate|overkill|underpowered - task shape - short reason.", html, StringComparison.Ordinal);
    Assert.Contains("<code>codex exec prompt.md</code>", html, StringComparison.Ordinal);
    Assert.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/{taskNumber}/start?confirmDispatchStart=true\"", html, StringComparison.Ordinal);
    Assert.Contains("Start prepared work", html, StringComparison.Ordinal);
    Assert.Contains($"href=\"/api/goals/{goalPrefix}/tasks/{taskNumber}/brief\"", html, StringComparison.Ordinal);
    Assert.Contains("Dispatch model: OpenAI/gpt-5.3-codex complexity=Simple reasoning=medium", transcript, StringComparison.Ordinal);
    Assert.Contains("Prompt size: 321 chars", transcript, StringComparison.Ordinal);
    Assert.Contains("Dispatch models:", transcript, StringComparison.Ordinal);
    Assert.Contains("- OpenAI/gpt-5.3-codex (Simple) reasoning medium [potentially paid]: 1 dispatch, prompt 321 chars", transcript, StringComparison.Ordinal);
    Assert.Contains("using OpenAI/gpt-5.3-codex Simple reasoning medium", evidenceDto.Tasks.Single(item => item.TaskId == task.Id.Value).Message, StringComparison.Ordinal);
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

    [Xunit.Fact(DisplayName = "GoalWorkSummary_surfaces_ready_task_parallel_plan")]
    public void GoalWorkSummarySurfacesReadyTaskParallelPlan()
{
    var kernel = new AgentOrchestratorKernel();
    var first = new TaskSpec(TaskId.New(), "Inspect first ready subscription task.", AgentRole.Planner);
    var second = new TaskSpec(TaskId.New(), "Inspect second ready subscription task.", AgentRole.Planner);
    var goal = kernel.CreateGoal("Show ready-task parallel safety", [first, second]);
    var agent = new AgentDefinition(
        new AgentId("subscription-planner"),
        "Subscription planner",
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));
    kernel.ActivateGoal(goal.Id, [agent]);

    var workSummary = DashboardResponseMapper.ToGoalWorkSummaryDto(kernel, goal, WorkerProfileCatalog.Default(), [agent]);

    Assert.True(workSummary.ParallelPlan is not null);
    Assert.Equal(2, workSummary.ParallelPlan!.Batches.Count);
    Assert.True(workSummary.ParallelPlan.Decisions.Any(decision =>
        decision.IntentId == first.Id.Value &&
        decision.Disposition == ParallelExecutionDisposition.Concurrent &&
        decision.BatchNumber == 1));
    Assert.True(workSummary.ParallelPlan.Decisions.Any(decision =>
        decision.IntentId == second.Id.Value &&
        decision.Disposition == ParallelExecutionDisposition.Serialized &&
        decision.BatchNumber == 2));
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
                  false,
                  "none",
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

    var html = DashboardRenderer.Render(kernel, RenderOptions(
        EnableOperatorControls: true,
        HealthReport: health,
        View: DashboardView.Config));

    Assert.Contains("<option value=\"Ollama\" selected>Ollama</option>", html, StringComparison.Ordinal);
    Assert.Contains("<option value=\"qwen2.5-coder:7b\" selected>Qwen2.5 Coder 7B</option>", html, StringComparison.Ordinal);
    Assert.Contains("<option value=\"\" selected>None</option>", html, StringComparison.Ordinal);
    Assert.Contains("max tokens: 8192", html, StringComparison.Ordinal);
    Assert.Contains("name=\"maxOutputTokens\" min=\"1\" placeholder=\"8192\" value=\"\"", html, StringComparison.Ordinal);
    Assert.Contains("name=\"complexMaxOutputTokens\" min=\"1\" placeholder=\"8192\" value=\"8192\"", html, StringComparison.Ordinal);
    Assert.Contains("Ollama: {", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("qwen2.5-coder:7b", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
    Assert.Contains("preferredProfile: ''", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
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
                  "none",
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

    var html = DashboardRenderer.Render(kernel, RenderOptions(
        EnableOperatorControls: true,
        HealthReport: health,
        View: DashboardView.Config));

    Assert.Contains("data-default-provider=\"Ollama\"", html, StringComparison.Ordinal);
    Assert.Contains("name=\"name\" value=\"Ollama developer\"", html, StringComparison.Ordinal);
    Assert.Contains("<option value=\"Ollama\" selected>Ollama</option>", html, StringComparison.Ordinal);
    Assert.Contains("<option value=\"qwen2.5-coder:7b\" selected>Qwen2.5 Coder 7B</option>", html, StringComparison.Ordinal);
    Assert.Contains("<option value=\"\" selected>None</option>", html, StringComparison.Ordinal);
    Assert.Contains("form.dataset.defaultProvider || 'OpenAI'", DashboardAssets.OperatorControlsScript, StringComparison.Ordinal);
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
    var html = DashboardRenderer.Render(kernel, RenderOptions(
        EnableOperatorControls: true,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix));
    var preparedControls = ExtractTaskControls(html, 1);
    var runningControls = ExtractTaskControls(html, 2);
    var noProcessControls = ExtractTaskControls(html, 3);

    Assert.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/1/start?confirmDispatchStart=true\"", preparedControls, StringComparison.Ordinal);
    Assert.False(preparedControls.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/1/refresh\"", StringComparison.Ordinal));
    Assert.False(preparedControls.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/1/cancel\"", StringComparison.Ordinal));
    Assert.Contains("Task has no background process to refresh.", preparedControls, StringComparison.Ordinal);
    Assert.Contains("Task has no background process to cancel.", preparedControls, StringComparison.Ordinal);

    Assert.False(runningControls.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/2/start\"", StringComparison.Ordinal));
    Assert.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/2/refresh\"", runningControls, StringComparison.Ordinal);
    Assert.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/2/cancel\"", runningControls, StringComparison.Ordinal);
    Assert.Contains("Task already has a running process pid=1234.", runningControls, StringComparison.Ordinal);

    Assert.False(noProcessControls.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/3/start\"", StringComparison.Ordinal));
    Assert.False(noProcessControls.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/3/refresh\"", StringComparison.Ordinal));
    Assert.False(noProcessControls.Contains($"data-action-button=\"/api/goals/{goalPrefix}/tasks/3/cancel\"", StringComparison.Ordinal));
    Assert.Contains("Task has no recorded dispatch.", noProcessControls, StringComparison.Ordinal);
    Assert.Contains("Task has no background process to refresh.", noProcessControls, StringComparison.Ordinal);
    Assert.Contains("Task has no background process to cancel.", noProcessControls, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "Dashboard_surfaces_dispatch_heartbeat_status_for_running_processes")]
    public void DashboardSurfacesDispatchHeartbeatStatusForRunningProcesses()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Render heartbeat", [new TaskSpec(TaskId.New(), "Run process", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.Single();
    var exitPath = Path.Combine(root, "worker.exit.txt");
    var process = new TaskProcessRecord(
        321,
        "codex exec prompt.md",
        root,
        Path.Combine(root, "worker.out.log"),
        Path.Combine(root, "worker.err.log"),
        exitPath,
        DateTimeOffset.Parse("2026-06-12T19:59:00Z"),
        null,
        null);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", process.Command, root, DateTimeOffset.Parse("2026-06-12T19:58:00Z")));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    var heartbeatPath = BackgroundDispatchRunner.GetHeartbeatPath(process);
    File.WriteAllText(heartbeatPath, """
{"pid":321,"childPid":654,"ownedPids":[321,987],"state":"running","lastObservedAt":"2026-06-12T20:00:10Z","lastProgressAt":"2026-06-12T20:00:00Z","stdoutBytes":99,"stderrBytes":11}
""");

    var detail = DashboardResponseMapper.ToTaskDetailDto(goal, task);
    var logs = DashboardResponseMapper.ToProcessLogDto(goal, task);
    var workSummary = DashboardResponseMapper.ToGoalWorkSummaryDto(kernel, goal, WorkerProfileCatalog.Default());
    var html = DashboardRenderer.Render(kernel, RenderOptions(EnableOperatorControls: true, View: DashboardView.Goal, FocusGoalPrefix: goal.Id.Value[..8]));
    var transcript = GoalTranscriptRenderer.Render(kernel, goal, WorkerProfileCatalog.Default());

    Assert.True(detail.LastProcess!.Heartbeat.IsAvailable);
    Assert.Equal(heartbeatPath, detail.LastProcess.Heartbeat.Path);
    Assert.Equal("running", detail.LastProcess.Heartbeat.State);
    Assert.Equal(654, detail.LastProcess.Heartbeat.ChildProcessId);
    Assert.Equal<int>([321, 987], detail.LastProcess.Heartbeat.OwnedProcessIds);
    Assert.Equal(99, detail.LastProcess.Heartbeat.StandardOutputBytes);
    Assert.Equal(11, detail.LastProcess.Heartbeat.StandardErrorBytes);
    Assert.NotNull(detail.LastProcess.HeartbeatAgeSeconds);
    Assert.NotNull(detail.LastProcess.HeartbeatIdleDurationSeconds);
    Assert.Equal(99, detail.LastProcess.HeartbeatStdoutBytes);
    Assert.Equal(11, detail.LastProcess.HeartbeatStderrBytes);
    Assert.Equal(heartbeatPath, detail.LastProcess.HeartbeatPath);
    Assert.True(logs.Heartbeat.IsAvailable);
    Assert.Equal(heartbeatPath, logs.Heartbeat.Path);
    Assert.NotNull(logs.HeartbeatAgeSeconds);
    Assert.NotNull(logs.HeartbeatIdleDurationSeconds);
    Assert.Equal(99, logs.HeartbeatStdoutBytes);
    Assert.Equal(11, logs.HeartbeatStderrBytes);
    Assert.Equal(heartbeatPath, logs.HeartbeatPath);
    Assert.Equal(heartbeatPath, workSummary.Tasks.Single().LastProcess!.Heartbeat.Path);
    Assert.Contains("heartbeat running", html, StringComparison.Ordinal);
    Assert.Contains("owned_pids=321,987", html, StringComparison.Ordinal);
    Assert.Contains("stdout_bytes=99", html, StringComparison.Ordinal);
    Assert.Contains(heartbeatPath, html, StringComparison.Ordinal);
    Assert.Contains("heartbeat: available state=running pid=321 child_pid=654 owned_pids=321,987", transcript, StringComparison.Ordinal);
    Assert.Contains("log bytes: stdout=99 stderr=11", transcript, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "Dashboard_omits_dispatch_heartbeat_status_when_file_is_missing")]
    public void DashboardOmitsDispatchHeartbeatStatusWhenFileIsMissing()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Render missing heartbeat", [new TaskSpec(TaskId.New(), "Run process", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.Single();
    var process = new TaskProcessRecord(
        321,
        "codex exec prompt.md",
        root,
        Path.Combine(root, "worker.out.log"),
        Path.Combine(root, "worker.err.log"),
        Path.Combine(root, "worker.exit.txt"),
        DateTimeOffset.Parse("2026-06-12T19:59:00Z"),
        null,
        null);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", process.Command, root, DateTimeOffset.Parse("2026-06-12T19:58:00Z")));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    var heartbeatPath = BackgroundDispatchRunner.GetHeartbeatPath(process);

    var detail = DashboardResponseMapper.ToTaskDetailDto(goal, task);
    var logs = DashboardResponseMapper.ToProcessLogDto(goal, task);
    var html = DashboardRenderer.Render(kernel, RenderOptions(EnableOperatorControls: true, View: DashboardView.Goal, FocusGoalPrefix: goal.Id.Value[..8]));

    Assert.False(detail.LastProcess!.Heartbeat.IsAvailable);
    Assert.Null(detail.LastProcess.HeartbeatAgeSeconds);
    Assert.Null(detail.LastProcess.HeartbeatIdleDurationSeconds);
    Assert.Null(detail.LastProcess.HeartbeatStdoutBytes);
    Assert.Null(detail.LastProcess.HeartbeatStderrBytes);
    Assert.Null(detail.LastProcess.HeartbeatPath);
    Assert.False(logs.Heartbeat.IsAvailable);
    Assert.Null(logs.HeartbeatAgeSeconds);
    Assert.Null(logs.HeartbeatIdleDurationSeconds);
    Assert.Null(logs.HeartbeatStdoutBytes);
    Assert.Null(logs.HeartbeatStderrBytes);
    Assert.Null(logs.HeartbeatPath);
    Assert.DoesNotContain("heartbeat unavailable", html, StringComparison.Ordinal);
    Assert.DoesNotContain(heartbeatPath, html, StringComparison.Ordinal);
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
    var control = DashboardNextActionControls.Build(goal, item, WorkerProfileCatalog.Default(), agents);

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

static DashboardRenderOptions RenderOptions(
    int? AutoRefreshSeconds = null,
    bool EnableOperatorControls = false,
    OrchestratorHealthReport? HealthReport = null,
    DashboardWorkspaceContext? Workspace = null,
    IReadOnlyList<DashboardContinuationStatusDto>? ContinuationWatches = null,
    string? FocusGoalPrefix = null,
    DashboardView View = DashboardView.Ops,
    IReadOnlyList<AgentDefinition>? AgentDefinitions = null,
    WorkerProfileCatalog? WorkerProfiles = null,
    OperatorInboxReportDto? OperatorInbox = null,
    IReadOnlyList<TaskDurationStatsDto>? TaskDurationStats = null,
    IReadOnlyList<string>? FocusGoalChangedFiles = null) =>
    new(
        AutoRefreshSeconds,
        EnableOperatorControls,
        HealthReport,
        Workspace,
        ContinuationWatches,
        FocusGoalPrefix,
        View,
        AgentDefinitions,
        WorkerProfiles ?? WorkerProfileCatalog.Default(),
        OperatorInbox,
        TaskDurationStats,
        FocusGoalChangedFiles);

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
          false,
          "none",
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
    var defaultHtml = DashboardRenderer.Render(kernel, RenderOptions(EnableOperatorControls: true));

    // Goal detail view for focused goal shows task actions
    var goalDetailHtml = DashboardRenderer.Render(
        kernel,
        RenderOptions(
            EnableOperatorControls: true,
            FocusGoalPrefix: focusedPrefix,
            View: DashboardView.Goal));

    // Ops view with focus query param shows focused goal in ops list
    var focusedOpsHtml = DashboardRenderer.Render(
        kernel,
        RenderOptions(
            EnableOperatorControls: true,
            FocusGoalPrefix: focusedPrefix));

    Assert.Contains("Goal Archive", defaultHtml, StringComparison.Ordinal);
    Assert.Contains("id=\"goal-archive-focus\"", defaultHtml, StringComparison.Ordinal);
    Assert.Contains("list=\"goal-archive-options\"", defaultHtml, StringComparison.Ordinal);
    Assert.Contains($"<option value=\"{focusedPrefix}\">Focused older goal</option>", defaultHtml, StringComparison.Ordinal);
    Assert.Contains($"href=\"/?goal={focusedPrefix}\"", defaultHtml, StringComparison.Ordinal);
    Assert.Contains("Focused older goal", focusedOpsHtml, StringComparison.Ordinal);
    Assert.Contains($"Focused goal filter: <code>{focusedPrefix}</code>", focusedOpsHtml, StringComparison.Ordinal);
    Assert.Contains($"value=\"{focusedPrefix}\"", focusedOpsHtml, StringComparison.Ordinal);
    Assert.Contains($"data-action=\"/api/goals/{focusedPrefix}/tasks/1/complete-verify\"", goalDetailHtml, StringComparison.Ordinal);
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
        kernel.CompleteGoal(completed.Id, "Done.");
        oldestCompletedPrefix ??= completed.Id.Value[..8];
    }

    var activePrefix = active.Id.Value[..8];
    var html = DashboardRenderer.Render(kernel, RenderOptions(EnableOperatorControls: true));

    Assert.Contains($"<h2><a href=\"/goal/{activePrefix}\">Active older goal</a></h2>", html, StringComparison.Ordinal);
    Assert.False(html.Contains($"<h2><a href=\"/goal/{oldestCompletedPrefix}\">Completed recent goal 0</a></h2>", StringComparison.Ordinal));
    Assert.Contains($"<option value=\"{oldestCompletedPrefix}\">Completed recent goal 0</option>", html, StringComparison.Ordinal);
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
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", "C:\\repo", now, WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        "codex exec",
        "C:\\repo",
        1,
        string.Empty,
        $"ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at {retryTime:h:mm tt}.",
        now));

    var goalPrefix = goal.Id.Value[..8];

    // Goal detail view shows evidence and retry queue
    var goalHtml = DashboardRenderer.Render(kernel, RenderOptions(
        EnableOperatorControls: true,
        View: DashboardView.Goal,
        FocusGoalPrefix: goalPrefix));

    Assert.Contains("Retry later", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Recoverable subscription usage limit", goalHtml, StringComparison.Ordinal);
    Assert.Contains("task Assigned", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Subscription retry queue", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Subscription handoff is paused", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Retry after", goalHtml, StringComparison.Ordinal);
    Assert.Contains("1 limit failure", goalHtml, StringComparison.Ordinal);
    Assert.Contains("Developer", goalHtml, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "DashboardRenderer_html_encodes_dynamic_content")]
    public void DashboardRendererHtmlEncodesDynamicContent()
{
    var kernel = new AgentOrchestratorKernel();
    kernel.CreateGoal("<script>alert(1)</script>");

    var html = DashboardRenderer.Render(kernel);

    Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
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

    var transcript = GoalTranscriptRenderer.Render(kernel, goal, WorkerProfileCatalog.Default());

    Assert.Contains("# Goal", transcript, StringComparison.Ordinal);
    Assert.Contains("Objective: Export transcript", transcript, StringComparison.Ordinal);
    Assert.Contains("## Recommended Next Steps", transcript, StringComparison.Ordinal);
    Assert.Contains("## Goal Completion", transcript, StringComparison.Ordinal);
    Assert.Contains("Accepted: False", transcript, StringComparison.Ordinal);
    Assert.Contains("## Recorded Proof", transcript, StringComparison.Ordinal);
    Assert.Contains("dispatch: 1", transcript, StringComparison.Ordinal);
    Assert.Contains("## Task Readiness", transcript, StringComparison.Ordinal);
    Assert.Contains("Ready for acceptance: False", transcript, StringComparison.Ordinal);
    Assert.Contains("## Verification Status", transcript, StringComparison.Ordinal);
    Assert.Contains("## Human Decisions", transcript, StringComparison.Ordinal);
    Assert.Contains("Which branch?", transcript, StringComparison.Ordinal);
    Assert.Contains($"Suggested command: answer {request.Id.Value[..8]} <answer>", transcript, StringComparison.Ordinal);
    Assert.Contains("## Verification To-Do", transcript, StringComparison.Ordinal);
    Assert.Contains("Suggested command: task 1", transcript, StringComparison.Ordinal);
    Assert.Contains("retry 4 <note>", transcript, StringComparison.Ordinal);
    Assert.Contains("provider-smoke openai", transcript, StringComparison.Ordinal);
    Assert.Contains($"Verification plan: {task.VerificationPlan}", transcript, StringComparison.Ordinal);
    Assert.Contains("OpenAI: ok", transcript, StringComparison.Ordinal);
    Assert.Contains("Task timeline", transcript, StringComparison.Ordinal);

    var readyKernel = new AgentOrchestratorKernel();
    var readyGoal = readyKernel.CreateGoal("Ready transcript");
    var readyAgents = AgentCatalog.Default().Agents;
    readyKernel.ActivateGoal(readyGoal.Id, readyAgents);
    var readyTranscript = GoalTranscriptRenderer.Render(readyKernel, readyGoal, WorkerProfileCatalog.Default());
    Assert.Contains("Suggested command: run", readyTranscript, StringComparison.Ordinal);
    Assert.False(readyTranscript.Contains("subscription-dispatch 1 | api-run 1", StringComparison.Ordinal));
    var costAwareTranscript = GoalTranscriptRenderer.Render(readyKernel, readyGoal, WorkerProfileCatalog.Default(), readyAgents);
    var nextSteps = costAwareTranscript[..costAwareTranscript.IndexOf("## Needs Attention", StringComparison.Ordinal)];
    Assert.Contains("Suggested command: subscription-dispatch 1", nextSteps, StringComparison.Ordinal);
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

private static void RunGit(string workingDirectory, params string[] arguments)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "git",
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git.");
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(30000))
    {
        string termination;
        try
        {
            process.Kill(entireProcessTree: true);
            termination = process.WaitForExit(5000)
                ? "process tree terminated"
                : "process tree did not exit within 5 seconds after termination";
        }
        catch (InvalidOperationException)
        {
            termination = "process exited before termination";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            termination = $"process termination failed: {ex.Message}";
        }

        var timedOutOutput = outputTask.IsCompletedSuccessfully ? outputTask.Result : "<stream still open>";
        var timedOutError = errorTask.IsCompletedSuccessfully ? errorTask.Result : "<stream still open>";
        throw new TimeoutException(
            $"git {string.Join(' ', arguments)} did not exit within 30 seconds; {termination}: stdout={timedOutOutput} stderr={timedOutError}");
    }

    var output = outputTask.GetAwaiter().GetResult();
    var error = errorTask.GetAwaiter().GetResult();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit {process.ExitCode}: {output}{error}");
    }
}

private static void WriteHeartbeat(
    TaskProcessRecord process,
    DateTimeOffset lastObservedAt,
    DateTimeOffset lastProgressAt,
    int? childPid = null,
    IReadOnlyList<int>? ownedPids = null)
{
    var heartbeat = new
    {
        pid = process.ProcessId,
        childPid,
        ownedPids = ownedPids ?? Array.Empty<int>(),
        startedAt = process.StartedAt,
        lastObservedAt,
        lastProgressAt,
        state = "running",
        stdoutBytes = 13,
        stderrBytes = 0,
        exitFileExists = false
    };
    File.WriteAllText(
        BackgroundDispatchRunner.GetHeartbeatPath(process),
        JsonSerializer.Serialize(heartbeat));
}
}

public sealed class GoalScopeCollisionDashboardMapperTests
{
    [Xunit.Fact(DisplayName = "Dashboard_scope_collision_advisory_mapper_preserves_operator_evidence")]
    public void PreservesOperatorEvidence()
    {
        var goal = new AgentOrchestratorKernel().CreateGoal(
            "Change src/Feature/File.cs.",
            [new TaskSpec(TaskId.New(), "Implement planned work.", AgentRole.Developer)]);
        var report = GoalScopeCollisionAdvisor.Build(
            ["Also change src/Feature/File.cs."],
            [goal]);
        var item = new BacklogIntakeItem(
            "backlog-1",
            "Feature change",
            "Body",
            ["src/Feature/File.cs"],
            [AgentRole.Developer],
            ["concurrency"],
            ["Focused tests."],
            ["none"],
            "Also change src/Feature/File.cs.",
            "workspace",
            "acceptance",
            "follow-up",
            RepositoryScopeConfidence.Precise,
            []);

        var dto = DashboardResponseMapper.ToGoalScopeCollisionAdvisoryDto([(item, report)]);

        var mappedReport = Xunit.Assert.Single(dto.Reports);
        var collision = Xunit.Assert.Single(mappedReport.Collisions);
        Xunit.Assert.Equal("overlap-detected", mappedReport.Verdict);
        Xunit.Assert.Equal(goal.Id.Value, collision.GoalId);
        Xunit.Assert.Equal("src/Feature/File.cs", collision.ProposedPath);
        Xunit.Assert.Equal("Explicit", collision.ProposedProvenance);
        Xunit.Assert.Equal("Explicit", collision.ConflictingProvenance);
    }
}
