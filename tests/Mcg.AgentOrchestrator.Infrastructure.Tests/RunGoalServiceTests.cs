using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// This class pins RunGoalService control flow only. Every fact injects a scripted in-process advance
// step, so no fact starts an operating-system process and no fact needs an xunit Timeout. The single
// real-process contract lives in RunGoalServiceProcessContractTests.
public sealed class RunGoalServiceTests
{
    private static readonly RunGoalService.SleepFunc NoSleep = async (_, _) => await Task.Yield();

    // The injected advance step never launches a worker, so profile commands exist only to keep the
    // catalog well formed for the profile names the agents reference.
    private const string UnexecutedProfileCommand = "Write-Output run-goal-service-tests-never-executes-this";

    private static readonly string PlannerPlanText = WorkerDispatchTestSupport.PlannerContractPlanFixture().Replace(
        "`seed.txt`, ",
        string.Empty,
        StringComparison.Ordinal);

    private static string CreateTempDirectory()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        _ = StateDbMigrations.EnsureUpToDate(OrchestratorWorkspace.ForDirectory(root).SqliteStatePath);
        return root;
    }

    private static WorkerProfileCatalog ProfileNames(params string[] names) => new WorkerProfileCatalog(
        [.. names.Select(name => new WorkerProfile(name, UnexecutedProfileCommand))]);

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
                ? "LlamaCpp"
                : "OpenAI";
        var modelName = providerName switch
        {
            "Anthropic" => "claude-haiku-4-5",
            "LlamaCpp" => LlamaCppDefaults.DefaultModelAlias,
            _ => AgentCatalog.OpenAiSubscriptionModelAlias
        };

        return new AgentDefinition(
            new AgentId(id),
            name,
            AgentRole.Planner,
            new ModelProfile(providerName, modelName, ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile(profileName));
    }

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

    // ---------------------------------------------------------------------------------------------
    // Injected advance step. Mirrors GoalAdvancementOperations.AdvanceGoalWithSubscriptionsUntilBlocked:
    // each scripted iteration records on the kernel exactly what the real step would have recorded for
    // that iteration - dispatches, verification results, failures, completions - and returns the loop
    // outcome the real step would have returned.
    // ---------------------------------------------------------------------------------------------

    private sealed record AdvanceStepCall(
        AgentOrchestratorKernel Kernel,
        IReadOnlyList<AgentDefinition> Agents,
        OrchestratorWorkspace Workspace,
        Goal Goal);

    private sealed class ScriptedAdvanceStep
    {
        private readonly List<Func<AdvanceStepCall, GoalAdvanceLoopOutcome>> iterations;
        private int completedCalls;

        private ScriptedAdvanceStep(IEnumerable<Func<AdvanceStepCall, GoalAdvanceLoopOutcome>> iterations)
            => this.iterations = [.. iterations];

        public static ScriptedAdvanceStep Script(params Func<AdvanceStepCall, GoalAdvanceLoopOutcome>[] iterations)
            => new(iterations);

        public int CallCount => completedCalls;

        public RunGoalService.AdvanceStepFunc Step => (kernel, agents, _, workspace, goal, _, _) =>
        {
            // Loud failure: an unscripted iteration means RunAsync looped past what the fact pins,
            // which is a control-flow regression, not a fixture gap to absorb silently.
            if (completedCalls >= iterations.Count)
            {
                throw new InvalidOperationException(
                    $"RunGoalService requested advance iteration {completedCalls + 1} but the fact scripted only " +
                    $"{iterations.Count}; RunAsync looped further than this fact pins.");
            }

            return iterations[completedCalls++](new AdvanceStepCall(kernel, agents, workspace, goal));
        };
    }

    // Dispatch timestamps only have to order the kernel's own staleness comparisons against the retry
    // records the kernel writes with its system clock; they never pace the test.
    private sealed class DispatchLabels
    {
        private int tick;

        public DateTimeOffset Next() => DateTimeOffset.UtcNow.AddSeconds(++tick);
    }

    // The advance step now returns the application outcome instead of the presentation DTO. The fields
    // RunGoalService reads map one to one - GoalId (typed GoalId instead of its string value), Executed,
    // StepCount, StopReason, BlockingAction (NextActionItem instead of NextActionDto), Steps,
    // ContinueAfter, StateChanged, Failure - so every fact below pins the same loop input it did before.
    private static GoalAdvanceLoopOutcome Blocked(Goal goal, string stopReason, NextActionItem? blockingAction = null)
        => new(goal.Id, Executed: false, StepCount: 0, stopReason, blockingAction, []);

    private static GoalAdvanceLoopOutcome Advanced(Goal goal, int stepCount, string stopReason)
        => new(goal.Id, Executed: true, stepCount, stopReason, null, [], StateChanged: true);

    // NextActionItem carries the identity RunGoalService resolves the stop task from (Kind, TaskId,
    // HumanInputRequestId, Message, ResumeCommand). The DTO's Priority, TaskNumber, Control and Recovery
    // were presentation projections the loop never read, so they have no application counterpart to set.
    private static NextActionItem BlockingAction(TaskSpec task, NextActionKind kind, string message)
        => new(
            kind,
            task.Id,
            null,
            message,
            "run-goal");

    private static AgentDefinition AssignedAgent(AdvanceStepCall call, TaskSpec task)
        => call.Agents.Single(agent => agent.Id == task.AssignedAgentId);

    // The successful shape the real subscription dispatch leaves behind: a dispatch record naming the
    // assigned agent's worker profile, then an exit-0 verification whose stdout carries the Planner
    // contract plan, the resolved subscription model name, and the fact's marker line.
    private static void RecordCompletedDispatch(
        AdvanceStepCall call,
        TaskSpec task,
        DispatchLabels labels,
        string marker)
    {
        var agent = AssignedAgent(call, task);
        var workerName = agent.Subscription!.WorkerProfileName;
        var command = $"{workerName}: Write-Output planner-contract-plan; Write-Output {marker}";
        var dispatchedAt = labels.Next();
        call.Kernel.RecordTaskDispatch(call.Goal.Id, task.Id, new TaskDispatchRecord(
            workerName,
            command,
            call.Workspace.ExecutionDirectory,
            dispatchedAt,
            ProviderName: agent.Model.ProviderName,
            ModelName: agent.Model.ModelName));
        call.Kernel.RecordDispatchExecutionResult(call.Goal.Id, task.Id, new TaskVerificationRecord(
            command,
            call.Workspace.ExecutionDirectory,
            0,
            string.Join(Environment.NewLine, PlannerPlanText, agent.Model.ModelName, marker),
            string.Empty,
            dispatchedAt.AddSeconds(1)));
    }

    private static void RecordStalledDispatch(AdvanceStepCall call, TaskSpec task, DispatchLabels labels)
    {
        var agent = AssignedAgent(call, task);
        var workerName = agent.Subscription!.WorkerProfileName;
        var dispatchedAt = labels.Next();
        RecordHeartbeatStall(
            call.Kernel,
            call.Goal,
            task,
            call.Workspace,
            workerName,
            $"{workerName}: stalled command",
            dispatchedAt);
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
        string workerName,
        string command,
        DateTimeOffset completedAt)
    {
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(workerName, command, workspace.ExecutionDirectory, completedAt));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            command,
            workspace.ExecutionDirectory,
            1,
            string.Empty,
            "Background dispatch made no observable progress before the stall timeout; wrapper heartbeat state=running.",
            completedAt.AddSeconds(1)));
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
        kernel.ActivateGoal(goal.Id, [agent]);
        var labels = new DispatchLabels();
        var advance = ScriptedAdvanceStep.Script(
            call =>
            {
                RecordCompletedDispatch(call, task1, labels, "first-echo-ok");
                return Advanced(goal, 2, $"Background work is still running for task {task2.Id.Value[..8]}; continue after it exits.");
            },
            call =>
            {
                RecordCompletedDispatch(call, task2, labels, "second-echo-ok");
                return Advanced(goal, 2, "No next actions are available.");
            });

        var result = await RunGoalService.RunAsync(
            kernel,
            [agent],
            ProfileNames("local"),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            pollInterval: TimeSpan.FromMilliseconds(50),
            sleep: NoSleep,
            advanceStep: advance.Step,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Executed);
        // Stricter than the count alone, and asserted first so a missing completion names the task it lost.
        Assert.Equal(
            ["First echo task", "Second echo task"],
            result.CompletedTasks.Select(summary => summary.Description).ToArray());
        Assert.Equal(2, result.CompletedTasks.Count);
        Assert.True(result.CompletedTasks.All(t => t.Succeeded));
        Assert.True(result.ContinueAfter is null);
        Assert.True(result.StopEvidence is null);
        Assert.Equal(WorkTaskStatus.Completed, task1.Status);
        Assert.Equal(WorkTaskStatus.Completed, task2.Status);
        // The background-running stop reason must drive exactly one more loop iteration, not zero and not many.
        Assert.Equal(2, advance.CallCount);
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

        var advance = ScriptedAdvanceStep.Script(
            call => Blocked(
                goal,
                $"Task {task.Id.Value[..8]} failed; inspect the preserved failure evidence before continuing.",
                BlockingAction(task, NextActionKind.InspectFailedTask, "Inspect the failed task.")));

        var result = await RunGoalService.RunAsync(
            kernel,
            [agent],
            ProfileNames("local"),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep,
            advanceStep: advance.Step,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Executed);
        Assert.Equal(NextActionKind.InspectFailedTask, result.BlockingAction?.Kind);
        Assert.True(result.ContinueAfter is null);
        Assert.Contains("Simulated failure output.", result.StopEvidence?.OutputTail ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, advance.CallCount);
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

        var advance = ScriptedAdvanceStep.Script(
            call => Blocked(
                goal,
                $"Task {task.Id.Value[..8]} is waiting for a human answer.",
                BlockingAction(task, NextActionKind.AnswerHumanInput, "Answer the pending human input request.")));

        var result = await RunGoalService.RunAsync(
            kernel,
            [agent],
            ProfileNames("local"),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep,
            advanceStep: advance.Step,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Executed);
        Assert.Equal(NextActionKind.AnswerHumanInput, result.BlockingAction?.Kind);
        Assert.True(result.ContinueAfter is null);
        Assert.Equal(1, result.StopEvidence?.TaskNumber);
        Assert.Equal(1, advance.CallCount);
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

        var advance = ScriptedAdvanceStep.Script(
            call => Blocked(
                goal,
                "blocked: repeated recoverable subscription limits require operator review",
                BlockingAction(task, NextActionKind.RunAssignedTask, "Review the repeated subscription limit before redispatch.")));

        var result = await RunGoalService.RunAsync(
            kernel,
            [agent],
            ProfileNames("local"),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep,
            advanceStep: advance.Step,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Executed);
        Assert.Contains("usage limit", result.StopReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.ContinueAfter is null);
        Assert.Equal(1, result.StopEvidence?.TaskNumber);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(1, advance.CallCount);
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

        var advance = ScriptedAdvanceStep.Script(
            call => Blocked(
                goal,
                $"Task {task.Id.Value[..8]} hit a recoverable subscription usage limit; retry after {retryTime:u}.",
                BlockingAction(task, NextActionKind.RunAssignedTask, "Wait for the subscription retry window.")));

        var result = await RunGoalService.RunAsync(
            kernel,
            [agent],
            ProfileNames("local"),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep,
            advanceStep: advance.Step,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Executed);
        Assert.True(result.ContinueAfter is null);
        Assert.Contains("alternate", result.StopReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Planner", result.StopReason, StringComparison.Ordinal);
        Assert.Contains("retry deferral", result.StopReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, result.StopEvidence?.TaskNumber);
        Assert.Equal(1, advance.CallCount);
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
        var labels = new DispatchLabels();
        var advance = ScriptedAdvanceStep.Script(
            call => Blocked(
                goal,
                "Automatic handoff is blocked while the recoverable subscription usage limit stands.",
                BlockingAction(task, NextActionKind.RunAssignedTask, "Redispatch after the usage limit clears.")),
            call =>
            {
                RecordCompletedDispatch(call, task, labels, "alternate-ok");
                return Advanced(goal, 2, "No next actions are available.");
            });

        var result = await RunGoalService.RunAsync(
            kernel,
            [limited, alternate],
            ProfileNames("limited", "alternate"),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            pollInterval: TimeSpan.FromMilliseconds(50),
            sleep: NoSleep,
            advanceStep: advance.Step,
            cancellationToken: TestContext.Current.CancellationToken);

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
        Assert.Equal(2, advance.CallCount);
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
            new ModelProfile("LlamaCpp", LlamaCppDefaults.DefaultModelAlias, ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey),
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
        var labels = new DispatchLabels();
        var advance = ScriptedAdvanceStep.Script(
            call => Blocked(
                goal,
                "Automatic handoff is blocked while the recoverable subscription usage limit stands.",
                BlockingAction(task, NextActionKind.RunAssignedTask, "Redispatch after the usage limit clears.")),
            call =>
            {
                RecordCompletedDispatch(call, task, labels, "catalog-alternate-ok");
                return Advanced(goal, 2, "No next actions are available.");
            });

        var result = await RunGoalService.RunAsync(
            kernel,
            agents,
            ProfileNames("qwen-code-cli"),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            pollInterval: TimeSpan.FromMilliseconds(50),
            sleep: NoSleep,
            advanceStep: advance.Step,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Executed);
        Xunit.Assert.True(task.Status == WorkTaskStatus.Completed, DescribeRunGoalStop(result, task));
        Xunit.Assert.True(result.StopEvidence is null, result.StopEvidence?.Reason ?? result.StopReason);
        Assert.Equal(alternate.Id, task.AssignedAgentId);
        Assert.Equal("qwen-code-cli", task.LastDispatch!.WorkerName);
        Assert.Contains("catalog-alternate-ok", task.LastVerification!.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(2, advance.CallCount);
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
        RecordHeartbeatStall(kernel, goal, task, workspace, "stalled", "stalled command", DateTimeOffset.UtcNow);
        var labels = new DispatchLabels();
        var advance = ScriptedAdvanceStep.Script(
            call => Blocked(
                goal,
                "Automatic handoff is blocked while the heartbeat stall evidence stands.",
                BlockingAction(task, NextActionKind.InspectFailedTask, "Inspect the stalled dispatch.")),
            call =>
            {
                RecordCompletedDispatch(call, task, labels, "heartbeat-ok");
                return Advanced(goal, 2, "No next actions are available.");
            });

        var result = await RunGoalService.RunAsync(
            kernel,
            [stalled, alternate],
            ProfileNames("stalled", "alternate"),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            pollInterval: TimeSpan.FromMilliseconds(50),
            sleep: NoSleep,
            advanceStep: advance.Step,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Executed);
        Xunit.Assert.True(result.StopEvidence is null, DescribeRunGoalStop(result, task));
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(alternate.Id, task.AssignedAgentId);
        Assert.Equal("alternate", task.LastDispatch!.WorkerName);
        Assert.Contains("heartbeat-ok", task.LastVerification!.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(2, task.VerificationHistory.Count);
        var redelegation = Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRedelegated));
        Assert.Contains(stalled.Id.Value, redelegation.Message, StringComparison.Ordinal);
        Assert.Contains(alternate.Id.Value, redelegation.Message, StringComparison.Ordinal);
        Assert.Contains(
            goal.Timeline,
            item => item.Kind == ProgressKind.TaskRetried &&
                item.Message.Contains(
                    "provider-neutral heartbeat/progress stall evidence",
                    StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, advance.CallCount);
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

        var advance = ScriptedAdvanceStep.Script(
            call => Blocked(
                goal,
                "Automatic handoff is blocked while the provider connectivity failure stands.",
                BlockingAction(task, NextActionKind.RunAssignedTask, "Redispatch after provider connectivity recovers.")));

        var result = await RunGoalService.RunAsync(
            kernel,
            [primary],
            ProfileNames("codex-cli"),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep,
            advanceStep: advance.Step,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Executed);
        Assert.Contains("recoverable provider connectivity", result.StopReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no available unused alternate", result.StopReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Planner", result.StopReason, StringComparison.Ordinal);
        Assert.Contains("websocket", result.StopEvidence?.OutputTail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(primary.Id, task.AssignedAgentId);
        Assert.Equal(1, advance.CallCount);
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
            "Changed files: src/example.cs\nModel fit: OpenAI/gpt-5.5 - adequate - useful work before provider error.", // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
            "Error: websocket transport failed with OS error 10013 after partial work.",
            DateTimeOffset.UtcNow));

        var advance = ScriptedAdvanceStep.Script(
            call => Blocked(
                goal,
                $"Task {task.Id.Value[..8]} failed; inspect the preserved failure evidence before continuing.",
                BlockingAction(task, NextActionKind.InspectFailedTask, "Inspect the failed task.")));

        var result = await RunGoalService.RunAsync(
            kernel,
            [primary],
            ProfileNames("codex-cli"),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep,
            advanceStep: advance.Step,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Executed);
        Assert.False(result.StopReason.Contains("recoverable provider connectivity", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(primary.Id, task.AssignedAgentId);
        // No failover fired, so RunAsync must stop on the first iteration rather than loop.
        Assert.Equal(1, advance.CallCount);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRedelegated);
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

        var advance = ScriptedAdvanceStep.Script(
            call => Blocked(
                goal,
                "Automatic handoff is blocked while the provider connectivity failure stands.",
                BlockingAction(task, NextActionKind.RunAssignedTask, "Redispatch after provider connectivity recovers.")));

        var result = await RunGoalService.RunAsync(
            kernel,
            [primary],
            ProfileNames("claude-cli"),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep,
            advanceStep: advance.Step,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Executed);
        Assert.Contains("recoverable provider connectivity", result.StopReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no available unused alternate", result.StopReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ConnectionRefused", result.StopEvidence?.OutputTail ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(primary.Id, task.AssignedAgentId);
        Assert.Equal(1, advance.CallCount);
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
        var labels = new DispatchLabels();
        var advance = ScriptedAdvanceStep.Script(
            call => Blocked(
                goal,
                "Automatic handoff is blocked while the provider connectivity failure stands.",
                BlockingAction(task, NextActionKind.RunAssignedTask, "Redispatch after provider connectivity recovers.")),
            call =>
            {
                RecordCompletedDispatch(call, task, labels, "connectivity-alternate-ok");
                return Advanced(goal, 2, "No next actions are available.");
            });

        var result = await RunGoalService.RunAsync(
            kernel,
            [primary, alternate],
            ProfileNames("codex-cli", "qwen-code-cli"),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            pollInterval: TimeSpan.FromMilliseconds(50),
            sleep: NoSleep,
            advanceStep: advance.Step,
            cancellationToken: TestContext.Current.CancellationToken);

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
        Assert.Equal(2, advance.CallCount);
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
            "Planner output contract failed: missing required evidence. Retry Planner for contract repair.\n" +
            "ERROR: invalid model 'gpt-5.3-codex' does not exist for this account."); // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
        var labels = new DispatchLabels();
        var advance = ScriptedAdvanceStep.Script(
            call => Blocked(
                goal,
                "Automatic handoff is blocked while the provider model rejection stands.",
                BlockingAction(task, NextActionKind.RunAssignedTask, "Redispatch with a supported model.")),
            call =>
            {
                RecordCompletedDispatch(call, task, labels, "model-rejection-alternate-ok");
                return Advanced(goal, 2, "No next actions are available.");
            });

        var result = await RunGoalService.RunAsync(
            kernel,
            [primary, alternate],
            ProfileNames("codex-cli", "qwen-code-cli"),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            pollInterval: TimeSpan.FromMilliseconds(50),
            sleep: NoSleep,
            advanceStep: advance.Step,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Executed);
        Xunit.Assert.True(result.StopEvidence is null, DescribeRunGoalStop(result, task));
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(alternate.Id, task.AssignedAgentId);
        Assert.Equal("qwen-code-cli", task.LastDispatch!.WorkerName);
        Assert.Contains("model-rejection-alternate-ok", task.LastVerification!.StandardOutput, StringComparison.Ordinal);
        Assert.True(goal.Timeline.Any(evt => evt.Kind == ProgressKind.TaskRedelegated && evt.Message.Contains("qwen-planner", StringComparison.Ordinal)));
        Assert.Equal(2, advance.CallCount);
    }

    [Xunit.Fact]
    public async Task RunGoalServicePlannerContractFailureDoesNotRedelegate()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Task with Planner contract failure", AgentRole.Planner);
        var goal = CreateRefinedGoal(kernel, "Goal should preserve Planner failure", [task]);
        var primary = SubscriptionPlanner("codex-planner", "Codex Planner", "codex-cli");
        var alternate = SubscriptionPlanner("qwen-planner", "Qwen Planner", "qwen-code-cli");
        kernel.ActivateGoal(goal.Id, [primary, alternate]);
        var completedAt = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli",
            "codex-cli exec",
            workspace.ExecutionDirectory,
            completedAt,
            WorkerProviderKind: WorkerProviderCatalog.Default().ResolveProfile("codex-cli").Identity.Kind));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex-cli exec",
            workspace.ExecutionDirectory,
            1,
            "Connecting to API...",
            "  Planner output contract failed: model-home target citation 'models/gpt-5.6-sol' does not exist. Retry Planner for contract repair.", // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
            completedAt));

        var advance = ScriptedAdvanceStep.Script(
            call => Blocked(
                goal,
                $"Task {task.Id.Value[..8]} failed; inspect the preserved failure evidence before continuing.",
                BlockingAction(task, NextActionKind.InspectFailedTask, "Inspect the failed task.")));

        var result = await RunGoalService.RunAsync(
            kernel,
            [primary, alternate],
            ProfileNames("codex-cli", "qwen-code-cli"),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep,
            advanceStep: advance.Step,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Executed);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(primary.Id, task.AssignedAgentId);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRedelegated);
        Assert.Contains("Planner output contract failed", result.StopEvidence?.OutputTail ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(1, advance.CallCount);
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

        var advance = ScriptedAdvanceStep.Script(
            call => Blocked(
                goal,
                "Automatic handoff is blocked while the recoverable subscription usage limit stands.",
                BlockingAction(task, NextActionKind.RunAssignedTask, "Redispatch after the usage limit clears.")));

        var result = await RunGoalService.RunAsync(
            kernel,
            [limited],
            ProfileNames("limited"),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep,
            advanceStep: advance.Step,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Executed);
        Assert.Contains("no available unused alternate", result.StopReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Planner", result.StopReason, StringComparison.Ordinal);
        Assert.Contains("limited-planner", result.StopReason, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(limited.Id, task.AssignedAgentId);
        Assert.Contains("usage limit", result.StopEvidence?.OutputTail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, advance.CallCount);
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
        RecordHeartbeatStall(kernel, goal, task, workspace, "first", "first command", DateTimeOffset.UtcNow);
        var labels = new DispatchLabels();
        var advance = ScriptedAdvanceStep.Script(
            call => Blocked(
                goal,
                "Automatic handoff is blocked while the heartbeat stall evidence stands.",
                BlockingAction(task, NextActionKind.InspectFailedTask, "Inspect the stalled dispatch.")),
            call =>
            {
                // The alternate stalls the same way, so the second failover round finds no unused agent.
                RecordStalledDispatch(call, task, labels);
                return Advanced(goal, 1, "Automatic handoff stopped after the alternate stalled as well.");
            });

        var result = await RunGoalService.RunAsync(
            kernel,
            [first, second],
            ProfileNames("first", "second"),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            pollInterval: TimeSpan.FromMilliseconds(50),
            sleep: NoSleep,
            advanceStep: advance.Step,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Executed);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(second.Id, task.AssignedAgentId);
        Assert.Contains("Previously failed agent(s): first-planner, second-planner", result.StopReason, StringComparison.Ordinal);
        Assert.Equal(1, goal.Timeline.Count(evt => evt.Kind == ProgressKind.TaskRedelegated));
        Assert.False(goal.Timeline.Any(evt =>
            evt.Kind == ProgressKind.TaskRedelegated &&
            evt.Message.Contains("to agent 'first-planner'", StringComparison.Ordinal)));
        Assert.Equal(2, advance.CallCount);
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

        var advance = ScriptedAdvanceStep.Script(
            call => Blocked(
                goal,
                "Paid subscription start requires explicit confirmation: 13000 prompt chars across 1 task(s). " +
                $"rerun with {SubscriptionPromptCostGuard.CliConfirmationFlag} after inspecting subscription-plan.",
                BlockingAction(task, NextActionKind.RunAssignedTask, "Confirm the paid subscription start.")));

        var result = await RunGoalService.RunAsync(
            kernel,
            [agent],
            ProfileNames("local"),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep,
            advanceStep: advance.Step,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("--confirm-large-paid-subscription-start", result.StopReason, StringComparison.Ordinal);
        Assert.True(result.ContinueAfter is null);
        Assert.Equal(1, result.StopEvidence?.TaskNumber);
        Assert.Equal(1, advance.CallCount);
    }
}
