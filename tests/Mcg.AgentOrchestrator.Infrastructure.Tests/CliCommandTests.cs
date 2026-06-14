using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;

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

    [Xunit.Fact(DisplayName = "Cli_state_rollback_requires_confirmation_and_restores_in_memory_kernel")]
    public void CliStateRollbackRequiresConfirmationAndRestoresInMemoryKernel()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root, tenantName: "acme");
        var first = new AgentOrchestratorKernel();
        first.CreateGoal("Rollback target");
        OrchestratorStateStore.Save(workspace.StatePath, first);
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal("Current primary");
        OrchestratorStateStore.Save(workspace.StatePath, kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = kernel.Goals.Single();

        var blocked = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["state-rollback"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var changed = CliCommandDispatcher.ExecuteCommand(
            ["state-rollback", "--confirm-state-rollback"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        Xunit.Assert.Contains("--confirm-state-rollback", blocked.Message);
        Xunit.Assert.False(changed);
        Xunit.Assert.Equal("Rollback target", kernel.Goals.Single().Objective);
        Xunit.Assert.Equal(kernel.Goals.Single().Id, currentGoal!.Id);
        Xunit.Assert.Equal("Rollback target", OrchestratorStateStore.Load(workspace.StatePath).Goals.Single().Objective);
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

    [Xunit.Fact(DisplayName = "Cli_autonomy_policies_lists_named_modes")]
    public void CliAutonomyPoliciesListsNamedModes()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["autonomy-policies"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("Default autonomy policy: supervised-auto", output);
        Xunit.Assert.Contains("Policy observe:", output);
        Xunit.Assert.Contains("Policy safe-auto:", output);
        Xunit.Assert.Contains("Policy supervised-auto:", output);
        Xunit.Assert.Contains("acceptance=True", output);
    }

    [Xunit.Fact(DisplayName = "Cli_observe_autonomy_blocks_worker_start")]
    public void CliObserveAutonomyBlocksWorkerStart()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Observe only", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
        IReadOnlyList<AgentDefinition> agents = [agent];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var ex = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["start-subscription-ready", "--confirm-batch-start", "--autonomy", "observe"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("policy 'observe' blocks start-subscription-ready", ex.Message);
        Xunit.Assert.Null(goal.Tasks.Single().LastDispatch);
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

    [Xunit.Fact(DisplayName = "Cli_next_prints_goal_health_recommendation")]
    public void CliNextPrintsGoalHealthRecommendation()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Expose goal health", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.ApiOnly)
        ];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["next"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("Health: Active score=70", output);
        Xunit.Assert.Contains("recommendation:", output);
        Xunit.Assert.Contains("command: run 1", output);
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

    [Xunit.Fact(DisplayName = "Cli_backlog_intake_prints_goal_slice_without_mutating_state")]
    public void CliBacklogIntakePrintsGoalSliceWithoutMutatingState()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "BACKLOG.md"), """
        # Backlog

        ## Decision record (durable context, not work items)

        Not work.

        ## Add dashboard operator inbox

        Add dashboard subscription-backed operator inbox for failed preflights and acceptance gates. Done when dashboard evidence is visible.
        """);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var originalOut = Console.Out;
        using var writer = new StringWriter();

        bool changed;
        try
        {
            Console.SetOut(writer);
            changed = CliCommandDispatcher.ExecuteCommand(
                ["backlog-intake", "dashboard operator inbox"],
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

        var output = writer.ToString();
        Xunit.Assert.False(changed);
        Xunit.Assert.Null(currentGoal);
        Xunit.Assert.Empty(kernel.Goals);
        Xunit.Assert.Contains("Backlog intake: 1 item", output);
        Xunit.Assert.Contains("## Add dashboard operator inbox", output);
        Xunit.Assert.Contains("Roles:", output);
        Xunit.Assert.Contains("Risks: subscription-cost, operator-ux", output);
        Xunit.Assert.Contains("Target files/scopes:", output);
        Xunit.Assert.Contains("src/Mcg.AgentOrchestrator.App/Dashboard", output);
        Xunit.Assert.Contains("Create commands:", output);
        Xunit.Assert.Contains("--create-goal", output);
    }

    [Xunit.Fact(DisplayName = "Cli_backlog_intake_create_simple_goal_requires_explicit_flag")]
    public void CliBacklogIntakeCreateSimpleGoalRequiresExplicitFlag()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "BACKLOG.md"), """
        # Backlog

        ## Add deterministic build/test broker

        Add deterministic build and test broker for CS2012 and isolated artifacts. Done when focused tests prove isolation.
        """);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var originalOut = Console.Out;
        using var writer = new StringWriter();

        bool changed;
        try
        {
            Console.SetOut(writer);
            changed = CliCommandDispatcher.ExecuteCommand(
                ["backlog-intake", "build/test broker", "--create-simple-goal"],
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

        Xunit.Assert.True(changed);
        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.Single(kernel.Goals);
        Xunit.Assert.Single(currentGoal!.Tasks);
        Xunit.Assert.Contains("Backlog slice: Add deterministic build/test broker", currentGoal.Objective);
        Xunit.Assert.Contains("scripts/Invoke-IsolatedDotnet.ps1", currentGoal.Objective);
        Xunit.Assert.Contains("Created simple goal from backlog slice.", writer.ToString());
    }

    [Xunit.Fact(DisplayName = "Cli_goal_plan_prints_dependency_graph_without_mutating_state")]
    public void CliGoalPlanPrintsDependencyGraphWithoutMutatingState()
    {
        var root = CreateTempDirectory();
        WritePlanningBacklog(root);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var originalOut = Console.Out;
        using var writer = new StringWriter();

        bool changed;
        try
        {
            Console.SetOut(writer);
            changed = CliCommandDispatcher.ExecuteCommand(
                ["goal-plan"],
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

        var output = writer.ToString();
        Xunit.Assert.False(changed);
        Xunit.Assert.Null(currentGoal);
        Xunit.Assert.Empty(kernel.Goals);
        Xunit.Assert.Contains("Goal plan: 3 node(s)", output);
        Xunit.Assert.Contains("Compiled graph:", output);
        Xunit.Assert.Contains("Rollback boundary:", output);
        Xunit.Assert.Contains("Capabilities:", output);
        Xunit.Assert.Contains("Verification contracts:", output);
        Xunit.Assert.Contains("0 dependency edge(s)", output);
        Xunit.Assert.Contains("Parallel batches:", output);
        Xunit.Assert.Contains("Create commands:", output);
    }

    [Xunit.Fact(DisplayName = "Cli_goal_prints_objective_plan_before_task_creation")]
    public void CliGoalPrintsObjectivePlanBeforeTaskCreation()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var originalOut = Console.Out;
        using var writer = new StringWriter();

        bool changed;
        try
        {
            Console.SetOut(writer);
            changed = CliCommandDispatcher.ExecuteCommand(
                ["goal", "Update docs/usage.md to explain goal objective planning"],
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

        var output = writer.ToString();
        Xunit.Assert.True(changed);
        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.Single(kernel.Goals);
        Xunit.Assert.StartsWith("Goal objective plan:", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("\"riskLabels\"", output);
        Xunit.Assert.Contains("\"taskBoundaries\"", output);
        Xunit.Assert.Contains("\"requiredVerification\"", output);
        Xunit.Assert.Contains("docs/usage.md", output);
    }

    [Xunit.Fact(DisplayName = "Cli_goal_rejects_ambiguous_objective_without_mutating_state")]
    public void CliGoalRejectsAmbiguousObjectiveWithoutMutatingState()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var ex = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["goal", "Improve things"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("needs clarification", ex.Message);
        Xunit.Assert.Null(currentGoal);
        Xunit.Assert.Empty(kernel.Goals);
    }

    [Xunit.Fact(DisplayName = "GoalObjectivePlanner_classifies_low_medium_and_high_risk_objectives")]
    public void GoalObjectivePlannerClassifiesLowMediumAndHighRiskObjectives()
    {
        var docs = GoalObjectivePlanner.Build("Inspect docs/usage.md and summarize current behavior", simple: false);
        var code = GoalObjectivePlanner.Build("Implement src/Mcg.AgentOrchestrator.App/GoalObjectivePlanner.cs with tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.cs coverage", simple: true);
        var high = GoalObjectivePlanner.Build("Update auth token rollback policy across src/Mcg.AgentOrchestrator.App/AuthPolicy.cs tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AuthPolicyTests.cs scripts/RepairAuth.ps1 config/auth.json", simple: false);

        Xunit.Assert.True(docs.CanCreateGoal);
        Xunit.Assert.Contains("docs/usage.md", docs.FileScopes);
        Xunit.Assert.Contains(docs.RequiredVerification, item => item.Contains("documentation diff", StringComparison.Ordinal));
        Xunit.Assert.True(code.CanCreateGoal);
        Xunit.Assert.Contains(code.RequiredTools, item => item.Contains("Invoke-IsolatedDotnet", StringComparison.Ordinal));
        Xunit.Assert.Single(code.TaskBoundaries);
        Xunit.Assert.Equal(AgentRole.Developer, code.TaskBoundaries[0].Role);
        Xunit.Assert.True(high.CanCreateGoal);
        Xunit.Assert.Contains("high-risk", high.RiskLabels);
        Xunit.Assert.Contains("multi-scope", high.RiskLabels);
        Xunit.Assert.Equal(5, high.TaskBoundaries.Count);
    }

    [Xunit.Fact(DisplayName = "Cli_intent_template_prints_feature_objective_without_mutating_state")]
    public void CliIntentTemplatePrintsFeatureObjectiveWithoutMutatingState()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var originalOut = Console.Out;
        using var writer = new StringWriter();

        bool changed;
        try
        {
            Console.SetOut(writer);
            changed = CliCommandDispatcher.ExecuteCommand(
                ["intent-template", "feature", "Add dashboard action recommendations"],
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

        var output = writer.ToString();
        Xunit.Assert.False(changed);
        Xunit.Assert.Null(currentGoal);
        Xunit.Assert.Empty(kernel.Goals);
        Xunit.Assert.Contains("Intent template: feature", output);
        Xunit.Assert.Contains("Request: Add dashboard action recommendations", output);
        Xunit.Assert.Contains("Decomposition rules:", output);
        Xunit.Assert.Contains("Required evidence:", output);
        Xunit.Assert.Contains("Verification policy:", output);
        Xunit.Assert.Contains("Deterministic workflows:", output);
        Xunit.Assert.Contains("--create-goal", output);
    }

    [Xunit.Fact(DisplayName = "Cli_intent_template_creates_simple_goal_from_dashboard_template")]
    public void CliIntentTemplateCreatesSimpleGoalFromDashboardTemplate()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var originalOut = Console.Out;
        using var writer = new StringWriter();

        bool changed;
        try
        {
            Console.SetOut(writer);
            changed = CliCommandDispatcher.ExecuteCommand(
                ["intent-template", "dashboard", "Expose failure triage in the goal page", "--create-simple-goal"],
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

        Xunit.Assert.True(changed);
        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.Single(kernel.Goals);
        Xunit.Assert.Single(currentGoal!.Tasks);
        Xunit.Assert.Contains("Intent template: dashboard", currentGoal.Objective);
        Xunit.Assert.Contains("Request: Expose failure triage in the goal page", currentGoal.Objective);
        Xunit.Assert.Contains("Expose the same state through API DTOs before relying on rendered HTML.", currentGoal.Objective);
        Xunit.Assert.Contains("Run focused dashboard rendering/API tests.", currentGoal.Objective);
        Xunit.Assert.Contains("Created simple goal from intent template.", writer.ToString());
    }

    [Xunit.Fact(DisplayName = "GoalDependencyPlanner_compiles_stable_graph_and_batches_independent_scopes")]
    public void GoalDependencyPlannerCompilesStableGraphAndBatchesIndependentScopes()
    {
        var root = CreateTempDirectory();
        WriteCompilationBacklog(root);

        var first = GoalDependencyPlanner.Build(BacklogIntakePlanner.Build(root, maxItems: 2));
        var second = GoalDependencyPlanner.Build(BacklogIntakePlanner.Build(root, maxItems: 2));

        Xunit.Assert.True(first.CompiledGraph.IsRunnable);
        Xunit.Assert.Equal(first.CompiledGraph.GraphId, second.CompiledGraph.GraphId);
        Xunit.Assert.Equal(2, first.CompiledGraph.Nodes.Count);
        Xunit.Assert.All(first.CompiledGraph.Nodes, node => Xunit.Assert.True(node.CanCreateGoal));
        Xunit.Assert.Contains(first.CompiledGraph.Nodes[0].RequiredCapabilities, item => item == "workspace-write");
        Xunit.Assert.Contains(first.CompiledGraph.Nodes[0].VerificationContracts, item => item.Contains("Focused unit tests", StringComparison.Ordinal));
        Xunit.Assert.Equal(2, first.ParallelPlan.Batches.Count);
        Xunit.Assert.Equal(["g1"], first.ParallelPlan.Batches[0].IntentIds);
        Xunit.Assert.Equal(["g2"], first.ParallelPlan.Batches[1].IntentIds);
    }

    [Xunit.Fact(DisplayName = "GoalDependencyPlanner_serializes_conflicting_compiled_file_scopes")]
    public void GoalDependencyPlannerSerializesConflictingCompiledFileScopes()
    {
        var root = CreateTempDirectory();
        WriteConflictingCompilationBacklog(root);

        var plan = GoalDependencyPlanner.Build(BacklogIntakePlanner.Build(root, maxItems: 2));

        Xunit.Assert.True(plan.CompiledGraph.IsRunnable);
        Xunit.Assert.Equal(2, plan.ParallelPlan.Batches.Count);
        Xunit.Assert.Equal(["g1"], plan.ParallelPlan.Batches[0].IntentIds);
        Xunit.Assert.Equal(["g2"], plan.ParallelPlan.Batches[1].IntentIds);
        var second = plan.CompiledGraph.Nodes.Single(node => node.Id == "g2");
        Xunit.Assert.Equal(2, second.ParallelBatch);
        Xunit.Assert.Equal(ParallelExecutionDisposition.Serialized, second.ParallelDisposition);
    }

    [Xunit.Fact(DisplayName = "Dashboard_goal_plan_dto_exposes_compiled_graph_before_goal_creation")]
    public void DashboardGoalPlanDtoExposesCompiledGraphBeforeGoalCreation()
    {
        var root = CreateTempDirectory();
        WriteCompilationBacklog(root);
        var intake = BacklogIntakePlanner.Build(root, maxItems: 2);
        var plan = GoalDependencyPlanner.Build(intake);

        var dto = DashboardResponseMapper.ToBacklogGoalPlanDto(intake, plan);

        Xunit.Assert.Equal(2, dto.NodeCount);
        Xunit.Assert.Equal(plan.CompiledGraph.GraphId, dto.CompiledGraph.GraphId);
        Xunit.Assert.True(dto.CompiledGraph.IsRunnable);
        Xunit.Assert.Equal(2, dto.CompiledGraph.Nodes.Count);
        Xunit.Assert.Contains(dto.CompiledGraph.Nodes[0].FileScopes, scope => scope.Contains("src/FeatureA", StringComparison.Ordinal));
        Xunit.Assert.Equal(2, dto.ParallelPlan.Batches.Count);
    }

    [Xunit.Fact(DisplayName = "Cli_goal_plan_create_simple_goals_embeds_explicit_dependencies")]
    public void CliGoalPlanCreateSimpleGoalsEmbedsExplicitDependencies()
    {
        var root = CreateTempDirectory();
        WritePlanningBacklog(root);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var originalOut = Console.Out;
        using var writer = new StringWriter();

        bool changed;
        try
        {
            Console.SetOut(writer);
            changed = CliCommandDispatcher.ExecuteCommand(
                ["goal-plan", "--create-simple-goals"],
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

        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(3, kernel.Goals.Count);
        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.DoesNotContain(kernel.Goals, goal => goal.Objective.Contains("Explicit dependencies:", StringComparison.Ordinal));
        Xunit.Assert.All(kernel.Goals, goal => Xunit.Assert.Single(goal.Tasks));
        Xunit.Assert.Contains("Created simple goal", writer.ToString());
    }

    [Xunit.Fact(DisplayName = "Cli_goal_recovery_reports_stale_process_and_resume_commands")]
    public void CliGoalRecoveryReportsStaleProcessAndResumeCommands()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Run interrupted worker", AgentRole.Developer);
        var goal = kernel.CreateGoal("Recover interrupted goal", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", root, DateTimeOffset.UtcNow));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(999999, "codex exec prompt.md", root, "out.log", "err.log", "exit.txt", DateTimeOffset.UtcNow, null, null));
        var originalOut = Console.Out;
        using var writer = new StringWriter();

        bool changed;
        try
        {
            Console.SetOut(writer);
            changed = CliCommandDispatcher.ExecuteCommand(
                ["goal-recovery"],
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

        var output = writer.ToString();
        Xunit.Assert.False(changed);
        Xunit.Assert.Contains("Goal recovery", output);
        Xunit.Assert.Contains("recorded process pid=999999 is not alive", output);
        Xunit.Assert.Contains("command: refresh-dispatch 1", output);
        Xunit.Assert.Contains("workspace create", output);
        Xunit.Assert.Contains($"park-goal {goal.Id.Value[..8]} <reason> --confirm-goal-park", output);
    }

    [Xunit.Fact(DisplayName = "HistoricalDogfoodEvaluation_scores_recorded_goal_state_without_starting_workers")]
    public void HistoricalDogfoodEvaluationScoresRecordedGoalStateWithoutStartingWorkers()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement without proof", AgentRole.Developer);
        var goal = kernel.CreateGoal("Evaluate historical dogfood scenario", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        kernel.ActivateGoal(goal.Id, agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Worker claimed completion without evidence.");

        var report = HistoricalDogfoodEvaluationHarness.Evaluate(kernel, goal, agents, profiles, workspace);

        Xunit.Assert.Equal(goal.Id, report.GoalId);
        Xunit.Assert.True(report.Score < 100);
        Xunit.Assert.Contains(report.Metrics, metric => metric.Name == "false-completion-risk" && metric.Value == 1);
        Xunit.Assert.Contains(report.Metrics, metric => metric.Name == "verification-gaps" && metric.Value == 1);
        Xunit.Assert.Contains(report.Recommendations, item => item.Contains("verification", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "Cli_dogfood_eval_prints_replayable_metrics_without_mutating_state")]
    public void CliDogfoodEvalPrintsReplayableMetricsWithoutMutatingState()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement without proof", AgentRole.Developer);
        var goal = kernel.CreateGoal("Evaluate historical dogfood scenario", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Worker claimed completion without evidence.");

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["dogfood-eval", goal.Id.Value[..8]],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Equal(goal.Id, currentGoal!.Id);
        Xunit.Assert.Single(kernel.Goals);
        Xunit.Assert.Contains("Dogfood evaluation", output);
        Xunit.Assert.Contains("false-completion-risk", output);
        Xunit.Assert.Contains("verification-gaps", output);
        Xunit.Assert.Contains("Recommendations:", output);
    }

    [Xunit.Fact(DisplayName = "Cli_goal_recovery_reports_completed_task_missing_verification")]
    public void CliGoalRecoveryReportsCompletedTaskMissingVerification()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Verify me", AgentRole.Tester);
        var goal = kernel.CreateGoal("Recover missing verification", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Worker reported done.");
        var originalOut = Console.Out;
        using var writer = new StringWriter();

        try
        {
            Console.SetOut(writer);
            CliCommandDispatcher.ExecuteCommand(
                ["goal-recovery", goal.Id.Value[..8]],
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

        var output = writer.ToString();
        Xunit.Assert.Contains("task completed without verification evidence", output);
        Xunit.Assert.Contains("command: verify 1 <command>", output);
    }

    [Xunit.Fact(DisplayName = "Cli_supervisor_dry_run_reports_refresh_proposal")]
    public void CliSupervisorDryRunReportsRefreshProposal()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Refresh running worker", AgentRole.Planner);
        var goal = kernel.CreateGoal("Supervise running goal", [task]);
        IReadOnlyList<AgentDefinition> agents = [SubscriptionPlanner("planner", "Planner")];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["supervisor", "--autonomy", "safe-auto"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains("Supervisor goal:", output);
        Xunit.Assert.Contains("RefreshRunningProcess task 1", output);
        Xunit.Assert.Contains("canApply=True", output);
        Xunit.Assert.Equal(WorkTaskStatus.Running, task.Status);
    }

    [Xunit.Fact(DisplayName = "Cli_supervisor_apply_safe_refreshes_stale_process")]
    public void CliSupervisorApplySafeRefreshesStaleProcess()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Refresh stale worker", AgentRole.Planner);
        var goal = kernel.CreateGoal("Apply supervisor refresh", [task]);
        IReadOnlyList<AgentDefinition> agents = [SubscriptionPlanner("planner", "Planner")];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["supervisor", "--apply-safe", "--autonomy", "safe-auto"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });

        Xunit.Assert.True(output.Contains("Applied actions: 1", StringComparison.Ordinal), output);
        Xunit.Assert.Contains("refresh-dispatch 1", output);
        Xunit.Assert.False(task.LastProcess!.IsRunning);
        Xunit.Assert.NotNull(task.LastVerification);
        Xunit.Assert.Contains(goal.Timeline, (ProgressEvent evt) =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("allowed supervisor refresh", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Cli_supervisor_apply_safe_redelegates_recoverable_stall")]
    public void CliSupervisorApplySafeRedelegatesRecoverableStall()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Recover stalled worker", AgentRole.Planner);
        var goal = kernel.CreateGoal("Apply supervisor failover", [task]);
        var primary = SubscriptionPlanner("primary-planner", "Primary Planner");
        var alternate = SubscriptionPlanner("alternate-planner", "Alternate Planner");
        IReadOnlyList<AgentDefinition> agents = [primary, alternate];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", root, DateTimeOffset.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "claude prompt",
            root,
            1,
            string.Empty,
            "Background dispatch made no observable progress before the stall timeout; wrapper heartbeat state=running.",
            DateTimeOffset.UtcNow));
        var plan = GoalSupervisor.Build(kernel, goal, agents, workspace.ExecutionDirectory, AutonomyPolicy.SafeAuto);
        Xunit.Assert.True(
            plan.Proposals.Any(proposal => proposal.Kind == GoalSupervisorProposalKind.ReDelegateAfterRecoverableFailure && proposal.CanApply),
            string.Join(" | ", plan.Proposals.Select(proposal => $"{proposal.Kind}:{proposal.CanApply}:{proposal.Reason}")));

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["supervisor", "--apply-safe", "--autonomy", "safe-auto"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });

        Xunit.Assert.True(output.Contains("Applied actions: 1", StringComparison.Ordinal), output);
        Xunit.Assert.Contains("re-delegate 1", output);
        Xunit.Assert.Equal(alternate.Id, task.AssignedAgentId);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Xunit.Assert.Contains(goal.Timeline, (ProgressEvent evt) =>
            evt.Kind == ProgressKind.TaskRedelegated &&
            evt.Message.Contains("Alternate Planner", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Cli_drain_goals_dry_run_reports_subscription_and_operator_gates")]
    public void CliDrainGoalsDryRunReportsSubscriptionAndOperatorGates()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var subscriptionGoal = kernel.CreateGoal("Drain subscription work", [new TaskSpec(TaskId.New(), "Inspect docs/feature.md", AgentRole.Planner)]);
        var verificationTask = new TaskSpec(TaskId.New(), "Implement without verification", AgentRole.Developer);
        var gatedGoal = kernel.CreateGoal("Drain gated work", [verificationTask]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("planner"),
                "Planner",
                AgentRole.Planner,
                new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("codex-cli")),
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey))
        ];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = subscriptionGoal;
        kernel.ActivateGoal(subscriptionGoal.Id, agents);
        kernel.ActivateGoal(gatedGoal.Id, agents);
        kernel.ReportTaskProgress(gatedGoal.Id, verificationTask.Id, WorkTaskStatus.Completed, "Worker claimed completion.");

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["drain-goals", "--autonomy", "safe-auto"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains("Goal drain:", output);
        Xunit.Assert.Contains("subscription-start", output);
        Xunit.Assert.Contains("operator-gate", output);
        Xunit.Assert.Contains("Drain policy: default; maxStarts=1", output);
        Xunit.Assert.Contains("verify 1 <command>", output);
        Xunit.Assert.Null(subscriptionGoal.Tasks.Single().LastDispatch);
    }

    [Xunit.Fact(DisplayName = "Cli_drain_goals_loads_persisted_policy_and_blocks_disallowed_starts")]
    public void CliDrainGoalsLoadsPersistedPolicyAndBlocksDisallowedStarts()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory, GoalDrainPolicyStore.FileName), """
        {
          "name": "overnight-safe",
          "maxSubscriptionStartsPerDrain": 0,
          "allowedRoles": [ "Planner" ],
          "allowedProviders": [ "OpenAI" ],
          "largePromptBehavior": "defer",
          "requireReadinessRiskConfirmation": true,
          "requireAcceptanceGate": true
        }
        """);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Drain persisted policy", [new TaskSpec(TaskId.New(), "Inspect docs/feature.md", AgentRole.Planner)]);
        IReadOnlyList<AgentDefinition> agents = [SubscriptionPlanner("planner", "Planner")];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["drain-goals", "--apply", "--confirm-goal-drain", "--confirm-batch-start", "--autonomy", "safe-auto"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains("Drain policy: overnight-safe; maxStarts=0", output);
        Xunit.Assert.Contains("Applied actions: 0", output);
        Xunit.Assert.Null(goal.Tasks.Single().LastDispatch);
    }

    [Xunit.Fact(DisplayName = "GoalDrainPolicy_scheduled_windows_hold_starts_outside_allowed_time")]
    public void GoalDrainPolicyScheduledWindowsHoldStartsOutsideAllowedTime()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory, GoalDrainPolicyStore.FileName), """
        {
          "name": "overnight-safe",
          "maxSubscriptionStartsPerDrain": 2,
          "allowedRoles": [ "Planner" ],
          "allowedProviders": [ "OpenAI" ],
          "allowedLocalTimeWindows": [
            { "start": "22:00", "end": "06:00" }
          ]
        }
        """);
        var drainPolicy = GoalDrainPolicyStore.LoadOrDefault(workspace);
        var noon = new DateTimeOffset(new DateTime(2026, 6, 13, 12, 0, 0, DateTimeKind.Local));
        var night = new DateTimeOffset(new DateTime(2026, 6, 13, 23, 0, 0, DateTimeKind.Local));
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Drain only inside schedule", [new TaskSpec(TaskId.New(), "Inspect docs/feature.md", AgentRole.Planner)]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("planner"),
                "Planner",
                AgentRole.Planner,
                new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("codex-cli"))
        ];
        kernel.ActivateGoal(goal.Id, agents);

        var closedPlan = GoalDrainPlanner.Build(
            kernel,
            agents,
            WorkerProfileCatalog.Default(),
            workspace,
            AutonomyPolicy.SafeAuto,
            apply: true,
            drainPolicy: drainPolicy,
            now: noon);
        var openPlan = GoalDrainPlanner.Build(
            kernel,
            agents,
            WorkerProfileCatalog.Default(),
            workspace,
            AutonomyPolicy.SafeAuto,
            apply: true,
            drainPolicy: drainPolicy,
            now: night);
        var closedOutput = CaptureConsole(() => ConsoleViews.PrintGoalDrainPlan(closedPlan));

        Xunit.Assert.False(closedPlan.Schedule.IsOpen);
        Xunit.Assert.Equal(0, closedPlan.FirstBatchGoalCount);
        Xunit.Assert.Contains(closedPlan.Items, item =>
            item.Stage == "subscription-start" &&
            !item.CanApply &&
            item.SuggestedCommand == "blocked by drain policy" &&
            item.Detail.Contains("outside allowed local drain windows", StringComparison.Ordinal));
        Xunit.Assert.True(openPlan.Schedule.IsOpen);
        Xunit.Assert.Equal(1, openPlan.FirstBatchGoalCount);
        Xunit.Assert.Contains("windows=22:00-06:00", closedOutput);
        Xunit.Assert.Contains("Schedule: closed", closedOutput);
    }

    [Xunit.Fact(DisplayName = "Cli_drain_goals_apply_requires_explicit_confirmations")]
    public void CliDrainGoalsApplyRequiresExplicitConfirmations()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Drain guarded", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Planner)]);
        IReadOnlyList<AgentDefinition> agents = [SubscriptionPlanner("planner", "Planner")];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var ex = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["drain-goals", "--apply", "--autonomy", "safe-auto"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("--confirm-goal-drain", ex.Message);
        Xunit.Assert.Null(goal.Tasks.Single().LastDispatch);
    }

    [Xunit.Fact(DisplayName = "Cli_drain_goals_apply_runs_safe_supervisor_actions_without_crossing_gates")]
    public void CliDrainGoalsApplyRunsSafeSupervisorActionsWithoutCrossingGates()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Refresh stale worker", AgentRole.Planner);
        var goal = kernel.CreateGoal("Drain safe supervisor", [task]);
        IReadOnlyList<AgentDefinition> agents = [SubscriptionPlanner("planner", "Planner")];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["drain-goals", "--apply", "--confirm-goal-drain", "--confirm-batch-start", "--autonomy", "safe-auto"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });

        Xunit.Assert.False(task.LastProcess!.IsRunning);
        Xunit.Assert.NotNull(task.LastVerification);
        Xunit.Assert.Contains("Applied actions: 1", output);
        Xunit.Assert.Contains("refresh-dispatch 1", output);
        Xunit.Assert.DoesNotContain("acceptance-queue --apply", output);
    }

    [Xunit.Fact(DisplayName = "Cli_failure_triage_classifies_CS2012_with_allowed_remediation")]
    public void CliFailureTriageClassifiesCs2012WithAllowedRemediation()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Verify locked build output", AgentRole.Tester);
        var goal = kernel.CreateGoal("Triage CS2012", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "dotnet test",
            root,
            1,
            string.Empty,
            "error CS2012: Cannot open 'Core.dll' for writing",
            DateTimeOffset.UtcNow));

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["failure-triage", "--autonomy", "safe-auto"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains("BuildFileLock task 1", output);
        Xunit.Assert.Contains("action=RunBuildServerShutdown", output);
        Xunit.Assert.Contains("canAutoApply=True", output);
        Xunit.Assert.Contains("command=dotnet build-server shutdown", output);
    }

    [Xunit.Fact(DisplayName = "Cli_failure_triage_classifies_provider_connectivity_with_failover")]
    public void CliFailureTriageClassifiesProviderConnectivityWithFailover()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Recover provider connection failure", AgentRole.Planner);
        var goal = kernel.CreateGoal("Triage provider failover", [task]);
        var primary = SubscriptionPlanner("primary-planner", "Primary Planner");
        var alternate = SubscriptionPlanner("alternate-planner", "Alternate Planner");
        IReadOnlyList<AgentDefinition> agents = [primary, alternate];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", root, DateTimeOffset.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec",
            root,
            1,
            string.Empty,
            "ERROR: Unable to connect to API: connection refused",
            DateTimeOffset.UtcNow));

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["failure-triage", "--autonomy", "safe-auto"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains("ProviderConnectivity task 1", output);
        Xunit.Assert.Contains("action=ReRoute", output);
        Xunit.Assert.Contains("canAutoApply=True", output);
        Xunit.Assert.Contains("command=re-delegate 1 --autonomy safe-auto", output);
    }

    [Xunit.Fact(DisplayName = "Cli_failure_triage_classifies_provider_model_rejection_with_failover")]
    public void CliFailureTriageClassifiesProviderModelRejectionWithFailover()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Recover unsupported provider model", AgentRole.Planner);
        var goal = kernel.CreateGoal("Triage provider model rejection", [task]);
        var primary = SubscriptionPlanner("primary-planner", "Primary Planner");
        var alternate = SubscriptionPlanner("alternate-planner", "Alternate Planner");
        IReadOnlyList<AgentDefinition> agents = [primary, alternate];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", root, DateTimeOffset.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec",
            root,
            1,
            string.Empty,
            "ERROR: invalid model 'gpt-5.3-codex' does not exist for this account.",
            DateTimeOffset.UtcNow));

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["failure-triage", "--autonomy", "safe-auto"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains("ProviderModelRejected task 1", output);
        Xunit.Assert.Contains("action=ReRoute", output);
        Xunit.Assert.Contains("canAutoApply=True", output);
        Xunit.Assert.Contains("command=re-delegate 1 --autonomy safe-auto", output);
    }

    [Xunit.Fact(DisplayName = "Cli_retention_plan_keeps_active_goal_artifacts_as_dry_run")]
    public void CliRetentionPlanKeepsActiveGoalArtifactsAsDryRun()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement active work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Retention active", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        Directory.CreateDirectory(Path.Combine(root, ".orchestrator-context", goal.Id.Value));

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["retention-plan"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains("State: Active", output);
        Xunit.Assert.Contains("Dry run: True", output);
        Xunit.Assert.Contains("ContextPackage: Keep; exists=True", output);
        Xunit.Assert.Contains("Worktree: Keep", output);
    }

    [Xunit.Fact(DisplayName = "Cli_retention_plan_archives_abandoned_goal_evidence_and_deletes_orphaned_build_lease")]
    public void CliRetentionPlanArchivesAbandonedGoalEvidenceAndDeletesOrphanedBuildLease()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Fail work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Retention abandoned", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.CancelGoal(goal.Id, "Abandoned during retention test.");
        Directory.CreateDirectory(Path.Combine(root, ".orchestrator-context", goal.Id.Value));
        var environment = DotnetBuildEnvironmentManager.CreateAttempt(goal.Id, "retention");
        MakeLeaseOwnerStale(environment.LeaseMetadataPath!);

        try
        {
            var output = CaptureConsole(() =>
            {
                var changed = CliCommandDispatcher.ExecuteCommand(
                    ["retention-plan"],
                    kernel,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);
                Xunit.Assert.False(changed);
            });

            Xunit.Assert.Contains("State: Abandoned", output);
            Xunit.Assert.Contains("ContextPackage: Archive; exists=True", output);
            Xunit.Assert.Contains("BuildLease: DeleteNow; exists=True", output);
            Xunit.Assert.Contains("command: build-lease-cleanup --confirm-build-lease-cleanup", output);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal.Id);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_operator_inbox_reports_and_acknowledges_items")]
    public void CliOperatorInboxReportsAndAcknowledgesItems()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement inbox smoke", AgentRole.Developer);
        var goal = kernel.CreateGoal("Operator inbox smoke", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RequestHumanInput(goal.Id, task.Id, "Choose a retry path.");
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Worker failed before producing evidence.");

        var output = CaptureConsole(() =>
            CliCommandDispatcher.ExecuteCommand(
                ["operator-inbox", goal.Id.Value[..8]],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

        Xunit.Assert.Contains("Operator inbox:", output);
        Xunit.Assert.Contains("HumanInput", output);
        Xunit.Assert.Contains("FailedTask", output);
        Xunit.Assert.Contains("ReadinessPreflight", output);
        var itemId = output
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .First(value => value is not null && value.StartsWith("inbox-", StringComparison.Ordinal))!;

        var ackOutput = CaptureConsole(() =>
            CliCommandDispatcher.ExecuteCommand(
                ["operator-inbox-ack", itemId, "handled", "--goal", goal.Id.Value[..8]],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));
        Xunit.Assert.Contains("acknowledged", ackOutput);

        var hiddenOutput = CaptureConsole(() =>
            CliCommandDispatcher.ExecuteCommand(
                ["operator-inbox", goal.Id.Value[..8]],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));
        Xunit.Assert.DoesNotContain(itemId, hiddenOutput);

        var acknowledgedOutput = CaptureConsole(() =>
            CliCommandDispatcher.ExecuteCommand(
                ["operator-inbox", goal.Id.Value[..8], "--show-acknowledged"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));
        Xunit.Assert.Contains(itemId, acknowledgedOutput);
    }

    [Xunit.Fact(DisplayName = "CrossGoalSubscriptionStartPlanner_batches_independent_goals_and_serializes_conflicts")]
    public void CrossGoalSubscriptionStartPlannerBatchesIndependentGoalsAndSerializesConflicts()
    {
        var kernel = new AgentOrchestratorKernel();
        var first = kernel.CreateGoal("Update src/Alpha.cs", [new TaskSpec(TaskId.New(), "Change src/Alpha.cs", AgentRole.Planner)]);
        var second = kernel.CreateGoal("Update src/Beta.cs", [new TaskSpec(TaskId.New(), "Change src/Beta.cs", AgentRole.Researcher)]);
        var conflict = kernel.CreateGoal("Update src/Alpha.cs too", [new TaskSpec(TaskId.New(), "Change src/Alpha.cs", AgentRole.Researcher)]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new(
                new AgentId("planner-openai"),
                "Planner OpenAI",
                AgentRole.Planner,
                new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("codex-cli")),
            new(
                new AgentId("researcher-anthropic"),
                "Researcher Anthropic",
                AgentRole.Researcher,
                new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("claude-cli"))
        ];
        kernel.ActivateGoal(first.Id, agents);
        kernel.ActivateGoal(second.Id, agents);
        kernel.ActivateGoal(conflict.Id, agents);

        var plan = CrossGoalSubscriptionStartPlanner.Build(kernel, agents, WorkerProfileCatalog.Default());

        Assert.Equal(3, plan.Candidates.Count);
        Assert.True(plan.FirstBatchCandidates.Any(candidate => candidate.GoalId == first.Id.Value));
        Assert.True(plan.FirstBatchCandidates.Any(candidate => candidate.GoalId == second.Id.Value));
        Assert.True(plan.ParallelPlan.Decisions.Any(decision =>
            decision.IntentId == conflict.Id.Value &&
            decision.Disposition == ParallelExecutionDisposition.Serialized));
        var dto = DashboardResponseMapper.ToCrossGoalStartPlanDto(plan, new GoalDrainPolicy(
            "overnight-safe",
            2,
            ["Planner", "Researcher"],
            ["OpenAI", "Anthropic"],
            "defer",
            RequireReadinessRiskConfirmation: true,
            RequireAcceptanceGate: true));
        Assert.Equal(3, dto.CandidateCount);
        Xunit.Assert.NotNull(dto.DrainPolicy);
        Assert.Equal("overnight-safe", dto.DrainPolicy!.Name);
        Assert.Equal(2, dto.DrainPolicy.MaxSubscriptionStartsPerDrain);
        Assert.True(dto.DrainPolicy.AllowedRoles.Any(role => role == "Planner"));
        Assert.True(dto.ParallelPlan.Decisions.Any(decision =>
            decision.IntentId == conflict.Id.Value &&
            decision.Disposition == ParallelExecutionDisposition.Serialized));
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_respects_cross_goal_parallel_gate")]
    public void CliLifecycleSimpleGoalRespectsCrossGoalParallelGate()
    {
        var root = CreateTempDirectory();
        try
        {
            RunGit(root, "init", "-b", "main");
            RunGit(root, "config", "user.email", "tests@example.com");
            RunGit(root, "config", "user.name", "CLI Tests");
            File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
            RunGit(root, "add", "-A");
            RunGit(root, "commit", "-m", "Seed");

            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var existing = kernel.CreateGoal(
                "Existing active change src/Conflict.cs",
                [new TaskSpec(TaskId.New(), "Change src/Conflict.cs", AgentRole.Developer)]);
            IReadOnlyList<AgentDefinition> agents =
            [
                new(
                    new AgentId("developer-openai"),
                    "Developer OpenAI",
                    AgentRole.Developer,
                    new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
                    ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                    Subscription: new SubscriptionLaunchProfile("codex-cli"))
            ];
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = existing;
            kernel.ActivateGoal(existing.Id, agents);
            var ex = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
                [
                    "lifecycle-simple-goal",
                    "Ship another src/Conflict.cs change",
                    "--confirm-batch-start",
                    "--confirm-large-paid-subscription-start"
                ],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Contains("parallel safety gate", ex.Message);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }

                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "Cli_goal_recovery_classifies_branch_diff_verification_breadth")]
    public void CliGoalRecoveryClassifiesBranchDiffVerificationBreadth()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "tests@example.com");
        RunGit(root, "config", "user.name", "CLI Tests");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, "add", "-A");
        RunGit(root, "commit", "-m", "Seed");

        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Change shared infrastructure", AgentRole.Developer);
        var goal = kernel.CreateGoal("Recover classified diff", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        var changedPath = Path.Combine(worktree, "src", "Mcg.AgentOrchestrator.Infrastructure", "Workers");
        Directory.CreateDirectory(changedPath);
        File.WriteAllText(Path.Combine(changedPath, "WorkerProfileDispatcher.cs"), "namespace Test;");
        RunGit(worktree, "add", "-A");
        RunGit(worktree, "commit", "-m", "Shared infrastructure change");

        var originalOut = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            CliCommandDispatcher.ExecuteCommand(
                ["goal-recovery", goal.Id.Value[..8]],
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

        var output = writer.ToString();
        Xunit.Assert.Contains("Change classification:", output);
        Xunit.Assert.Contains("broad=True", output);
        Xunit.Assert.Contains("shared infrastructure changed", output);
    }

    [Xunit.Fact(DisplayName = "GoalOperationJournal_records_latest_status_and_interrupted_operations")]
    public void GoalOperationJournalRecordsLatestStatusAndInterruptedOperations()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Journal operation state");

        GoalOperationJournal.Begin(root, goal, "workspace:create", "start");
        GoalOperationJournal.Completed(root, goal, "workspace:create", "done");
        GoalOperationJournal.Begin(root, goal, "acceptance", "start");

        var summary = GoalOperationJournal.Read(root, goal.Id);

        Xunit.Assert.True(File.Exists(summary.Path));
        Xunit.Assert.Equal(3, summary.Entries.Count);
        Xunit.Assert.Contains(summary.LatestByOperation, entry =>
            entry.Operation == "workspace:create" &&
            entry.Status == GoalOperationStatus.Completed);
        var interrupted = Xunit.Assert.Single(summary.InterruptedOperations);
        Xunit.Assert.Equal("acceptance", interrupted.Operation);
        Xunit.Assert.Equal(GoalOperationJournal.Key(goal.Id, "acceptance"), interrupted.IdempotencyKey);
    }

    [Xunit.Fact(DisplayName = "Cli_goal_recovery_reports_interrupted_operation_journal")]
    public void CliGoalRecoveryReportsInterruptedOperationJournal()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Recover interrupted operation", [
            new TaskSpec(TaskId.New(), "Inspect", AgentRole.Researcher)
        ]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        GoalOperationJournal.Begin(root, goal, "acceptance", "acceptance started before interruption");

        var originalOut = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            CliCommandDispatcher.ExecuteCommand(
                ["goal-recovery", goal.Id.Value[..8]],
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

        var output = writer.ToString();
        Xunit.Assert.Contains("Operation journal:", output);
        Xunit.Assert.Contains("Interrupted operations:", output);
        Xunit.Assert.Contains("acceptance", output);
        Xunit.Assert.Contains("Recommended actions:", output);
    }

    [Xunit.Fact(DisplayName = "Cli_goal_recovery_reports_orphaned_build_lease_cleanup")]
    public void CliGoalRecoveryReportsOrphanedBuildLeaseCleanup()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Recover orphaned build lease", [
            new TaskSpec(TaskId.New(), "Inspect", AgentRole.Developer)
        ]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var environment = DotnetBuildEnvironmentManager.CreateAttempt(goal.Id, "stale");
        MakeLeaseOwnerStale(environment.LeaseMetadataPath!);
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            CliCommandDispatcher.ExecuteCommand(
                ["goal-recovery", goal.Id.Value[..8]],
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
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal.Id);
        }

        var output = writer.ToString();
        Xunit.Assert.Contains("Build lease: goal-", output);
        Xunit.Assert.Contains("canCleanup=True", output);
        Xunit.Assert.Contains("goal build lease is orphaned", output);
        Xunit.Assert.Contains("build-lease-cleanup --confirm-build-lease-cleanup", output);
    }

    [Xunit.Fact(DisplayName = "Cli_build_lease_cleanup_requires_confirmation_and_deletes_orphaned_lease")]
    public void CliBuildLeaseCleanupRequiresConfirmationAndDeletesOrphanedLease()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Clean orphaned build lease", [
            new TaskSpec(TaskId.New(), "Inspect", AgentRole.Developer)
        ]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var environment = DotnetBuildEnvironmentManager.CreateAttempt(goal.Id, "stale");
        MakeLeaseOwnerStale(environment.LeaseMetadataPath!);
        var blocked = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["build-lease-cleanup", goal.Id.Value[..8]],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        Xunit.Assert.Contains("--confirm-build-lease-cleanup", blocked.Message);

        var originalOut = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            CliCommandDispatcher.ExecuteCommand(
                ["build-lease-cleanup", goal.Id.Value[..8], "--confirm-build-lease-cleanup"],
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
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal.Id);
        }

        Xunit.Assert.Contains("Deleted orphaned build lease", writer.ToString());
        Xunit.Assert.False(Directory.Exists(environment.RootPath));
    }

    [Xunit.Fact(DisplayName = "GoalReadinessPreflight_allows_safe_read_only_goal")]
    public void GoalReadinessPreflightAllowsSafeReadOnlyGoal()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Inspect docs/usage.md and summarize current behavior", [
            new TaskSpec(TaskId.New(), "Inspect docs/usage.md and report findings", AgentRole.Researcher)
        ]);
        var agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(goal.Id, agents);

        var report = GoalReadinessPreflight.Build(goal, agents, root);

        Xunit.Assert.True(report.AllowsUnattendedStart);
        Xunit.Assert.True(report.AllowsStart(confirmed: false));
        Xunit.Assert.Equal(GoalReadinessRecommendation.Proceed, report.Recommendation);
        Xunit.Assert.False(report.RequiresWorkspace);
    }

    [Xunit.Fact(DisplayName = "GoalReadinessPreflight_requires_confirmation_for_high_risk_goal_with_workspace")]
    public void GoalReadinessPreflightRequiresConfirmationForHighRiskGoalWithWorkspace()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "tests@example.com");
        RunGit(root, "config", "user.name", "CLI Tests");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, "add", "-A");
        RunGit(root, "commit", "-m", "Seed");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Update auth token rollback policy in src/Mcg.AgentOrchestrator.App/AuthPolicy.cs", [
            new TaskSpec(TaskId.New(), "Implement auth token rollback policy in src/Mcg.AgentOrchestrator.App/AuthPolicy.cs", AgentRole.Developer)
        ]);
        var agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(goal.Id, agents);
        _ = GoalWorktrees.Ensure(root, goal.Id);

        var report = GoalReadinessPreflight.Build(goal, agents, root);

        Xunit.Assert.False(report.AllowsUnattendedStart);
        Xunit.Assert.False(report.AllowsStart(confirmed: false));
        Xunit.Assert.True(report.AllowsStart(confirmed: true));
        Xunit.Assert.True(report.RequiresOperatorConfirmation);
        Xunit.Assert.False(report.HasHardBlockers);
        Xunit.Assert.Equal(GoalReadinessRecommendation.RequireOperatorConfirmation, report.Recommendation);
    }

    [Xunit.Fact(DisplayName = "Cli_run_goal_blocks_file_work_without_workspace_before_dispatch")]
    public void CliRunGoalBlocksFileWorkWithoutWorkspaceBeforeDispatch()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Implement src/Mcg.AgentOrchestrator.App/Feature.cs", [
            new TaskSpec(TaskId.New(), "Implement src/Mcg.AgentOrchestrator.App/Feature.cs", AgentRole.Developer)
        ]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var ex = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["run-goal", "--confirm-batch-start", "--confirm-readiness-risk"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("workspace-missing", ex.Message);
        Xunit.Assert.Null(goal.Tasks.Single().LastDispatch);
        Xunit.Assert.Null(goal.Tasks.Single().LastProcess);
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_goal_requires_readiness_confirmation_for_high_risk_objective")]
    public void CliLifecycleGoalRequiresReadinessConfirmationForHighRiskObjective()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "tests@example.com");
        RunGit(root, "config", "user.name", "CLI Tests");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, "add", "-A");
        RunGit(root, "commit", "-m", "Seed");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var ex = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            [
                "lifecycle-simple-goal",
                "Update auth token rollback policy in src/Mcg.AgentOrchestrator.App/AuthPolicy.cs",
                "--confirm-batch-start",
                "--confirm-large-paid-subscription-start"
            ],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("--confirm-readiness-risk", ex.Message);
        var goal = kernel.Goals.Single();
        Xunit.Assert.Null(goal.Tasks.Single().LastDispatch);
        Xunit.Assert.Null(goal.Tasks.Single().LastProcess);

        var second = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            [
                "lifecycle-simple-goal",
                "Update auth token rollback policy in src/Mcg.AgentOrchestrator.App/AuthPolicy.cs",
                "--confirm-batch-start",
                "--confirm-large-paid-subscription-start"
            ],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("--confirm-readiness-risk", second.Message);
        Xunit.Assert.Single(kernel.Goals);
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
        var olderWorktreePath = GoalWorktrees.WorktreePath(root, olderGoal.Id);
        Directory.CreateDirectory(olderWorktreePath);
        File.WriteAllText(Path.Combine(olderWorktreePath, ".git"), "gitdir: ..");
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

    [Xunit.Fact(DisplayName = "Cli_task_details_and_logs_render_dispatch_heartbeat_status")]
    public void CliTaskDetailsAndLogsRenderDispatchHeartbeatStatus()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Render heartbeat cli", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        var process = new TaskProcessRecord(
            777,
            "codex exec prompt.md",
            root,
            Path.Combine(root, "worker.out.log"),
            Path.Combine(root, "worker.err.log"),
            Path.Combine(root, "worker.exit.txt"),
            DateTimeOffset.Parse("2026-06-12T19:59:00Z"),
            null,
            null);
        File.WriteAllText(process.StandardOutputPath, "hello stdout");
        File.WriteAllText(BackgroundDispatchRunner.GetHeartbeatPath(process), """
{"pid":777,"childPid":888,"state":"running","lastObservedAt":"2026-06-12T20:00:10Z","lastProgressAt":"2026-06-12T20:00:00Z","stdoutBytes":123,"stderrBytes":45}
""");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", process.Command, root, DateTimeOffset.Parse("2026-06-12T19:58:00Z")));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
        using var writer = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(writer);
            ConsoleViews.PrintTask(goal, task);
            ConsoleViews.PrintProcessLogs(task, ProcessLogStream.All);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();

        Xunit.Assert.Contains("heartbeat: available state=running pid=777 child_pid=888", output);
        Xunit.Assert.Contains($"heartbeat path: {BackgroundDispatchRunner.GetHeartbeatPath(process)}", output);
        Xunit.Assert.Contains("log bytes: stdout=123 stderr=45", output);
        Xunit.Assert.Contains("stdout: ", output);
        Xunit.Assert.Contains("hello stdout", output);
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

    [Xunit.Fact(DisplayName = "Cli_run_goal_requires_confirm_batch_start_flag")]
    public void CliRunGoalRequiresConfirmBatchStartFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Avoid accidental sequential subscription start", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Planner)]);
        var agent = new AgentDefinition(
            new AgentId("planner"),
            "Planner",
            AgentRole.Planner,
            new ModelProfile("OpenAI", "gpt-4o-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
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
                ["run-goal"],
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
        var plan = SubscriptionPlanBuilder.Build(
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

    [Xunit.Fact(DisplayName = "Cli_subscription_plan_prints_developer_prompt_budget_headroom")]
    public void CliSubscriptionPlanPrintsDeveloperPromptBudgetHeadroom()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement a focused change.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Show developer prompt budget headroom", [task]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "medium"));
        kernel.ActivateGoal(goal.Id, [agent]);
        var plan = SubscriptionPlanBuilder.Build(
            goal,
            [agent],
            WorkerProfileCatalog.Default(),
            _ => 8500);
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
        Xunit.Assert.Contains("estPrompt=8500chars", output);
        Xunit.Assert.Contains("budget=9000chars headroom=500chars", output);
    }

    [Xunit.Fact(DisplayName = "Cli_subscription_plan_prints_worker_route_decision")]
    public void CliSubscriptionPlanPrintsWorkerRouteDecision()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Update a dashboard label.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Show worker route decision", [task]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
        kernel.ActivateGoal(goal.Id, [agent]);
        var plan = SubscriptionPlanBuilder.Build(
            goal,
            [agent],
            WorkerProfileCatalog.Default(),
            _ => 1200);
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
        Xunit.Assert.Contains("route: Selected", output);
        Xunit.Assert.Contains("reason: role=Developer", output);
        Xunit.Assert.Contains("reason: provider=OpenAI", output);
        Xunit.Assert.Contains("alternative: Consider local Ollama/qwen", output);
    }

    [Xunit.Fact(DisplayName = "Cli_subscription_plan_prints_reviewer_prompt_budget_overage")]
    public void CliSubscriptionPlanPrintsReviewerPromptBudgetOverage()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Review the implementation.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Show reviewer prompt budget overage", [task]);
        var agent = new AgentDefinition(
            new AgentId("reviewer"),
            "Reviewer",
            AgentRole.Reviewer,
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
        kernel.ActivateGoal(goal.Id, [agent]);
        var plan = SubscriptionPlanBuilder.Build(
            goal,
            [agent],
            WorkerProfileCatalog.Default(),
            _ => 9100);
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
        Xunit.Assert.Contains("estPrompt=9100chars", output);
        Xunit.Assert.Contains("budget=8000chars over=1100chars", output);
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
        var plan = SubscriptionPlanBuilder.Build(
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
        var worktreePath = GoalWorktrees.WorktreePath(root, goal.Id);
        Directory.CreateDirectory(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: ..");
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

    [Xunit.Fact(DisplayName = "Cli_task_commands_accept_goal_prefix_for_non_current_goal")]
    public void CliTaskCommandsAcceptGoalPrefixForNonCurrentGoal()
    {
        // Seed two goals with deterministic IDs: one all-numeric prefix (the collision case) and one letters prefix.
        var numericPrefixId = new GoalId("97184249" + new string('0', 24));
        var lettersPrefixId = new GoalId("abcdef12" + new string('0', 24));

        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var numericGoal = kernel.CreateGoal(numericPrefixId, "All-numeric prefix goal", [new TaskSpec(TaskId.New(), "Do numeric goal work", AgentRole.Developer)]);
        var lettersGoal = kernel.CreateGoal(lettersPrefixId, "Letters prefix goal", [new TaskSpec(TaskId.New(), "Do letters goal work", AgentRole.Developer)]);
        var current = kernel.CreateGoal("Current goal", [new TaskSpec(TaskId.New(), "Do current work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("Fake", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = current;
        var numericTask = numericGoal.Tasks.Single();
        var lettersTask = lettersGoal.Tasks.Single();

        // All-numeric prefix: the collision case that previously misparsed as task display number.
        var numericRetryChanged = CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand("retry 97184249 1 Retry numeric prefix goal."),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);
        // Letters prefix: the original passing case.
        var lettersRetryChanged = CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand("retry abcdef12 1 Retry letters prefix goal."),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);
        var manualChanged = CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand("verify-manual --goal 97184249 1 passed Operator verified numeric prefix goal."),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        Xunit.Assert.True(numericRetryChanged);
        Xunit.Assert.True(lettersRetryChanged);
        Xunit.Assert.True(manualChanged);
        Xunit.Assert.Equal(numericGoal.Id, currentGoal!.Id);
        Xunit.Assert.Contains(numericGoal.Timeline, evt => evt.TaskId == numericTask.Id && evt.Kind == ProgressKind.TaskRetried);
        Xunit.Assert.Contains(lettersGoal.Timeline, evt => evt.TaskId == lettersTask.Id && evt.Kind == ProgressKind.TaskRetried);
        Xunit.Assert.NotNull(numericTask.LastVerification);
        Xunit.Assert.Empty(current.Tasks.Single().VerificationHistory);
    }

    [Xunit.Fact(DisplayName = "Cli_re_delegate_reassigns_orphaned_task_and_subscription_dispatch_uses_new_agent")]
    public void CliReDelegateReassignsOrphanedTaskAndSubscriptionDispatchUsesNewAgent()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Recover orphaned assignment", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var oldAgent = new AgentDefinition(
            new AgentId("anthropic-developer"),
            "Anthropic developer",
            AgentRole.Developer,
            new ModelProfile("Anthropic", "claude-haiku-4-5", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("claude-cli"));
        var newAgent = new AgentDefinition(
            new AgentId("openai-developer"),
            "OpenAI developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
        IReadOnlyList<AgentDefinition> agents = [oldAgent];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.Single();
        agents = [newAgent];
        var worktreePath = GoalWorktrees.WorktreePath(root, goal.Id);
        Directory.CreateDirectory(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: ..");

        var redelegated = CliCommandDispatcher.ExecuteCommand(
            ["re-delegate", "1"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);
        var dispatched = CliCommandDispatcher.ExecuteCommand(
            ["subscription-dispatch", "1"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        Xunit.Assert.True(redelegated);
        Xunit.Assert.True(dispatched);
        Xunit.Assert.Equal(newAgent.Id, task.AssignedAgentId);
        Xunit.Assert.Equal("OpenAI", task.LastDispatch!.ProviderName);
        Xunit.Assert.Equal("gpt-5.5", task.LastDispatch.ModelName);
        Xunit.Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskRedelegated &&
            evt.Message.Contains(oldAgent.Id.Value, StringComparison.Ordinal) &&
            evt.Message.Contains(newAgent.Id.Value, StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Cli_re_delegate_refuses_running_task_with_cancel_or_refresh_guidance")]
    public void CliReDelegateRefusesRunningTaskWithCancelOrRefreshGuidance()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Do not move running work", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey))
        ];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Started.");

        var ex = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["re-delegate", "1"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("cancel or refresh", ex.Message);
        Xunit.Assert.Equal(new AgentId("developer"), task.AssignedAgentId);
    }

    [Xunit.Fact(DisplayName = "Cli_subscription_dispatch_acknowledges_repeated_limit_review")]
    public void CliSubscriptionDispatchAcknowledgesRepeatedLimitReview()
    {
        var root = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "repo"));
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Review subscription limits", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
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
        var worktreePath = GoalWorktrees.WorktreePath(root, goal.Id);
        Directory.CreateDirectory(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: ..");
        var task = goal.Tasks.Single();

        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec attempt 1", root, DateTimeOffset.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec attempt 1",
            root,
            1,
            string.Empty,
            "ERROR: You've hit your usage limit. Visit settings to purchase more credits or try again later.",
            DateTimeOffset.UtcNow));
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec attempt 2", root, DateTimeOffset.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec attempt 2",
            root,
            1,
            string.Empty,
            "ERROR: You've hit your usage limit. Visit settings to purchase more credits or try again later.",
            DateTimeOffset.UtcNow));

        var blocked = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["subscription-dispatch", "1"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var missingNote = Xunit.Assert.Throws<ArgumentException>(() => CliCommandDispatcher.ExecuteCommand(
            ["subscription-dispatch", "1", "--confirm-limit-review"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var changed = CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand("subscription-dispatch 1 --confirm-limit-review Reviewed profile and timing."),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        Xunit.Assert.Contains("--confirm-limit-review <note>", blocked.Message);
        Xunit.Assert.Contains("--confirm-limit-review <note>", missingNote.Message);
        Xunit.Assert.True(changed);
        Xunit.Assert.False(DispatchFailureClassifier.RequiresSubscriptionLimitReview(task));
        Xunit.Assert.Equal("Reviewed profile and timing.", task.SubscriptionLimitReviewNote);
        Xunit.Assert.Equal(2, task.SubscriptionLimitReviewedFailureCount);
        Xunit.Assert.Equal(WorkTaskStatus.Running, task.Status);
        Xunit.Assert.NotNull(task.LastDispatch);
        Xunit.Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskSubscriptionLimitReviewAcknowledged &&
            evt.Message.Contains("Reviewed profile and timing", StringComparison.Ordinal));
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

    [Xunit.Fact(DisplayName = "Cli_agent_command_pins_subscription_model_from_flag")]
    public void CliAgentCommandPinsSubscriptionModelFromFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CliCommandDispatcher.ExecuteCommand(
            ["agent", "Developer", "OpenAI", "gpt-5.4-mini", "--subscription-model", "gpt-5.3-codex-spark"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        var agent = agents.Single(a => a.Role == AgentRole.Developer);
        Xunit.Assert.Equal("codex-cli", agent.Subscription!.WorkerProfileName);
        Xunit.Assert.Equal("gpt-5.3-codex-spark", agent.Subscription.ModelAlias);
    }

    [Xunit.Fact(DisplayName = "Cli_agent_command_replaces_existing_role")]
    public void CliAgentCommandReplacesExistingRole()
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

        var developer = agents.Single(agent => agent.Role == AgentRole.Developer);
        Xunit.Assert.Equal(5, agents.Count);
        Xunit.Assert.Equal("anthropic-developer", developer.Id.Value);
        Xunit.Assert.Equal("Anthropic", developer.Model.ProviderName);
    }

    [Xunit.Fact(DisplayName = "Cli_agent_add_preserves_primary_and_adds_same_role_alternate")]
    public void CliAgentAddPreservesPrimaryAndAddsSameRoleAlternate()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CliCommandDispatcher.ExecuteCommand(
            ["agent-add", "Developer", "Anthropic", "claude-haiku-4-5", "Claude fallback"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);
        CliCommandDispatcher.ExecuteCommand(
            ["agent-add", "Developer", "Anthropic", "claude-haiku-4-5", "Claude fallback", "--complex-model", "claude-sonnet-4-6"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        var developers = agents.Where(agent => agent.Role == AgentRole.Developer).ToList();
        var restoredDevelopers = AgentCatalogStore.Load(workspace.AgentCatalogPath).Agents
            .Where(agent => agent.Role == AgentRole.Developer)
            .ToList();

        Xunit.Assert.Equal(6, agents.Count);
        Xunit.Assert.Equal("openai-developer", developers[0].Id.Value);
        Xunit.Assert.Equal("anthropic-developer-claude-fallback", developers[1].Id.Value);
        Xunit.Assert.Equal("Claude fallback", developers[1].Name);
        Xunit.Assert.Equal("claude-sonnet-4-6", developers[1].ComplexModel!.ModelName);
        Xunit.Assert.Equal(2, restoredDevelopers.Count);
    }

    [Xunit.Fact(DisplayName = "Cli_agent_add_normalizes_one_shot_args_with_complex_model_flag")]
    public void CliAgentAddNormalizesOneShotArgsWithComplexModelFlag()
    {
        var parts = CliArgumentParser.NormalizeArgs(
            ["agent-add", "Developer", "Anthropic", "claude-haiku-4-5", "Claude", "fallback", "--complex-model", "claude-sonnet-4-6"]);

        Xunit.Assert.Equal(
            ["agent-add", "Developer", "Anthropic", "claude-haiku-4-5", "Claude fallback", "--complex-model", "claude-sonnet-4-6"],
            parts);
    }

    [Xunit.Fact(DisplayName = "Cli_agent_add_splits_interactive_multi_word_name_with_complex_model_flag")]
    public void CliAgentAddSplitsInteractiveMultiWordNameWithComplexModelFlag()
    {
        var parts = CliArgumentParser.SplitCommand(
            "agent-add Developer Anthropic claude-haiku-4-5 Claude fallback --complex-model claude-sonnet-4-6");

        Xunit.Assert.Equal(
            ["agent-add", "Developer", "Anthropic", "claude-haiku-4-5", "Claude fallback", "--complex-model", "claude-sonnet-4-6"],
            parts);
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_splits_objective_before_confirmation_flags")]
    public void CliLifecycleSimpleGoalSplitsObjectiveBeforeConfirmationFlags()
    {
        var parts = CliArgumentParser.SplitCommand(
            "lifecycle-simple-goal Do one focused implementation task --confirm-batch-start --confirm-large-paid-subscription-start");

        Xunit.Assert.Equal(
            ["lifecycle-simple-goal", "Do one focused implementation task", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
            parts);
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_goal_splits_objective_before_confirmation_flags")]
    public void CliLifecycleGoalSplitsObjectiveBeforeConfirmationFlags()
    {
        var parts = CliArgumentParser.SplitCommand(
            "lifecycle-goal Do five role implementation work --confirm-batch-start --confirm-large-paid-subscription-start");

        Xunit.Assert.Equal(
            ["lifecycle-goal", "Do five role implementation work", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
            parts);
    }

    [Xunit.Fact(DisplayName = "Cli_backlog_intake_splits_heading_before_create_flags")]
    public void CliBacklogIntakeSplitsHeadingBeforeCreateFlags()
    {
        var parts = CliArgumentParser.SplitCommand(
            "backlog-intake Add dashboard operator inbox --create-goal");

        Xunit.Assert.Equal(
            ["backlog-intake", "Add dashboard operator inbox", "--create-goal"],
            parts);
    }

    [Xunit.Fact(DisplayName = "Cli_goal_plan_splits_filter_before_create_flags")]
    public void CliGoalPlanSplitsFilterBeforeCreateFlags()
    {
        var parts = CliArgumentParser.SplitCommand(
            "goal-plan unattended supervisor --create-goals");

        Xunit.Assert.Equal(
            ["goal-plan", "unattended supervisor", "--create-goals"],
            parts);
    }

    [Xunit.Fact(DisplayName = "Cli_intent_template_splits_request_before_create_flags")]
    public void CliIntentTemplateSplitsRequestBeforeCreateFlags()
    {
        var parts = CliArgumentParser.SplitCommand(
            "intent-template feature add dashboard recommendations --create-goal");

        Xunit.Assert.Equal(
            ["intent-template", "feature add dashboard recommendations", "--create-goal"],
            parts);
    }

    [Xunit.Fact(DisplayName = "Cli_task_commands_normalize_one_shot_goal_targeted_notes")]
    public void CliTaskCommandsNormalizeOneShotGoalTargetedNotes()
    {
        var goalPrefix = "abc123ef";

        var manual = CliArgumentParser.NormalizeArgs(
            ["verify-manual", goalPrefix, "1", "passed", "Operator", "verified", "older", "goal."]);
        var retry = CliArgumentParser.NormalizeArgs(
            ["retry", "--goal", goalPrefix, "1", "Retry", "older", "goal."]);
        var progress = CliArgumentParser.NormalizeArgs(
            ["progress", goalPrefix, "1", "running", "Still", "working."]);
        var dispatch = CliArgumentParser.NormalizeArgs(
            ["dispatch", goalPrefix, "1", "local", "dotnet", "test", "--no-build"]);

        Xunit.Assert.Equal(["verify-manual", goalPrefix, "1", "passed", "Operator verified older goal."], manual);
        Xunit.Assert.Equal(["retry", "--goal", goalPrefix, "1", "Retry older goal."], retry);
        Xunit.Assert.Equal(["progress", goalPrefix, "1", "running", "Still working."], progress);
        Xunit.Assert.Equal(["dispatch", goalPrefix, "1", "local", "dotnet test --no-build"], dispatch);
    }

    [Xunit.Fact(DisplayName = "Cli_task_commands_normalize_one_shot_legacy_task_notes")]
    public void CliTaskCommandsNormalizeOneShotLegacyTaskNotes()
    {
        var note = CliArgumentParser.NormalizeArgs(["note", "1", "Keep", "dispatch", "ready."]);
        var verify = CliArgumentParser.NormalizeArgs(["verify", "1", "dotnet", "test", "--no-build"]);
        var ask = CliArgumentParser.NormalizeArgs(["ask", "1", "Need", "operator", "input?"]);

        Xunit.Assert.Equal(["note", "1", "Keep dispatch ready."], note);
        Xunit.Assert.Equal(["verify", "1", "dotnet test --no-build"], verify);
        Xunit.Assert.Equal(["ask", "1", "Need operator input?"], ask);
    }

    [Xunit.Fact(DisplayName = "Cli_simple_goal_with_alternate_developer_uses_first_primary")]
    public void CliSimpleGoalWithAlternateDeveloperUsesFirstPrimary()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CliCommandDispatcher.ExecuteCommand(
            ["agent-add", "Developer", "Anthropic", "claude-haiku-4-5", "Claude fallback"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);
        CliCommandDispatcher.ExecuteCommand(
            ["simple-goal", "Do one focused implementation task"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        var task = currentGoal!.Tasks.Single();
        Xunit.Assert.Equal("openai-developer", task.AssignedAgentId!.Value);
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

    [Xunit.Fact(DisplayName = "Cli_cancel_goal_requires_confirmation_for_active_goal_and_records_reason")]
    public void CliCancelGoalRequiresConfirmationForActiveGoalAndRecordsReason()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Stop stale validation", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey))
        ];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var goalPrefix = goal.Id.Value[..8];

        var blocked = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand($"cancel-goal {goalPrefix} Operator stopped stale validation."),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var changed = CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand($"cancel-goal {goalPrefix} Operator stopped stale validation. --confirm-goal-stop"),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        Xunit.Assert.Contains("--confirm-goal-stop", blocked.Message);
        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(GoalStatus.Cancelled, goal.Status);
        Xunit.Assert.Equal(goal.Id, currentGoal!.Id);
        Xunit.Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalCancelled &&
            evt.Message == "Operator stopped stale validation.");
    }

    [Xunit.Fact(DisplayName = "Cli_abandon_goal_prints_dry_run_without_mutating_state")]
    public void CliAbandonGoalPrintsDryRunWithoutMutatingState()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Abandon dry run", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var goalPrefix = goal.Id.Value[..8];

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                CliArgumentParser.SplitCommand($"abandon-goal {goalPrefix} Operator chose a different route."),
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Equal(GoalStatus.Active, goal.Status);
        Xunit.Assert.Contains("Goal abandon", output);
        Xunit.Assert.Contains("Dry run: True", output);
        Xunit.Assert.Contains("Can apply: True", output);
        Xunit.Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.GoalCancelled);
    }

    [Xunit.Fact(DisplayName = "Cli_park_goal_requires_confirmation_and_creates_resume_gate")]
    public void CliParkGoalRequiresConfirmationAndCreatesResumeGate()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Park interrupted work", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        var goalPrefix = goal.Id.Value[..8];

        var dryRunOutput = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                CliArgumentParser.SplitCommand($"park-goal {goalPrefix} Operator paused for review."),
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Equal(GoalStatus.Active, goal.Status);
        Xunit.Assert.True(task.LastProcess!.IsRunning);
        Xunit.Assert.Empty(kernel.HumanInputRequests);
        Xunit.Assert.Contains("Goal park dry run", dryRunOutput);

        var applyOutput = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                CliArgumentParser.SplitCommand($"park-goal {goalPrefix} Operator paused for review. --confirm-goal-park"),
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });

        Xunit.Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
        Xunit.Assert.False(task.LastProcess!.IsRunning);
        var request = Xunit.Assert.Single(kernel.HumanInputRequests);
        Xunit.Assert.False(request.IsCompleted);
        Xunit.Assert.Contains("Operator paused for review.", request.Question);
        Xunit.Assert.Contains("Resume gate: answer", applyOutput);
    }

    [Xunit.Fact(DisplayName = "Cli_rollback_goal_creates_revert_branch_from_acceptance_metadata")]
    public void CliRollbackGoalCreatesRevertBranchFromAcceptanceMetadata()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "tests@example.com");
        RunGit(root, "config", "user.name", "CLI Tests");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, "add", "-A");
        RunGit(root, "commit", "-m", "Seed");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Add bad file", AgentRole.Developer);
        var goal = kernel.CreateGoal("Accepted bad goal", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "manual",
            root,
            0,
            "passed",
            string.Empty,
            DateTimeOffset.UtcNow));
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        File.WriteAllText(Path.Combine(worktree, "bad.txt"), "bad");
        RunGit(worktree, "add", "-A");
        RunGit(worktree, "commit", "-m", "Bad accepted change");
        var range = GoalRollbackPlanner.CapturePendingAcceptance(root, goal.Id)
            ?? throw new InvalidOperationException("Expected rollback metadata capture.");
        var merge = GoalWorktrees.TryFastForwardMerge(root, goal.Id);
        Xunit.Assert.True(merge!.FastForwarded);
        GoalRollbackPlanner.RecordAcceptance(root, range);
        Xunit.Assert.True(File.Exists(Path.Combine(root, "bad.txt")));
        var goalPrefix = goal.Id.Value[..8];

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                CliArgumentParser.SplitCommand($"rollback-goal {goalPrefix} Revert bad acceptance. --confirm-goal-rollback"),
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });

        Xunit.Assert.Equal($"rollback/{goalPrefix}", RunGitOutput(root, "branch", "--show-current").Trim());
        Xunit.Assert.False(File.Exists(Path.Combine(root, "bad.txt")));
        Xunit.Assert.Contains("Created rollback branch", output);
        Xunit.Assert.Contains($"rollback/{goalPrefix}", output);
    }

    [Xunit.Fact(DisplayName = "Cli_abandon_goal_confirmed_cancels_and_removes_clean_workspace")]
    public void CliAbandonGoalConfirmedCancelsAndRemovesCleanWorkspace()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "tests@example.com");
        RunGit(root, "config", "user.name", "CLI Tests");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, "add", "-A");
        RunGit(root, "commit", "-m", "Seed");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Abandon with workspace", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        var goalPrefix = goal.Id.Value[..8];

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                CliArgumentParser.SplitCommand($"abandon-goal {goalPrefix} Operator chose a different route. --confirm-goal-abandon"),
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });

        Xunit.Assert.Equal(GoalStatus.Cancelled, goal.Status);
        Xunit.Assert.False(Directory.Exists(worktree));
        Xunit.Assert.Null(GoalWorktrees.TryResolve(root, goal.Id));
        Xunit.Assert.Contains("Dry run: False", output);
        Xunit.Assert.Contains("GoalStatus: Keep", output);
        Xunit.Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalCancelled &&
            evt.Message == "Operator chose a different route.");
    }

    [Xunit.Fact(DisplayName = "Cli_abandon_goal_confirmed_cancels_running_dispatch_records")]
    public void CliAbandonGoalConfirmedCancelsRunningDispatchRecords()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Abandon blocked", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        var goalPrefix = goal.Id.Value[..8];
        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                CliArgumentParser.SplitCommand($"abandon-goal {goalPrefix} Operator chose a different route. --confirm-goal-abandon"),
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });

        Xunit.Assert.Equal(GoalStatus.Cancelled, goal.Status);
        Xunit.Assert.False(task.LastProcess!.IsRunning);
        Xunit.Assert.Contains("RunningDispatches: Keep", output);
        Xunit.Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.GoalCancelled);
    }

    private static void WritePlanningBacklog(string root)
    {
        File.WriteAllText(Path.Combine(root, "BACKLOG.md"), """
        # Backlog

        ## Decision record (durable context, not work items)

        Not work.

        ## Add feature A planner

        Implement feature A planning in docs/feature-a.md. Done when focused documentation checks pass.

        ## Add feature B planner

        Implement feature B planning in docs/feature-b.md. Done when focused documentation checks pass.

        ## Add feature C planner

        Implement feature C planning in docs/feature-c.md. Done when focused documentation checks pass.
        """);
    }

    private static void WriteCompilationBacklog(string root)
    {
        File.WriteAllText(Path.Combine(root, "BACKLOG.md"), """
        # Backlog

        ## Add feature A compiler support

        Implement deterministic feature A planning in src/FeatureA/Planner.cs with tests in tests/FeatureA.Tests/PlannerTests.cs. Done when focused planner tests pass.

        ## Add feature B compiler report

        Implement feature B reporting in src/FeatureB/Report.cs with tests in tests/FeatureB.Tests/ReportTests.cs. Done when focused report tests pass.
        """);
    }

    private static void WriteConflictingCompilationBacklog(string root)
    {
        File.WriteAllText(Path.Combine(root, "BACKLOG.md"), """
        # Backlog

        ## Add feature A compiler support

        Implement deterministic feature A planning in src/FeatureA/Planner.cs. Done when focused planner tests pass.

        ## Refine feature A compiler support

        Update edge handling in src/FeatureA/Planner.cs. Done when focused planner tests pass.
        """);
    }

    private static void MakeLeaseOwnerStale(string metadataPath)
    {
        var text = File.ReadAllText(metadataPath);
        File.WriteAllText(metadataPath, text.Replace(
            $"\"ownerProcessId\": {Environment.ProcessId}",
            "\"ownerProcessId\": 999999",
            StringComparison.Ordinal));
    }

    private static string CaptureConsole(Action action)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            action();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return writer.ToString();
    }

    private static AgentDefinition SubscriptionPlanner(string id, string name) => new(
        new AgentId(id),
        name,
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile(id));

    private static void RecordRunningProcess(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        string workingDirectory)
    {
        var stdout = Path.Combine(workingDirectory, $"{task.Id.Value}-out.log");
        var stderr = Path.Combine(workingDirectory, $"{task.Id.Value}-err.log");
        var exit = Path.Combine(workingDirectory, $"{task.Id.Value}-exit.txt");
        File.WriteAllText(stdout, string.Empty);
        File.WriteAllText(stderr, string.Empty);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", workingDirectory, DateTimeOffset.UtcNow));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(999999, "codex exec prompt.md", workingDirectory, stdout, stderr, exit, DateTimeOffset.UtcNow, null, null));
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        _ = RunGitOutput(workingDirectory, arguments);
    }

    private static string RunGitOutput(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        }

        return output;
    }
}
