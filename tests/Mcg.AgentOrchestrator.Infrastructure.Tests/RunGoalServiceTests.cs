using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RunGoalServiceTests
{
    private static readonly RunGoalService.SleepFunc NoSleep = async (_, _) => await Task.Yield();

    private static RunGoalService.SleepFunc WaitForNextExitFile(string logDirectory)
    {
        var seen = 0;
        return async (_, ct) =>
        {
            while (Directory.EnumerateFiles(logDirectory, "*.exit.txt").Count() <= seen)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            seen = Directory.EnumerateFiles(logDirectory, "*.exit.txt").Count();
        };
    }

    private static AgentDefinition EchoAgent() => new AgentDefinition(
        new AgentId("echo-planner"),
        "Echo Planner",
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-4o-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("local"));

    private static AgentDefinition SubscriptionPlanner(string id, string name, string profileName)
    {
        var providerName = profileName.Contains("claude", StringComparison.OrdinalIgnoreCase)
            ? "Anthropic"
            : profileName.Contains("qwen", StringComparison.OrdinalIgnoreCase)
                ? "Ollama"
                : "OpenAI";
        var modelName = providerName switch
        {
            "Anthropic" => "claude-haiku-4-5",
            "Ollama" => "qwen3:8b",
            _ => "gpt-5.5"
        };

        return new AgentDefinition(
            new AgentId(id),
            name,
            AgentRole.Planner,
            new ModelProfile(providerName, modelName, ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile(profileName));
    }

    private static string PlannerSuccessCommand(string marker)
    {
        var plan = WorkerDispatchTestSupport.PlannerContractPlanFixture().Replace(
            "`seed.txt`, ",
            string.Empty,
            StringComparison.Ordinal);
        var encodedPlan = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plan));
        return $"Write-Output ([System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encodedPlan}'))); Write-Output {marker}";
    }

    private static WorkerProfileCatalog EchoProfiles() => new WorkerProfileCatalog(
    [
        new WorkerProfile("local", $"Start-Sleep -Milliseconds 100; {PlannerSuccessCommand("{subscriptionModelName}")}")
    ]);

    private static WorkerProfileCatalog Profiles(params WorkerProfile[] profiles) => new WorkerProfileCatalog(profiles);

    private static string DescribeRunGoalStop(RunGoalService.RunGoalResult result, TaskSpec task)
    {
        var verification = task.LastVerification ?? task.VerificationHistory.LastOrDefault();
        var output = verification is null
            ? "none"
            : string.Join(" ", verification.StandardOutput, verification.StandardError).Trim();
        return $"stopReason={result.StopReason}; stopEvidence={result.StopEvidence?.Reason ?? "none"}; status={task.Status}; assigned={task.AssignedAgentId?.Value ?? "none"}; dispatch={task.LastDispatch?.WorkerName ?? "none"}; exit={verification?.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}; output={output}";
    }

    private static Goal CreateRefinedGoal(AgentOrchestratorKernel kernel, string objective, IReadOnlyList<TaskSpec> tasks)
    {
        var goal = kernel.CreateGoal(objective, tasks);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            objective,
            ["RunGoalService fixture goal is already refined."],
            VerificationClass.TestVerifiable,
            [],
            []));
        return goal;
    }

    private static void RecordRecoverableUsageLimit(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        OrchestratorWorkspace workspace,
        string command,
        DateTimeOffset completedAt,
        string output)
    {
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", command, workspace.ExecutionDirectory, completedAt, WorkerProviderKind: ProviderKind.OpenAICodexCli));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            command,
            workspace.ExecutionDirectory,
            1,
            string.Empty,
            output,
            completedAt));
    }

    private static void RecordHeartbeatStall(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        OrchestratorWorkspace workspace,
        string command,
        DateTimeOffset completedAt)
    {
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", command, workspace.ExecutionDirectory, completedAt));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            command,
            workspace.ExecutionDirectory,
            1,
            string.Empty,
            "Background dispatch made no observable progress before the stall timeout; wrapper heartbeat state=running.",
            completedAt));
    }

    private static void RecordProviderConnectivityFailure(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        OrchestratorWorkspace workspace,
        string workerName,
        string command,
        DateTimeOffset completedAt,
        string output)
    {
        var providerKind = WorkerProviderCatalog.Default().ResolveProfile(workerName).Identity.Kind;
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            workerName,
            command,
            workspace.ExecutionDirectory,
            completedAt,
            WorkerProviderKind: providerKind));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            command,
            workspace.ExecutionDirectory,
            1,
            string.Empty,
            output,
            completedAt));
    }

    [Xunit.Fact(DisplayName = "RunGoalService_completes_all_tasks_sequentially_and_stops_with_no_actions")]
    public async Task RunGoalServiceCompletesAllTasksSequentiallyAndStopsWithNoActions()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task1 = new TaskSpec(TaskId.New(), "First echo task", AgentRole.Planner);
        var task2 = new TaskSpec(TaskId.New(), "Second echo task", AgentRole.Planner);
        var goal = CreateRefinedGoal(kernel, "Sequential echo run", [task1, task2]);
        var agent = EchoAgent();
        var profiles = EchoProfiles();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await RunGoalService.RunAsync(
            kernel,
            [agent],
            profiles,
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            pollInterval: TimeSpan.FromMilliseconds(50),
            sleep: WaitForNextExitFile(workspace.LogDirectory),
            cancellationToken: cts.Token);

        Assert.True(result.Executed);
        Assert.Equal(2, result.CompletedTasks.Count);
        Assert.True(result.CompletedTasks.All(t => t.Succeeded));
        Assert.True(result.ContinueAfter is null);
        Assert.True(result.StopEvidence is null);
        Assert.Equal(WorkTaskStatus.Completed, task1.Status);
        Assert.Equal(WorkTaskStatus.Completed, task2.Status);
    }

    [Xunit.Fact(DisplayName = "RunGoalService_stops_on_task_failure")]
    public async Task RunGoalServiceStopsOnTaskFailure()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Task that will fail", AgentRole.Planner);
        var goal = CreateRefinedGoal(kernel, "Goal with failing task", [task]);
        var agent = EchoAgent();
        kernel.ActivateGoal(goal.Id, [agent]);
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "Write-Output failed", workspace.ExecutionDirectory, now));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "Write-Output failed",
            workspace.ExecutionDirectory,
            1,
            string.Empty,
            "Simulated failure output.",
            now));

        var result = await RunGoalService.RunAsync(
            kernel,
            [agent],
            EchoProfiles(),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep);

        Assert.False(result.Executed);
        Assert.Equal(NextActionKind.InspectFailedTask, result.BlockingAction?.Kind);
        Assert.True(result.ContinueAfter is null);
        Assert.Contains("Simulated failure output.", result.StopEvidence?.OutputTail ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
    }

    [Xunit.Fact(DisplayName = "RunGoalService_stops_on_human_input_request")]
    public async Task RunGoalServiceStopsOnHumanInputRequest()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Task requiring human input", AgentRole.Planner);
        var goal = CreateRefinedGoal(kernel, "Goal with human input block", [task]);
        var agent = EchoAgent();
        kernel.ActivateGoal(goal.Id, [agent]);
        kernel.RequestHumanInput(goal.Id, task.Id, "Which approach should be used?");

        var result = await RunGoalService.RunAsync(
            kernel,
            [agent],
            EchoProfiles(),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep);

        Assert.False(result.Executed);
        Assert.Equal(NextActionKind.AnswerHumanInput, result.BlockingAction?.Kind);
        Assert.True(result.ContinueAfter is null);
        Assert.Equal(1, result.StopEvidence?.TaskNumber);
    }

    [Xunit.Fact(DisplayName = "RunGoalService_stops_on_subscription_limit_review_required")]
    public async Task RunGoalServiceStopsOnSubscriptionLimitReviewRequired()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Task with repeated limit failures", AgentRole.Planner);
        var goal = CreateRefinedGoal(kernel, "Goal hitting usage limit", [task]);
        var agent = EchoAgent();
        kernel.ActivateGoal(goal.Id, [agent]);
        var now = DateTimeOffset.UtcNow;
        var limitOutput = "ERROR: You've hit your usage limit. Visit settings to purchase more credits.";

        for (var i = 0; i < 2; i++)
        {
            var cmd = $"cmd{i}";
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", cmd, workspace.ExecutionDirectory, now, WorkerProviderKind: ProviderKind.OpenAICodexCli));
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
                cmd, workspace.ExecutionDirectory, 1, string.Empty, limitOutput, now));
        }

        var result = await RunGoalService.RunAsync(
            kernel,
            [agent],
            EchoProfiles(),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep);

        Assert.False(result.Executed);
        Assert.Contains("usage limit", result.StopReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.ContinueAfter is null);
        Assert.Equal(1, result.StopEvidence?.TaskNumber);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    }

    [Xunit.Fact(DisplayName = "RunGoalService_stops_with_alternate_guidance_on_retry_window_without_alternate")]
    public async Task RunGoalServiceStopsWithAlternateGuidanceOnRetryWindowWithoutAlternate()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Task deferred by retry window", AgentRole.Planner);
        var goal = CreateRefinedGoal(kernel, "Goal with subscription retry deferral", [task]);
        var agent = EchoAgent();
        kernel.ActivateGoal(goal.Id, [agent]);
        var now = DateTimeOffset.UtcNow;
        var retryTime = now.AddHours(1);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "codex exec", workspace.ExecutionDirectory, now, WorkerProviderKind: ProviderKind.OpenAICodexCli));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec",
            workspace.ExecutionDirectory,
            1,
            string.Empty,
            $"ERROR: You've hit your usage limit. Visit settings to purchase more credits or try again at {retryTime:h:mm tt}.",
            now));

        var result = await RunGoalService.RunAsync(
            kernel,
            [agent],
            EchoProfiles(),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep);

        Assert.False(result.Executed);
        Assert.True(result.ContinueAfter is null);
        Assert.Contains("alternate", result.StopReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Planner", result.StopReason, StringComparison.Ordinal);
        Assert.Contains("retry deferral", result.StopReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, result.StopEvidence?.TaskNumber);
    }

    [Xunit.Fact(DisplayName = "RunGoalService_auto_failover_usage_limit_redelegates_and_continues")]
    public async Task RunGoalServiceAutoFailoverUsageLimitRedelegatesAndContinues()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Task with recoverable usage limit", AgentRole.Planner);
        var goal = CreateRefinedGoal(kernel, "Goal should fail over usage limit", [task]);
        var limited = SubscriptionPlanner("limited-planner", "Limited Planner", "limited");
        var alternate = SubscriptionPlanner("alternate-planner", "Alternate Planner", "alternate");
        kernel.ActivateGoal(goal.Id, [limited, alternate]);
        RecordRecoverableUsageLimit(
            kernel,
            goal,
            task,
            workspace,
            "limited command",
            DateTimeOffset.UtcNow,
            "ERROR: You've hit your usage limit. Visit settings to purchase more credits.");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await RunGoalService.RunAsync(
            kernel,
            [limited, alternate],
            Profiles(new WorkerProfile("alternate", PlannerSuccessCommand("{subscriptionModelName}; Write-Output alternate-ok"))),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            pollInterval: TimeSpan.FromMilliseconds(50),
            sleep: WaitForNextExitFile(workspace.LogDirectory),
            cancellationToken: cts.Token);

        Assert.True(result.Executed);
        Assert.True(result.StopEvidence is null);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(alternate.Id, task.AssignedAgentId);
        Assert.Equal("alternate", task.LastDispatch!.WorkerName);
        Assert.Contains("alternate-ok", task.LastVerification!.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(2, task.VerificationHistory.Count);
        Assert.Equal(1, result.CompletedTasks.Count);
        Assert.True(result.CompletedTasks.Single().Succeeded);
        Assert.True(goal.Timeline.Any(evt => evt.Kind == ProgressKind.TaskRedelegated && evt.Message.Contains("alternate-planner", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "RunGoalService_auto_failover_uses_added_same_role_catalog_alternate")]
    public async Task RunGoalServiceAutoFailoverUsesAddedSameRoleCatalogAlternate()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var alternate = new AgentDefinition(
            new AgentId("ollama-planner-qwen-fallback"),
            "Qwen fallback",
            AgentRole.Planner,
            new ModelProfile("Ollama", "qwen3:8b", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("qwen-code-cli"));
        var agents = AgentCatalog.Default().AddOrReplaceById(alternate).Agents;
        var task = new TaskSpec(TaskId.New(), "Task with catalog failover alternate", AgentRole.Planner);
        var goal = CreateRefinedGoal(kernel, "Goal should fail over to a catalog alternate", [task]);
        kernel.ActivateGoal(goal.Id, agents);
        Assert.Equal("openai-planner", task.AssignedAgentId!.Value);
        RecordRecoverableUsageLimit(
            kernel,
            goal,
            task,
            workspace,
            "primary command",
            DateTimeOffset.UtcNow,
            "ERROR: You've hit your usage limit. Visit settings to purchase more credits.");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await RunGoalService.RunAsync(
            kernel,
            agents,
            Profiles(new WorkerProfile("qwen-code-cli", PlannerSuccessCommand("{subscriptionModelName}; Write-Output catalog-alternate-ok"))),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            pollInterval: TimeSpan.FromMilliseconds(50),
            sleep: WaitForNextExitFile(workspace.LogDirectory),
            cancellationToken: cts.Token);

        Assert.True(result.Executed);
        Xunit.Assert.True(task.Status == WorkTaskStatus.Completed, DescribeRunGoalStop(result, task));
        Xunit.Assert.True(result.StopEvidence is null, result.StopEvidence?.Reason ?? result.StopReason);
        Assert.Equal(alternate.Id, task.AssignedAgentId);
        Assert.Equal("qwen-code-cli", task.LastDispatch!.WorkerName);
        Assert.Contains("catalog-alternate-ok", task.LastVerification!.StandardOutput, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "RunGoalService_auto_failover_heartbeat_stall_redelegates")]
    public async Task RunGoalServiceAutoFailoverHeartbeatStallRedelegates()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Task with heartbeat stall", AgentRole.Planner);
        var goal = CreateRefinedGoal(kernel, "Goal should fail over heartbeat stall", [task]);
        var stalled = SubscriptionPlanner("stalled-planner", "Stalled Planner", "stalled");
        var alternate = SubscriptionPlanner("heartbeat-alternate", "Heartbeat Alternate", "alternate");
        kernel.ActivateGoal(goal.Id, [stalled, alternate]);
        RecordHeartbeatStall(kernel, goal, task, workspace, "stalled command", DateTimeOffset.UtcNow);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await RunGoalService.RunAsync(
            kernel,
            [stalled, alternate],
            Profiles(new WorkerProfile("alternate", PlannerSuccessCommand("{subscriptionModelName}; Write-Output heartbeat-ok"))),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            pollInterval: TimeSpan.FromMilliseconds(50),
            sleep: WaitForNextExitFile(workspace.LogDirectory),
            cancellationToken: cts.Token);

        Assert.True(result.Executed);
        Xunit.Assert.True(result.StopEvidence is null, DescribeRunGoalStop(result, task));
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(alternate.Id, task.AssignedAgentId);
        Assert.Equal("alternate", task.LastDispatch!.WorkerName);
        Assert.Contains("heartbeat-ok", task.LastVerification!.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(2, task.VerificationHistory.Count);
    }

    [Xunit.Fact(DisplayName = "RunGoalService_auto_failover_codex_websocket_connectivity_stops_when_no_alternate_exists")]
    public async Task RunGoalServiceAutoFailoverCodexWebsocketConnectivityStopsWhenNoAlternateExists()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Task with codex websocket failure", AgentRole.Planner);
        var goal = CreateRefinedGoal(kernel, "Goal should ask for alternate after codex connectivity failure", [task]);
        var primary = SubscriptionPlanner("codex-planner", "Codex Planner", "codex-cli");
        kernel.ActivateGoal(goal.Id, [primary]);
        RecordProviderConnectivityFailure(
            kernel,
            goal,
            task,
            workspace,
            "codex-cli",
            "codex-cli exec",
            DateTimeOffset.UtcNow,
            "Prompt reminder: include Model fit: and Changed files: in final output.\n" +
            "Error: websocket transport failed with OS error 10013 before session start.");

        var result = await RunGoalService.RunAsync(
            kernel,
            [primary],
            Profiles(new WorkerProfile("codex-cli", "Write-Output should-not-run")),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep);

        Assert.False(result.Executed);
        Assert.Contains("recoverable provider connectivity", result.StopReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no available unused alternate", result.StopReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Planner", result.StopReason, StringComparison.Ordinal);
        Assert.Contains("websocket", result.StopEvidence?.OutputTail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(primary.Id, task.AssignedAgentId);
    }

    [Xunit.Fact(DisplayName = "RunGoalService_auto_failover_provider_connectivity_ignores_prompt_echo_but_not_stdout_summary")]
    public async Task RunGoalServiceAutoFailoverProviderConnectivityIgnoresPromptEchoButNotStdoutSummary()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Task with useful stdout plus connectivity text", AgentRole.Planner);
        var goal = CreateRefinedGoal(kernel, "Goal should not fail over after useful stdout", [task]);
        var primary = SubscriptionPlanner("codex-planner", "Codex Planner", "codex-cli");
        kernel.ActivateGoal(goal.Id, [primary]);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex-cli exec", workspace.ExecutionDirectory, DateTimeOffset.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex-cli exec",
            workspace.ExecutionDirectory,
            1,
            "Changed files: src/example.cs\nModel fit: OpenAI/gpt-5.5 - adequate - useful work before provider error.",
            "Error: websocket transport failed with OS error 10013 after partial work.",
            DateTimeOffset.UtcNow));

        var result = await RunGoalService.RunAsync(
            kernel,
            [primary],
            Profiles(new WorkerProfile("codex-cli", "Write-Output should-not-run")),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep);

        Assert.False(result.Executed);
        Assert.False(result.StopReason.Contains("recoverable provider connectivity", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(primary.Id, task.AssignedAgentId);
    }

    [Xunit.Fact(DisplayName = "RunGoalService_auto_failover_claude_api_connectionrefused_stops_when_no_alternate_exists")]
    public async Task RunGoalServiceAutoFailoverClaudeApiConnectionRefusedStopsWhenNoAlternateExists()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Task with claude API connectivity failure", AgentRole.Planner);
        var goal = CreateRefinedGoal(kernel, "Goal should ask for alternate after claude connectivity failure", [task]);
        var primary = SubscriptionPlanner("claude-planner", "Claude Planner", "claude-cli");
        kernel.ActivateGoal(goal.Id, [primary]);
        RecordProviderConnectivityFailure(
            kernel,
            goal,
            task,
            workspace,
            "claude-cli",
            "claude-cli --print",
            DateTimeOffset.UtcNow,
            "Error: Unable to connect to API: ConnectionRefused while opening provider transport.");

        var result = await RunGoalService.RunAsync(
            kernel,
            [primary],
            Profiles(new WorkerProfile("claude-cli", "Write-Output should-not-run")),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep);

        Assert.False(result.Executed);
        Assert.Contains("recoverable provider connectivity", result.StopReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no available unused alternate", result.StopReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ConnectionRefused", result.StopEvidence?.OutputTail ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(primary.Id, task.AssignedAgentId);
    }

    [Xunit.Fact(DisplayName = "RunGoalService_auto_failover_provider_connectivity_redelegates_same_role_alternate_and_continues")]
    public async Task RunGoalServiceAutoFailoverProviderConnectivityRedelegatesSameRoleAlternateAndContinues()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Task with provider connectivity failover", AgentRole.Planner);
        var goal = CreateRefinedGoal(kernel, "Goal should continue with same-role alternate", [task]);
        var primary = SubscriptionPlanner("codex-planner", "Codex Planner", "codex-cli");
        var alternate = SubscriptionPlanner("qwen-planner", "Qwen Planner", "qwen-code-cli");
        kernel.ActivateGoal(goal.Id, [primary, alternate]);
        RecordProviderConnectivityFailure(
            kernel,
            goal,
            task,
            workspace,
            "codex-cli",
            "codex-cli exec",
            DateTimeOffset.UtcNow,
            "Error: websocket transport failed with os error 10013 before useful work.");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await RunGoalService.RunAsync(
            kernel,
            [primary, alternate],
            Profiles(new WorkerProfile("qwen-code-cli", PlannerSuccessCommand("{subscriptionModelName}; Write-Output connectivity-alternate-ok"))),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            pollInterval: TimeSpan.FromMilliseconds(50),
            sleep: WaitForNextExitFile(workspace.LogDirectory),
            cancellationToken: cts.Token);

        Assert.True(result.Executed);
        Xunit.Assert.True(result.StopEvidence is null, DescribeRunGoalStop(result, task));
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(alternate.Id, task.AssignedAgentId);
        Assert.Equal(task.RequiredRole, alternate.Role);
        Assert.Equal("qwen-code-cli", task.LastDispatch!.WorkerName);
        Assert.Contains("connectivity-alternate-ok", task.LastVerification!.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(2, task.VerificationHistory.Count);
        Assert.Contains("10013", task.VerificationHistory.First().StandardError, StringComparison.Ordinal);
        Assert.Equal(1, result.CompletedTasks.Count);
        Assert.True(result.CompletedTasks.Single().Succeeded);
        Assert.True(goal.Timeline.Any(evt => evt.Kind == ProgressKind.TaskRedelegated && evt.Message.Contains("qwen-planner", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "RunGoalService_auto_failover_provider_model_rejection_redelegates")]
    public async Task RunGoalServiceAutoFailoverProviderModelRejectionRedelegates()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Task with provider model rejection", AgentRole.Planner);
        var goal = CreateRefinedGoal(kernel, "Goal should fail over unsupported model", [task]);
        var primary = SubscriptionPlanner("codex-planner", "Codex Planner", "codex-cli");
        var alternate = SubscriptionPlanner("qwen-planner", "Qwen Planner", "qwen-code-cli");
        kernel.ActivateGoal(goal.Id, [primary, alternate]);
        RecordProviderConnectivityFailure(
            kernel,
            goal,
            task,
            workspace,
            "codex-cli",
            "codex-cli exec",
            DateTimeOffset.UtcNow,
            "ERROR: invalid model 'gpt-5.3-codex' does not exist for this account.");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await RunGoalService.RunAsync(
            kernel,
            [primary, alternate],
            Profiles(new WorkerProfile("qwen-code-cli", PlannerSuccessCommand("{subscriptionModelName}; Write-Output model-rejection-alternate-ok"))),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            pollInterval: TimeSpan.FromMilliseconds(50),
            sleep: WaitForNextExitFile(workspace.LogDirectory),
            cancellationToken: cts.Token);

        Assert.True(result.Executed);
        Xunit.Assert.True(result.StopEvidence is null, DescribeRunGoalStop(result, task));
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(alternate.Id, task.AssignedAgentId);
        Assert.Equal("qwen-code-cli", task.LastDispatch!.WorkerName);
        Assert.Contains("model-rejection-alternate-ok", task.LastVerification!.StandardOutput, StringComparison.Ordinal);
        Assert.True(goal.Timeline.Any(evt => evt.Kind == ProgressKind.TaskRedelegated && evt.Message.Contains("qwen-planner", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "RunGoalService_auto_failover_stops_when_no_alternate_exists")]
    public async Task RunGoalServiceAutoFailoverStopsWhenNoAlternateExists()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Task without alternate", AgentRole.Planner);
        var goal = CreateRefinedGoal(kernel, "Goal should ask for alternate", [task]);
        var limited = SubscriptionPlanner("limited-planner", "Limited Planner", "limited");
        kernel.ActivateGoal(goal.Id, [limited]);
        RecordRecoverableUsageLimit(
            kernel,
            goal,
            task,
            workspace,
            "limited command",
            DateTimeOffset.UtcNow,
            "ERROR: You've hit your usage limit. Visit settings to purchase more credits.");

        var result = await RunGoalService.RunAsync(
            kernel,
            [limited],
            Profiles(new WorkerProfile("limited", "Write-Output should-not-run")),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep);

        Assert.False(result.Executed);
        Assert.Contains("no available unused alternate", result.StopReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Planner", result.StopReason, StringComparison.Ordinal);
        Assert.Contains("limited-planner", result.StopReason, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(limited.Id, task.AssignedAgentId);
        Assert.Contains("usage limit", result.StopEvidence?.OutputTail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "RunGoalService_auto_failover_does_not_loop_back_to_failed_agent")]
    public async Task RunGoalServiceAutoFailoverDoesNotLoopBackToFailedAgent()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Task with two failed agents", AgentRole.Planner);
        var goal = CreateRefinedGoal(kernel, "Goal should not ping-pong failover", [task]);
        var first = SubscriptionPlanner("first-planner", "First Planner", "first");
        var second = SubscriptionPlanner("second-planner", "Second Planner", "second");
        kernel.ActivateGoal(goal.Id, [first, second]);
        RecordHeartbeatStall(kernel, goal, task, workspace, "first command", DateTimeOffset.UtcNow);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await RunGoalService.RunAsync(
            kernel,
            [first, second],
            Profiles(new WorkerProfile("second", "Write-Output {subscriptionModelName}; Write-Output 'Background dispatch made no observable progress before the stall timeout; wrapper heartbeat state=running.'; exit 1")),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            pollInterval: TimeSpan.FromMilliseconds(50),
            sleep: WaitForNextExitFile(workspace.LogDirectory),
            cancellationToken: cts.Token);

        Assert.True(result.Executed);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(second.Id, task.AssignedAgentId);
        Assert.Contains("Previously failed agent(s): first-planner, second-planner", result.StopReason, StringComparison.Ordinal);
        Assert.Equal(1, goal.Timeline.Count(evt => evt.Kind == ProgressKind.TaskRedelegated));
        Assert.False(goal.Timeline.Any(evt =>
            evt.Kind == ProgressKind.TaskRedelegated &&
            evt.Message.Contains("to agent 'first-planner'", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "RunGoalService_stops_when_cost_guard_flag_missing")]
    public async Task RunGoalServiceStopsWhenCostGuardFlagMissing()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Planner);
        var goal = CreateRefinedGoal(kernel, "Goal with oversized paid prompt", [task]);
        var agent = EchoAgent();
        kernel.ActivateGoal(goal.Id, [agent]);
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "local", "Write-Output gpt-4o-mini", workspace.ExecutionDirectory, now,
            ProviderName: "OpenAI", ModelName: "gpt-4o-mini", PromptCharacterCount: 13000));

        var result = await RunGoalService.RunAsync(
            kernel,
            [agent],
            EchoProfiles(),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep);

        Assert.Contains("--confirm-large-paid-subscription-start", result.StopReason, StringComparison.Ordinal);
        Assert.True(result.ContinueAfter is null);
        Assert.Equal(1, result.StopEvidence?.TaskNumber);
    }
}
