using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliCommandTests
{
    [Xunit.Fact(DisplayName = "Cli_run_blocks_subscription_capable_agents_without_calling_provider")]
    public void CliRunBlocksSubscriptionCapableAgentsWithoutCallingProvider()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Avoid accidental CLI API spend", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
        IReadOnlyList<AgentDefinition> agents = [agent];
        var provider = new FakeSmokeProvider();
        var providers = new InMemoryModelProviderRegistry([provider]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        InvalidOperationException? ex = null;
        try
        {
            CliCommandDispatcher.ExecuteCommand(
                ["run", "1"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }

        Xunit.Assert.NotNull(ex);
        Xunit.Assert.Contains("subscription-dispatch 1", ex!.Message);
        Xunit.Assert.Contains("api-run 1", ex.Message);
        Xunit.Assert.Null(provider.LastRequest);
        Xunit.Assert.Null(goal.Tasks.Single().LastExecution);
    }

    [Xunit.Fact(DisplayName = "Cli_api_run_executes_subscription_capable_agents_when_explicit")]
    public void CliApiRunExecutesSubscriptionCapableAgentsWhenExplicit()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Allow explicit CLI API execution", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
        IReadOnlyList<AgentDefinition> agents = [agent];
        var provider = new FakeSmokeProvider();
        var providers = new InMemoryModelProviderRegistry([provider]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var changed = CliCommandDispatcher.ExecuteCommand(
            ["api-run", "1"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        Xunit.Assert.True(changed);
        Xunit.Assert.NotNull(provider.LastRequest);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, goal.Tasks.Single().Status);
        Xunit.Assert.NotNull(goal.Tasks.Single().LastExecution);
    }

    [Xunit.Fact(DisplayName = "Cli_api_run_blocks_subscription_capable_agents_after_dispatch_evidence")]
    public void CliApiRunBlocksSubscriptionCapableAgentsAfterDispatchEvidence()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Avoid duplicate API execution after subscription work", [task]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
        IReadOnlyList<AgentDefinition> agents = [agent];
        var provider = new FakeSmokeProvider();
        var providers = new InMemoryModelProviderRegistry([provider]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("codex-cli", "codex exec prompt.md", root, DateTimeOffset.UtcNow));

        InvalidOperationException? ex = null;
        try
        {
            CliCommandDispatcher.ExecuteCommand(
                ["api-run", "1"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }

        Xunit.Assert.NotNull(ex);
        Xunit.Assert.Contains("only available before subscription work", ex!.Message);
        Xunit.Assert.Null(provider.LastRequest);
        Xunit.Assert.Null(goal.Tasks.Single().LastExecution);
    }

    [Xunit.Fact(DisplayName = "Cli_provider_smoke_all_requires_confirm_flag")]
    public void CliProviderSmokeAllRequiresConfirmFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider()]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        InvalidOperationException? ex = null;
        try
        {
            CliCommandDispatcher.ExecuteCommand(
                ["provider-smoke", "all"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }

        Xunit.Assert.NotNull(ex);
        Xunit.Assert.Contains("--confirm-all", ex!.Message);
    }
}
