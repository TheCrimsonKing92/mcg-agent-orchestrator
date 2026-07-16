using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Text.Json;

[Xunit.Collection("EnvMutation")]
public sealed class WorkerDispatchTestsModelSelectionEnvMutation : WorkerDispatchTestSupport
{
    [Xunit.Fact(DisplayName = "StartDispatches_fails_closed_when_recorded_worker_profile_is_missing")]
    public void StartDispatchesFailsClosedWhenRecordedWorkerProfileIsMissing()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    var kernel = new AgentOrchestratorKernel(new TestClock(DateTimeOffset.Parse("2026-06-28T14:10:00Z")));
    var developer = new TaskSpec(TaskId.New(), "Implement retry prompt regeneration.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Fix stale retry dispatch prompts", [developer]);
    kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
        "Fix stale retry dispatch prompts",
        ["Recorded worker starts fail closed when the previous worker profile cannot be resolved."],
        VerificationClass.TestVerifiable,
        [],
        []));
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox {sandboxMode} --cd {workingDirectory}")
    ]);
    var originalDisableStart = Environment.GetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable);

    try
    {
        var prepared = GoalManagementCommandService.ProfileDispatchTask(
            kernel,
            workspace,
            goal,
            developer,
            profiles.GetRequired("codex-cli"),
            [agent]);
        var missingProfiles = new WorkerProfileCatalog([]);
        Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, "1");

        var ex = Assert.ThrowsAny<InvalidOperationException>(() =>
            GoalManagementCommandService.StartDispatches(kernel, workspace, goal, [agent], missingProfiles));

        Assert.Contains("worker profile 'codex-cli' is not available", ex.Message);
        Assert.Equal(prepared.PromptPath, developer.LastDispatch!.PromptPath);
        Xunit.Assert.Null(developer.LastProcess);
    }
    finally
    {
        Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, originalDisableStart);
    }
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_prepares_subscription_tasks_by_assigned_provider")]
    public void WorkerProfileDispatcherPreparesSubscriptionTasksByAssignedProvider()
{
    // Hermetic: clear the operator's MCG_WORKER_SANDBOX so this asserts the default dispatch mode
    // regardless of how the suite was launched (see ClearWorkerSandboxEnv).
    using var _sandboxEnv = ClearWorkerSandboxEnv();
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Dispatch subscription-backed workers");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var workingDirectory = GoalWorktrees.Ensure(root, goal.Id);

    var results = WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(
        kernel,
        goal,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt,
        commandExists: RealClaudeLauncherExists);

    Assert.Equal(5, results.Count);
    var developer = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var researcher = goal.Tasks.First(task => task.RequiredRole == AgentRole.Researcher);
    Assert.Equal("codex-cli", developer.LastDispatch!.WorkerName);
    Assert.Contains("codex exec", developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains($"--model '{AgentCatalog.OpenAiSubscriptionModelAlias}'", developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("model_reasoning_effort='low'", developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--sandbox 'workspace-write'", developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains($"--cd '{workingDirectory}'", developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.False(developer.LastDispatch.Command.Contains("{workingDirectory}", StringComparison.Ordinal));
    Assert.Equal("claude-cli", researcher.LastDispatch!.WorkerName);
    Assert.Contains("claude --model 'claude-haiku-4-5'", researcher.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Equal("Anthropic", researcher.LastDispatch.ProviderName);
    Assert.Equal("claude-haiku-4-5", researcher.LastDispatch.ModelName);
    Assert.True(File.Exists(results.Single(result => result.Task.Id == developer.Id).PromptPath));
    Assert.Equal(WorkTaskStatus.Running, developer.Status);
    Assert.Equal(workingDirectory, developer.LastDispatch.WorkingDirectory);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_ready_batch_dispatches_Tester_after_persisted_Developer_completion_without_process_start")]
    public void WorkerProfileDispatcherReadyBatchDispatchesTesterAfterPersistedDeveloperCompletionWithoutProcessStart()
{
    using var _sandboxEnv = ClearWorkerSandboxEnv();
    var root = CreateTempDirectory();
    File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Dispatch Tester after Developer completion");
    var agents = new[]
    {
        TestSubscriptionAgent("planner", "Planner", AgentRole.Planner),
        TestSubscriptionAgent("researcher", "Researcher", AgentRole.Researcher),
        TestSubscriptionAgent("developer", "Developer", AgentRole.Developer),
        TestSubscriptionAgent("tester", "Tester", AgentRole.Tester)
    };
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("test-subscription", "worker --model {subscriptionModelName} --reasoning {subscriptionReasoningEffort} --prompt {promptPath} --cd {workingDirectory}")
    ]);
    kernel.ActivateGoal(goal.Id, agents);
    foreach (var task in goal.Tasks.Where(task =>
        task.RequiredRole is AgentRole.Planner or AgentRole.Researcher or AgentRole.Developer))
    {
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, $"{task.RequiredRole} completed in persisted state.");
    }

    var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles);
    var readiness = DispatchReadinessEvaluator.EvaluateDispatchReadiness(goal, plan, DateTimeOffset.UtcNow);
    var batch = GoalManagementCommandService.SubscriptionDispatchReadyBatch(
        kernel,
        CreateRefinedWorkspace(root),
        goal,
        agents,
        profiles);

    Assert.IsType<DispatchReadinessReady>(readiness);
    Assert.Single(batch.Dispatches);
    var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
    Assert.Equal(tester.Id, batch.Dispatches.Single().Task.Id);
    Assert.Equal(WorkTaskStatus.Running, tester.Status);
    Assert.Null(tester.LastProcess);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_ready_batch_records_provider_from_claude_launcher")]
    public void WorkerProfileDispatcherReadyBatchRecordsProviderFromClaudeLauncher()
{
    using var _sandboxEnv = ClearWorkerSandboxEnv();
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Dispatch Claude write worker",
        [new TaskSpec(TaskId.New(), "Update src/example.txt.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-haiku-4-5"));
    kernel.ActivateGoal(goal.Id, [agent]);

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        goal.Tasks.Single(),
        [agent],
        WorkerProfileCatalog.Default(),
        workingDirectory,
        dispatchedAt,
        sandboxOptions: new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget),
        commandExists: RealClaudeLauncherExists);
    Assert.True(preflight.Allowed, string.Join("\n", preflight.Findings));

    var results = WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(
        kernel,
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt,
        commandExists: RealClaudeLauncherExists);

    Assert.Single(results);
    var developer = goal.Tasks.Single();
    Assert.Equal("claude-cli", developer.LastDispatch!.WorkerName);
    Assert.Contains("claude --model 'claude-haiku-4-5' --permission-mode 'bypassPermissions'", developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain(" -p", developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain("Get-Content -Raw", developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Equal("Anthropic", developer.LastDispatch.ProviderName);
}

}
