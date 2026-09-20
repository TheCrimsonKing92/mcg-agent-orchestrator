using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

using static InfrastructureTestSupport;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsAgentReassignmentRouting
{
    [Xunit.Fact]
    public void ReassignmentBeforeStartAuthorizationRebuildsPreparedCommandForAcknowledgedAgent()
    {
        var (kernel, goal, task, firstAgent, reassignedAgent, workspace, profiles) = PreparedScenario();

        kernel.ReassignTaskAgent(goal.Id, task.Id, reassignedAgent);

        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.Equal("harness-a", task.LastDispatch!.WorkerName);
        Assert.Equal(firstAgent.Id.Value, task.LastDispatch.AssignedAgentId);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.TaskRedelegated &&
            evt.Message.Contains("prepared command is invalidated", StringComparison.OrdinalIgnoreCase));

        _ = new GoalDispatchOperations().RefreshPreparedDispatchBeforeStart(
            kernel,
            workspace,
            goal,
            task,
            [firstAgent, reassignedAgent],
            profiles,
            sandboxOptions: DisabledSandbox);

        Assert.Equal(reassignedAgent.Id.Value, task.LastDispatch!.AssignedAgentId);
        Assert.Equal("harness-b", task.LastDispatch.WorkerName);
        Assert.Equal("model-b", task.LastDispatch.ModelName);
        Assert.Equal("high", task.LastDispatch.ReasoningEffort);
    }

    [Xunit.Fact]
    public void ReassignmentAfterAuthorizedStartLeavesRunningAttemptUntouchedAndReportsNextAttempt()
    {
        var (kernel, goal, task, _, reassignedAgent, _, _) = PreparedScenario();
        var dispatch = task.LastDispatch!;
        var process = new TaskProcessRecord(
            4242,
            dispatch.Command,
            dispatch.WorkingDirectory,
            "worker.out.log",
            "worker.err.log",
            "worker.exit.txt",
            DateTimeOffset.Parse("2026-09-18T18:00:00Z"),
            CompletedAt: null,
            ExitCode: null);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);

        kernel.ReassignTaskAgent(goal.Id, task.Id, reassignedAgent);

        Assert.Equal(reassignedAgent.Id, task.AssignedAgentId);
        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.Same(dispatch, task.LastDispatch);
        Assert.Same(process, task.LastProcess);
        Assert.Equal("harness-a", task.LastDispatch.WorkerName);
        var report = Assert.Single(goal.Timeline.Where(evt =>
            evt.Kind == ProgressKind.TaskRedelegated &&
            evt.Message.Contains("next attempt", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("harness-a", report.Message, StringComparison.Ordinal);
        Assert.Contains("remains unchanged", report.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void ReassignmentRoleMismatchReturnsTypedHoldAndRecordsNoDispatch()
    {
        var (kernel, goal, task, firstAgent, _, workspace, profiles) = PreparedScenario();
        var mismatched = new AgentDefinition(
            new AgentId("planner-b"),
            "Planner B",
            AgentRole.Planner,
            new ModelProfile("Anthropic", "planner-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("harness-b", "planner-model", "high"));
        IReadOnlyList<AgentDefinition> agents = [firstAgent, mismatched];
        Goal? currentGoal = goal;
        var dispatchCount = task.DispatchHistory.Count;

        var error = CaptureConsoleError(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["reassign-agent", "1", mismatched.Id.Value],
                kernel,
                workspace,
                ref agents,
                new InMemoryModelProviderRegistry([]),
                ref profiles,
                ref currentGoal);
            Assert.False(changed);
        });

        Assert.Contains("AGENT_REASSIGNMENT_HOLD code=RoleMismatch", error, StringComparison.Ordinal);
        Assert.Equal(firstAgent.Id, task.AssignedAgentId);
        Assert.Equal(dispatchCount, task.DispatchHistory.Count);
        Assert.Null(task.LastProcess);
    }

    [Xunit.Fact]
    public void UnavailableAssignedAgentCreatesTypedStartRefusalAndDoesNotLaunch()
    {
        var (kernel, goal, task, firstAgent, reassignedAgent, workspace, profiles) = PreparedScenario();
        var offlineAgent = reassignedAgent with { Status = AgentStatus.Offline };
        kernel.ReassignTaskAgent(goal.Id, task.Id, offlineAgent);

        var result = new GoalDispatchOperations().StartDispatches(
            kernel,
            workspace,
            goal,
            agents: [firstAgent, offlineAgent],
            profiles: profiles,
            refreshBeforeStart: true,
            runner: new BackgroundDispatchRunner(disableProcessStart: true),
            sandboxOptions: DisabledSandbox);

        var refusal = Assert.Single(result.StartRefusals!);
        Assert.Equal(task.Id, refusal.TaskId);
        Assert.Equal(DispatchAssignmentHoldCode.AgentUnavailable, refusal.AssignmentHold!.Code);
        Assert.Equal(offlineAgent.Id.Value, refusal.AssignmentHold.AssignedAgentId);
        Assert.Contains("DISPATCH_ASSIGNMENT_HOLD code=AgentUnavailable", refusal.Reason, StringComparison.Ordinal);
        Assert.Null(task.LastProcess);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("DISPATCH_ASSIGNMENT_HOLD code=AgentUnavailable", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void ReassignmentToUnavailableAgentReturnsTypedCliHoldAndKeepsCurrentAssignment()
    {
        var (kernel, goal, task, firstAgent, reassignedAgent, workspace, profiles) = PreparedScenario();
        var offlineAgent = reassignedAgent with { Status = AgentStatus.Offline };
        IReadOnlyList<AgentDefinition> agents = [firstAgent, offlineAgent];
        Goal? currentGoal = goal;

        var error = CaptureConsoleError(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["reassign-agent", "1", offlineAgent.Id.Value],
                kernel,
                workspace,
                ref agents,
                new InMemoryModelProviderRegistry([]),
                ref profiles,
                ref currentGoal);
            Assert.False(changed);
        });

        Assert.Contains("AGENT_REASSIGNMENT_HOLD code=AgentUnavailable", error, StringComparison.Ordinal);
        Assert.Equal(firstAgent.Id, task.AssignedAgentId);
        Assert.Null(task.LastProcess);
    }

    private static readonly WorkerSandboxOptions DisabledSandbox = new(
        Enabled: false,
        WorkerSandboxOptions.DefaultAccount,
        WorkerSandboxOptions.DefaultCredentialTarget);

    private static (
        AgentOrchestratorKernel Kernel,
        Goal Goal,
        TaskSpec Task,
        AgentDefinition FirstAgent,
        AgentDefinition ReassignedAgent,
        OrchestratorWorkspace Workspace,
        WorkerProfileCatalog Profiles) PreparedScenario()
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        var workspace = OrchestratorWorkspace.ForDirectory(root, workingDirectory);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Route the acknowledged agent.", AgentRole.Developer);
        var goal = MarkGoalRefined(kernel, kernel.CreateGoal("Honor agent reassignment", [task]));
        var firstAgent = SubscriptionAgent("developer-a", "Developer A", "OpenAI", "model-a", "harness-a", "medium");
        var reassignedAgent = SubscriptionAgent("developer-b", "Developer B", "Anthropic", "model-b", "harness-b", "high");
        var profiles = new WorkerProfileCatalog([
            new WorkerProfile("harness-a", "Write-Output harness-a"),
            new WorkerProfile("harness-b", "Write-Output harness-b")
        ]);
        kernel.ActivateGoal(goal.Id, [firstAgent]);
        _ = new GoalDispatchOperations().ProfileDispatchTask(
            kernel,
            workspace,
            goal,
            task,
            profiles.GetRequired("harness-a"),
            [firstAgent, reassignedAgent],
            sandboxOptions: DisabledSandbox);
        return (kernel, goal, task, firstAgent, reassignedAgent, workspace, profiles);
    }

    private static AgentDefinition SubscriptionAgent(
        string id,
        string name,
        string provider,
        string model,
        string harness,
        string reasoning) =>
        new(
            new AgentId(id),
            name,
            AgentRole.Developer,
            new ModelProfile(provider, model, ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile(harness, model, reasoning));
}
