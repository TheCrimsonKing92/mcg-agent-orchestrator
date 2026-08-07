using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text.Json;

[Xunit.Collection("GoalWorktreeCleanupHooks")]
public sealed class CliCommandTestsGoalLifecycleCommands : CliCommandTestBase
{
    [Xunit.Fact(DisplayName = "Cli_goal_mark_landed_splits_confirmation_and_force_flags")]
    public void CliGoalMarkLandedSplitsConfirmationAndForceFlags()
    {
        var parts = CliArgumentParser.SplitCommand("goal-mark-landed abcdef12 --confirm-goal-mark-landed --force");

        Xunit.Assert.Equal(
            ["goal-mark-landed", "abcdef12", "--confirm-goal-mark-landed", "--force"],
            parts);
    }


    [Xunit.Fact(DisplayName = "Cli_goal_mark_landed_splits_single_confirmation_flag")]
    public void CliGoalMarkLandedSplitsSingleConfirmationFlag()
    {
        var parts = CliArgumentParser.SplitCommand("goal-mark-landed abcdef12 --confirm-goal-mark-landed");

        Xunit.Assert.Equal(
            ["goal-mark-landed", "abcdef12", "--confirm-goal-mark-landed"],
            parts);
    }


    [Xunit.Fact(DisplayName = "Cli_goal_mark_landed_interactive_and_one_shot_args_match")]
    public void CliGoalMarkLandedInteractiveAndOneShotArgsMatch()
    {
        var interactive = CliArgumentParser.SplitCommand("goal-mark-landed abcdef12 --confirm-goal-mark-landed --force");
        var oneShot = CliArgumentParser.NormalizeArgs(
            ["goal-mark-landed", "abcdef12", "--confirm-goal-mark-landed", "--force"]);

        Xunit.Assert.Equal(oneShot, interactive);
    }

