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

    private static WorkerProfileCatalog EchoProfiles() => new WorkerProfileCatalog(
    [
        new WorkerProfile("local", "Start-Sleep -Milliseconds 100; Write-Output {subscriptionModelName}")
    ]);

    [Xunit.Fact(DisplayName = "RunGoalService_completes_all_tasks_sequentially_and_stops_with_no_actions")]
    public async Task RunGoalServiceCompletesAllTasksSequentiallyAndStopsWithNoActions()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task1 = new TaskSpec(TaskId.New(), "First echo task", AgentRole.Planner);
        var task2 = new TaskSpec(TaskId.New(), "Second echo task", AgentRole.Planner);
        var goal = kernel.CreateGoal("Sequential echo run", [task1, task2]);
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
        var goal = kernel.CreateGoal("Goal with failing task", [task]);
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
        Assert.Contains(result.StopEvidence?.OutputTail ?? string.Empty, text => text.Contains("Simulated failure output.", StringComparison.Ordinal));
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
    }

    [Xunit.Fact(DisplayName = "RunGoalService_stops_on_human_input_request")]
    public async Task RunGoalServiceStopsOnHumanInputRequest()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Task requiring human input", AgentRole.Planner);
        var goal = kernel.CreateGoal("Goal with human input block", [task]);
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
        var goal = kernel.CreateGoal("Goal hitting usage limit", [task]);
        var agent = EchoAgent();
        kernel.ActivateGoal(goal.Id, [agent]);
        var now = DateTimeOffset.UtcNow;
        var limitOutput = "ERROR: You've hit your usage limit. Visit settings to purchase more credits.";

        for (var i = 0; i < 2; i++)
        {
            var cmd = $"cmd{i}";
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", cmd, workspace.ExecutionDirectory, now));
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
        Assert.Contains(result.StopReason, text => text.Contains("usage limit", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.ContinueAfter is null);
        Assert.Equal(1, result.StopEvidence?.TaskNumber);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    }

    [Xunit.Fact(DisplayName = "RunGoalService_stops_with_continue_after_on_retry_window")]
    public async Task RunGoalServiceStopsWithContinueAfterOnRetryWindow()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Task deferred by retry window", AgentRole.Planner);
        var goal = kernel.CreateGoal("Goal with subscription retry deferral", [task]);
        var agent = EchoAgent();
        kernel.ActivateGoal(goal.Id, [agent]);
        var now = DateTimeOffset.UtcNow;
        var retryTime = now.AddHours(1);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "codex exec", workspace.ExecutionDirectory, now));
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
        Assert.True(result.ContinueAfter.HasValue);
        Assert.True(result.ContinueAfter!.Value > now);
        Assert.Contains(result.StopReason, text => text.Contains("retry", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, result.StopEvidence?.TaskNumber);
    }

    [Xunit.Fact(DisplayName = "RunGoalService_stops_when_cost_guard_flag_missing")]
    public async Task RunGoalServiceStopsWhenCostGuardFlagMissing()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Planner);
        var goal = kernel.CreateGoal("Goal with oversized paid prompt", [task]);
        var agent = EchoAgent();
        kernel.ActivateGoal(goal.Id, [agent]);
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "local", "Write-Output gpt-4o-mini", workspace.ExecutionDirectory, now,
            ProviderName: "OpenAI", ModelName: "gpt-4o-mini", PromptCharacterCount: 7000));

        var result = await RunGoalService.RunAsync(
            kernel,
            [agent],
            EchoProfiles(),
            workspace,
            goal,
            allowLargePaidSubscriptionStart: false,
            sleep: NoSleep);

        Assert.Contains(result.StopReason, text => text.Contains("--confirm-large-paid-subscription-start", StringComparison.Ordinal));
        Assert.True(result.ContinueAfter is null);
        Assert.Equal(1, result.StopEvidence?.TaskNumber);
    }
}
