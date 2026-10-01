using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Text.Json;

public sealed class WorkerDispatchTestsModelSelectionEnvMutation : WorkerDispatchTestSupport
{
    private static readonly WorkerSandboxOptions DisabledSandbox = new(
        false,
        WorkerSandboxOptions.DefaultAccount,
        WorkerSandboxOptions.DefaultCredentialTarget);

    [Xunit.Fact(DisplayName = "StartDispatches_fails_closed_when_recorded_worker_profile_is_missing")]
    public void StartDispatchesFailsClosedWhenRecordedWorkerProfileIsMissing()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
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
        new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox {sandboxMode} --cd {workingDirectory}")
    ]);
    var prepared = new GoalDispatchOperations().ProfileDispatchTask(
        kernel,
        workspace,
        goal,
        developer,
        profiles.GetRequired("codex-cli"),
        [agent],
        sandboxOptions: DisabledSandbox);
    var missingProfiles = new WorkerProfileCatalog([]);

    var result = new GoalDispatchOperations().StartDispatches(
        kernel,
        workspace,
        goal,
        [agent],
        missingProfiles,
        runner: new BackgroundDispatchRunner(disableProcessStart: true),
        sandboxOptions: DisabledSandbox);

    var refusal = Assert.Single(result.StartRefusals!);
    Assert.Equal(developer.Id, refusal.TaskId);
    Assert.Equal(DispatchAssignmentHoldCode.ProfileUnavailable, refusal.AssignmentHold!.Code);
    Assert.Equal(agent.Id.Value, refusal.AssignmentHold.AssignedAgentId);
    Assert.Equal("codex-cli", refusal.AssignmentHold.WorkerProfileName);
    Assert.Contains("DISPATCH_ASSIGNMENT_HOLD code=ProfileUnavailable", refusal.Reason);
    Assert.Equal(prepared.PromptPath, developer.LastDispatch!.PromptPath);
    Xunit.Assert.Null(developer.LastProcess);
}

    [Xunit.Fact(DisplayName = "StartDispatches_resolves_profile_from_acknowledged_assignment_when_recorded_profile_is_missing")]
    public void StartDispatchesResolvesProfileFromAcknowledgedAssignmentWhenRecordedProfileIsMissing()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        var kernel = new AgentOrchestratorKernel(new TestClock(DateTimeOffset.Parse("2026-06-28T14:10:00Z")));
        var developer = new TaskSpec(TaskId.New(), "Honor the acknowledged assignment.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Resolve the assigned worker profile", [developer]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Resolve the assigned worker profile",
            ["The start boundary resolves the acknowledged assignment instead of the recorded worker."],
            VerificationClass.TestVerifiable,
            [],
            []));
        var firstAgent = new AgentDefinition(
            new AgentId("developer-a"),
            "Developer A",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "model-a", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("harness-a", "model-a", "medium"));
        var reassignedAgent = new AgentDefinition(
            new AgentId("developer-b"),
            "Developer B",
            AgentRole.Developer,
            new ModelProfile("Anthropic", "model-b", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("harness-b", "model-b", "high"));
        var initialProfiles = new WorkerProfileCatalog([
            new WorkerProfile("harness-a", "Write-Output harness-a"),
            new WorkerProfile("harness-b", "Write-Output harness-b")
        ]);
        kernel.ActivateGoal(goal.Id, [firstAgent]);
        _ = new GoalDispatchOperations().ProfileDispatchTask(
            kernel,
            workspace,
            goal,
            developer,
            initialProfiles.GetRequired("harness-a"),
            [firstAgent, reassignedAgent],
            sandboxOptions: DisabledSandbox);
        kernel.ReassignTaskAgent(goal.Id, developer.Id, reassignedAgent);
        var assignmentProfiles = new WorkerProfileCatalog([
            new WorkerProfile("harness-b", "Write-Output harness-b")
        ]);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new GoalDispatchOperations().StartDispatches(
                kernel,
                workspace,
                goal,
                [firstAgent, reassignedAgent],
                assignmentProfiles,
                runner: new BackgroundDispatchRunner(disableProcessStart: true),
                sandboxOptions: DisabledSandbox));

        Assert.Contains("Background dispatch process start is disabled", ex.Message);
        Assert.Equal(reassignedAgent.Id.Value, developer.LastDispatch!.AssignedAgentId);
        Assert.Equal("harness-b", developer.LastDispatch.WorkerName);
        Assert.Equal("model-b", developer.LastDispatch.ModelName);
        Assert.Equal("high", developer.LastDispatch.ReasoningEffort);
    }

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_prepares_subscription_tasks_by_assigned_provider")]
    public void WorkerProfileDispatcherPreparesSubscriptionTasksByAssignedProvider()
{
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Dispatch subscription-backed workers");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var workingDirectory = GoalWorktrees.Ensure(root, goal.Id);

    var researchResults = WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(
        kernel,
        goal,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt,
        commandExists: RealClaudeLauncherExists,
        sandboxOptions: DisabledSandbox);
    Assert.Single(researchResults);
    Assert.Equal(AgentRole.Researcher, researchResults.Single().Task.RequiredRole);
    CompleteResearcherArtifact(kernel, goal);

    var plannerResults = WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(
        kernel,
        goal,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt.AddMinutes(1),
        commandExists: RealClaudeLauncherExists,
        sandboxOptions: DisabledSandbox);
    Assert.Single(plannerResults);
    Assert.Equal(AgentRole.Planner, plannerResults.Single().Task.RequiredRole);
    CompletePlannerArtifact(kernel, goal);

    var downstreamResults = WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(
        kernel,
        goal,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt.AddMinutes(2),
        commandExists: RealClaudeLauncherExists,
        sandboxOptions: DisabledSandbox);
    var results = researchResults.Concat(plannerResults).Concat(downstreamResults).ToList();

    Assert.Equal(5, results.Count);
    var developer = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var researcher = goal.Tasks.First(task => task.RequiredRole == AgentRole.Researcher);
    Assert.Equal("codex-spark", developer.LastDispatch!.WorkerName);
    Assert.Contains("codex exec", developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--model 'gpt-5.3-codex-spark'", developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Equal("codex-spark", developer.LastDispatch.DispatchLane);
    Assert.Contains("cheap-lane: Developer small-task", developer.LastDispatch.ModelSelectionReason, StringComparison.Ordinal);
    Assert.Contains("model_reasoning_effort='low'", developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--sandbox 'workspace-write'", developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains($"--cd '{workingDirectory}'", developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.False(developer.LastDispatch.Command.Contains("{workingDirectory}", StringComparison.Ordinal));
    Assert.Equal("claude-cli", researcher.LastDispatch!.WorkerName);
    Assert.Contains("claude -p --model 'claude-haiku-4-5'", researcher.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Equal("Anthropic", researcher.LastDispatch.ProviderName);
    Assert.Equal("claude-haiku-4-5", researcher.LastDispatch.ModelName);
    Assert.True(File.Exists(results.Single(result => result.Task.Id == developer.Id).PromptPath));
    Assert.Equal(WorkTaskStatus.Running, developer.Status);
    Assert.Equal(workingDirectory, developer.LastDispatch.WorkingDirectory);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_ready_batch_dispatches_Tester_after_persisted_Developer_completion_without_process_start")]
    public void WorkerProfileDispatcherReadyBatchDispatchesTesterAfterPersistedDeveloperCompletionWithoutProcessStart()
{
    var root = CreateTempDirectory();
    File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Dispatch Tester after Developer completion");
    MarkGoalRefined(kernel, goal);
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
    CompleteResearcherAndPlannerArtifacts(kernel, goal);
    var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
    kernel.ReportTaskProgress(
        goal.Id,
        developer.Id,
        WorkTaskStatus.Completed,
        "Developer completed in persisted state.");

    var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles);
    var readiness = DispatchReadinessEvaluator.EvaluateDispatchReadiness(goal, plan, DateTimeOffset.UtcNow);
    var batch = new GoalDispatchOperations().SubscriptionDispatchReadyBatch(
        kernel,
        CreateRefinedWorkspace(root),
        goal,
        agents,
        profiles,
        sandboxOptions: DisabledSandbox);

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
        new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
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
        commandExists: RealClaudeLauncherExists,
        sandboxOptions: DisabledSandbox);

    Assert.Single(results);
    var developer = goal.Tasks.Single();
    Assert.Equal("claude-cli", developer.LastDispatch!.WorkerName);
    Assert.Contains("claude -p --model 'claude-haiku-4-5' --permission-mode 'bypassPermissions'", developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains(" -p ", developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain("Get-Content -Raw", developer.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Equal("Anthropic", developer.LastDispatch.ProviderName);
}

}