    [Xunit.Fact(DisplayName = "Cli_goal_timing_all_prints_rollup")]
    public void CliGoalTimingAllPrintsRollup()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement timed work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Roll up goal timing", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", root, DateTimeOffset.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec prompt.md",
            root,
            0,
            "WORKER_RESULT:\nfiles: src/file.cs\ncommands: build\ntests: pass - focused\nblockers: none\nEND_WORKER_RESULT",
            string.Empty,
            DateTimeOffset.UtcNow.AddMinutes(1),
            WorkerResultPresent: true,
            HasCommittedChanges: true,
            HeartbeatStandardOutputBytes: 100));
        GoalOperationJournal.Completed(root, goal, "acceptance", "Acceptance passed and merge completed.");

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["goal-timing", "--all"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("Goal timing rollup: goals=1", output);
        Xunit.Assert.Contains("Phase", output);
        Xunit.Assert.Contains("Daily trend:", output);
    }

    [Xunit.Fact(DisplayName = "Cli_goal_timing_links_backlog_reference_from_objective_and_labels_pending_landing")]
    public void CliGoalTimingLinksBacklogReferenceFromObjectiveAndLabelsPendingLanding()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var backlogCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-20);
        var backlogId = "1234abcd000000000000000000000000";
        new BacklogStore(workspace.BacklogStorePath).UpsertAsync(new BacklogItem(
            backlogId,
            "Timing backlog item",
            string.Empty,
            BacklogItemStatus.Open,
            backlogCreatedAt,
            backlogCreatedAt,
            null)).GetAwaiter().GetResult();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement linked backlog timing", AgentRole.Developer);
        var goal = kernel.CreateGoal($"Handle backlog {backlogId[..8]} timing", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", root, DateTimeOffset.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec prompt.md",
            root,
            0,
            "WORKER_RESULT:\nfiles: src/file.cs\ncommands: build\ntests: pass - focused\nblockers: none\nEND_WORKER_RESULT",
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: true,
            HeartbeatStandardOutputBytes: 100));

        var output = ExecuteCliAndCapture(["goal-timing", goal.Id.Value[..8]], kernel, workspace);

        Xunit.Assert.Contains("source=pending", output);
        Xunit.Assert.Contains("backlogIntentWait=", output);
        Xunit.Assert.DoesNotContain("backlogIntentWait=0s", output);
    }

    [Xunit.Fact(DisplayName = "Cli_goal_timing_uses_integration_commit_for_hand_landed_goal")]
    public void CliGoalTimingUsesIntegrationCommitForHandLandedGoal()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "tests@example.invalid");
        RunGit(root, "config", "user.name", "Tests");
        File.WriteAllText(Path.Combine(root, "README.md"), "seed");
        RunGit(root, "add", "README.md");
        RunGit(root, "commit", "-m", "Seed");
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement hand landing timing", AgentRole.Developer);
        var goal = kernel.CreateGoal("Hand landed timing", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", root, DateTimeOffset.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec prompt.md",
            root,
            0,
            "WORKER_RESULT:\nfiles: src/file.cs\ncommands: build\ntests: pass - focused\nblockers: none\nEND_WORKER_RESULT",
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: true,
            HeartbeatStandardOutputBytes: 100));
        File.WriteAllText(Path.Combine(root, "landed.txt"), goal.Id.Value);
        RunGit(root, "add", "landed.txt");
        RunGit(root, "commit", "-m", $"Integrate {GoalWorktrees.BranchName(goal.Id)}");

        var output = ExecuteCliAndCapture(["goal-timing", goal.Id.Value[..8]], kernel, workspace);

        Xunit.Assert.Contains("source=git-integration-commit", output);
        Xunit.Assert.DoesNotContain("source=pending", output);
    }

    [Xunit.Fact(DisplayName = "Cli_goal_timing_uses_terminal_disposition_for_sweep_reconciliation")]
    public void CliGoalTimingUsesTerminalDispositionForSweepReconciliation()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement sweep timing", AgentRole.Developer);
        var goal = kernel.CreateGoal("Sweep reconciled timing", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", root, DateTimeOffset.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec prompt.md",
            root,
            0,
            "WORKER_RESULT:\nfiles: src/file.cs\ncommands: build\ntests: pass - focused\nblockers: none\nEND_WORKER_RESULT",
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: true,
            HeartbeatStandardOutputBytes: 100));
        GoalOperationJournal.RecordTerminalDisposition(
            root,
            goal,
            new GoalTerminalDisposition(GoalTerminalDispositionKind.Landed, "Terminal sweep reconciled landed goal."));

        var output = ExecuteCliAndCapture(["goal-timing", goal.Id.Value[..8]], kernel, workspace);

        Xunit.Assert.Contains("source=terminal-disposition", output);
        Xunit.Assert.DoesNotContain("source=pending", output);
    }

    [Xunit.Fact(DisplayName = "Cli_goal_timing_keeps_conductor_acceptance_pass_without_landing_pending")]
    public void CliGoalTimingKeepsConductorAcceptancePassWithoutLandingPending()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement acceptance timing", AgentRole.Developer);
        var goal = kernel.CreateGoal("Acceptance landed timing", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", root, DateTimeOffset.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec prompt.md",
            root,
            0,
            "WORKER_RESULT:\nfiles: src/file.cs\ncommands: build\ntests: pass - focused\nblockers: none\nEND_WORKER_RESULT",
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: true,
            HeartbeatStandardOutputBytes: 100));
        GoalOperationJournal.Begin(root, goal, "conductor:acceptance", "Running acceptance.");
        GoalOperationJournal.AcceptancePassed(root, goal, "conductor:acceptance", "branch-head", "main-head", "Acceptance passed.");

        var output = ExecuteCliAndCapture(["goal-timing", goal.Id.Value[..8]], kernel, workspace);

        Xunit.Assert.Contains("source=pending", output);
        Xunit.Assert.Contains("gate=", output);
        Xunit.Assert.DoesNotContain("source=acceptance-journal", output);
        Xunit.Assert.DoesNotContain("source=landing-journal", output);
    }

    [Xunit.Fact(DisplayName = "Cli_goal_timing_uses_conductor_landing_journal_for_landing")]
    public void CliGoalTimingUsesConductorLandingJournalForLanding()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement conductor landing timing", AgentRole.Developer);
        var goal = kernel.CreateGoal("Conductor landed timing", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", root, DateTimeOffset.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec prompt.md",
            root,
            0,
            "WORKER_RESULT:\nfiles: src/file.cs\ncommands: build\ntests: pass - focused\nblockers: none\nEND_WORKER_RESULT",
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: true,
            HeartbeatStandardOutputBytes: 100));
        GoalOperationJournal.Begin(root, goal, "conductor:acceptance", "Running acceptance.");
        GoalOperationJournal.AcceptancePassed(root, goal, "conductor:acceptance", "branch-head", "main-head", "Acceptance passed.");
        GoalOperationJournal.Begin(root, goal, "conductor:land", "Landing goal via integration branch.");
        GoalOperationJournal.Completed(root, goal, "conductor:land", "Landed.");

        var output = ExecuteCliAndCapture(["goal-timing", goal.Id.Value[..8]], kernel, workspace);

        Xunit.Assert.Contains("source=landing-journal", output);
        Xunit.Assert.DoesNotContain("source=pending", output);
        Xunit.Assert.DoesNotContain("source=acceptance-journal", output);
    }


    [Xunit.Fact(DisplayName = "Cli_abandon_goal_keeps_reason_text_grouped_before_confirmation")]
    public void CliAbandonGoalKeepsReasonTextGroupedBeforeConfirmation()
    {
        var interactive = CliArgumentParser.SplitCommand(
            "abandon-goal abcdef12 Operator chose a different route. --confirm-goal-abandon");
        var oneShot = CliArgumentParser.NormalizeArgs(
            ["abandon-goal", "abcdef12", "Operator", "chose", "a", "different", "route.", "--confirm-goal-abandon"]);

        Xunit.Assert.Equal(
            ["abandon-goal", "abcdef12", "Operator chose a different route.", "--confirm-goal-abandon"],
            interactive);
        Xunit.Assert.Equal(oneShot, interactive);
    }


    [Xunit.Fact(DisplayName = "Cli_conduct_help_prints_usage_without_resolving_goal")]
    public void CliConductHelpPrintsUsageWithoutResolvingGoal()
    {
        AssertHelpCommandDoesNotResolveGoal(["conduct", "--help"], "Usage: conduct <goal-id-prefix>");
        AssertHelpCommandDoesNotResolveGoal(["conduct", "-h"], "Usage: conduct <goal-id-prefix>");
        AssertHelpCommandDoesNotResolveGoal(["conduct", "--loop", "--help"], "Usage: conduct <goal-id-prefix>");
    }


    [Xunit.Fact(DisplayName = "Cli_conduct_loop_watch_accepts_poll_seconds_and_default")]
    public void CliConductLoopWatchAcceptsPollSecondsAndDefault()
    {
        foreach (var testCase in new[]
        {
            new { Args = new[] { "conduct", "--loop", "--watch", "--poll-seconds", "5", "--max-iterations", "0" }, ExpectedOutput = "sleep 5s" },
            new { Args = new[] { "conduct", "--loop", "--watch", "--max-iterations", "0" }, ExpectedOutput = $"sleep {ConductorBatchLoop.DefaultWatchIntervalSeconds}s" }
        })
        {
            var root = CreateTempDirectory();
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;

            var output = CaptureConsole(() =>
            {
                CliCommandDispatcher.ExecuteCommand(
                    testCase.Args,
                    kernel,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);
            });

            Xunit.Assert.Contains(testCase.ExpectedOutput, output);
        }
    }


    [Xunit.Fact(DisplayName = "Cli_conduct_scoped_watch_accepts_poll_seconds_and_default")]
    public void CliConductScopedWatchAcceptsPollSecondsAndDefault()
    {
        foreach (var testCase in new (string? PollSeconds, string ExpectedOutput)[]
        {
            ("5", "poll 5s"),
            (null, $"poll {ConductorBatchLoop.DefaultWatchIntervalSeconds}s")
        })
        {
            var root = CreateTempDirectory();
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, agents, "Watch one goal");
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;
            var args = new List<string> { "conduct", goal.Id.Value[..8], "--watch", "--max-duration", "0" };
            if (testCase.PollSeconds is not null)
            {
                args.Add("--poll-seconds");
                args.Add(testCase.PollSeconds);
            }

            var output = CaptureConsole(() =>
            {
                CliCommandDispatcher.ExecuteCommand(
                    args,
                    kernel,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);
            });

            Xunit.Assert.Contains(testCase.ExpectedOutput, output);
        }
    }


    [Xunit.Theory(DisplayName = "Cli_conduct_poll_seconds_rejects_invalid_values")]
    [Xunit.InlineData("0")]
    [Xunit.InlineData("-1")]
    [Xunit.InlineData("foo")]
    public void CliConductPollSecondsRejectsInvalidValues(string invalidPollSeconds)
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, agents, "Invalid poll goal");
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        var loopError = Xunit.Assert.ThrowsAny<ArgumentException>(() =>
        {
            CliCommandDispatcher.ExecuteCommand(
                ["conduct", "--loop", "--watch", "--poll-seconds", invalidPollSeconds, "--max-iterations", "0"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        });
        Xunit.Assert.Contains("--poll-seconds requires a positive integer value.", loopError.Message);

        var scopedError = Xunit.Assert.ThrowsAny<ArgumentException>(() =>
        {
            CliCommandDispatcher.ExecuteCommand(
                ["conduct", goal.Id.Value[..8], "--watch", "--poll-seconds", invalidPollSeconds, "--max-duration", "0"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        });
        Xunit.Assert.Contains("--poll-seconds requires a positive integer value.", scopedError.Message);
    }


    [Xunit.Fact(DisplayName = "Cli_conduct_scoped_poll_seconds_requires_watch")]
    public void CliConductScopedPollSecondsRequiresWatch()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, agents, "Poll without watch goal");
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        var error = Xunit.Assert.ThrowsAny<ArgumentException>(() =>
        {
            CliCommandDispatcher.ExecuteCommand(
                ["conduct", goal.Id.Value[..8], "--poll-seconds", "5"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        });

        Xunit.Assert.Contains("--poll-seconds requires --watch.", error.Message);
    }


    [Xunit.Fact(DisplayName = "Cli_workspace_help_prints_usage_without_resolving_goal")]
    public void CliWorkspaceHelpPrintsUsageWithoutResolvingGoal()
    {
        AssertHelpCommandDoesNotResolveGoal(["workspace", "--help"], "Usage: workspace [create|merge|rebase|remove] [goal-id-prefix]");
        AssertHelpCommandDoesNotResolveGoal(["workspace", "-h"], "Usage: workspace [create|merge|rebase|remove] [goal-id-prefix]");
        AssertHelpCommandDoesNotResolveGoal(["workspace", "create", "-h"], "Usage: workspace create [goal-id-prefix]");
    }


    [Xunit.Fact(DisplayName = "Cli_project_commands_create_select_list_and_show")]
    public void CliProjectCommandsCreateSelectListAndShow()
    {
        var defaultRoot = CreateTempDirectory();
        var projectRoot = CreateTempDirectory();
        var registry = new OrchestratorProjectRegistry(CreateTempDirectory());
        SeedProjectCreationSource(defaultRoot);

        var output = CaptureConsole(() =>
        {
            Xunit.Assert.Equal(0, ProjectCliCommand.Execute(
                ["project", "create", "client_a", "--root", projectRoot],
                registry,
                defaultRoot,
                activeProjectOverride: null));
            Xunit.Assert.Equal(0, ProjectCliCommand.Execute(
                ["project", "select", "client_a"],
                registry,
                defaultRoot,
                activeProjectOverride: null));
            Xunit.Assert.Equal(0, ProjectCliCommand.Execute(
                ["project", "list"],
                registry,
                defaultRoot,
                activeProjectOverride: null));
            Xunit.Assert.Equal(0, ProjectCliCommand.Execute(
                ["project", "show"],
                registry,
                defaultRoot,
                activeProjectOverride: null));
        });

        Xunit.Assert.Contains("Project created: client_a", output);
        Xunit.Assert.Contains("Project selected: client_a", output);
        Xunit.Assert.Contains("* client_a:", output);
        Xunit.Assert.Contains($"Root: {projectRoot}", output);
        Xunit.Assert.Contains(Path.Combine(projectRoot, ".orchestrator", "projects", "client_a", "state.db"), output);
        Xunit.Assert.True(File.Exists(Path.Combine(projectRoot, ".orchestrator", "projects", "client_a", "state.db")));
        Xunit.Assert.True(File.Exists(Path.Combine(projectRoot, ".orchestrator", "projects", "client_a", "backlog.db")));
        Xunit.Assert.Equal("client_a", registry.ReadSelectedProjectName());
    }

    [Xunit.Fact(DisplayName = "Cli_project_create_seeds_independent_runnable_configuration")]
    public async Task CliProjectCreateSeedsIndependentRunnableConfiguration()
    {
        var defaultRoot = CreateTempDirectory();
        var projectRootA = CreateTempDirectory();
        var projectRootB = CreateTempDirectory();
        var registry = new OrchestratorProjectRegistry(CreateTempDirectory());
        var sourceWorkspace = SeedProjectCreationSource(defaultRoot);
        var sourceConfigBefore = SnapshotConfigurationBytes(sourceWorkspace);

        ProjectCliCommand.Execute(
            ["project", "create", "client_a", "--root", projectRootA],
            registry,
            defaultRoot,
            activeProjectOverride: null);
        ProjectCliCommand.Execute(
            ["project", "create", "client_b", "--root", projectRootB],
            registry,
            defaultRoot,
            activeProjectOverride: null);

        var workspaceA = registry.GetRequiredProject("client_a").ResolveWorkspace();
        var workspaceB = registry.GetRequiredProject("client_b").ResolveWorkspace();
        var pathGroups = new[]
        {
            new[] { sourceWorkspace.OrchestratorDirectory, workspaceA.OrchestratorDirectory, workspaceB.OrchestratorDirectory },
            new[] { sourceWorkspace.ModelFunctionCatalogPath, workspaceA.ModelFunctionCatalogPath, workspaceB.ModelFunctionCatalogPath },
            new[] { sourceWorkspace.AgentCatalogPath, workspaceA.AgentCatalogPath, workspaceB.AgentCatalogPath },
            new[] { sourceWorkspace.WorkerProfilePath, workspaceA.WorkerProfilePath, workspaceB.WorkerProfilePath },
            new[] { sourceWorkspace.SqliteStatePath, workspaceA.SqliteStatePath, workspaceB.SqliteStatePath },
            new[] { sourceWorkspace.BacklogStorePath, workspaceA.BacklogStorePath, workspaceB.BacklogStorePath }
        };
        foreach (var paths in pathGroups)
        {
            Xunit.Assert.Equal(3, paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        foreach (var configPath in new[]
        {
            workspaceA.ModelFunctionCatalogPath,
            workspaceA.AgentCatalogPath,
            workspaceA.WorkerProfilePath,
            workspaceB.ModelFunctionCatalogPath,
            workspaceB.AgentCatalogPath,
            workspaceB.WorkerProfilePath
        })
        {
            Xunit.Assert.True(File.Exists(configPath));
            Xunit.Assert.Null(new FileInfo(configPath).LinkTarget);
            Xunit.Assert.Equal(0, (int)(File.GetAttributes(configPath) & FileAttributes.ReparsePoint));
        }

        var modelFunctionsA = ModelFunctionCatalogStore.Load(workspaceA.ModelFunctionCatalogPath);
        var agentsA = AgentCatalogStore.Load(workspaceA.AgentCatalogPath);
        var profilesA = WorkerProfileStore.Load(workspaceA.WorkerProfilePath);
        Xunit.Assert.Single(modelFunctionsA.ForPurpose(ModelFunctionPurposes.SpecRefiner));
        Xunit.Assert.NotEmpty(agentsA.Agents);
        Xunit.Assert.NotEmpty(profilesA.Profiles);
        Xunit.Assert.Contains(projectRootA, profilesA.GetRequired("project-local").CommandTemplate, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.DoesNotContain(defaultRoot, profilesA.GetRequired("project-local").CommandTemplate, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains(
            defaultRoot + "-tools",
            profilesA.GetRequired("machine-global-sibling").CommandTemplate,
            StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.DoesNotContain(
            projectRootA + "-tools",
            profilesA.GetRequired("machine-global-sibling").CommandTemplate,
            StringComparison.OrdinalIgnoreCase);

        var restoredA = await CreateMigratedStateRepository(workspaceA.SqliteStatePath).LoadAsync();
        var backlogA = await new BacklogStore(workspaceA.BacklogStorePath).ListAsync(includeAll: true);
        Xunit.Assert.Empty(restoredA.Goals);
        Xunit.Assert.Empty(backlogA);
        Xunit.Assert.Equal(sourceConfigBefore, SnapshotConfigurationBytes(sourceWorkspace));

        var projectBConfigBefore = SnapshotConfigurationBytes(workspaceB);
        var projectBWorkspaceBefore = SnapshotWorkspaceBytes(workspaceB);
        AgentCatalogStore.Save(
            workspaceA.AgentCatalogPath,
            new AgentCatalog(agentsA.Agents.Append(TestAgent("client-a-only", AgentRole.Developer)).ToList()));
        ModelFunctionCatalogStore.Save(
            workspaceA.ModelFunctionCatalogPath,
            new ModelFunctionCatalog(modelFunctionsA.Bindings.Append(new ModelFunctionBinding(
                ModelFunctionPurposes.AcceptanceJudge,
                ModelLane.Local,
                new ModelProfile("local-only", "judge", ModelCapability.Text, SubscriptionMode.LocalBridge))).ToList()));
        WorkerProfileStore.Save(
            workspaceA.WorkerProfilePath,
            profilesA.Upsert(new WorkerProfile("client-a-only", "client-a-worker {promptPath}")));
        Xunit.Assert.Equal(sourceConfigBefore, SnapshotConfigurationBytes(sourceWorkspace));
        Xunit.Assert.Equal(projectBConfigBefore, SnapshotConfigurationBytes(workspaceB));

        var refinementKernel = new AgentOrchestratorKernel();
        var refinementGoal = refinementKernel.CreateGoal("Resolve the project-owned spec refiner");
        var refinementService = new GoalRefinementService(
            new InMemoryModelProviderRegistry([]),
            modelFunctionsA,
            new FakeCollaborationItemStore(),
            new SpecRefinerPrecedentStore(Path.Combine(CreateTempDirectory(), "precedents.json")));
        var refinement = await refinementService.RefineAsync(refinementKernel, refinementGoal.Id);
        Xunit.Assert.Equal(RefinementOutcome.AutoRefined, refinement.Outcome);
        Xunit.Assert.NotNull(refinementKernel.GetGoal(refinementGoal.Id).RefinedSpec);

        var missingBindingKernel = new AgentOrchestratorKernel();
        var missingBindingGoal = missingBindingKernel.CreateGoal("Prove the resolver rejects an empty catalog");
        var missingBindingService = new GoalRefinementService(
            new InMemoryModelProviderRegistry([]),
            ModelFunctionCatalog.Empty,
            new FakeCollaborationItemStore(),
            new SpecRefinerPrecedentStore(Path.Combine(CreateTempDirectory(), "precedents.json")));
        await Xunit.Assert.ThrowsAsync<InvalidOperationException>(
            () => missingBindingService.RefineAsync(missingBindingKernel, missingBindingGoal.Id));

        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalogStore.Load(workspaceA.AgentCatalogPath).Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileStore.Load(workspaceA.WorkerProfilePath);
        Goal? currentGoal = null;
        _ = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["simple-goal", "Inspect docs/project.md and summarize"],
            kernel,
            workspaceA,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var goal = Xunit.Assert.Single(kernel.Goals);
        var task = Xunit.Assert.Single(goal.Tasks);

        var worktreePath = GoalWorktrees.WorktreePath(projectRootA, goal.Id);
        Directory.CreateDirectory(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: ..");
        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["subscription-dispatch", "1"],
            kernel,
            workspaceA,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        Xunit.Assert.NotNull(task.LastDispatch);
        Xunit.Assert.Null(task.LastProcess);
        Xunit.Assert.Equal(projectBWorkspaceBefore, SnapshotWorkspaceBytes(workspaceB));
    }

    [Xunit.Fact(DisplayName = "Cli_project_create_is_idempotent_and_rejects_repointing")]
    public async Task CliProjectCreateIsIdempotentAndRejectsRepointing()
    {
        var defaultRoot = CreateTempDirectory();
        var projectRoot = CreateTempDirectory();
        var otherRoot = CreateTempDirectory();
        var registry = new OrchestratorProjectRegistry(CreateTempDirectory());
        SeedProjectCreationSource(defaultRoot);

        ProjectCliCommand.Execute(
            ["project", "create", "client_a", "--root", projectRoot],
            registry,
            defaultRoot,
            activeProjectOverride: null);
        var workspace = registry.GetRequiredProject("client_a").ResolveWorkspace();
        var customizedAgents = AgentCatalogStore.Load(workspace.AgentCatalogPath)
            .AddOrReplaceById(TestAgent("client-a-custom", AgentRole.Developer));
        AgentCatalogStore.Save(workspace.AgentCatalogPath, customizedAgents);
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal("Preserve this goal");
        await CreateMigratedStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        await new BacklogStore(workspace.BacklogStorePath).UpsertAsync(new BacklogItem(
            "preserve-backlog",
            "Preserve this backlog item",
            "Idempotent project create must not replace backlog state.",
            BacklogItemStatus.Open,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            SourceGoalId: null));
        var filesBefore = SnapshotWorkspaceBytes(workspace);

        var output = CaptureConsole(() => ProjectCliCommand.Execute(
            ["project", "create", "client_a", "--root", projectRoot],
            registry,
            defaultRoot,
            activeProjectOverride: null));

        Xunit.Assert.Contains("Project already exists: client_a", output);
        Xunit.Assert.Equal(filesBefore, SnapshotWorkspaceBytes(workspace));

        var error = Xunit.Assert.Throws<InvalidOperationException>(() => ProjectCliCommand.Execute(
            ["project", "create", "client_a", "--root", otherRoot],
            registry,
            defaultRoot,
            activeProjectOverride: null));
        Xunit.Assert.Contains("cannot be repointed", error.Message);
        Xunit.Assert.Equal(projectRoot, registry.GetRequiredProject("client_a").RootDirectory);
        Xunit.Assert.False(Directory.Exists(OrchestratorWorkspace.ForProject("client_a", otherRoot).OrchestratorDirectory));
    }

    [Xunit.Fact(DisplayName = "Cli_project_create_missing_refiner_fails_before_destination_or_registry_mutation")]
    public void CliProjectCreateMissingRefinerFailsBeforeDestinationOrRegistryMutation()
    {
        var defaultRoot = CreateTempDirectory();
        var projectRoot = CreateTempDirectory();
        var registry = new OrchestratorProjectRegistry(CreateTempDirectory());
        var destination = OrchestratorWorkspace.ForProject("client_a", projectRoot);

        var error = Xunit.Assert.Throws<InvalidOperationException>(() => ProjectCliCommand.Execute(
            ["project", "create", "client_a", "--root", projectRoot],
            registry,
            defaultRoot,
            activeProjectOverride: null));

        Xunit.Assert.Contains("source configuration validation", error.Message);
        Xunit.Assert.Contains("missing 'spec-refiner'", error.Message);
        Xunit.Assert.Contains("Configure the invoking default project", error.Message);
        Xunit.Assert.Empty(registry.ListProjects());
        Xunit.Assert.False(Directory.Exists(destination.OrchestratorDirectory));
    }

    [Xunit.Fact(DisplayName = "Cli_project_create_rejects_nonempty_destination_without_mutation")]
    public void CliProjectCreateRejectsNonemptyDestinationWithoutMutation()
    {
        var defaultRoot = CreateTempDirectory();
        var projectRoot = CreateTempDirectory();
        var registry = new OrchestratorProjectRegistry(CreateTempDirectory());
        SeedProjectCreationSource(defaultRoot);
        var destination = OrchestratorWorkspace.ForProject("client_a", projectRoot);
        Directory.CreateDirectory(destination.OrchestratorDirectory);
        var sentinelPath = Path.Combine(destination.OrchestratorDirectory, "keep.txt");
        File.WriteAllText(sentinelPath, "keep");

        var error = Xunit.Assert.Throws<InvalidOperationException>(() => ProjectCliCommand.Execute(
            ["project", "create", "client_a", "--root", projectRoot],
            registry,
            defaultRoot,
            activeProjectOverride: null));

        Xunit.Assert.Contains("destination validation", error.Message);
        Xunit.Assert.Equal("keep", File.ReadAllText(sentinelPath));
        Xunit.Assert.Empty(registry.ListProjects());
    }

    [Xunit.Fact(DisplayName = "Cli_project_create_registry_failure_leaves_recoverable_unregistered_workspace")]
    public void CliProjectCreateRegistryFailureLeavesRecoverableUnregisteredWorkspace()
    {
        var defaultRoot = CreateTempDirectory();
        var projectRoot = CreateTempDirectory();
        SeedProjectCreationSource(defaultRoot);
        var blockedRegistryPath = Path.Combine(CreateTempDirectory(), "registry-blocked");
        File.WriteAllText(blockedRegistryPath, "not a directory");
        var registry = new OrchestratorProjectRegistry(blockedRegistryPath);
        var destination = OrchestratorWorkspace.ForProject("client_a", projectRoot);

        var error = Xunit.Assert.Throws<InvalidOperationException>(() => ProjectCliCommand.Execute(
            ["project", "create", "client_a", "--root", projectRoot],
            registry,
            defaultRoot,
            activeProjectOverride: null));

        Xunit.Assert.Contains("registry registration", error.Message);
        Xunit.Assert.Empty(registry.ListProjects());
        Xunit.Assert.True(File.Exists(destination.ModelFunctionCatalogPath));
        Xunit.Assert.True(File.Exists(destination.AgentCatalogPath));
        Xunit.Assert.True(File.Exists(destination.WorkerProfilePath));
        Xunit.Assert.True(File.Exists(destination.SqliteStatePath));
        Xunit.Assert.True(File.Exists(destination.BacklogStorePath));
    }

    private static OrchestratorWorkspace SeedProjectCreationSource(string root)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        ModelFunctionCatalogStore.Save(
            workspace.ModelFunctionCatalogPath,
            new ModelFunctionCatalog(
            [
                new ModelFunctionBinding(
                    ModelFunctionPurposes.SpecRefiner,
                    ModelLane.CheapApi,
                    new ModelProfile(
                        "missing-provider",
                        "fake-model",
                        ModelCapability.Text,
                        SubscriptionMode.ApiKey),
                    Name: ModelFunctionPurposes.SpecRefiner)
            ]));
        AgentCatalogStore.Save(workspace.AgentCatalogPath, AgentCatalog.Default());
        WorkerProfileStore.Save(
            workspace.WorkerProfilePath,
            WorkerProfileCatalog.Default().Upsert(new WorkerProfile(
                "project-local",
                $"\"{Path.Combine(root, "tools", "worker.exe")}\" --workspace \"{Path.Combine(root, ".orchestrator", "prompts")}\""))
                .Upsert(new WorkerProfile(
                    "machine-global-sibling",
                    $"\"{Path.Combine(root + "-tools", "worker.exe")}\" --version")));
        return workspace;
    }

    private static string SnapshotConfigurationBytes(OrchestratorWorkspace workspace) =>
        SnapshotPaths(
        [
            workspace.ModelFunctionCatalogPath,
            workspace.AgentCatalogPath,
            workspace.WorkerProfilePath
        ],
        includeLastWriteTime: false);

    private static string SnapshotWorkspaceBytes(OrchestratorWorkspace workspace) =>
        SnapshotPaths(
            Directory.EnumerateFiles(workspace.OrchestratorDirectory, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            includeLastWriteTime: true);

    private static string SnapshotPaths(IEnumerable<string> paths, bool includeLastWriteTime) =>
        string.Join(
            Environment.NewLine,
            paths.Select(path =>
            {
                var info = new FileInfo(path);
                var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
                return includeLastWriteTime
                    ? $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{hash}"
                    : $"{Path.GetFullPath(path)}|{info.Length}|{hash}";
            }));


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
        var output = CaptureConsole(() =>
        {
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
        });
        Xunit.Assert.Contains("Tenant: acme", output);
        Xunit.Assert.Contains("Tenant scoped: True", output);
        Xunit.Assert.Contains(Path.Combine(".orchestrator", "tenants", "acme", "state.db"), output);
        Xunit.Assert.Contains("Architecture:", output);
        Xunit.Assert.Contains("subscriptions: Subscription dispatches use worker profiles with role-based sandbox/permission placeholders", output);
        Xunit.Assert.Contains("state stores:", output);
        Xunit.Assert.Contains("api surfaces:", output);
        Xunit.Assert.Contains("/api/system/architecture", output);
        Xunit.Assert.Contains("safety gates:", output);
        Xunit.Assert.Contains("Tenant names are normalized", output);
    }


    [Xunit.Fact(DisplayName = "Cli_autonomy_policies_lists_named_modes")]
    public void CliAutonomyPoliciesListsNamedModes()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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

        var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
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
        var output = CaptureConsole(() => ConsoleViews.PrintNextActions(goal, kernel.BuildNextActions(goal.Id), WorkerProfileCatalog.Default(), [agent]));
        Xunit.Assert.Contains("command: run 2 --confirm-paid-api-run --confirm-large-paid-api-prompt", output);
        Xunit.Assert.Contains("cost: prior overkill API model. Prior evidence says OpenAI/gpt-5-codex was overkill; try local Ollama/qwen3:8b via agent configuration before paid API run.", output);
    }


    [Xunit.Fact(DisplayName = "Cli_next_prints_goal_health_recommendation")]
    public void CliNextPrintsGoalHealthRecommendation()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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


    [Xunit.Fact(DisplayName = "Cli_goal_prints_objective_plan_before_task_creation")]
    public void CliGoalPrintsObjectivePlanBeforeTaskCreation()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        bool changed = false;
        var output = CaptureConsole(() =>
        {
            changed = CliCommandDispatcher.ExecuteCommand(
                ["goal", "Update docs/usage.md to explain goal objective planning"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        });
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
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
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


    [Xunit.Fact(DisplayName = "Cli_goal_recovery_reports_stale_process_and_resume_commands")]
    public void CliGoalRecoveryReportsStaleProcessAndResumeCommands()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Run interrupted worker", AgentRole.Researcher);
        var goal = kernel.CreateGoal(
            "Recover interrupted goal",
            [task, new TaskSpec(TaskId.New(), "Implement recovered work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", root, DateTimeOffset.UtcNow));
        var stdout = Path.Combine(root, "stale.out.log");
        var stderr = Path.Combine(root, "stale.err.log");
        var exit = Path.Combine(root, "stale.exit.txt");
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(999999, "codex exec prompt.md", root, stdout, stderr, exit, DateTimeOffset.UtcNow, null, null));
        bool changed = false;
        var output = CaptureConsole(() =>
        {
            changed = CliCommandDispatcher.ExecuteCommand(
                ["goal-recovery"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        });
        Xunit.Assert.False(changed);
        Xunit.Assert.Contains("Goal recovery", output);
        Xunit.Assert.Contains("recorded process pid=999999 is not alive", output);
        Xunit.Assert.Contains("recovery: action='mark-stale' evidence='heartbeat-absent'", output);
        Xunit.Assert.Contains("command: refresh-dispatch 1", output);
        Xunit.Assert.Contains("workspace create", output);
        Xunit.Assert.Contains($"park-goal {goal.Id.Value[..8]} <reason> --confirm-goal-park", output);
    }


    [Xunit.Fact(DisplayName = "HistoricalDogfoodEvaluation_scores_recorded_goal_state_without_starting_workers")]
    public void HistoricalDogfoodEvaluationScoresRecordedGoalStateWithoutStartingWorkers()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Verify me", AgentRole.Tester);
        var goal = kernel.CreateGoal("Recover missing verification", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Worker reported done.");
        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["goal-recovery", goal.Id.Value[..8]],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        Xunit.Assert.Contains("task completed without verification evidence", output);
        Xunit.Assert.Contains("command: verify 1 <command>", output);
    }


    [Xunit.Fact(DisplayName = "Cli_supervisor_dry_run_reports_refresh_proposal")]
    public void CliSupervisorDryRunReportsRefreshProposal()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Xunit.Assert.Null(task.LastProcess);
        Xunit.Assert.Null(task.LastDispatch);
        Xunit.Assert.Null(task.LastVerification);
        Xunit.Assert.Single(task.VerificationHistory, verification =>
            verification.StandardError.Contains("Dispatch recovery policy action='retry-stale'", StringComparison.Ordinal));
        Xunit.Assert.Contains(goal.Timeline, (ProgressEvent evt) =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("StaleDispatchAutoRequeued", StringComparison.Ordinal) &&
            evt.Message.Contains("auto_requeue=1/2", StringComparison.Ordinal));
        Xunit.Assert.Contains(goal.Timeline, (ProgressEvent evt) =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("allowed supervisor refresh", StringComparison.Ordinal));
    }


    [Xunit.Fact(DisplayName = "Cli_recover_handles_auto_requeued_stale_dispatch")]
    public void CliRecoverHandlesAutoRequeuedStaleDispatch()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Recover stale worker", AgentRole.Planner);
        var goal = kernel.CreateGoal("Recover auto-requeued stale dispatch", [task]);
        IReadOnlyList<AgentDefinition> agents = [SubscriptionPlanner("planner", "Planner")];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["recover", goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)], "reconcile safe stale dispatch"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });

        Xunit.Assert.Contains("recover: reset task 1 to dispatchable.", output);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Xunit.Assert.Null(task.LastProcess);
        Xunit.Assert.Null(task.LastDispatch);
        Xunit.Assert.Null(task.LastVerification);
        Xunit.Assert.Single(task.VerificationHistory, verification =>
            verification.StandardError.Contains("Dispatch recovery policy action='retry-stale'", StringComparison.Ordinal));
        Xunit.Assert.Contains(goal.Timeline, (ProgressEvent evt) =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("StaleDispatchAutoRequeued", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Cli_dirty_worktree_recovery_reports_paths_without_false_lifecycle_desync")]
    public void CliDirtyWorktreeRecoveryReportsPathsWithoutFalseLifecycleDesync()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        EnsureGitRepository(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement blocked file work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Diagnose dirty dispatch wedge", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        var dirtyPath = Path.Combine(worktree, "src", "nested", "dirty.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(dirtyPath)!);
        File.WriteAllText(dirtyPath, "// preserve me");

        var reportOutput = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["goal-recovery", goal.Id.Value[..8]],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });
        var recoverOutput = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["recover", goal.Id.Value[..8], "diagnose without mutating files"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains("Commit-worthy paths", reportOutput, StringComparison.Ordinal);
        Xunit.Assert.Contains("src/nested/dirty.cs", reportOutput, StringComparison.Ordinal);
        Xunit.Assert.Contains("dirty worktree blocks Developer/Tester dispatch", recoverOutput, StringComparison.Ordinal);
        Xunit.Assert.Contains("src/nested/dirty.cs", recoverOutput, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("lifecycle/task desync", recoverOutput, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.True(File.Exists(dirtyPath));
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Xunit.Assert.Null(task.LastDispatch);
        Xunit.Assert.Null(task.LastProcess);
    }


    [Xunit.Fact(DisplayName = "Cli_reassign_agent_updates_task_to_exact_agent_id")]
    public void CliReassignAgentUpdatesTaskToExactAgentId()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Reassign exact planner", AgentRole.Planner);
        var goal = kernel.CreateGoal("Exact agent reassignment", [task]);
        var primary = SubscriptionPlanner("primary-planner", "Primary Planner");
        var alternate = SubscriptionPlanner("alternate-planner", "Alternate Planner");
        IReadOnlyList<AgentDefinition> agents = [primary, alternate];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, [primary]);

        CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["reassign-agent", "1", alternate.Id.Value],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });

        Xunit.Assert.Equal(alternate.Id, task.AssignedAgentId);
        Xunit.Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.TaskRedelegated &&
            evt.Message.Contains("primary-planner", StringComparison.Ordinal) &&
            evt.Message.Contains("alternate-planner", StringComparison.Ordinal));
    }


    [Xunit.Fact(DisplayName = "Cli_reassign_agent_reports_missing_agent_without_throwing")]
    public void CliReassignAgentReportsMissingAgentWithoutThrowing()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Reject missing planner", AgentRole.Planner);
        var goal = kernel.CreateGoal("Missing agent reassignment", [task]);
        IReadOnlyList<AgentDefinition> agents = [SubscriptionPlanner("planner", "Planner")];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var stderr = CaptureConsoleError(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["reassign-agent", "1", "missing-agent"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Equal("planner", task.AssignedAgentId!.Value);
        Xunit.Assert.Contains("ERROR: agent id 'missing-agent' was not found.", stderr);
    }


    [Xunit.Fact(DisplayName = "Cli_drain_goals_loads_persisted_policy_and_blocks_disallowed_starts")]
    public void CliDrainGoalsLoadsPersistedPolicyAndBlocksDisallowedStarts()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Drain guarded", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Planner)]);
        IReadOnlyList<AgentDefinition> agents = [SubscriptionPlanner("planner", "Planner")];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
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
        var workspace = CreateRefinedWorkspace(root);
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

        Xunit.Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Xunit.Assert.Null(task.LastProcess);
        Xunit.Assert.Null(task.LastDispatch);
        Xunit.Assert.Null(task.LastVerification);
        Xunit.Assert.Single(task.VerificationHistory, verification =>
            verification.StandardError.Contains("Dispatch recovery policy action='retry-stale'", StringComparison.Ordinal));
        Xunit.Assert.Contains(goal.Timeline, (ProgressEvent evt) =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("StaleDispatchAutoRequeued", StringComparison.Ordinal) &&
            evt.Message.Contains("auto_requeue=1/2", StringComparison.Ordinal));
        Xunit.Assert.Contains("Applied actions: 1", output);
        Xunit.Assert.Contains("refresh-dispatch 1", output);
        Xunit.Assert.DoesNotContain("acceptance-queue --apply", output);
    }


    [Xunit.Fact(DisplayName = "Cli_failure_triage_classifies_CS2012_with_allowed_remediation")]
    public void CliFailureTriageClassifiesCs2012WithAllowedRemediation()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli",
            "codex exec",
            root,
            DateTimeOffset.UtcNow,
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
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
        var workspace = CreateRefinedWorkspace(root);
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
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli",
            "codex exec",
            root,
            DateTimeOffset.UtcNow,
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
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
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement active work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Retention active", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        Directory.CreateDirectory(Path.Combine(root, ".orchestrator-context", goal.Id.Value));
        Directory.CreateDirectory(Path.Combine(root, ".orchestrator", "pre-review-evidence-attempts", goal.Id.Value));

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
        Xunit.Assert.Contains("TestEvidence: Keep; exists=True", output);
        Xunit.Assert.Contains("Worktree: Keep", output);
    }


    [Xunit.Fact(DisplayName = "Cli_retention_plan_archives_abandoned_goal_evidence_and_deletes_orphaned_build_lease")]
    public void CliRetentionPlanArchivesAbandonedGoalEvidenceAndDeletesOrphanedBuildLease()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        Directory.CreateDirectory(Path.Combine(root, ".orchestrator", "pre-review-evidence-attempts", goal.Id.Value));
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
            Xunit.Assert.Contains("TestEvidence: Archive; exists=True", output);
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
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement inbox smoke", AgentRole.Developer);
        var goal = kernel.CreateGoal("Operator inbox smoke", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "manual-verification passed",
            root,
            0,
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow,
            ModelFitNote: "Model fit: Anthropic/claude-opus-5 - adequate - conflict analysis"));
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "manual-verification passed",
            root,
            0,
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow,
            ModelFitNote: "Model fit: anthropic/CLAUDE-OPUS-5 - underpowered - landing repair"));
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
        Xunit.Assert.DoesNotContain("An item with the same key has already been added", ackOutput, StringComparison.Ordinal);

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

            var workspace = CreateRefinedWorkspace(root);
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
            var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
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
        var root = CreateShortAcceptanceRepository();

        var workspace = CreateRefinedWorkspace(root);
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

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["goal-recovery", goal.Id.Value[..8]],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
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
        var missingGoalId = GoalId.New();
        var summaries = GoalOperationJournal.ReadAll(root, [goal.Id, missingGoalId]);

        Xunit.Assert.True(File.Exists(summary.Path));
        Xunit.Assert.Equal(3, summary.Entries.Count);
        Xunit.Assert.Equal(summary.Path, summaries[goal.Id].Path);
        Xunit.Assert.Equal(summary.Entries, summaries[goal.Id].Entries);
        Xunit.Assert.Equal(summary.LatestByOperation, summaries[goal.Id].LatestByOperation);
        Xunit.Assert.Equal(summary.InterruptedOperations, summaries[goal.Id].InterruptedOperations);
        Xunit.Assert.False(summaries[missingGoalId].HasEntries);
        Xunit.Assert.Equal(GoalOperationJournal.PathFor(root, missingGoalId), summaries[missingGoalId].Path);
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
        var workspace = CreateRefinedWorkspace(root);
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

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["goal-recovery", goal.Id.Value[..8]],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        Xunit.Assert.Contains("Operation journal:", output);
        Xunit.Assert.Contains("Interrupted operations:", output);
        Xunit.Assert.Contains("acceptance", output);
        Xunit.Assert.Contains("Recommended actions:", output);
    }


    [Xunit.Fact(DisplayName = "Cli_goal_recovery_reports_orphaned_build_lease_cleanup")]
    public void CliGoalRecoveryReportsOrphanedBuildLeaseCleanup()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        string output;
        try
        {
            output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["goal-recovery", goal.Id.Value[..8]],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal.Id);
        }
        Xunit.Assert.Contains("Build lease: goal-", output);
        Xunit.Assert.Contains("canCleanup=True", output);
        Xunit.Assert.Contains("goal build lease is orphaned", output);
        Xunit.Assert.Contains("build-lease-cleanup --confirm-build-lease-cleanup", output);
    }


    [Xunit.Fact(DisplayName = "Cli_build_lease_cleanup_requires_confirmation_and_deletes_orphaned_lease")]
    public void CliBuildLeaseCleanupRequiresConfirmationAndDeletesOrphanedLease()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        var blocked = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["build-lease-cleanup", goal.Id.Value[..8]],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        Xunit.Assert.Contains("--confirm-build-lease-cleanup", blocked.Message);

        string leaseOutput;
        try
        {
            leaseOutput = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["build-lease-cleanup", goal.Id.Value[..8], "--confirm-build-lease-cleanup"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal.Id);
        }

        Xunit.Assert.Contains("Deleted orphaned build lease", leaseOutput);
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
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
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

        var second = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
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


    [Xunit.Fact(DisplayName = "Cli_simple_goal_with_alternate_developer_uses_first_primary")]
    public void CliSimpleGoalWithAlternateDeveloperUsesFirstPrimary()
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


    [Xunit.Fact(DisplayName = "Cli_cancel_goal_requires_confirmation_for_active_goal_and_records_reason")]
    public void CliCancelGoalRequiresConfirmationForActiveGoalAndRecordsReason()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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

        var blocked = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
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


    [Xunit.Fact(DisplayName = "Cli_goal_disposition_text_file_preserves_literal_confirmation_flags")]
    public void CliGoalDispositionTextFilePreservesLiteralConfirmationFlags()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var cancelGoal = kernel.CreateGoal("Cancel with literal flag", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var supersedeGoal = kernel.CreateGoal("Supersede with literal flag", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var abandonGoal = kernel.CreateGoal("Abandon with literal flag", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var parkGoal = kernel.CreateGoal("Park with literal flag", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var unparkGoal = kernel.CreateGoal("Unpark with literal flag", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = cancelGoal;
        kernel.ActivateGoal(cancelGoal.Id, agents);
        kernel.ActivateGoal(supersedeGoal.Id, agents);
        kernel.ActivateGoal(abandonGoal.Id, agents);
        kernel.ActivateGoal(parkGoal.Id, agents);
        kernel.ActivateGoal(unparkGoal.Id, agents);
        kernel.ParkGoal(unparkGoal.Id, "set up parked goal");
        var cancelReason = "Operator note mentions --confirm-goal-stop literally.";
        var supersedeReason = "Operator note also mentions --confirm-goal-stop literally.";
        var abandonReason = "Operator note mentions --confirm-goal-abandon literally.";
        var parkReason = "Operator note mentions --confirm-goal-park literally.";
        var unparkReason = "Operator note mentions --confirm-goal-unpark literally.";
        var cancelPath = Path.Combine(root, "cancel.md");
        var supersedePath = Path.Combine(root, "supersede.md");
        var abandonPath = Path.Combine(root, "abandon.md");
        var parkPath = Path.Combine(root, "park.md");
        var unparkPath = Path.Combine(root, "unpark.md");
        File.WriteAllText(cancelPath, cancelReason, System.Text.Encoding.UTF8);
        File.WriteAllText(supersedePath, supersedeReason, System.Text.Encoding.UTF8);
        File.WriteAllText(abandonPath, abandonReason, System.Text.Encoding.UTF8);
        File.WriteAllText(parkPath, parkReason, System.Text.Encoding.UTF8);
        File.WriteAllText(unparkPath, unparkReason, System.Text.Encoding.UTF8);

        CliCommandDispatcher.ExecuteCommand(["cancel-goal", cancelGoal.Id.Value[..8], "--text-file", cancelPath, "--confirm-goal-stop"], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
        CliCommandDispatcher.ExecuteCommand(["supersede-goal", supersedeGoal.Id.Value[..8], "--text-file", supersedePath, "--confirm-goal-stop"], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
        CliCommandDispatcher.ExecuteCommand(["abandon-goal", abandonGoal.Id.Value[..8], "--text-file", abandonPath, "--confirm-goal-abandon"], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
        CliCommandDispatcher.ExecuteCommand(["park-goal", parkGoal.Id.Value[..8], "--text-file", parkPath, "--confirm-goal-park"], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
        CliCommandDispatcher.ExecuteCommand(["unpark-goal", unparkGoal.Id.Value[..8], "--text-file", unparkPath, "--confirm-goal-unpark"], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

        Xunit.Assert.Contains(cancelGoal.Timeline, evt => evt.Kind == ProgressKind.GoalCancelled && evt.Message == cancelReason);
        Xunit.Assert.Contains(supersedeGoal.Timeline, evt => evt.Kind == ProgressKind.GoalSuperseded && evt.Message == supersedeReason);
        Xunit.Assert.Contains(abandonGoal.Timeline, evt => evt.Kind == ProgressKind.GoalCancelled && evt.Message == abandonReason);
        Xunit.Assert.Contains(parkGoal.Timeline, evt => evt.Kind == ProgressKind.GoalPolicyDecision && evt.Message == $"Goal parked: {parkReason}");
        Xunit.Assert.Equal(GoalStatus.Active, unparkGoal.Status);
        Xunit.Assert.Contains(unparkGoal.Timeline, evt => evt.Kind == ProgressKind.GoalPolicyDecision && evt.Message == $"Goal unparked: {unparkReason}");
    }


    [Xunit.Fact(DisplayName = "Cli_unpark_goal_requires_confirmation_and_records_reason")]
    public void CliUnparkGoalRequiresConfirmationAndRecordsReason()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Resume parked work", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.ParkGoal(goal.Id, "operator paused churn");
        var goalPrefix = goal.Id.Value[..8];

        var dryRunOutput = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                CliArgumentParser.SplitCommand($"unpark-goal {goalPrefix} Ready to resume."),
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Equal(GoalStatus.Parked, goal.Status);
        Xunit.Assert.Contains("Goal unpark dry run", dryRunOutput);
        Xunit.Assert.Contains("status change: Parked -> Active", dryRunOutput);

        var applyOutput = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                CliArgumentParser.SplitCommand($"unpark-goal {goalPrefix} Ready to resume. --confirm-goal-unpark"),
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });

        Xunit.Assert.Equal(GoalStatus.Active, goal.Status);
        Xunit.Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message == "Goal unparked: Ready to resume.");
        Xunit.Assert.Contains("Goal unparked", applyOutput);
    }


    [Xunit.Fact(DisplayName = "Cli_stop_alias_accepts_text_file_reason")]
    public void CliStopAliasAcceptsTextFileReason()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Stop alias from file", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var reason = "Stop alias reason from file.\n\nPreserve the long operator note.";
        var reasonPath = Path.Combine(root, "stop.md");
        File.WriteAllText(reasonPath, reason, System.Text.Encoding.UTF8);

        var changed = CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand($"stop {goal.Id.Value[..8]} --text-file {reasonPath} --as cancel --confirm-goal-stop"),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(GoalStatus.Cancelled, goal.Status);
        Xunit.Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.GoalCancelled && evt.Message == reason);
    }


    [Xunit.Fact(DisplayName = "Cli_abandon_goal_prints_dry_run_without_mutating_state")]
    public void CliAbandonGoalPrintsDryRunWithoutMutatingState()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
        var workspace = CreateRefinedWorkspace(root);
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
        var journal = GoalOperationJournal.Read(root, goal.Id);
        Xunit.Assert.True(GoalOperationJournal.HasRetiredTerminalDisposition(journal));

        var firstSweep = TerminalGoalSweep.Run(kernel, root, goal.Id);
        var secondSweep = TerminalGoalSweep.Run(kernel, root, goal.Id);

        Xunit.Assert.Empty(firstSweep.Goals);
        Xunit.Assert.Empty(secondSweep.Goals);
        Xunit.Assert.Equal(GoalStatus.Cancelled, goal.Status);
        Xunit.Assert.All(goal.Tasks, task => Xunit.Assert.Equal(WorkTaskStatus.Assigned, task.Status));
    }


    [Xunit.Fact(DisplayName = "Cli_goal_role_flags_assign_named_agents_at_creation")]
    public void CliGoalRoleFlagsAssignNamedAgentsAtCreation()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default()
            .AddOrReplaceById(TestAgent("planner-alt", AgentRole.Planner))
            .AddOrReplaceById(TestAgent("researcher-alt", AgentRole.Researcher))
            .AddOrReplaceById(TestAgent("developer-alt", AgentRole.Developer))
            .AddOrReplaceById(TestAgent("tester-alt", AgentRole.Tester))
            .AddOrReplaceById(TestAgent("reviewer-alt", AgentRole.Reviewer))
            .Agents;
        var originalAgents = agents.ToList();
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            [
                "goal",
                "Implement roster overrides",
                "--planner",
                "planner-alt",
                "--researcher",
                "researcher-alt",
                "--developer",
                "developer-alt",
                "--tester",
                "tester-alt",
                "--reviewer",
                "reviewer-alt"
            ],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.Equal("planner-alt", AssignedAgentId(currentGoal!, AgentRole.Planner));
        Xunit.Assert.Equal("researcher-alt", AssignedAgentId(currentGoal, AgentRole.Researcher));
        Xunit.Assert.Equal("developer-alt", AssignedAgentId(currentGoal, AgentRole.Developer));
        Xunit.Assert.Equal("tester-alt", AssignedAgentId(currentGoal, AgentRole.Tester));
        Xunit.Assert.Equal("reviewer-alt", AssignedAgentId(currentGoal, AgentRole.Reviewer));
        Xunit.Assert.Equal(originalAgents.Select(agent => agent.Id.Value), agents.Select(agent => agent.Id.Value));
    }


    [Xunit.Fact(DisplayName = "Cli_goal_role_flags_leave_omitted_roles_on_defaults")]
    public void CliGoalRoleFlagsLeaveOmittedRolesOnDefaults()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default()
            .AddOrReplaceById(TestAgent("planner-alt", AgentRole.Planner))
            .Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["goal", "Use planner override only", "--planner", "planner-alt"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.Equal("planner-alt", AssignedAgentId(currentGoal!, AgentRole.Planner));
        Xunit.Assert.Equal("openai-developer", AssignedAgentId(currentGoal, AgentRole.Developer));
        Xunit.Assert.Equal("openai-reviewer", AssignedAgentId(currentGoal, AgentRole.Reviewer));
    }


    [Xunit.Fact(DisplayName = "Cli_goal_role_flag_unknown_agent_errors_clearly")]
    public void CliGoalRoleFlagUnknownAgentErrorsClearly()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var ex = Xunit.Assert.ThrowsAny<ArgumentException>(() => CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["goal", "Fail unknown agent", "--developer", "missing-agent"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal)));

        Xunit.Assert.Contains("Unknown agent id 'missing-agent' for --developer", ex.Message);
        Xunit.Assert.Empty(kernel.Goals);
    }


    [Xunit.Fact(DisplayName = "Cli_simple_goal_developer_flag_assigns_named_agent_at_creation")]
    public void CliSimpleGoalDeveloperFlagAssignsNamedAgentAtCreation()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default()
            .AddOrReplaceById(TestAgent("developer-alt", AgentRole.Developer))
            .Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["simple-goal", "Implement one task", "--developer", "developer-alt"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.Single(currentGoal!.Tasks);
        Xunit.Assert.Equal("developer-alt", currentGoal.Tasks.Single().AssignedAgentId!.Value);
    }


    [Xunit.Fact(DisplayName = "Cli_goal_simple_alias_forwards_developer_flag")]
    public void CliGoalSimpleAliasForwardsDeveloperFlag()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default()
            .AddOrReplaceById(TestAgent("developer-alt", AgentRole.Developer))
            .Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["goal", "Implement one task through alias", "--simple", "--developer", "developer-alt"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.Single(currentGoal!.Tasks);
        Xunit.Assert.Equal("developer-alt", currentGoal.Tasks.Single().AssignedAgentId!.Value);
    }


    [Xunit.Fact(DisplayName = "Cli_simple_goal_brief_file_creates_goal_with_file_content")]
    public void CliSimpleGoalBriefFileCreatesGoalWithFileContent()
    {
        var root = CreateTempDirectory();
        var briefContent = "Implement src/Mcg.AgentOrchestrator.App/Cli/CliArgumentParser.NormalizeArgs.cs with --brief-file flag support and tests coverage.\n\nMulti-line brief content that would overflow an inline CLI argument.";
        var briefPath = Path.Combine(root, "brief.md");
        File.WriteAllText(briefPath, briefContent, System.Text.Encoding.UTF8);
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["simple-goal", "--brief-file", briefPath],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Single(kernel.Goals);
        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.Equal(briefContent, currentGoal!.Objective);
    }


    [Xunit.Fact(DisplayName = "Cli_goal_brief_file_launcher_path_preserves_file_content_for_simple_alias")]
    public void CliGoalBriefFileLauncherPathPreservesFileContentForSimpleAlias()
    {
        var root = CreateTempDirectory();
        var briefContent = "Create the goal from a launcher-style --brief-file command.\n\nThe file content must be the objective byte-for-byte.";
        var briefPath = Path.Combine(root, "brief.md");
        File.WriteAllText(briefPath, briefContent, System.Text.Encoding.UTF8);
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand($"goal --brief-file {briefPath} --simple"),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Single(kernel.Goals);
        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.Single(currentGoal!.Tasks);
        Xunit.Assert.Equal(briefContent, currentGoal.Objective);
    }


    [Xunit.Fact(DisplayName = "Cli_simple_goal_text_file_alias_creates_goal_with_file_content")]
    public void CliSimpleGoalTextFileAliasCreatesGoalWithFileContent()
    {
        var root = CreateTempDirectory();
        var briefContent = "Implement a goal from the uniform --text-file alias.\n\nKeep multiline content intact.";
        var briefPath = Path.Combine(root, "brief.md");
        File.WriteAllText(briefPath, briefContent, System.Text.Encoding.UTF8);
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["simple-goal", "--text-file", briefPath],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Single(kernel.Goals);
        Xunit.Assert.Equal(briefContent, currentGoal!.Objective);
    }


    [Xunit.Fact(DisplayName = "Cli_simple_goal_text_file_rejects_inline_objective")]
    public void CliSimpleGoalTextFileRejectsInlineObjective()
    {
        var root = CreateTempDirectory();
        var briefPath = Path.Combine(root, "brief.md");
        File.WriteAllText(briefPath, "File objective.", System.Text.Encoding.UTF8);
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var ex = Xunit.Assert.Throws<ArgumentException>(() => CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand($"simple-goal Inline objective --text-file {briefPath}"),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("either inline text or --text-file", ex.Message);
        Xunit.Assert.Empty(kernel.Goals);
    }


    [Xunit.Fact(DisplayName = "Cli_status_prints_cleanup_backoff_for_snapshot_visibility")]
    public void CliStatusPrintsCleanupBackoffForSnapshotVisibility()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var originalNow = GoalWorktrees.CleanupUtcNow;
        var originalBackoff = GoalWorktrees.CleanupBackoffDuration;
        try
        {
            var now = DateTimeOffset.Parse("2026-07-03T12:00:00Z");
            GoalWorktrees.CleanupUtcNow = () => now;
            GoalWorktrees.CleanupBackoffDuration = TimeSpan.FromMinutes(15);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Cleanup debt visible in status");
            GoalWorktrees.RecordGoalCleanupNeeded(workspace.ExecutionDirectory, goal.Id, "remove:branch-delete-failed");
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["status", goal.Id.Value[..8]],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Contains("Cleanup backoff:", output);
            Xunit.Assert.Contains("reason=remove:branch-delete-failed", output);
            Xunit.Assert.Contains("skip_until_utc=2026-07-03T12:15:00.0000000+00:00", output);
            Xunit.Assert.Contains("remaining_wait=00:15:00", output);
            Xunit.Assert.Contains($"Cleanup retry: conduct {goal.Id.Value[..8].ToLowerInvariant()} --loop", output);
        }
        finally
        {
            GoalWorktrees.CleanupUtcNow = originalNow;
            GoalWorktrees.CleanupBackoffDuration = originalBackoff;
        }
    }

    [Xunit.Fact(DisplayName = "Cli_cleanup_status_uses_caller_cleanup_clock_and_backoff")]
    public void CliCleanupStatusUsesCallerCleanupClockAndBackoff()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var now = DateTimeOffset.Parse("2026-07-03T12:00:00Z");
        var hooks = new GoalWorktreeCleanupHooks
        {
            CleanupUtcNow = () => now,
            CleanupBackoffDuration = static () => TimeSpan.FromMinutes(10),
            CleanupWarningSink = _ => { }
        };
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Cleanup debt uses caller hooks");
        GoalWorktrees.RecordGoalCleanupNeeded(
            workspace.ExecutionDirectory,
            goal.Id,
            "remove:branch-delete-failed",
            hooks);
        now = now.AddMinutes(5);

        var context = new CliExecutionContext(
            kernel,
            workspace,
            new InMemoryModelProviderRegistry([]),
            AgentCatalog.Default().Agents,
            WorkerProfileCatalog.Default(),
            goal)
        {
            CleanupHooks = hooks
        };

        var output = CaptureConsole(() => CliCommandHandlers.Execute(["cleanup-status"], context));

        Xunit.Assert.Contains("age=00:05:00", output);
        Xunit.Assert.Contains("remaining_wait=00:05:00", output);
    }


    [Xunit.Fact(DisplayName = "Cli_simple_goal_brief_file_missing_gives_clear_error")]
    public void CliSimpleGoalBriefFileMissingGivesClearError()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var missingPath = Path.Combine(root, "does-not-exist.md");

        var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["simple-goal", "--brief-file", missingPath],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("--brief-file not found", ex.Message);
        Xunit.Assert.Contains(missingPath, ex.Message);
        Xunit.Assert.Empty(kernel.Goals);
    }


    [Xunit.Fact(DisplayName = "Cli_acceptance_records_dogfood_entry_in_sqlite_without_committing_log_file")]
    public async Task CliAcceptanceRecordsDogfoodEntryInSqliteWithoutCommittingLogFile()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "tests@example.com");
        RunGit(root, "config", "user.name", "CLI Tests");
        File.WriteAllText(Path.Combine(root, "DOGFOOD_LOG.md"), "# Dogfood Log" + Environment.NewLine);
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, "add", "-A");
        RunGit(root, "commit", "-m", "Seed");

        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Commit dogfood entry test", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.UtcNow));
        Xunit.Assert.Equal(GoalStatus.Verified, goal.Status);

        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        File.WriteAllText(Path.Combine(worktree, "feature.txt"), "goal work");
        RunGit(worktree, "add", "-A");
        RunGit(worktree, "commit", "-m", "Goal work");

        var goalPrefix = goal.Id.Value[..8];
        var workspace = CreateRefinedWorkspace(root);
        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["acceptance", "--skip-verify", "--keep-workspace"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var statusOutput = RunGitOutput(root, "status", "--porcelain", "DOGFOOD_LOG.md");
        Xunit.Assert.Equal(string.Empty, statusOutput.Trim());
        var record = await new DogfoodLogStore(workspace.DogfoodLogStorePath)
            .GetByGoalIdAsync(goal.Id.Value);
        Xunit.Assert.NotNull(record);
        Xunit.Assert.Contains("Commit dogfood entry test", record!.RenderedMarkdown);

        var logOutput = RunGitOutput(root, "log", "--oneline", "-5");
        Xunit.Assert.DoesNotContain($"Record dogfood entry for goal {goalPrefix}", logOutput);
    }


    [Xunit.Fact(DisplayName = "Cli_acceptance_pins_selected_stable_slot_and_records_receipt")]
    public void CliAcceptancePinsSelectedStableSlotAndRecordsReceipt()
    {
        var previousRoot = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        var isolatedRoot = Path.Combine(Path.GetTempPath(), $"{DotnetBuildEnvironmentManager.RootDirectoryName}-cli-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, isolatedRoot);
        var root = CreateShortAcceptanceRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
            var goal = kernel.CreateGoal("Pin acceptance slot", [task]);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;
            kernel.ActivateGoal(goal.Id, agents);
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-07-06T15:00:00Z")));
            CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
            // De-slotting: acceptance acquires the goal's own deterministic build permit rather
            // than overflowing to the first free slot. Hold the complement permit so the foreign
            // lease never collides with the goal's permit (which would deterministically block
            // acceptance under the no-overflow acquisition), while still proving acceptance
            // acquires and pins its own permit regardless of an unrelated held slot.
            var goalBuildPermit =
                DotnetBuildEnvironmentManager.CreateAttempt(goal.Id, "permit-probe").BuildPermitIndex ?? 0;
            var foreignSlot = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(goalBuildPermit == 0 ? 1 : 0);
            using var foreignSlotLock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(foreignSlot);
            var leaseHeldObserved = false;
            var verifier = new ProbeAcceptanceVerifier(stableSlotLease =>
            {
                var reacquire = Xunit.Assert.ThrowsAny<IOException>(() =>
                    DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(
                        stableSlotLease!.Environment,
                        TimeSpan.FromMilliseconds(50)));
                Xunit.Assert.IsType<DotnetBuildSlotsBusyException>(reacquire);
                leaseHeldObserved = true;
            });

            var workspace = CreateRefinedWorkspace(root);
            var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["acceptance", "--keep-workspace"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal,
                acceptanceVerifier: verifier,
                phaseTimings: new CliPhaseTimingRecorder("acceptance"),
                stableSlotAcquisitionTimeout: TimeSpan.FromSeconds(1)));

            Xunit.Assert.Equal(goalBuildPermit, verifier.LastStableSlotIndex);
            var selectedSlot = verifier.LastStableSlotLease?.Environment.SlotOwnerToken;
            Xunit.Assert.False(string.IsNullOrWhiteSpace(selectedSlot));
            Xunit.Assert.True(leaseHeldObserved);
            Xunit.Assert.Contains("PHASE_TIMING command=acceptance phase=verification-suite", output);
            Xunit.Assert.Contains($"slot=slot-{verifier.LastStableSlotIndex}", output);

            var conductEvent = File.ReadAllLines(workspace.ConductEventsLogPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Single(record => record.EventKind == "acceptance");
            Xunit.Assert.Equal(goal.Id.Value[..8], conductEvent.GoalId);
            Xunit.Assert.Contains("result=passed", conductEvent.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, previousRoot);
            if (Directory.Exists(isolatedRoot))
            {
                Directory.Delete(isolatedRoot, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_tests_use_fixture_isolated_dotnet_root")]
    public void CliAcceptanceTestsUseFixtureIsolatedDotnetRoot()
    {
        var isolatedRoot = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);

        Xunit.Assert.False(string.IsNullOrWhiteSpace(isolatedRoot));
        Xunit.Assert.Contains(
            $"{DotnetBuildEnvironmentManager.RootDirectoryName}-slot-run-",
            isolatedRoot,
            StringComparison.Ordinal);
        Xunit.Assert.NotEqual(
            Path.Combine(Path.GetTempPath(), DotnetBuildEnvironmentManager.RootDirectoryName),
            isolatedRoot);
    }


    [Xunit.Fact(DisplayName = "Cli_acceptance_slot_timeout_prints_wait_and_blocker_to_stdout")]
    public void CliAcceptanceSlotTimeoutPrintsWaitAndBlockerToStdout()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Timeout acceptance slot", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-07-06T15:00:00Z")));
        CommitGoalWork(root, goal.Id, "feature.txt", "goal work");

        var output = CaptureConsole(() =>
        {
            var ex = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
                ["acceptance", "--keep-workspace"],
                kernel,
                CreateRefinedWorkspace(root),
                ref agents,
                providers,
                ref profiles,
                ref currentGoal,
                acceptanceVerifier: new ProbeAcceptanceVerifier(() => { }),
                phaseTimings: new CliPhaseTimingRecorder("acceptance"),
                stableSlotAcquisitionTimeout: TimeSpan.FromMilliseconds(1),
                stableSlotSelector: (_, onWait) =>
                {
                    onWait?.Invoke(new DotnetBuildStableSlotWait(2, 12345));
                    throw new IOException("Timed out waiting for an available stable dotnet build slot.");
                }));
            Xunit.Assert.Contains("stable dotnet build slot", ex.Message);
        });

        Xunit.Assert.Contains("waiting for build-2 permit held by pid 12345", output);
        Xunit.Assert.Contains("BLOCKER step=verification reason=build-slot-timeout", output);
    }


    [Xunit.Fact(DisplayName = "Cli_acceptance_normalizes_raw_completed_unmerged_branch_before_merge")]
    public void CliAcceptanceNormalizesRawCompletedUnmergedBranchBeforeMerge()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Raw completed but unmerged", [task]);
            cleanupGoalId = goal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            kernel.ActivateGoal(goal.Id, agents);
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/raw-completed.txt", "goal work");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
            goal = kernel.GetGoal(goal.Id);
            Goal? currentGoal = goal;
            var workspace = CreateRefinedWorkspace(root);

            var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["acceptance", "--skip-verify", "--keep-workspace", "--no-record"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Contains("Acceptance repair: normalized raw Completed goal", output, StringComparison.Ordinal);
            Xunit.Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
            Xunit.Assert.True(File.Exists(Path.Combine(root, "src", "raw-completed.txt")));
            Xunit.Assert.Equal("main", RunGitOutput(root, "branch", "--show-current").Trim());
            Xunit.Assert.True(GoalWorktrees.IsBranchMergedIntoCurrent(root, goal.Id));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "Cli_goal_mark_landed_records_completed_goal_and_writes_cleanup_journal_entry")]
    public void CliGoalMarkLandedRecordsCompletedGoalAndWritesCleanupJournalEntry()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Developer);
        var goal = kernel.CreateGoal("Force-landed feature", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "dotnet test", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        Xunit.Assert.Equal(GoalStatus.Verified, goal.Status);
        var goalPrefix = goal.Id.Value[..8];

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["goal-mark-landed", goalPrefix, "--confirm-goal-mark-landed", "--force"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });

        Xunit.Assert.Contains("cleanup: goal marked landed; cleanup-needed recorded", output);
        Xunit.Assert.Contains("Workspace cleanup deferred", output);
        var journal = GoalOperationJournal.Read(root, goal.Id);
        Xunit.Assert.Contains(journal.Entries, entry =>
            entry.Operation == GoalOperationJournal.TerminalDispositionOperation &&
            entry.Detail.Contains("\"kind\":\"Landed\"", StringComparison.Ordinal));
        var cleanupEntry = journal.LatestByOperation.FirstOrDefault(e =>
            e.Operation == "conductor:cleanup" && e.Status == GoalOperationStatus.Failed);
        Xunit.Assert.NotNull(cleanupEntry);
        Xunit.Assert.Contains("Deferred cleanup after goal-mark-landed", cleanupEntry.Detail, StringComparison.Ordinal);
        var facts = new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: false);
        Xunit.Assert.Equal(GoalLifecycleState.Recorded, GoalLifecycle.ResolveState(kernel.GetGoal(goal.Id), facts));
        var sweep = TerminalGoalSweep.Run(kernel, root, goal.Id);
        Xunit.Assert.Empty(sweep.Goals);
        Xunit.Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
    }

    [Xunit.Fact]
    public async Task GoalMarkLanded_MissingBranchWithIntegrateCommit_CompletesAndResolvesAttention()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "tests@example.invalid");
        RunGit(root, "config", "user.name", "Tests");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, "add", "seed.txt");
        RunGit(root, "commit", "-m", "Seed");
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Developer);
        var goal = kernel.CreateGoal("Already integrated feature", [task]);
        var other = kernel.CreateGoal("Live feature", [new TaskSpec(TaskId.New(), "Keep working", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ActivateGoal(other.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        File.WriteAllText(Path.Combine(root, "landed.txt"), "landed");
        RunGit(root, "add", "landed.txt");
        RunGit(root, "commit", "-m", $"Integrate {GoalWorktrees.BranchName(goal.Id)}");
        var integrateSha = RunGitOutput(root, "rev-parse", "HEAD").Trim();
        Xunit.Assert.NotEqual(0, GitCli.Run(root, "rev-parse", "--verify", $"refs/heads/{GoalWorktrees.BranchName(goal.Id)}").ExitCode);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        await store.RaiseAsync(CollaborationItemType.Decision, goal.Id.Value, "Landed decision", "body", "landed-decision");
        await store.RaiseAsync(CollaborationItemType.Verify, goal.Id.Value, "Landed verify", "body", "landed-verify");
        await store.RaiseAsync(CollaborationItemType.Clarification, other.Id.Value, "Live clarification", "body", "live-clarification");
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });

        Xunit.Assert.Equal(GoalStatus.Completed, goal.Status);
        Xunit.Assert.Null(task.LastDispatch);
        Xunit.Assert.Contains("Resolved attention items: 2", output, StringComparison.Ordinal);
        var open = await store.GetAttentionQueueAsync();
        Xunit.Assert.Single(open);
        Xunit.Assert.Equal(other.Id.Value, open[0].GoalId);
        var resolved = await store.ListAsync(goal.Id.Value);
        Xunit.Assert.All(resolved, item => Xunit.Assert.Contains(integrateSha, item.Resolution));
    }

    [Xunit.Fact]
    public void GoalMarkLanded_MergedGoalBranchWithoutIntegrateSubject_CompletesWithoutForce()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Developer);
            var goal = kernel.CreateGoal("Merged branch without conventional subject", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/merged-without-integrate-subject.txt", "goal work");
            var branch = GoalWorktrees.BranchName(goal.Id);
            var branchTip = RunGitOutput(root, "rev-parse", branch).Trim();
            RunGit(root, "merge", "--ff-only", branch);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var changed = CliCommandDispatcher.ExecuteCommand(
                ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);

            Xunit.Assert.True(changed);
            Xunit.Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
            var journal = GoalOperationJournal.Read(root, goal.Id);
            Xunit.Assert.True(GoalOperationJournal.HasMergeEvidenceTerminalDisposition(journal));
            Xunit.Assert.Contains(journal.Entries, entry =>
                entry.Operation == GoalOperationJournal.LandingIntentOperation &&
                entry.Detail.Contains(branchTip, StringComparison.Ordinal));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }

    [Xunit.Fact]
    public void GoalMarkLanded_NoBranchOrIntegrateCommit_ReportsBothSearches()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "tests@example.invalid");
        RunGit(root, "config", "user.name", "Tests");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, "add", "seed.txt");
        RunGit(root, "commit", "-m", "Seed");
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Developer);
        var goal = kernel.CreateGoal("Not integrated feature", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        var error = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("searched both artifacts", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("branch not found locally or remotely", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("no reachable 'Integrate goal/", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("Use --force", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Equal(GoalStatus.Verified, goal.Status);
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_build_lock_blocked_stays_ready_without_failure_history")]
    public void CliAcceptanceBuildLockBlockedStaysReadyWithoutFailureHistory()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Build lock acceptance", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-07-06T15:00:00Z")));
        CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
        var lockedPath = Path.Combine(root, "src", "Mcg.AgentOrchestrator.App", "bin", "Debug", "net10.0", "Mcg.AgentOrchestrator.App.dll");
        var attribution = new BuildLockAttribution(
            lockedPath,
            [new BuildLockHolder(12345, "dotnet", "dotnet test --artifacts-path slot-0", true)],
            "test");
        var verifier = new ProbeAcceptanceVerifier(_ => throw new BuildLockBlockedException(attribution));
        var workspace = CreateRefinedWorkspace(root);

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["acceptance", "--keep-workspace"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal,
            acceptanceVerifier: verifier,
            phaseTimings: new CliPhaseTimingRecorder("acceptance"),
            stableSlotAcquisitionTimeout: TimeSpan.FromSeconds(1)));

        Xunit.Assert.Contains("BUILD_LOCK_BLOCKED", output);
        Xunit.Assert.Contains("Mcg.AgentOrchestrator.App.dll", output);
        Xunit.Assert.Contains("pid-12345:dotnet", output);
        Xunit.Assert.Contains("goal remains ready", output);
        Xunit.Assert.Contains("Acceptance outcomes:", output);
        Xunit.Assert.Contains("current:", output);
        Xunit.Assert.Contains("blocked:BUILD_LOCK_BLOCKED", output);
        Xunit.Assert.Equal(1, verifier.RunCount);
        Xunit.Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id)!.Status);
        Xunit.Assert.Null(kernel.GetGoal(goal.Id)!.LatestAcceptanceFailure);

        var conductEvent = File.ReadAllLines(workspace.ConductEventsLogPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .Single(record => record.EventKind == "lock-blocker");
        Xunit.Assert.Equal(goal.Id.Value[..8], conductEvent.GoalId);
        Xunit.Assert.Contains("BUILD_LOCK_BLOCKED", conductEvent.Detail, StringComparison.Ordinal);
        Xunit.Assert.Contains("Mcg.AgentOrchestrator.App.dll", conductEvent.Detail, StringComparison.Ordinal);
        Xunit.Assert.Contains("12345", conductEvent.Detail, StringComparison.Ordinal);

        var journal = GoalOperationJournal.Read(root, goal.Id);
        var blockedOutcome = journal.Entries.LastOrDefault(entry => entry.AcceptanceOutcome == "blocked:BUILD_LOCK_BLOCKED");
        Xunit.Assert.NotNull(blockedOutcome);
        Xunit.Assert.Equal(GoalOperationStatus.Failed, blockedOutcome.Status);
        Xunit.Assert.False(string.IsNullOrWhiteSpace(blockedOutcome.BranchHeadSha));
        Xunit.Assert.False(string.IsNullOrWhiteSpace(blockedOutcome.MainHeadSha));
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_slots_busy_journals_blocked_outcome")]
    public void CliAcceptanceSlotsBusyJournalsBlockedOutcome()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Slots busy acceptance", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-07-06T15:00:00Z")));
        CommitGoalWork(root, goal.Id, "feature.txt", "goal work");

        var busy = new DotnetBuildLeaseAcquisition.SlotsBusy(
            "first-available-stable-slot",
            [new DotnetBuildStableSlotWait(0, 12345)]);
        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["acceptance", "--keep-workspace"],
            kernel,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal,
            acceptanceVerifier: new ProbeAcceptanceVerifier(() => { }),
            phaseTimings: new CliPhaseTimingRecorder("acceptance"),
            stableSlotAcquisitionTimeout: TimeSpan.FromSeconds(1),
            stableSlotSelector: (_, _) => throw new DotnetBuildSlotsBusyException(busy)));

        Xunit.Assert.Contains("SLOTS_BUSY", output);
        Xunit.Assert.Contains("goal remains ready", output);
        Xunit.Assert.Contains("Acceptance outcomes:", output);
        Xunit.Assert.Contains("current:", output);
        Xunit.Assert.Contains("blocked:slot-unavailable", output);
        Xunit.Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id)!.Status);
        Xunit.Assert.Null(kernel.GetGoal(goal.Id)!.LatestAcceptanceFailure);
        var journal = GoalOperationJournal.Read(root, goal.Id);
        var blockedOutcome = journal.Entries.LastOrDefault(entry => entry.AcceptanceOutcome == "blocked:slot-unavailable");
        Xunit.Assert.NotNull(blockedOutcome);
        Xunit.Assert.Equal(GoalOperationStatus.Failed, blockedOutcome.Status);
        Xunit.Assert.False(string.IsNullOrWhiteSpace(blockedOutcome.BranchHeadSha));
        Xunit.Assert.False(string.IsNullOrWhiteSpace(blockedOutcome.MainHeadSha));
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_timeout_journals_blocked_outcome")]
    public void CliAcceptanceTimeoutJournalsBlockedOutcome()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Timeout acceptance", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-07-06T15:00:00Z")));
        CommitGoalWork(root, goal.Id, "feature.txt", "goal work");

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["acceptance", "--keep-workspace"],
            kernel,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal,
            acceptanceVerifier: new ProbeAcceptanceVerifier(new AcceptanceVerificationResult(
                false,
                false,
                -1,
                "timed out waiting for tests",
                ArtifactsPath: Path.Combine(root, "artifacts"),
                Checks:
                [
                    new AcceptanceCheckResult(
                        "acceptance-check-timeout: infrastructure-tests elapsed=25m budget=25m",
                        false,
                        -1,
                        "timed out waiting for tests",
                        ArtifactsPath: Path.Combine(root, "artifacts"),
                        ResultSummary: "timed out")
                ])),
            phaseTimings: new CliPhaseTimingRecorder("acceptance"),
            stableSlotAcquisitionTimeout: TimeSpan.FromSeconds(1)));

        Xunit.Assert.Contains("BLOCKER step=verification reason=timeout", output);
        Xunit.Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id)!.Status);
        Xunit.Assert.Null(kernel.GetGoal(goal.Id)!.LatestAcceptanceFailure);
        var journal = GoalOperationJournal.Read(root, goal.Id);
        var blockedOutcome = journal.Entries.LastOrDefault(entry => entry.AcceptanceOutcome == "blocked:timeout");
        Xunit.Assert.NotNull(blockedOutcome);
        Xunit.Assert.Equal(GoalOperationStatus.Failed, blockedOutcome.Status);
        Xunit.Assert.False(string.IsNullOrWhiteSpace(blockedOutcome.BranchHeadSha));
        Xunit.Assert.False(string.IsNullOrWhiteSpace(blockedOutcome.MainHeadSha));
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_journals_candidate_outcome_with_base_build_cache_receipt")]
    public void CliAcceptanceJournalsCandidateOutcomeWithBaseBuildCacheReceipt()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Cached acceptance", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-07-06T15:00:00Z")));
        var mainSha = RunGitOutput(root, "rev-parse", "HEAD").Trim();
        CommitGoalWork(root, goal.Id, "feature.txt", "goal work");

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["acceptance", "--keep-workspace"],
            kernel,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal,
            acceptanceVerifier: new ProbeAcceptanceVerifier(new AcceptanceVerificationResult(
                true,
                false,
                0,
                "Passed.",
                ArtifactsPath: Path.Combine(root, "artifacts"),
                Checks:
                [
                    new AcceptanceCheckResult(
                        "infrastructure-tests",
                        true,
                        0,
                        "Passed.",
                        ResultSummary: $"base-build-cache main_sha={mainSha} build_phase_ms=42 projects=Core=hit,Infrastructure=miss,published built_projects=Infrastructure evictions=none; Passed: 1")
                ])),
            phaseTimings: new CliPhaseTimingRecorder("acceptance"),
            stableSlotAcquisitionTimeout: TimeSpan.FromSeconds(1)));

        Xunit.Assert.Contains("Verification: passed", output);
        var journal = GoalOperationJournal.Read(root, goal.Id);
        var passedOutcome = journal.Entries.LastOrDefault(entry => entry.AcceptanceOutcome == "passed");
        Xunit.Assert.NotNull(passedOutcome);
        Xunit.Assert.Equal(GoalOperationStatus.Completed, passedOutcome.Status);
        Xunit.Assert.False(string.IsNullOrWhiteSpace(passedOutcome.BranchHeadSha));
        Xunit.Assert.Equal(mainSha, passedOutcome.MainHeadSha, StringComparer.OrdinalIgnoreCase);
        Xunit.Assert.Equal(mainSha, passedOutcome.BaseBuildCacheMainSha, StringComparer.OrdinalIgnoreCase);
        Xunit.Assert.Equal(42, passedOutcome.BuildPhaseMilliseconds);
        Xunit.Assert.Equal("Core=hit,Infrastructure=miss,published", passedOutcome.BaseBuildCacheProjects);
        Xunit.Assert.Equal("Infrastructure", passedOutcome.BaseBuildCacheBuiltProjects);
        Xunit.Assert.Equal("none", passedOutcome.BaseBuildCacheEvictions);
        Xunit.Assert.Equal(
            $"base-build-cache main_sha={mainSha} build_phase_ms=42 projects=Core=hit,Infrastructure=miss,published built_projects=Infrastructure evictions=none",
            passedOutcome.BaseBuildCacheReceipt);
    }

    [Xunit.Fact(DisplayName = "Acceptance_journal_orders_current_pair_outcomes_newest_first")]
    public void AcceptanceJournalOrdersCurrentPairOutcomesNewestFirst()
    {
        var goalId = new GoalId("11112222333344445555666677778888");
        var older = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        var newer = DateTimeOffset.Parse("2026-07-20T12:05:00Z");
        var journal = new GoalOperationJournalSummary(
            "journal.jsonl",
            [
                new GoalOperationJournalEntry("legacy", goalId, "acceptance", GoalOperationStatus.Failed, newer.AddMinutes(1), "legacy", null, null, "failed"),
                new GoalOperationJournalEntry("old-pair", goalId, "acceptance", GoalOperationStatus.Failed, newer.AddMinutes(2), "old pair", "branch-old", "main-old", "failed"),
                new GoalOperationJournalEntry("current-older", goalId, "acceptance", GoalOperationStatus.Failed, older, "older current", "branch-current", "main-current", "failed"),
                new GoalOperationJournalEntry("current-newer", goalId, "acceptance", GoalOperationStatus.Completed, newer, "newer current", "branch-current", "main-current", "passed")
            ],
            [],
            []);

        var outcomes = GoalOperationJournal.AcceptanceOutcomesForCandidate(
            journal,
            "branch-current",
            "main-current");

        Xunit.Assert.Collection(
            outcomes,
            first => Xunit.Assert.Equal("passed", first.AcceptanceOutcome),
            second => Xunit.Assert.Equal("failed", second.AcceptanceOutcome));
    }

    [Xunit.Fact(DisplayName = "Acceptance_outcomes_for_candidate_prefer_later_append_at_same_timestamp")]
    public void AcceptanceOutcomesForCandidatePreferLaterAppendAtSameTimestamp()
    {
        var goalId = new GoalId("99992222333344445555666677778888");
        var at = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        var journal = new GoalOperationJournalSummary(
            "journal.jsonl",
            [
                new GoalOperationJournalEntry("failed", goalId, "acceptance", GoalOperationStatus.Failed, at, "failed", "branch", "main", "failed"),
                new GoalOperationJournalEntry("gate-passed", goalId, "acceptance", GoalOperationStatus.Completed, at, "gate passed", "branch", "main", "gate-passed")
            ],
            [],
            []);

        var outcomes = GoalOperationJournal.AcceptanceOutcomesForCandidate(
            journal,
            "branch",
            "main");

        Xunit.Assert.Equal("gate-passed", outcomes[0].AcceptanceOutcome);
        Xunit.Assert.Equal("failed", outcomes[1].AcceptanceOutcome);
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_current_green_rerun_supersedes_same_candidate_failure")]
    public void CliAcceptanceCurrentGreenRerunSupersedesSameCandidateFailure()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Retry same acceptance candidate", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-07-06T15:00:00Z")));
        var mainSha = RunGitOutput(root, "rev-parse", "HEAD").Trim();
        var worktree = CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
        var branchSha = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
        kernel.RecordAcceptanceFailure(goal.Id, ["interrupted gate"], branchSha, mainSha);
        GoalOperationJournal.AcceptanceFailed(
            root,
            goal,
            "acceptance",
            branchSha,
            mainSha,
            "Interrupted gate failed for this candidate.");

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["acceptance", "--keep-workspace", "--no-record"],
            kernel,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal,
            acceptanceVerifier: new ProbeAcceptanceVerifier(() => { }),
            phaseTimings: new CliPhaseTimingRecorder("acceptance"),
            stableSlotAcquisitionTimeout: TimeSpan.FromSeconds(1)));

        Xunit.Assert.Contains("Verification: passed", output);
        Xunit.Assert.DoesNotContain("Acceptance evidence: blocked", output);
        Xunit.Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id)!.Status);
        var journal = GoalOperationJournal.Read(root, goal.Id);
        Xunit.Assert.Contains(journal.Entries, entry =>
            entry.AcceptanceOutcome == "gate-passed" &&
            entry.HasCandidate(branchSha, mainSha));
        var outcomes = GoalOperationJournal.AcceptanceOutcomesForCandidate(
            journal,
            branchSha,
            mainSha);
        Xunit.Assert.Equal("passed", outcomes[0].AcceptanceOutcome);
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_treats_old_candidate_failure_as_historical_after_rebase")]
    public void CliAcceptanceTreatsOldCandidateFailureAsHistoricalAfterRebase()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Rebased acceptance", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-07-06T15:00:00Z")));
        var oldMain = RunGitOutput(root, "rev-parse", "HEAD").Trim();
        var worktree = CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
        var oldBranch = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
        kernel.RecordAcceptanceFailure(goal.Id, ["old acceptance failure"], oldBranch, oldMain);
        GoalOperationJournal.AcceptanceFailed(root, goal, "acceptance", oldBranch, oldMain, "old acceptance failure");
        File.WriteAllText(Path.Combine(root, "main-change.txt"), "main moved");
        RunGitOutput(root, "add", "main-change.txt");
        RunGitOutput(root, "commit", "-m", "Move main");

        var verifier = new ProbeAcceptanceVerifier(() => { });
        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["acceptance", "--keep-workspace", "--no-record"],
            kernel,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal,
            acceptanceVerifier: verifier,
            phaseTimings: new CliPhaseTimingRecorder("acceptance"),
            stableSlotAcquisitionTimeout: TimeSpan.FromSeconds(1)));

        Xunit.Assert.Contains("superseded failure is historical", output);
        Xunit.Assert.DoesNotContain("historical:", output);
        Xunit.Assert.Equal(1, verifier.RunCount);
        Xunit.Assert.Null(kernel.GetGoal(goal.Id)!.LatestAcceptanceFailure);
        var journal = GoalOperationJournal.Read(root, goal.Id);
        var currentOutcome = journal.Entries.Last(entry => entry.AcceptanceOutcome == "passed");
        Xunit.Assert.Equal(GoalOperationStatus.Completed, currentOutcome.Status);
        Xunit.Assert.NotEqual(oldBranch, currentOutcome.BranchHeadSha, StringComparer.OrdinalIgnoreCase);
        var superseded = GoalOperationJournal.SupersededAcceptanceOutcomes(
            journal,
            currentOutcome.BranchHeadSha,
            currentOutcome.MainHeadSha);
        Xunit.Assert.Contains(superseded, entry =>
            entry.AcceptanceOutcome == "failed" &&
            string.Equals(entry.BranchHeadSha, oldBranch, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.MainHeadSha, oldMain, StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "Monitor_lifecycle_facts_ignore_superseded_candidate_failure")]
    public void MonitorLifecycleFactsIgnoreSupersededCandidateFailure()
    {
        var (root, workspace, kernel, goal) = CreateSupersededAcceptanceFailureProjection();

        var facts = GoalMonitoringSubscriptionCommand.ReadLifecycleFacts(workspace, kernel.GetGoal(goal.Id));
        var lifecycle = GoalLifecycle.ResolveState(kernel.GetGoal(goal.Id), facts);

        Xunit.Assert.False(facts.IsBlocked);
        Xunit.Assert.Equal(GoalLifecycleState.Verified, lifecycle);
    }

    [Xunit.Fact(DisplayName = "Dashboard_acceptance_status_ignores_superseded_candidate_failure")]
    public void DashboardAcceptanceStatusIgnoresSupersededCandidateFailure()
    {
        var (root, workspace, kernel, goal) = CreateSupersededAcceptanceFailureProjection();
        var facts = GoalMonitoringSubscriptionCommand.ReadLifecycleFacts(workspace, kernel.GetGoal(goal.Id));
        var lifecycle = GoalLifecycle.ResolveState(kernel.GetGoal(goal.Id), facts);
        var summary = GoalAcceptanceStatusProjector.Build(kernel, kernel.GetGoal(goal.Id), root);
        var dto = DashboardResponseMapper.ToGoalAcceptanceSummaryDto(kernel.GetGoal(goal.Id), summary, lifecycle);

        Xunit.Assert.Equal(GoalStatus.Verified, dto.Status);
        Xunit.Assert.False(dto.IsAccepted);
        Xunit.Assert.DoesNotContain(dto.Blockers, blocker => blocker.Kind == GoalAcceptanceBlockerKind.AcceptanceFailed);
        var current = Xunit.Assert.Single(dto.Outcomes);
        Xunit.Assert.Equal("unverified (needs a gate run)", current.Outcome);
        Xunit.Assert.True(current.IsCurrentCandidate);
        Xunit.Assert.Equal("unverified (needs a gate run)", current.Message);
    }

    [Xunit.Fact(DisplayName = "Operator_inbox_ignores_superseded_candidate_failure")]
    public void OperatorInboxIgnoresSupersededCandidateFailure()
    {
        var (_, workspace, kernel, goal) = CreateSupersededAcceptanceFailureProjection();

        var inbox = OperatorInbox.Build(
            kernel,
            AgentCatalog.Default().Agents,
            WorkerProfileCatalog.Default(),
            workspace,
            goal.Id.Value[..8],
            includeAcknowledged: false);

        Xunit.Assert.DoesNotContain(inbox.Items, item =>
            item.Kind == OperatorInboxKind.AcceptanceGate &&
            (item.Message.Contains("old acceptance failure", StringComparison.OrdinalIgnoreCase) ||
             item.Title.Contains("blocked", StringComparison.OrdinalIgnoreCase)));
        Xunit.Assert.Contains(inbox.Items, item =>
            item.Kind == OperatorInboxKind.AcceptanceGate &&
            item.Title.Contains("ready for acceptance", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact]
    public void CliGoalTiming_CurrentHold_PrintsTimeInStatus()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement held work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Audit held time", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-12);
        kernel.ObserveGoalHold(
            goal.Id,
            "WorkspaceReady",
            "workspace is busy",
            startedAt,
            TimeSpan.FromMinutes(10));

        var output = ExecuteCliAndCapture(["goal-timing", goal.Id.Value[..8]], kernel, workspace);

        Xunit.Assert.Contains("Current status: held", output, StringComparison.Ordinal);
        Xunit.Assert.Contains($"since={startedAt:u}", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("blocker=workspace is busy", output, StringComparison.Ordinal);
    }

    private (
        string Root,
        OrchestratorWorkspace Workspace,
        AgentOrchestratorKernel Kernel,
        Goal Goal) CreateSupersededAcceptanceFailureProjection()
    {
        var root = CreateShortAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Rebased acceptance projection", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "manual",
            root,
            0,
            "ok",
            string.Empty,
            DateTimeOffset.UtcNow));
        var oldMain = RunGitOutput(root, "rev-parse", "HEAD").Trim();
        var worktree = CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
        var oldBranch = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
        kernel.RecordAcceptanceFailure(goal.Id, ["old acceptance failure"], oldBranch, oldMain);
        GoalOperationJournal.AcceptanceFailed(root, goal, "acceptance", oldBranch, oldMain, "old acceptance failure");
        File.WriteAllText(Path.Combine(root, "main-change.txt"), "main moved");
        RunGitOutput(root, "add", "main-change.txt");
        RunGitOutput(root, "commit", "-m", "Move main");

        return (root, workspace, kernel, goal);
    }
}
