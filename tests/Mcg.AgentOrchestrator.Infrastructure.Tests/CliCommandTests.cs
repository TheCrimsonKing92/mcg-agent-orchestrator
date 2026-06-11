using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
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

    [Xunit.Fact(DisplayName = "Cli_tenant_and_architecture_report_tenant_scoped_runtime_paths")]
    public void CliTenantAndArchitectureReportTenantScopedRuntimePaths()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root, tenantName: "acme");
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var originalOut = Console.Out;
        using var writer = new StringWriter();

        try
        {
            Console.SetOut(writer);

            var tenantChanged = CliCommandDispatcher.ExecuteCommand(
                ["tenant"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            var architectureChanged = CliCommandDispatcher.ExecuteCommand(
                ["architecture"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);

            Xunit.Assert.False(tenantChanged);
            Xunit.Assert.False(architectureChanged);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();
        Xunit.Assert.Contains("Tenant: acme", output);
        Xunit.Assert.Contains("Tenant scoped: True", output);
        Xunit.Assert.Contains(Path.Combine(".orchestrator", "tenants", "acme", "state.json"), output);
        Xunit.Assert.Contains("Architecture:", output);
        Xunit.Assert.Contains("subscriptions: Subscription dispatches use worker profiles with role-based sandbox/permission placeholders", output);
        Xunit.Assert.Contains("rollback: Goal worktrees isolate file-touching work", output);
        Xunit.Assert.Contains("state stores:", output);
        Xunit.Assert.Contains("api surfaces:", output);
        Xunit.Assert.Contains("/api/system/architecture", output);
        Xunit.Assert.Contains("safety gates:", output);
        Xunit.Assert.Contains("Tenant names are normalized", output);
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

    [Xunit.Fact(DisplayName = "Cli_next_action_command_confirms_complex_paid_api_run")]
    public void CliNextActionCommandConfirmsComplexPaidApiRun()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Plan architecture work",
            [new TaskSpec(TaskId.New(), "Design and implement a production multi-tenant architecture.", AgentRole.Developer)]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly,
            ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
        kernel.ActivateGoal(goal.Id, [agent]);
        var action = kernel.BuildNextActions(goal.Id).Items.Single();

        var command = ConsoleViews.BuildSuggestedCommand(goal, action, [agent]);

        Xunit.Assert.Equal("run 1 --confirm-paid-api-run --confirm-large-paid-api-prompt", command);
    }

    [Xunit.Fact(DisplayName = "Cli_next_actions_prints_cost_recommendation")]
    public void CliNextActionsPrintsCostRecommendation()
    {
        var kernel = new AgentOrchestratorKernel();
        var priorTask = new TaskSpec(TaskId.New(), "Update the old label.", AgentRole.Developer);
        var nextTask = new TaskSpec(TaskId.New(), "Update the next label.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Avoid repeating overkill API model", [priorTask, nextTask]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey, "medium", 768),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        kernel.ActivateGoal(goal.Id, [agent]);
        kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
            "manual-verification passed",
            "C:\\repo",
            0,
            "Evidence checked.\nModel fit: OpenAI/gpt-5-codex - overkill - copy-only change.",
            string.Empty,
            DateTimeOffset.UtcNow));
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);

            ConsoleViews.PrintNextActions(goal, kernel.BuildNextActions(goal.Id), [agent]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();
        Xunit.Assert.Contains("command: run 2 --confirm-paid-api-run --confirm-large-paid-api-prompt", output);
        Xunit.Assert.Contains("cost: prior overkill API model. Prior evidence says OpenAI/gpt-5-codex was overkill; try local Ollama/qwen3:8b via agent configuration before paid API run.", output);
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

    [Xunit.Fact(DisplayName = "Cli_subscription_dispatch_can_target_non_latest_goal")]
    public void CliSubscriptionDispatchCanTargetNonLatestGoal()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var olderGoal = kernel.CreateGoal(
            "Keep older worker reachable",
            [new TaskSpec(TaskId.New(), "Do older work", AgentRole.Developer)]);
        var latestGoal = kernel.CreateGoal(
            "Do newer work",
            [new TaskSpec(TaskId.New(), "Do newer work", AgentRole.Developer)]);
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
        Goal? currentGoal = latestGoal;
        kernel.ActivateGoal(olderGoal.Id, agents);
        kernel.ActivateGoal(latestGoal.Id, agents);
        var olderGoalPrefix = olderGoal.Id.Value[..8];

        var changed = CliCommandDispatcher.ExecuteCommand(
            ["subscription-dispatch", olderGoalPrefix, "1"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(olderGoal.Id, currentGoal!.Id);
        Xunit.Assert.NotNull(olderGoal.Tasks.Single().LastDispatch);
        Xunit.Assert.Null(latestGoal.Tasks.Single().LastDispatch);
    }

    [Xunit.Fact(DisplayName = "Cli_logs_stream_arg_is_not_misinterpreted_as_goal_prefix")]
    public void CliLogsStreamArgIsNotMisinterpretedAsGoalPrefix()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Read stdout logs", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        // Regression guard: "logs 1 stdout" must resolve task 1 on the current goal, not treat "1"
        // as a goal prefix and "stdout" as a task number.
        InvalidOperationException? ex = null;
        try
        {
            CliCommandDispatcher.ExecuteCommand(
                ["logs", "1", "stdout"],
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

        // Must fail because the task has no process, not because the goal/task wasn't found.
        Xunit.Assert.NotNull(ex);
        Xunit.Assert.Contains("no background process logs", ex!.Message);
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

    [Xunit.Fact(DisplayName = "Cli_api_run_blocks_prior_overkill_paid_model_without_large_prompt_confirm")]
    public void CliApiRunBlocksPriorOverkillPaidModelWithoutLargePromptConfirm()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var priorTask = new TaskSpec(TaskId.New(), "Update the old label.", AgentRole.Developer);
        var nextTask = new TaskSpec(TaskId.New(), "Update the next label.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Avoid repeating overkill API model", [priorTask, nextTask]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.ApiOnly);
        IReadOnlyList<AgentDefinition> agents = [agent];
        var provider = new FakeSmokeProvider(providerName: "OpenAI");
        var providers = new InMemoryModelProviderRegistry([provider]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
            "manual-verification passed",
            root,
            0,
            "Evidence checked.\nModel fit: OpenAI/gpt-5-codex - overkill - copy-only change.",
            string.Empty,
            DateTimeOffset.UtcNow));

        InvalidOperationException? ex = null;
        try
        {
            CliCommandDispatcher.ExecuteCommand(
                ["api-run", "2", "--confirm-paid-api-run"],
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
        Xunit.Assert.Contains("--confirm-large-paid-api-prompt", ex!.Message);
        Xunit.Assert.Contains("prior overkill model-fit note", ex.Message);
        Xunit.Assert.Contains("shapes copy-only change", ex.Message);
        Xunit.Assert.Null(provider.LastRequest);
        Xunit.Assert.Null(nextTask.LastExecution);
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

    [Xunit.Fact(DisplayName = "Cli_api_run_blocks_complex_paid_model_without_confirm_flag")]
    public void CliApiRunBlocksComplexPaidModelWithoutConfirmFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Plan architecture work",
            [new TaskSpec(TaskId.New(), "Design and implement a production multi-tenant architecture.", AgentRole.Developer)]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli"),
            ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
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

        Xunit.Assert.Equal(TaskComplexity.Complex, preview.TaskComplexity);
        Xunit.Assert.True(preview.PromptCharacterCount <= 6000);
        Xunit.Assert.NotNull(ex);
        Xunit.Assert.Contains("--confirm-large-paid-api-prompt", ex!.Message);
        Xunit.Assert.Contains("complex paid model", ex.Message);
        Xunit.Assert.Contains("Confirm this task needs the complex paid model before API run", ex.Message);
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

    [Xunit.Fact(DisplayName = "Cli_profile_dispatch_allows_complex_paid_subscription_start_under_size_threshold")]
    public void CliProfileDispatchAllowsComplexPaidSubscriptionStartUnderSizeThreshold()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Plan architecture work",
            [new TaskSpec(TaskId.New(), "Design and implement a production multi-tenant architecture.", AgentRole.Developer)]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini-codex", "low"),
            ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
        IReadOnlyList<AgentDefinition> agents = [agent];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.Single();

        using var writer = new StringWriter();
        var originalOut = Console.Out;
        bool dispatched;
        try
        {
            Console.SetOut(writer);
            dispatched = CliCommandDispatcher.ExecuteCommand(
                ["profile-dispatch", "1", "codex-cli"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var dispatchOutput = writer.ToString();
        var risk = SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(goal, task);

        Xunit.Assert.True(dispatched);
        Xunit.Assert.Contains("Cost note: paid subscription handoff prepared", dispatchOutput);
        Xunit.Assert.Contains("try local Ollama/qwen3:8b via agent configuration when the task is routine", dispatchOutput);
        Xunit.Assert.Equal("OpenAI", task.LastDispatch!.ProviderName);
        Xunit.Assert.Equal("gpt-5.5", task.LastDispatch.ModelName);
        Xunit.Assert.Equal(TaskComplexity.Complex, task.LastDispatch.TaskComplexity);
        Xunit.Assert.Null(risk);
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

    [Xunit.Fact(DisplayName = "Cli_subscription_plan_prints_ready_start_cost_risk")]
    public void CliSubscriptionPlanPrintsReadyStartCostRisk()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Plan architecture work",
            [new TaskSpec(TaskId.New(), "Design and implement a production multi-tenant architecture.", AgentRole.Developer)]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini-codex", "medium"),
            ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
        kernel.ActivateGoal(goal.Id, [agent]);
        var plan = DashboardResponseMapper.BuildSubscriptionPlan(
            goal,
            [agent],
            WorkerProfileCatalog.Default(),
            task => kernel.BuildTaskBrief(goal.Id, task.Id).Content.Length);
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);

            ConsoleViews.PrintSubscriptionPlan(plan);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();
        Xunit.Assert.DoesNotContain("Ready start risk:", output);
        Xunit.Assert.DoesNotContain("--confirm-large-paid-subscription-start", output);
        Xunit.Assert.Contains("gpt-5.5", output);
        Xunit.Assert.Contains("Complex", output);
    }

    [Xunit.Fact(DisplayName = "Cli_subscription_plan_prints_model_fit_recommendation")]
    public void CliSubscriptionPlanPrintsModelFitRecommendation()
    {
        var kernel = new AgentOrchestratorKernel();
        var priorTask = new TaskSpec(TaskId.New(), "Update the old button label.", AgentRole.Developer);
        var nextTask = new TaskSpec(TaskId.New(), "Update the next button label.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Tune model choice from CLI evidence", [priorTask, nextTask]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
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
        var plan = DashboardResponseMapper.BuildSubscriptionPlan(
            goal,
            [agent],
            WorkerProfileCatalog.Default(),
            task => kernel.BuildTaskBrief(goal.Id, task.Id).Content.Length);
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);

            ConsoleViews.PrintSubscriptionPlan(plan);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();
        Xunit.Assert.Contains("prior fit 1: overkill 1", output);
        Xunit.Assert.Contains("shapes copy-only change", output);
        Xunit.Assert.Contains("try local Ollama/qwen3:8b via agent configuration before paid start", output);
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
        Xunit.Assert.Contains("default local Ollama smoke first", ex.Message);
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
        Xunit.Assert.Contains("default local Ollama smoke first", ex.Message);
    }

    [Xunit.Fact(DisplayName = "Cli_worker_profile_check_validates_active_subscription_routes")]
    public void CliWorkerProfileCheckValidatesActiveSubscriptionRoutes()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", "gpt-test", ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("custom-agent"))
        ];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider()]);
        var profiles = new WorkerProfileCatalog([]);
        Goal? currentGoal = null;

        InvalidOperationException? ex = null;
        try
        {
            CliCommandDispatcher.ExecuteCommand(
                ["worker-profile-check"],
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
        Xunit.Assert.Contains("active subscription routes", ex!.Message);
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

    [Xunit.Fact(DisplayName = "Cli_note_preserves_task_and_allows_subscription_dispatch_and_retry")]
    public void CliNotePreservesTaskAndAllowsSubscriptionDispatchAndRetry()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Keep guidance status-neutral", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
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
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.Single();

        var noteChanged = CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand("note 1 Preserve dispatch readiness."),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);
        var statusAfterNote = task.Status;
        var retryChanged = CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand("retry 1 Retry remains available."),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);
        var statusAfterRetry = task.Status;
        var secondNoteChanged = CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand("note 1 Dispatch remains available."),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);
        var dispatchChanged = CliCommandDispatcher.ExecuteCommand(
            ["subscription-dispatch", "1"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        Xunit.Assert.True(noteChanged);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, statusAfterNote);
        Xunit.Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskNote && evt.Message == "Preserve dispatch readiness.");
        Xunit.Assert.True(retryChanged);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, statusAfterRetry);
        Xunit.Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried && evt.Message == "Retry remains available.");
        Xunit.Assert.True(secondNoteChanged);
        Xunit.Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskNote && evt.Message == "Dispatch remains available.");
        Xunit.Assert.True(dispatchChanged);
        Xunit.Assert.NotNull(task.LastDispatch);
    }

    [Xunit.Fact(DisplayName = "Cli_agent_command_creates_agent_with_complex_model_from_flag")]
    public void CliAgentCommandCreatesAgentWithComplexModelFromFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CliCommandDispatcher.ExecuteCommand(
            ["agent", "Developer", "Anthropic", "claude-haiku-4-5", "--complex-model", "claude-sonnet-4-6"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        var agent = agents.Single(a => a.Role == AgentRole.Developer);
        Xunit.Assert.Equal("Anthropic", agent.Model.ProviderName);
        Xunit.Assert.Equal("claude-haiku-4-5", agent.Model.ModelName);
        Xunit.Assert.Equal("Anthropic", agent.ComplexModel!.ProviderName);
        Xunit.Assert.Equal("claude-sonnet-4-6", agent.ComplexModel.ModelName);
    }

    [Xunit.Fact(DisplayName = "Cli_agent_command_uses_default_complex_model_when_flag_omitted")]
    public void CliAgentCommandUsesDefaultComplexModelWhenFlagOmitted()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CliCommandDispatcher.ExecuteCommand(
            ["agent", "Developer", "Anthropic", "claude-haiku-4-5"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        var agent = agents.Single(a => a.Role == AgentRole.Developer);
        Xunit.Assert.Equal("Anthropic", agent.Model.ProviderName);
        Xunit.Assert.Equal("claude-haiku-4-5", agent.Model.ModelName);
        Xunit.Assert.Equal("Anthropic", agent.ComplexModel!.ProviderName);
        Xunit.Assert.Equal("claude-sonnet-4-6", agent.ComplexModel.ModelName);
    }

    [Xunit.Fact(DisplayName = "Cli_agent_command_rejects_complex_model_flag_without_value")]
    public void CliAgentCommandRejectsComplexModelFlagWithoutValue()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        ArgumentException? ex = null;
        try
        {
            CliCommandDispatcher.ExecuteCommand(
                ["agent", "Developer", "Anthropic", "claude-haiku-4-5", "--complex-model"],
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
        Xunit.Assert.Contains("--complex-model", ex!.Message);
    }
}
