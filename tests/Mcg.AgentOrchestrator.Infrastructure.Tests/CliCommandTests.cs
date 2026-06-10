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

    [Xunit.Fact(DisplayName = "Cli_next_action_command_confirms_large_paid_prepared_dispatch")]
    public void CliNextActionCommandConfirmsLargePaidPreparedDispatch()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Start costly prepared dispatch",
            [new TaskSpec(TaskId.New(), "Run prepared paid work", AgentRole.Developer)]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.3-codex", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
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

        var command = ConsoleViews.BuildSuggestedCommand(goal, action);

        Xunit.Assert.Equal("execute-dispatch 1 --confirm-dispatch-start --confirm-large-paid-subscription-start", command);
    }

    [Xunit.Fact(DisplayName = "Cli_next_action_command_confirms_large_paid_api_run")]
    public void CliNextActionCommandConfirmsLargePaidApiRun()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "production architecture api cli dashboard provider subscription worker persistence state tests docs " + new string('o', 5000),
            [
                new TaskSpec(
                    TaskId.New(),
                    "Design and implement complete integration with authentication migration rollback state persistence and dashboard api tests. " + new string('d', 5000),
                    AgentRole.Developer,
                    "Run end-to-end integration tests, dashboard smoke tests, api tests, cli tests, and rollback checks. " + new string('v', 5000))
            ]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        kernel.ActivateGoal(goal.Id, [agent]);
        var action = kernel.BuildNextActions(goal.Id).Items.Single();

        var command = ConsoleViews.BuildSuggestedCommand(goal, action, [agent]);

        Xunit.Assert.Equal("run 1 --confirm-paid-api-run --confirm-large-paid-api-prompt", command);
    }

    [Xunit.Fact(DisplayName = "Cli_next_action_command_prefers_subscription_dispatch_for_subscription_agent")]
    public void CliNextActionCommandPrefersSubscriptionDispatchForSubscriptionAgent()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Prefer subscription handoff",
            [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
        kernel.ActivateGoal(goal.Id, [agent]);
        var action = kernel.BuildNextActions(goal.Id).Items.Single();

        var command = ConsoleViews.BuildSuggestedCommand(goal, action, [agent]);

        Xunit.Assert.Equal("subscription-dispatch 1", command);
    }

    [Xunit.Fact(DisplayName = "Cli_api_run_blocks_paid_provider_without_confirm_flag")]
    public void CliApiRunBlocksPaidProviderWithoutConfirmFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Avoid accidental paid API execution", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
        IReadOnlyList<AgentDefinition> agents = [agent];
        var provider = new FakeSmokeProvider(providerName: "OpenAI");
        var providers = new InMemoryModelProviderRegistry([provider]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

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
        Xunit.Assert.Contains("--confirm-paid-api-run", ex!.Message);
        Xunit.Assert.Null(provider.LastRequest);
        Xunit.Assert.Null(goal.Tasks.Single().LastExecution);
    }

    [Xunit.Fact(DisplayName = "Cli_api_run_executes_paid_provider_with_confirm_flag")]
    public void CliApiRunExecutesPaidProviderWithConfirmFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Allow confirmed paid API execution", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
        IReadOnlyList<AgentDefinition> agents = [agent];
        var provider = new FakeSmokeProvider(providerName: "OpenAI");
        var providers = new InMemoryModelProviderRegistry([provider]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var changed = CliCommandDispatcher.ExecuteCommand(
            ["api-run", "1", "--confirm-paid-api-run"],
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

    [Xunit.Fact(DisplayName = "Cli_api_run_blocks_large_paid_prompt_without_confirm_flag")]
    public void CliApiRunBlocksLargePaidPromptWithoutConfirmFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Design and implement a production multi-tenant distributed architecture " + new string('o', 5000),
            [
                new TaskSpec(
                    TaskId.New(),
                    "Build an end-to-end distributed integration with horizontal scaling across API CLI dashboard provider subscription worker persistence state tests docs " + new string('t', 5000),
                    AgentRole.Developer,
                    "Verify the full integration with build, tests, dashboard smoke, and focused regression evidence. " + new string('v', 5000))
            ]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
        IReadOnlyList<AgentDefinition> agents = [agent];
        var provider = new FakeSmokeProvider(providerName: "OpenAI");
        var providers = new InMemoryModelProviderRegistry([provider]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.Single();
        var preview = AgentTaskRunner.PreviewRun(goal, task, agents);

        InvalidOperationException? ex = null;
        try
        {
            CliCommandDispatcher.ExecuteCommand(
                ["api-run", "1", "--confirm-paid-api-run"],
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

        Xunit.Assert.True(preview.PromptCharacterCount > 6000);
        Xunit.Assert.NotNull(ex);
        Xunit.Assert.Contains("--confirm-large-paid-api-prompt", ex!.Message);
        Xunit.Assert.Null(provider.LastRequest);
        Xunit.Assert.Null(task.LastExecution);
    }

    [Xunit.Fact(DisplayName = "Cli_run_allows_local_provider_without_paid_confirm_flag")]
    public void CliRunAllowsLocalProviderWithoutPaidConfirmFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Allow local API execution", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("Ollama", "qwen3", ModelCapability.Text, SubscriptionMode.LocalBridge),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        IReadOnlyList<AgentDefinition> agents = [agent];
        var provider = new FakeSmokeProvider(providerName: "Ollama");
        var providers = new InMemoryModelProviderRegistry([provider]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var changed = CliCommandDispatcher.ExecuteCommand(
            ["run", "1"],
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

    [Xunit.Fact(DisplayName = "Cli_advance_subscription_requires_confirm_flag")]
    public void CliAdvanceSubscriptionRequiresConfirmFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Avoid accidental subscription handoff", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
        IReadOnlyList<AgentDefinition> agents = [agent];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        InvalidOperationException? ex = null;
        try
        {
            CliCommandDispatcher.ExecuteCommand(
                ["advance-subscription"],
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
        Xunit.Assert.Contains("--confirm-subscription-advance", ex!.Message);
        Xunit.Assert.Null(goal.Tasks.Single().LastDispatch);
        Xunit.Assert.Null(goal.Tasks.Single().LastProcess);
    }

    [Xunit.Fact(DisplayName = "Cli_start_subscription_ready_requires_confirm_flag")]
    public void CliStartSubscriptionReadyRequiresConfirmFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Avoid accidental batch subscription start", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
        IReadOnlyList<AgentDefinition> agents = [agent];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        InvalidOperationException? ex = null;
        try
        {
            CliCommandDispatcher.ExecuteCommand(
                ["start-subscription-ready"],
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
        Xunit.Assert.Contains("--confirm-batch-start", ex!.Message);
        Xunit.Assert.Null(goal.Tasks.Single().LastDispatch);
        Xunit.Assert.Null(goal.Tasks.Single().LastProcess);
    }

    [Xunit.Fact(DisplayName = "Cli_start_dispatch_blocks_large_paid_subscription_prompt_without_confirm_flag")]
    public void CliStartDispatchBlocksLargePaidSubscriptionPromptWithoutConfirmFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Avoid accidentally starting a large paid prompt", [task]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey))
        ];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(
                "codex-cli",
                "Write-Output ok",
                root,
                DateTimeOffset.UtcNow,
                ProviderName: "OpenAI",
                ModelName: "gpt-5-codex",
                TaskComplexity: TaskComplexity.Simple,
                PromptCharacterCount: 12001));

        InvalidOperationException? ex = null;
        try
        {
            CliCommandDispatcher.ExecuteCommand(
                ["start-dispatch", "1", "--confirm-dispatch-start"],
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
        Xunit.Assert.Contains("--confirm-large-paid-subscription-start", ex!.Message);
        Xunit.Assert.Contains("12001 prompt chars", ex.Message);
        Xunit.Assert.Null(task.LastProcess);
    }

    [Xunit.Fact(DisplayName = "Cli_execute_dispatch_blocks_large_paid_subscription_prompt_without_confirm_flag")]
    public void CliExecuteDispatchBlocksLargePaidSubscriptionPromptWithoutConfirmFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Avoid accidentally executing a large paid prompt", [task]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey))
        ];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(
                "codex-cli",
                "Write-Output ok",
                root,
                DateTimeOffset.UtcNow,
                ProviderName: "OpenAI",
                ModelName: "gpt-5-codex",
                TaskComplexity: TaskComplexity.Simple,
                PromptCharacterCount: 12001));

        InvalidOperationException? ex = null;
        try
        {
            CliCommandDispatcher.ExecuteCommand(
                ["execute-dispatch", "1", "--confirm-dispatch-start"],
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
        Xunit.Assert.Contains("--confirm-large-paid-subscription-start", ex!.Message);
        Xunit.Assert.Contains("12001 prompt chars", ex.Message);
        Xunit.Assert.Empty(task.VerificationHistory);
    }

    [Xunit.Fact(DisplayName = "Cli_advance_subscription_blocks_large_paid_prepared_prompt_without_confirm_flag")]
    public void CliAdvanceSubscriptionBlocksLargePaidPreparedPromptWithoutConfirmFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Run prepared paid work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Avoid advance starting a large paid prompt", [task]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("codex-cli"))
        ];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(
                "codex-cli",
                "Write-Output ok",
                root,
                DateTimeOffset.UtcNow,
                ProviderName: "OpenAI",
                ModelName: "gpt-5-codex",
                TaskComplexity: TaskComplexity.Simple,
                PromptCharacterCount: 12001));

        var changed = CliCommandDispatcher.ExecuteCommand(
            ["advance-subscription", "--confirm-subscription-advance"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        Xunit.Assert.False(changed);
        Xunit.Assert.Null(task.LastProcess);
    }

    [Xunit.Fact(DisplayName = "Cli_execute_dispatch_requires_confirm_flag")]
    public void CliExecuteDispatchRequiresConfirmFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Avoid accidental foreground process start", [task]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider()]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("local", "Write-Output ok", root, DateTimeOffset.UtcNow));

        InvalidOperationException? ex = null;
        try
        {
            CliCommandDispatcher.ExecuteCommand(
                ["execute-dispatch", "1"],
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
        Xunit.Assert.Contains("--confirm-dispatch-start", ex!.Message);
        Xunit.Assert.Empty(goal.Tasks.Single().VerificationHistory);
    }

    [Xunit.Fact(DisplayName = "Cli_start_dispatch_requires_confirm_flag")]
    public void CliStartDispatchRequiresConfirmFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Avoid accidental background process start", [task]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider()]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("local", "Write-Output ok", root, DateTimeOffset.UtcNow));

        InvalidOperationException? ex = null;
        try
        {
            CliCommandDispatcher.ExecuteCommand(
                ["start-dispatch", "1"],
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
        Xunit.Assert.Contains("--confirm-dispatch-start", ex!.Message);
        Xunit.Assert.Null(goal.Tasks.Single().LastProcess);
    }

    [Xunit.Fact(DisplayName = "Cli_start_dispatches_requires_confirm_flag")]
    public void CliStartDispatchesRequiresConfirmFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Avoid accidental batch process start", [task]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider()]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("local", "Write-Output ok", root, DateTimeOffset.UtcNow));

        InvalidOperationException? ex = null;
        try
        {
            CliCommandDispatcher.ExecuteCommand(
                ["start-dispatches"],
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
        Xunit.Assert.Contains("--confirm-batch-start", ex!.Message);
        Xunit.Assert.Null(goal.Tasks.Single().LastProcess);
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

    [Xunit.Fact(DisplayName = "Cli_paid_provider_smoke_requires_confirm_flag")]
    public void CliPaidProviderSmokeRequiresConfirmFlag()
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
                ["provider-smoke", "openai"],
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
        Xunit.Assert.Contains("--confirm-paid-smoke", ex!.Message);
    }

    [Xunit.Fact(DisplayName = "Cli_retry_requires_message_without_clearing_evidence")]
    public void CliRetryRequiresMessageWithoutClearingEvidence()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Avoid evidence-free retry", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider()]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.Single();
        var verification = new TaskVerificationRecord("dotnet test", root, 0, "passed", "", DateTimeOffset.UtcNow);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, task.Id, verification);

        ArgumentException? ex = null;
        try
        {
            CliCommandDispatcher.ExecuteCommand(
                ["retry", "1"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        }
        catch (ArgumentException caught)
        {
            ex = caught;
        }

        Xunit.Assert.NotNull(ex);
        Xunit.Assert.Contains("retry <task-number> <message>", ex!.Message);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Xunit.Assert.Equal(verification, task.LastVerification);
    }
}
