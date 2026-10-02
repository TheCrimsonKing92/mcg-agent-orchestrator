using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text.Json;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsSubscriptionDispatchCommands : CliCommandTestBase
{
    [Xunit.Fact(DisplayName = "Cli_start_dispatch_interactive_and_one_shot_multi_flags_match")]
    public void CliStartDispatchInteractiveAndOneShotMultiFlagsMatch()
    {
        var interactive = CliArgumentParser.SplitCommand(
            "start-dispatch abcdef12 1 --confirm-dispatch-start --confirm-large-paid-subscription-start");
        var oneShot = CliArgumentParser.NormalizeArgs(
            ["start-dispatch", "abcdef12", "1", "--confirm-dispatch-start", "--confirm-large-paid-subscription-start"]);

        Xunit.Assert.Equal(oneShot, interactive);
    }


    [Xunit.Fact(DisplayName = "Cli_run_blocks_subscription_capable_agents_without_calling_provider")]
    public void CliRunBlocksSubscriptionCapableAgentsWithoutCallingProvider()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
            new ModelProfile("OpenAI", AgentCatalog.StaleOpenAiCodexSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
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
            AgentCatalog.StaleOpenAiCodexSubscriptionModelAlias,
            "medium",
            TaskComplexity.Complex,
            20000));
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
            ComplexModel: new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
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



    [Xunit.Fact(DisplayName = "Cli_next_reports_dispatch_recovery_policy_action_for_refresh")]
    public void CliNextReportsDispatchRecoveryPolicyActionForRefresh()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Refresh interrupted worker", AgentRole.Researcher);
        var goal = kernel.CreateGoal("Next recovery policy", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["next"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains("RefreshRunningProcess", output);
        Xunit.Assert.Contains("recovery: action='mark-stale' evidence='heartbeat-absent'", output);
        Xunit.Assert.Contains("command: refresh-dispatch 1", output);
    }


    [Xunit.Fact(DisplayName = "Cli_goal_diagnostics_reports_active_dispatch_bounded_without_worktree_git_inspection")]
    public void CliGoalDiagnosticsReportsActiveDispatchBoundedWithoutWorktreeGitInspection()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Keep worker running", AgentRole.Developer);
        var goal = kernel.CreateGoal("Diagnose active worker", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root, Environment.ProcessId);
        WriteIdentityBoundHeartbeat(
            kernel.GetGoal(goal.Id)!.Tasks.Single().LastProcess!,
            Environment.ProcessId);
        Goal? currentGoal = kernel.GetGoal(goal.Id);

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["goal-diagnostics", goal.Id.Value[..8]],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains("Goal diagnostics", output);
        Xunit.Assert.Contains("Mode: bounded", output);
        Xunit.Assert.Contains("dispatch-state: state='Running'", output);
        Xunit.Assert.Contains("live=True", output);
        Xunit.Assert.Contains("dirty_worktree=unknown", output);
        Xunit.Assert.Contains("Deeper commands:", output);
        Xunit.Assert.DoesNotContain("Goal readiness", output);
        Xunit.Assert.DoesNotContain("subscription plan:", output);
        Xunit.Assert.InRange(CountNonEmptyLines(output), 1, 40);
    }


    [Xunit.Fact(DisplayName = "Cli_supervisor_apply_safe_redelegates_recoverable_stall")]
    public void CliSupervisorApplySafeRedelegatesRecoverableStall()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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


    [Xunit.Fact(DisplayName = "Cli_agent_replace_warns_about_inflight_tasks_pinned_to_old_agent")]
    public void CliAgentReplaceWarnsAboutInflightTasksPinnedToOldAgent()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Warn about stale planner pin", AgentRole.Planner);
        var goal = kernel.CreateGoal("Warn on role replacement", [task]);
        IReadOnlyList<AgentDefinition> agents = [SubscriptionPlanner("openai-planner", "OpenAI Planner")];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var stderr = CaptureConsoleError(() => CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["agent", "Planner", "Anthropic", "claude-haiku-4-5", "Anthropic Planner"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        }));

        Xunit.Assert.Equal("openai-planner", task.AssignedAgentId!.Value);
        Xunit.Assert.Contains("Warning: replaced Planner agent 'openai-planner'", stderr);
        Xunit.Assert.Contains(task.Id.Value, stderr);
        Xunit.Assert.Contains("reassign-agent", stderr);
    }


    [Xunit.Fact(DisplayName = "Cli_drain_goals_dry_run_reports_subscription_and_operator_gates")]
    public void CliDrainGoalsDryRunReportsSubscriptionAndOperatorGates()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
                new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("codex-cli")),
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey))
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


    [Xunit.Fact(DisplayName = "CrossGoalSubscriptionStartPlanner_batches_independent_goals_and_serializes_conflicts")]
    public void CrossGoalSubscriptionStartPlannerBatchesIndependentGoalsAndSerializesConflicts()
    {
        var kernel = new AgentOrchestratorKernel();
        var first = kernel.CreateGoal("Update src/Alpha.cs", [new TaskSpec(TaskId.New(), "Change src/Alpha.cs", AgentRole.Planner)]);
        var second = kernel.CreateGoal("Update src/Beta.cs", [new TaskSpec(TaskId.New(), "Change src/Beta.cs", AgentRole.Developer)]);
        var conflict = kernel.CreateGoal("Update src/Alpha.cs too", [new TaskSpec(TaskId.New(), "Change src/Alpha.cs", AgentRole.Researcher)]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new(
                new AgentId("planner-openai"),
                "Planner OpenAI",
                AgentRole.Planner,
                new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("codex-cli")),
            new(
                new AgentId("developer-ollama"),
                "Developer Ollama",
                AgentRole.Developer,
                new ModelProfile("Ollama", "qwen3:8b", ModelCapability.Text, SubscriptionMode.LocalBridge),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("qwen-code-cli")),
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
    }


    [Xunit.Fact(DisplayName = "Cli_goal_recovery_surfaces_orchestrator_commit_path_for_dirty_failed_dispatch")]
    public void CliGoalRecoverySurfacesOrchestratorCommitPathForDirtyFailedDispatch()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Recover dirty worker output", AgentRole.Developer);
        var goal = kernel.CreateGoal("Recover dirty worker output goal", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var devTask = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, devTask.Id, new TaskDispatchRecord("codex-cli", "codex exec", root, DateTimeOffset.UtcNow));
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            devTask.Id,
            new TaskVerificationRecord(
                "codex exec",
                root,
                1,
                "WORKER_RESULT:\nfiles: src/Foo.cs\ntests: pass\nblockers: none\nEND_WORKER_RESULT",
                "Developer/Tester dispatch exited 0 but left the worktree dirty. branch=goal/abc; head=def; worktree=dirty; commits_after_dispatch=0; status_short=M src/Foo.cs.",
                DateTimeOffset.UtcNow));

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["goal-recovery", goal.Id.Value[..8]],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.True(output.Contains("orchestrator commit", StringComparison.OrdinalIgnoreCase));
        Xunit.Assert.Contains("dirty-useful", output);
        Xunit.Assert.Contains("command: task 1", output);
        Xunit.Assert.Contains("Recommended actions:", output);
        Xunit.Assert.Contains("task 1", output);
    }


    [Xunit.Fact(DisplayName = "Cli_run_goal_blocks_file_work_without_workspace_before_dispatch")]
    public void CliRunGoalBlocksFileWorkWithoutWorkspaceBeforeDispatch()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Implement src/Mcg.AgentOrchestrator.App/Feature.cs", [
            new TaskSpec(TaskId.New(), "Implement src/Mcg.AgentOrchestrator.App/Feature.cs", AgentRole.Developer)
        ]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
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


    [Xunit.Fact(DisplayName = "Cli_subscription_dispatch_can_target_non_latest_goal")]
    public void CliSubscriptionDispatchCanTargetNonLatestGoal()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var olderGoal = kernel.CreateGoal(
            "Keep older worker reachable",
            [new TaskSpec(TaskId.New(), "Do older work", AgentRole.Developer)]);
        var latestGoal = kernel.CreateGoal(
            "Do newer work",
            [new TaskSpec(TaskId.New(), "Do newer work", AgentRole.Developer)]);
        MarkGoalRefined(kernel, olderGoal);
        MarkGoalRefined(kernel, latestGoal);
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
        SeedLocalSkillCatalog(olderWorktreePath);
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


    [Xunit.Fact(DisplayName = "Cli_subscription_dispatch_goal_flag_targets_named_goal_over_current")]
    public void CliSubscriptionDispatchGoalFlagTargetsNamedGoalOverCurrent()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var olderGoal = kernel.CreateGoal(
            "Keep older worker reachable",
            [new TaskSpec(TaskId.New(), "Do older work", AgentRole.Developer)]);
        var latestGoal = kernel.CreateGoal(
            "Do newer work",
            [new TaskSpec(TaskId.New(), "Do newer work", AgentRole.Developer)]);
        MarkGoalRefined(kernel, olderGoal);
        MarkGoalRefined(kernel, latestGoal);
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
        SeedLocalSkillCatalog(olderWorktreePath);
        File.WriteAllText(Path.Combine(olderWorktreePath, ".git"), "gitdir: ..");
        var olderGoalPrefix = olderGoal.Id.Value[..8];

        var changed = CliCommandDispatcher.ExecuteCommand(
            ["subscription-dispatch", "1", "--goal", olderGoalPrefix],
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


    [Xunit.Fact(DisplayName = "Cli_subscription_dispatch_already_verified_task_throws_with_goal_context")]
    public void CliSubscriptionDispatchAlreadyVerifiedTaskThrowsWithGoalContext()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Target goal",
            [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        MarkGoalRefined(kernel, goal);
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
        var worktreePath = GoalWorktrees.WorktreePath(root, goal.Id);
        Directory.CreateDirectory(worktreePath);
        SeedLocalSkillCatalog(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: ..");
        kernel.RecordTaskVerification(goal.Id, goal.Tasks.Single().Id,
            new TaskVerificationRecord("check", root, 0, "passed", "", DateTimeOffset.UtcNow));

        InvalidOperationException? ex = null;
        try
        {
            CliCommandDispatcher.ExecuteCommand(
                ["subscription-dispatch", "1"],
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
        Xunit.Assert.Contains(goal.Id.Value[..8], ex!.Message);
        Xunit.Assert.Contains("task 1", ex.Message);
        Xunit.Assert.Contains("passing verification", ex.Message);
    }


    [Xunit.Fact(DisplayName = "Cli_logs_stream_arg_is_not_misinterpreted_as_goal_prefix")]
    public void CliLogsStreamArgIsNotMisinterpretedAsGoalPrefix()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        var output = CaptureConsole(() =>
        {
            ConsoleViews.PrintTask(goal, task);
            ConsoleViews.PrintProcessLogs(task, ProcessLogStream.All);
        });
        Xunit.Assert.Contains("Heartbeat Status:", output);
        Xunit.Assert.Contains("heartbeat: available state=running pid=777 child_pid=888", output);
        Xunit.Assert.Contains($"heartbeat path: {BackgroundDispatchRunner.GetHeartbeatPath(process)}", output);
        Xunit.Assert.Contains("log bytes: stdout=123 stderr=45", output);
        Xunit.Assert.Contains("stdout: ", output);
        Xunit.Assert.Contains("hello stdout", output);
    }


    [Xunit.Fact(DisplayName = "Cli_task_details_and_logs_render_missing_dispatch_heartbeat_status")]
    public void CliTaskDetailsAndLogsRenderMissingDispatchHeartbeatStatus()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Render missing heartbeat cli", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
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
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", process.Command, root, DateTimeOffset.Parse("2026-06-12T19:58:00Z")));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);

        var output = CaptureConsole(() =>
        {
            ConsoleViews.PrintTask(goal, task);
            ConsoleViews.PrintProcessLogs(task, ProcessLogStream.All);
        });

        Xunit.Assert.Contains("Heartbeat Status:", output);
        Xunit.Assert.Contains("heartbeat: unavailable (missing)", output);
        Xunit.Assert.Contains($"heartbeat path: {BackgroundDispatchRunner.GetHeartbeatPath(process)}", output);
        Xunit.Assert.Contains("last observed: n/a age=n/a", output);
        Xunit.Assert.Contains("last progress: n/a idle=n/a", output);
        Xunit.Assert.Contains("log bytes: stdout=n/a stderr=n/a", output);
    }


    [Xunit.Fact(DisplayName = "Cli_api_run_blocks_paid_provider_without_confirm_flag")]
    public void CliApiRunBlocksPaidProviderWithoutConfirmFlag()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
            ComplexModel: new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
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
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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


    [Xunit.Fact(DisplayName = "Cli_subscription_dispatch_ready_writes_ready_blocked_lines_to_stderr_in_task_order")]
    public void CliSubscriptionDispatchReadyWritesReadyBlockedLinesToStderrInTaskOrder()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var first = new TaskSpec(TaskId.New(), "Inspect docs/one.md", AgentRole.Planner);
        var second = new TaskSpec(TaskId.New(), "Inspect docs/two.md", AgentRole.Researcher);
        var goal = kernel.CreateGoal("Report blocked ready tasks", [first, second]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("planner"),
                "Planner",
                AgentRole.Planner,
                new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("codex-cli")),
            new AgentDefinition(
                new AgentId("researcher"),
                "Researcher",
                AgentRole.Researcher,
                new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("codex-cli"))
        ];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = new WorkerProfileCatalog([new WorkerProfile("codex-cli", "Write-Output {promptPath}")]);
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        EnsureGitRepository(root);
        GoalWorktrees.Ensure(root, goal.Id);

        string stdout = string.Empty;
        var stderr = CaptureConsoleError(() =>
        {
            stdout = CaptureConsole(() =>
            {
                var changed = CliCommandDispatcher.ExecuteCommand(
                    ["subscription-dispatch-ready"],
                    kernel,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);
                Xunit.Assert.False(changed);
            });
        });

        Xunit.Assert.Contains("Subscription dispatches created: 0", stdout);
        var lines = ReadyBlockedLines(stderr);
        Xunit.Assert.Equal(2, lines.Length);
        Xunit.Assert.StartsWith($"READY_BLOCKED goal={goal.Id.Value[..8]} task=1 provider=codex-cli reason=worker-profile details=", lines[0]);
        Xunit.Assert.StartsWith($"READY_BLOCKED goal={goal.Id.Value[..8]} task=2 provider=codex-cli reason=worker-profile details=", lines[1]);
        Xunit.Assert.Null(first.LastDispatch);
        Xunit.Assert.Null(second.LastDispatch);
    }


    [Xunit.Fact(DisplayName = "Cli_start_subscription_ready_writes_ready_blocked_lines_to_stderr")]
    public void CliStartSubscriptionReadyWritesReadyBlockedLinesToStderr()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Update src/one.txt", AgentRole.Developer);
        var goal = kernel.CreateGoal("Report blocked start tasks", [task]);
        MarkGoalRefined(kernel, goal);
        IReadOnlyList<AgentDefinition> agents = [SubscriptionDeveloper()];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        DirtyGoalWorktree(root, goal);

        string stdout = string.Empty;
        var stderr = CaptureConsoleError(() =>
        {
            stdout = CaptureConsole(() =>
            {
                var changed = CliCommandDispatcher.ExecuteCommand(
                    ["start-subscription-ready", "--confirm-batch-start"],
                    kernel,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);
                Xunit.Assert.False(changed);
            });
        });

        Xunit.Assert.Contains("Subscription dispatches created: 0", stdout);
        var line = Xunit.Assert.Single(ReadyBlockedLines(stderr));
        Xunit.Assert.StartsWith($"READY_BLOCKED goal={goal.Id.Value[..8]} task=1 provider=codex-spark reason=dirty-worktree details=", line);
        Xunit.Assert.Contains("dirty.txt", line, StringComparison.Ordinal);
        Xunit.Assert.Null(task.LastDispatch);
        Xunit.Assert.Null(task.LastProcess);
    }



    [Xunit.Fact(DisplayName = "Cli_start_subscription_ready_policy_gate_writes_distinct_ready_blocked_reason")]
    public void CliStartSubscriptionReadyPolicyGateWritesDistinctReadyBlockedReason()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Observe only", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = [SubscriptionDeveloper()];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var stderr = CaptureConsoleError(() =>
        {
            var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
                ["start-subscription-ready", "--confirm-batch-start", "--autonomy", "observe"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));
            Xunit.Assert.Contains("policy 'observe' blocks start-subscription-ready", ex.Message);
        });

        var line = Xunit.Assert.Single(ReadyBlockedLines(stderr));
        Xunit.Assert.Equal($"READY_BLOCKED goal={goal.Id.Value[..8]} task=1 provider=codex-spark reason=autonomy-policy", line);
    }

    [Xunit.Fact(DisplayName = "Cli_start_subscription_ready_surfaces_cross_goal_provider_budget_hold")]
    public void CliStartSubscriptionReadySurfacesCrossGoalProviderBudgetHold()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var sourceGoal = kernel.CreateGoal(
            "Observe exhausted OpenAI binding",
            [new TaskSpec(TaskId.New(), "Run provider work", AgentRole.Developer)]);
        var targetGoal = kernel.CreateGoal(
            "Avoid exhausted OpenAI binding",
            [new TaskSpec(TaskId.New(), "Run later provider work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = [SubscriptionDeveloper()];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = targetGoal;
        kernel.ActivateGoal(sourceGoal.Id, agents);
        kernel.ActivateGoal(targetGoal.Id, agents);
        var sourceTask = sourceGoal.Tasks.Single();
        var dispatchedAt = DateTimeOffset.Parse("2026-09-06T19:24:00Z");
        kernel.RecordTaskDispatch(
            sourceGoal.Id,
            sourceTask.Id,
            new TaskDispatchRecord(
                "codex-cli",
                "codex exec",
                root,
                dispatchedAt,
                ProviderName: "OpenAI",
                GoalId: sourceGoal.Id));
        kernel.RecordTaskVerification(
            sourceGoal.Id,
            sourceTask.Id,
            new TaskVerificationRecord(
                "codex exec",
                root,
                1,
                string.Empty,
                "API error (status 402 Payment Required): usage balance exhausted",
                dispatchedAt.AddMinutes(1),
                StandardErrorPath: "provider.err.log",
                ProviderFailureKind: ProviderFailureKind.BudgetExhausted,
                DispatchStartedAt: dispatchedAt));

        var stderr = CaptureConsoleError(() =>
        {
            _ = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
                ["start-subscription-ready", "--confirm-batch-start", "--autonomy", "observe"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));
        });

        var line = Xunit.Assert.Single(ReadyBlockedLines(stderr));
        Xunit.Assert.Contains("reason=provider-budget-exhausted", line, StringComparison.Ordinal);
        Xunit.Assert.Contains("binding=openai::<provider-default>", line, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains("receipt=provider.err.log", line, StringComparison.Ordinal);
    }


    [Xunit.Fact(DisplayName = "Cli_run_goal_requires_confirm_batch_start_flag")]
    public void CliRunGoalRequiresConfirmBatchStartFlag()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
        var root = CreateShortAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Plan architecture work",
            [new TaskSpec(TaskId.New(), "Design and implement a production multi-tenant architecture.", AgentRole.Developer)]);
        MarkGoalRefined(kernel, goal);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini-codex", "low"),
            ComplexModel: new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
        IReadOnlyList<AgentDefinition> agents = [agent];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.Single();

        bool dispatched = false;
        var dispatchOutput = CaptureConsole(() =>
        {
            dispatched = CliCommandDispatcher.ExecuteCommand(
                ["profile-dispatch", "1", "codex-cli"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        });
        var risk = SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(goal, task);

        Xunit.Assert.True(dispatched);
        Xunit.Assert.Contains("Cost note: paid subscription handoff prepared", dispatchOutput);
        Xunit.Assert.Contains("try local Ollama/qwen3:8b via agent configuration when the task is routine", dispatchOutput);
        Xunit.Assert.Equal("OpenAI", task.LastDispatch!.ProviderName);
        // Subscription launch profiles pin the configured alias; complex tasks still use the API complex model only for effort selection.
        Xunit.Assert.Equal("gpt-5-mini-codex", task.LastDispatch.ModelName);
        Xunit.Assert.Equal("codex-cli", task.LastDispatch.DispatchLane);
        Xunit.Assert.Equal(TaskComplexity.Complex, task.LastDispatch.TaskComplexity);
        Xunit.Assert.Null(risk);
        Xunit.Assert.Null(task.LastProcess);
    }


    [Xunit.Fact(DisplayName = "Cli_execute_dispatch_blocks_large_paid_subscription_prompt_without_confirm_flag")]
    public void CliExecuteDispatchBlocksLargePaidSubscriptionPromptWithoutConfirmFlag()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
            ComplexModel: new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
        kernel.ActivateGoal(goal.Id, [agent]);
        var plan = SubscriptionPlanBuilder.Build(
            goal,
            [agent],
            WorkerProfileCatalog.Default(),
            task => kernel.BuildTaskBrief(goal.Id, task.Id).Content.Length);
        var output = CaptureConsole(() => ConsoleViews.PrintSubscriptionPlan(plan));
        Xunit.Assert.DoesNotContain("Ready start risk:", output);
        Xunit.Assert.DoesNotContain("--confirm-large-paid-subscription-start", output);
        Xunit.Assert.Contains(AgentCatalog.OpenAiSubscriptionModelAlias, output);
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
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "medium"));
        kernel.ActivateGoal(goal.Id, [agent]);
        var plan = SubscriptionPlanBuilder.Build(
            goal,
            [agent],
            WorkerProfileCatalog.Default(),
            _ => 8500);
        var output = CaptureConsole(() => ConsoleViews.PrintSubscriptionPlan(plan));
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
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "low"));
        kernel.ActivateGoal(goal.Id, [agent]);
        var plan = SubscriptionPlanBuilder.Build(
            goal,
            [agent],
            WorkerProfileCatalog.Default(),
            _ => 1200);
        var output = CaptureConsole(() => ConsoleViews.PrintSubscriptionPlan(plan));
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
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "low"));
        kernel.ActivateGoal(goal.Id, [agent]);
        var plan = SubscriptionPlanBuilder.Build(
            goal,
            [agent],
            WorkerProfileCatalog.Default(),
            _ => 9100);
        var output = CaptureConsole(() => ConsoleViews.PrintSubscriptionPlan(plan));
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
        kernel.RecordGoalPolicyDecision(
            goal.Id,
            "Intake pipeline decision (auto): developer-reviewer; reasons: preserve model-fit evidence lane; risk labels: high-risk.");
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
        var output = CaptureConsole(() => ConsoleViews.PrintSubscriptionPlan(plan));
        Xunit.Assert.Contains("prior fit 1: overkill 1", output);
        Xunit.Assert.Contains("shapes copy-only change", output);
        Xunit.Assert.Contains("try local Ollama/qwen3:8b via agent configuration before paid start", output);
    }


    [Xunit.Fact(DisplayName = "Cli_execute_dispatch_requires_confirm_flag")]
    public void CliExecuteDispatchRequiresConfirmFlag()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
        Xunit.Assert.Contains("default local LlamaCpp smoke first", ex.Message);
    }


    [Xunit.Fact(DisplayName = "Cli_paid_provider_smoke_requires_confirm_flag")]
    public void CliPaidProviderSmokeRequiresConfirmFlag()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        Xunit.Assert.Contains("default local LlamaCpp smoke first", ex.Message);
    }


    [Xunit.Fact(DisplayName = "Cli_worker_profile_check_validates_active_subscription_routes")]
    public void CliWorkerProfileCheckValidatesActiveSubscriptionRoutes()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
        Xunit.Assert.Equal(verification with { AcceptanceCriteriaVersionHash = "no-refined-spec" }, task.LastVerification);
    }


    [Xunit.Fact(DisplayName = "Cli_text_file_task_commands_record_file_content")]
    public void CliTextFileTaskCommandsRecordFileContent()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Use file-backed task command text", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
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
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.Single();
        var retryText = "Retry with file-backed feedback.\n\nInclude the full operator receipt.";
        var progressText = "Progress from file.\n\nWorker is running with evidence.";
        var verifyText = "Manual verification from file.\n\nModel fit: fake adequate.";
        var addTaskText = "Added task from file.\n\nPreserve the full description.";
        var answerText = "Answer from file.\n\nUse the longer clarification response.";
        var noteText = "Note from file.\n\nKeep the full neutral operator receipt.";
        var retryPath = Path.Combine(root, "retry.md");
        var progressPath = Path.Combine(root, "progress.md");
        var verifyPath = Path.Combine(root, "verify.md");
        var addTaskPath = Path.Combine(root, "add-task.md");
        var answerPath = Path.Combine(root, "answer.md");
        var notePath = Path.Combine(root, "note.md");
        File.WriteAllText(retryPath, retryText, System.Text.Encoding.UTF8);
        File.WriteAllText(progressPath, progressText, System.Text.Encoding.UTF8);
        File.WriteAllText(verifyPath, verifyText, System.Text.Encoding.UTF8);
        File.WriteAllText(addTaskPath, addTaskText, System.Text.Encoding.UTF8);
        File.WriteAllText(answerPath, answerText, System.Text.Encoding.UTF8);
        File.WriteAllText(notePath, noteText, System.Text.Encoding.UTF8);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Initial failure.");

        CliCommandDispatcher.ExecuteCommand(["retry", "1", "--text-file", retryPath], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
        Xunit.Assert.Equal(RetryCause.Unknown, task.PendingRetryCause);
        CliCommandDispatcher.ExecuteCommand(
            ["retry", "1", "Retry with explicit unavailable classification.", "--cause", "Unknown"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);
        Xunit.Assert.Equal(RetryCause.Unknown, task.PendingRetryCause);
        CliCommandDispatcher.ExecuteCommand(["note", "1", "--text-file", notePath], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
        CliCommandDispatcher.ExecuteCommand(["progress", "1", "running", "--text-file", progressPath], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
        CliCommandDispatcher.ExecuteCommand(["verify-manual", "1", "passed", "--text-file", verifyPath], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
        CliCommandDispatcher.ExecuteCommand(["add-task", "Tester", "--text-file", addTaskPath], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Need a longer answer.");
        CliCommandDispatcher.ExecuteCommand(["answer", request.Id.Value[..8], "--text-file", answerPath], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

        Xunit.Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried && evt.Message == retryText);
        Xunit.Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried && evt.Message == "Retry with explicit unavailable classification.");
        Xunit.Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.OperatorTaskNote && evt.Message == noteText);
        Xunit.Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.TaskStarted && evt.Message == progressText);
        Xunit.Assert.Equal(verifyText, task.LastVerification!.StandardOutput);
        Xunit.Assert.Contains(goal.Tasks, candidate => candidate.Description == addTaskText);
        Xunit.Assert.Equal(answerText.Trim(), request.Answer);
    }


    [Xunit.Fact(DisplayName = "Cli_text_file_task_commands_reject_inline_text_and_file")]
    public void CliTextFileTaskCommandsRejectInlineTextAndFile()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var path = Path.Combine(root, "retry.md");
        File.WriteAllText(path, "File feedback.", System.Text.Encoding.UTF8);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Reject duplicate retry text", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Initial failure.");

        var ex = Xunit.Assert.Throws<ArgumentException>(() => CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand($"retry 1 Inline feedback --text-file {path} --cause ContractClarification"),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("either inline text or --text-file", ex.Message);
    }

    [Xunit.Fact]
    public void AddTask_GoalTargetWithoutRole_PreservesArgsForUsageError()
    {
        var args = new[] { "add-task", "--goal", "abcdef12" };

        var normalized = CliArgumentParser.NormalizeArgs(args);

        Xunit.Assert.Equal(args, normalized);
    }

    [Xunit.Fact]
    public void AddTask_GoalTarget_AddsTaskToNamedGoal()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal(
            "Target goal",
            [
                new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer)
            ]);
        var current = kernel.CreateGoal(
            "Current goal",
            [new TaskSpec(TaskId.New(), "Implement elsewhere", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = current;
        kernel.ActivateGoal(target.Id, agents);
        kernel.ActivateGoal(current.Id, agents);

        var command = $"add-task --goal {target.Id.Value[..8]} Tester Resolve pre-review mapping --before-role Reviewer";
        CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand(command),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        Xunit.Assert.Contains(target.Tasks, task =>
            task.RequiredRole == AgentRole.Tester && task.Description == "Resolve pre-review mapping");
        Xunit.Assert.True(
            target.Tasks.ToList().FindIndex(task => task.RequiredRole == AgentRole.Tester) <
            target.Tasks.ToList().FindIndex(task => task.RequiredRole == AgentRole.Reviewer));
        Xunit.Assert.DoesNotContain(current.Tasks, task => task.RequiredRole == AgentRole.Tester);
        Xunit.Assert.Equal(target.Id, currentGoal?.Id);
    }


    [Xunit.Fact(DisplayName = "Cli_note_preserves_task_and_allows_subscription_dispatch_and_retry")]
    public void CliNotePreservesTaskAndAllowsSubscriptionDispatchAndRetry()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Keep guidance status-neutral", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        MarkGoalRefined(kernel, goal);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("codex-cli"))
        ];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var worktreePath = GoalWorktrees.WorktreePath(root, goal.Id);
        Directory.CreateDirectory(worktreePath);
        SeedLocalSkillCatalog(worktreePath);
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
            CliArgumentParser.SplitCommand("retry 1 Retry remains available. --cause ContractClarification"),
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
        Xunit.Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.OperatorTaskNote && evt.Message == "Preserve dispatch readiness.");
        Xunit.Assert.True(retryChanged);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, statusAfterRetry);
        Xunit.Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried && evt.Message == "Retry remains available.");
        Xunit.Assert.True(secondNoteChanged);
        Xunit.Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.OperatorTaskNote && evt.Message == "Dispatch remains available.");
        Xunit.Assert.True(dispatchChanged);
        Xunit.Assert.NotNull(task.LastDispatch);
    }


    [Xunit.Fact(DisplayName = "Cli_note_confirms_and_keeps_status_across_task_states")]
    public void CliNoteConfirmsAndKeepsStatusAcrossTaskStates()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Annotate tasks",
            [
                new TaskSpec(TaskId.New(), "Pending task", AgentRole.Planner),
                new TaskSpec(TaskId.New(), "Assigned task", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Running task", AgentRole.Tester),
                new TaskSpec(TaskId.New(), "Done task", AgentRole.Reviewer),
                new TaskSpec(TaskId.New(), "Blocked task", AgentRole.Researcher)
            ]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(new AgentId("developer"), "Developer", AgentRole.Developer, new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey)),
            new AgentDefinition(new AgentId("tester"), "Tester", AgentRole.Tester, new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey)),
            new AgentDefinition(new AgentId("reviewer"), "Reviewer", AgentRole.Reviewer, new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey)),
            new AgentDefinition(new AgentId("researcher"), "Researcher", AgentRole.Researcher, new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey))
        ];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        kernel.ActivateGoal(goal.Id, agents);
        kernel.ReportTaskProgress(goal.Id, goal.Tasks[2].Id, WorkTaskStatus.Running, "Started.");
        kernel.ReportTaskProgress(goal.Id, goal.Tasks[3].Id, WorkTaskStatus.Completed, "Done.");
        kernel.ReportTaskProgress(goal.Id, goal.Tasks[4].Id, WorkTaskStatus.Failed, "Blocked.");
        var expectedStatuses = goal.Tasks.Select(task => task.Status).ToArray();

        for (var index = 0; index < goal.Tasks.Count; index++)
        {
            var taskNumber = index + 1;
            var message = $"operator note {taskNumber}";
            var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["note", taskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), message],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Equal($"Note added to task {goal.Tasks[index].Id}{Environment.NewLine}", output);
            Xunit.Assert.Equal(expectedStatuses[index], goal.Tasks[index].Status);
            Xunit.Assert.Contains(goal.Timeline, evt =>
                evt.TaskId == goal.Tasks[index].Id &&
                evt.Kind == ProgressKind.OperatorTaskNote &&
                evt.Message == message);
        }
    }


    [Xunit.Fact(DisplayName = "Cli_note_rejects_missing_empty_and_unknown_task")]
    public void CliNoteRejectsMissingEmptyAndUnknownTask()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Annotate task", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        var missingMessage = Xunit.Assert.ThrowsAny<ArgumentException>(() => CliCommandDispatcher.ExecuteCommand(
            ["note", "1"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var emptyMessage = Xunit.Assert.ThrowsAny<ArgumentException>(() => CliCommandDispatcher.ExecuteCommand(
            ["note", "1", "   "],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var unknownTask = Xunit.Assert.ThrowsAny<KeyNotFoundException>(() => CliCommandDispatcher.ExecuteCommand(
            ["note", "99", "Known typo."],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var notePath = Path.Combine(root, "note.md");
        File.WriteAllText(notePath, "File note.", System.Text.Encoding.UTF8);
        var inlineAndFile = Xunit.Assert.ThrowsAny<ArgumentException>(() => CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand($"note 1 Inline note --text-file {notePath}"),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var missingPath = Path.Combine(root, "missing-note.md");
        var missingFile = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["note", "1", "--text-file", missingPath],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("Usage: note <task-number>", missingMessage.Message);
        Xunit.Assert.Contains("Task note message cannot be empty.", emptyMessage.Message);
        Xunit.Assert.Contains("99", unknownTask.Message);
        Xunit.Assert.Contains("either inline text or --text-file", inlineAndFile.Message);
        Xunit.Assert.Contains("--text-file not found", missingFile.Message);
        Xunit.Assert.Contains(missingPath, missingFile.Message);
    }


    [Xunit.Fact(DisplayName = "Cli_task_commands_accept_goal_prefix_for_non_current_goal")]
    public void CliTaskCommandsAcceptGoalPrefixForNonCurrentGoal()
    {
        // Seed two goals with deterministic IDs: one all-numeric prefix (the collision case) and one letters prefix.
        var numericPrefixId = new GoalId("97184249" + new string('0', 24));
        var lettersPrefixId = new GoalId("abcdef12" + new string('0', 24));

        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
            CliArgumentParser.SplitCommand("retry 97184249 1 Retry numeric prefix goal. --cause ContractClarification"),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);
        // Letters prefix: the original passing case.
        var lettersRetryChanged = CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand("retry abcdef12 1 Retry letters prefix goal. --cause ContractClarification --mechanical"),
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
        Xunit.Assert.Equal(RetryRoundKind.Mechanical, lettersTask.PendingRetryRoundKind);
        Xunit.Assert.DoesNotContain(lettersTask.CriterionRetryFeedback, feedback =>
            feedback.Contains("--mechanical", StringComparison.OrdinalIgnoreCase));
        Xunit.Assert.NotNull(numericTask.LastVerification);
        Xunit.Assert.Empty(current.Tasks.Single().VerificationHistory);
    }


    [Xunit.Fact(DisplayName = "Cli_re_delegate_reassigns_orphaned_task_and_subscription_dispatch_uses_new_agent")]
    public void CliReDelegateReassignsOrphanedTaskAndSubscriptionDispatchUsesNewAgent()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Recover orphaned assignment", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        MarkGoalRefined(kernel, goal);
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
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "low"));
        IReadOnlyList<AgentDefinition> agents = [oldAgent];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.Single();
        agents = [newAgent];
        var worktreePath = GoalWorktrees.WorktreePath(root, goal.Id);
        Directory.CreateDirectory(worktreePath);
        SeedLocalSkillCatalog(worktreePath);
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
        Xunit.Assert.Equal("gpt-5.3-codex-spark", task.LastDispatch.ModelName);
        Xunit.Assert.Equal("codex-spark", task.LastDispatch.DispatchLane);
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
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Do not move running work", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey))
        ];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Started.");

        var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
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
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Review subscription limits", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        MarkGoalRefined(kernel, goal);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("codex-cli"))
        ];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var worktreePath = GoalWorktrees.WorktreePath(root, goal.Id);
        Directory.CreateDirectory(worktreePath);
        SeedLocalSkillCatalog(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: ..");
        var task = goal.Tasks.Single();

        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli",
            "codex exec attempt 1",
            root,
            DateTimeOffset.UtcNow,
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec attempt 1",
            root,
            1,
            string.Empty,
            "ERROR: You've hit your usage limit. Visit settings to purchase more credits or try again later.",
            DateTimeOffset.UtcNow));
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli",
            "codex exec attempt 2",
            root,
            DateTimeOffset.UtcNow,
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec attempt 2",
            root,
            1,
            string.Empty,
            "ERROR: You've hit your usage limit. Visit settings to purchase more credits or try again later.",
            DateTimeOffset.UtcNow));

        var blocked = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["subscription-dispatch", "1"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var missingNote = Xunit.Assert.ThrowsAny<ArgumentException>(() => CliCommandDispatcher.ExecuteCommand(
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


    [Xunit.Fact(DisplayName = "Cli_subscription_dispatch_confirm_limit_review_accepts_text_file")]
    public void CliSubscriptionDispatchConfirmLimitReviewAcceptsTextFile()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Review subscription limit from file", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        MarkGoalRefined(kernel, goal);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("codex-cli"))
        ];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var worktreePath = GoalWorktrees.WorktreePath(root, goal.Id);
        Directory.CreateDirectory(worktreePath);
        SeedLocalSkillCatalog(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: ..");
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli",
            "codex exec attempt 1",
            root,
            DateTimeOffset.UtcNow,
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec attempt 1",
            root,
            1,
            string.Empty,
            "ERROR: You've hit your usage limit. Visit settings to purchase more credits or try again later.",
            DateTimeOffset.UtcNow));
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli",
            "codex exec attempt 2",
            root,
            DateTimeOffset.UtcNow,
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec attempt 2",
            root,
            1,
            string.Empty,
            "ERROR: You've hit your usage limit. Visit settings to purchase more credits or try again later.",
            DateTimeOffset.UtcNow));
        var note = "Reviewed from a file.\n\nProfile and timing are acceptable.";
        var notePath = Path.Combine(root, "limit-review.md");
        File.WriteAllText(notePath, note, System.Text.Encoding.UTF8);

        var changed = CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand($"subscription-dispatch 1 --confirm-limit-review --text-file {notePath}"),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(note, task.SubscriptionLimitReviewNote);
        Xunit.Assert.False(DispatchFailureClassifier.RequiresSubscriptionLimitReview(task));
    }


    [Xunit.Fact(DisplayName = "Cli_subscription_dispatch_confirm_limit_review_rejects_inline_text_and_file")]
    public void CliSubscriptionDispatchConfirmLimitReviewRejectsInlineTextAndFile()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Reject duplicate subscription limit note", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var notePath = Path.Combine(root, "limit-review.md");
        File.WriteAllText(notePath, "File note.", System.Text.Encoding.UTF8);

        var ex = Xunit.Assert.ThrowsAny<ArgumentException>(() => CliCommandDispatcher.ExecuteCommand(
            ["subscription-dispatch", "1", "--confirm-limit-review", "Inline note.", "--text-file", notePath],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("either inline text or --text-file", ex.Message);
    }


    [Xunit.Fact(DisplayName = "Cli_agent_command_creates_agent_with_complex_model_from_flag")]
    public void CliAgentCommandCreatesAgentWithComplexModelFromFlag()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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


    [Xunit.Fact(DisplayName = "Cli_agent_command_pins_subscription_model_and_reasoning_from_flags")]
    public void CliAgentCommandPinsSubscriptionModelAndReasoningFromFlags()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CliCommandDispatcher.ExecuteCommand(
            ["agent", "Developer", "OpenAI", "gpt-5.4-mini", "--subscription-model", AgentCatalog.OpenAiSolSubscriptionModelAlias, "--subscription-reasoning", "ultra"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        var agent = agents.Single(a => a.Role == AgentRole.Developer);
        var restored = AgentCatalogStore.Load(workspace.AgentCatalogPath).GetRequired(AgentRole.Developer);
        Xunit.Assert.Equal(AgentCatalog.OpenAiSolSubscriptionModelAlias, agent.Subscription!.ModelAlias);
        Xunit.Assert.Equal("ultra", agent.Subscription.ReasoningEffort);
        Xunit.Assert.Equal(AgentCatalog.OpenAiSolSubscriptionModelAlias, restored.Subscription!.ModelAlias);
        Xunit.Assert.Equal("ultra", restored.Subscription.ReasoningEffort);
    }


    [Xunit.Fact(DisplayName = "Cli_agent_command_replaces_existing_role")]
    public void CliAgentCommandReplacesExistingRole()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        Xunit.Assert.Equal(6, agents.Count);
        Xunit.Assert.Equal("anthropic-developer", developer.Id.Value);
        Xunit.Assert.Equal("Anthropic", developer.Model.ProviderName);
    }


    [Xunit.Fact(DisplayName = "Cli_agent_add_preserves_primary_and_adds_same_role_alternate")]
    public void CliAgentAddPreservesPrimaryAndAddsSameRoleAlternate()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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

        Xunit.Assert.Equal(7, agents.Count);
        Xunit.Assert.Equal("openai-developer", developers[0].Id.Value);
        Xunit.Assert.Equal("anthropic-developer-claude-fallback", developers[1].Id.Value);
        Xunit.Assert.Equal("Claude fallback", developers[1].Name);
        Xunit.Assert.Equal("claude-sonnet-4-6", developers[1].ComplexModel!.ModelName);
        Xunit.Assert.Equal(2, restoredDevelopers.Count);
    }


    [Xunit.Fact(DisplayName = "Cli_agent_add_persists_fable_subscription_alias")]
    public void CliAgentAddPersistsFableSubscriptionAlias()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CliCommandDispatcher.ExecuteCommand(
            ["agent-add", "Developer", "Anthropic", "claude-haiku-4-5", "Claude fable", "--subscription-model", "fable"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        var added = agents.Single(agent => agent.Id.Value == "anthropic-developer-claude-fable");
        var restored = AgentCatalogStore.Load(workspace.AgentCatalogPath).FindById("anthropic-developer-claude-fable")!;
        Xunit.Assert.Equal("claude-cli", added.Subscription!.WorkerProfileName);
        Xunit.Assert.Equal("fable", added.Subscription.ModelAlias);
        Xunit.Assert.True(added.Subscription.ReasoningEffort is null);
        Xunit.Assert.Equal("fable", restored.Subscription!.ModelAlias);
        Xunit.Assert.True(restored.Subscription.ReasoningEffort is null);
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
        var progressFile = CliArgumentParser.NormalizeArgs(
            ["progress", goalPrefix, "1", "running", "--text-file", "progress.md"]);
        var dispatch = CliArgumentParser.NormalizeArgs(
            ["dispatch", goalPrefix, "1", "local", "dotnet", "test", "--no-build"]);

        Xunit.Assert.Equal(["verify-manual", goalPrefix, "1", "passed", "Operator verified older goal."], manual);
        Xunit.Assert.Equal(["retry", "--goal", goalPrefix, "1", "Retry older goal."], retry);
        Xunit.Assert.Equal(["progress", goalPrefix, "1", "running", "Still working."], progress);
        Xunit.Assert.Equal(["progress", goalPrefix, "1", "running", "--text-file", "progress.md"], progressFile);
        Xunit.Assert.Equal(["dispatch", goalPrefix, "1", "local", "dotnet test --no-build"], dispatch);
    }


    [Xunit.Fact(DisplayName = "Cli_recover_normalizes_one_shot_goal_and_multi_word_note")]
    public void CliRecoverNormalizesOneShotGoalAndMultiWordNote()
    {
        // Regression: recover fell through to the default that collapsed `<goal> <note>` into a single
        // arg, so HandleRecover saw < 3 parts and rejected every one-shot invocation via the launcher.
        var single = CliArgumentParser.NormalizeArgs(["recover", "abc123ef", "reconcile"]);
        var multi = CliArgumentParser.NormalizeArgs(["recover", "abc123ef", "reconcile", "after", "crash"]);
        var file = CliArgumentParser.NormalizeArgs(["recover", "abc123ef", "--text-file", "recover.md"]);

        Xunit.Assert.Equal(["recover", "abc123ef", "reconcile"], single);
        Xunit.Assert.Equal(["recover", "abc123ef", "reconcile after crash"], multi);
        Xunit.Assert.Equal(["recover", "abc123ef", "--text-file", "recover.md"], file);
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


    [Xunit.Fact(DisplayName = "Cli_agent_command_uses_default_complex_model_when_flag_omitted")]
    public void CliAgentCommandUsesDefaultComplexModelWhenFlagOmitted()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        Xunit.Assert.Equal("claude-sonnet-5-5", agent.ComplexModel.ModelName);
    }


    [Xunit.Fact(DisplayName = "Cli_agent_command_rejects_complex_model_flag_without_value")]
    public void CliAgentCommandRejectsComplexModelFlagWithoutValue()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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


    [Xunit.Fact(DisplayName = "Cli_abandon_goal_confirmed_cancels_running_dispatch_records")]
    public void CliAbandonGoalConfirmedCancelsRunningDispatchRecords()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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

    [Xunit.Fact(DisplayName = "Cli_subscription_dispatch_with_confirm_dispatch_start_prepares_and_launches")]
    public void CliSubscriptionDispatchWithConfirmDispatchStartPreparesAndLaunches()
    {
        using var _sandboxEnv = ClearWorkerSandboxEnv();
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("One-step subscription dispatch", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        MarkGoalRefined(kernel, goal);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
        IReadOnlyList<AgentDefinition> agents = [agent];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var worktreePath = GoalWorktrees.WorktreePath(root, goal.Id);
        Directory.CreateDirectory(worktreePath);
        SeedLocalSkillCatalog(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: ..");
        var task = goal.Tasks.Single();

        try
        {
            CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["subscription-dispatch", "1", "--confirm-dispatch-start"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.NotNull(task.LastDispatch);
            Xunit.Assert.NotNull(task.LastProcess);
        }
        finally
        {
            if (task.LastProcess is { IsRunning: true })
                new BackgroundDispatchRunner().CancelLatestProcess(kernel, goal.Id, task.Id);
        }
    }


    [Xunit.Fact(DisplayName = "Cli_subscription_dispatch_without_confirm_dispatch_start_only_prepares")]
    public void CliSubscriptionDispatchWithoutConfirmDispatchStartOnlyPrepares()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Prepare-only subscription dispatch", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        MarkGoalRefined(kernel, goal);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
        IReadOnlyList<AgentDefinition> agents = [agent];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var worktreePath = GoalWorktrees.WorktreePath(root, goal.Id);
        Directory.CreateDirectory(worktreePath);
        SeedLocalSkillCatalog(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: ..");
        var task = goal.Tasks.Single();

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["subscription-dispatch", "1"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.NotNull(task.LastDispatch);
        Xunit.Assert.Null(task.LastProcess);
    }


    [Xunit.Fact(DisplayName = "Cli_simple_goal_with_dispatch_creates_goal_and_defers_worker_until_refined")]
    public void CliSimpleGoalWithDispatchCreatesGoalAndDefersWorkerUntilRefined()
    {
        using var _sandboxEnv = ClearWorkerSandboxEnv();
        var root = CreateTempDirectory();
        // Fake a git worktree at root so WorkerSandboxCapabilityPlanner passes the .git existence
        // check when no goal worktree has been created yet (EnsureGoalWorkspaceForDispatch is a
        // no-op outside a real git repo, so the working directory falls back to root).
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: fake");
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
        IReadOnlyList<AgentDefinition> agents = [agent];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var exception = Xunit.Assert.Throws<InvalidOperationException>(() =>
            CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["simple-goal", "Implement src/Test.cs with tests coverage", "--dispatch", "--confirm-dispatch-start"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal)));

        Xunit.Assert.Contains("SPEC_REFINEMENT_PENDING", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Single(kernel.Goals);
        Xunit.Assert.NotNull(currentGoal);
        var task = currentGoal!.Tasks.Single();
        Xunit.Assert.Null(task.LastDispatch);
        Xunit.Assert.Null(task.LastProcess);
    }


    [Xunit.Fact(DisplayName = "Cli_simple_goal_without_dispatch_only_creates_goal")]
    public void CliSimpleGoalWithoutDispatchOnlyCreatesGoal()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["simple-goal", "Inspect docs/usage.md and summarize"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Single(kernel.Goals);
        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.Null(currentGoal!.Tasks.Single().LastDispatch);
        Xunit.Assert.Contains(currentGoal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Intake pipeline decision (override): developer-only", StringComparison.Ordinal));
    }


    [Xunit.Fact(DisplayName = "Cli_profile_dispatch_with_confirm_dispatch_start_prepares_and_launches")]
    public void CliProfileDispatchWithConfirmDispatchStartPreparesAndLaunches()
    {
        using var _sandboxEnv = ClearWorkerSandboxEnv();
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("One-step profile dispatch", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        MarkGoalRefined(kernel, goal);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
        IReadOnlyList<AgentDefinition> agents = [agent];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]);
        var profiles = new WorkerProfileCatalog([new WorkerProfile("codex-cli", "Start-Sleep -Seconds 60")]);
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var worktreePath = GoalWorktrees.WorktreePath(root, goal.Id);
        Directory.CreateDirectory(worktreePath);
        SeedLocalSkillCatalog(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: ..");
        var task = goal.Tasks.Single();

        try
        {
            CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["profile-dispatch", "1", "codex-cli", "--confirm-dispatch-start"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.NotNull(task.LastDispatch);
            Xunit.Assert.NotNull(task.LastProcess);
        }
        finally
        {
            if (task.LastProcess is { IsRunning: true })
                new BackgroundDispatchRunner().CancelLatestProcess(kernel, goal.Id, task.Id);
        }
    }


    [Xunit.Fact(DisplayName = "Cli_profile_dispatch_without_confirm_dispatch_start_only_prepares")]
    public void CliProfileDispatchWithoutConfirmDispatchStartOnlyPrepares()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Prepare-only profile dispatch", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        MarkGoalRefined(kernel, goal);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
        IReadOnlyList<AgentDefinition> agents = [agent];
        var providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider(providerName: "OpenAI")]);
        var profiles = new WorkerProfileCatalog([new WorkerProfile("codex-cli", "Write-Output ok")]);
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var worktreePath = GoalWorktrees.WorktreePath(root, goal.Id);
        Directory.CreateDirectory(worktreePath);
        SeedLocalSkillCatalog(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: ..");
        var task = goal.Tasks.Single();

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["profile-dispatch", "1", "codex-cli"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.NotNull(task.LastDispatch);
        Xunit.Assert.Null(task.LastProcess);
    }


}
