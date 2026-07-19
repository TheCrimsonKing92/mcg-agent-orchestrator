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
public sealed class CliCommandTestsPersistentRunnerCommands : CliCommandTestBase
{
    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_backlog_show_loads_kernel_state_for_linked_goals")]
    public async Task PersistentRunnerBacklogShowLoadsKernelStateForLinkedGoals()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var backlogStore = new BacklogStore(workspace.BacklogStorePath);
        var item = await backlogStore.AddAsync("Persistent linked item");
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Persistent backlog-show linked goal", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        kernel.SetGoalSourceBacklogItemId(goal.Id, item.Id);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        await repository.SaveAsync(kernel);

        var output = CaptureConsole(() =>
        {
            var changed = CliPersistentStateRunner.ExecuteCommand(
                ["backlog-show", item.Id[..8]],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains("Linked goals:", output);
        Xunit.Assert.Contains(goal.Id.Value[..8], output);
        Xunit.Assert.Contains("landing=", output);
    }

    [Xunit.Fact(DisplayName = "Cli_startup_conduct_help_exits_before_workspace_setup")]
    public void CliStartupConductHelpExitsBeforeWorkspaceSetup()
    {
        var root = CreateTempDirectory();
        var beforeFiles = SnapshotFiles(root);

        var result = RunAppCommand(root, "conduct", "--help");

        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.Contains("Usage: conduct <goal-id-prefix>", result.Stdout);
        Xunit.Assert.Contains("-h, --help", result.Stdout);
        Xunit.Assert.DoesNotContain("Error:", result.Stderr);
        Xunit.Assert.Equal(beforeFiles, SnapshotFiles(root));
    }


    [Xunit.Fact(DisplayName = "Persistent_runner_park_goal_uses_real_sqlite_without_transaction_self_conflict")]
    public async Task PersistentRunnerParkGoalUsesRealSqliteWithoutTransactionSelfConflict()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Park from persistent runner", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        _ = kernel.RequestHumanInput(goal.Id, null, "Need operator decision.", HumanWaitKind.RiskReview);
        await repository.SaveAsync(kernel);

        var output = CaptureConsole(() =>
        {
            var changed = CliPersistentStateRunner.ExecuteCommand(
                CliArgumentParser.SplitCommand($"park-goal {goal.Id.Value[..8]} Operator froze churn. --confirm-goal-park"),
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });

        var restored = await repository.LoadAsync();
        var restoredGoal = restored.GetGoal(goal.Id);
        Xunit.Assert.Equal(GoalStatus.Parked, restoredGoal.Status);
        Xunit.Assert.All(
            restored.HumanInputRequests.Where(request => request.GoalId == goal.Id),
            request => Xunit.Assert.True(request.IsCompleted));
        Xunit.Assert.Contains("Goal parked", output);
        Xunit.Assert.Contains("Resolved human waits: 1", output);
    }


    [Xunit.Fact(DisplayName = "Persistent_runner_park_goal_text_file_records_file_reason")]
    public async Task PersistentRunnerParkGoalTextFileRecordsFileReason()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Park from file", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        var wait = kernel.RequestHumanInput(goal.Id, null, "Need operator decision.", HumanWaitKind.RiskReview);
        await repository.SaveAsync(kernel);
        var reason = "Operator parked from a text file.\n\nPreserve the full disposition receipt.";
        var reasonPath = Path.Combine(root, "park-reason.md");
        File.WriteAllText(reasonPath, reason, System.Text.Encoding.UTF8);

        var output = CaptureConsole(() =>
        {
            var changed = CliPersistentStateRunner.ExecuteCommand(
                CliArgumentParser.SplitCommand($"park-goal {goal.Id.Value[..8]} --text-file {reasonPath} --confirm-goal-park"),
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });

        var restored = await repository.LoadAsync();
        var restoredGoal = restored.GetGoal(goal.Id);
        var restoredWait = restored.HumanInputRequests.Single(request => request.Id == wait.Id);
        Xunit.Assert.Equal(GoalStatus.Parked, restoredGoal.Status);
        Xunit.Assert.Contains(restoredGoal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message == $"Goal parked: {reason}");
        Xunit.Assert.True(restoredWait.IsCompleted);
        Xunit.Assert.Equal($"Goal parked: {reason}", restoredWait.Answer);
        Xunit.Assert.Contains("Goal parked", output);
    }


    [Xunit.Fact(DisplayName = "Persistent_runner_unpark_goal_text_file_records_file_reason_and_exits_zero")]
    public async Task PersistentRunnerUnparkGoalTextFileRecordsFileReasonAndExitsZero()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Unpark from file", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.ParkGoal(goal.Id, "operator parked from test");
        await repository.SaveAsync(kernel);
        var reason = "Operator unparked from a text file.\n\nResume normal conductor handling.";
        var reasonPath = Path.Combine(root, "unpark-reason.md");
        File.WriteAllText(reasonPath, reason, System.Text.Encoding.UTF8);

        var result = RunAppCommand(root, "unpark-goal", goal.Id.Value[..8], "--text-file", reasonPath, "--confirm-goal-unpark");

        var restored = await repository.LoadAsync();
        var restoredGoal = restored.GetGoal(goal.Id);
        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.Contains("Goal unparked", result.Stdout);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        Xunit.Assert.Equal(GoalStatus.Active, restoredGoal.Status);
        Xunit.Assert.Contains(restoredGoal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message == $"Goal unparked: {reason}");
    }


    [Xunit.Fact(DisplayName = "Persistent_runner_lifecycle_disposition_audit_routes_side_effect_verbs_outside_generic_transaction")]
    public void PersistentRunnerLifecycleDispositionAuditRoutesSideEffectVerbsOutsideGenericTransaction()
    {
        Xunit.Assert.True(CliPersistentStateRunner.IsGoalLifecycleDispositionCommand(["park-goal", "abcdef12", "reason"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsGoalLifecycleDispositionCommand(["unpark-goal", "abcdef12", "reason"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsGoalLifecycleDispositionCommand(["abandon-goal", "abcdef12", "reason"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsGoalLifecycleDispositionCommand(["goal-mark-landed", "abcdef12", "--confirm-goal-mark-landed"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsGoalMarkLandedCommand(["goal-mark-landed", "abcdef12", "--confirm-goal-mark-landed"]));
        Xunit.Assert.DoesNotContain(
            CliArgumentParser.RecognizedCommands,
            command => command.Equals("resume-goal", StringComparison.OrdinalIgnoreCase));
    }


    [Xunit.Fact(DisplayName = "Persistent_runner_unpark_goal_rejects_non_parked_goals_with_nonzero_exit")]
    public async Task PersistentRunnerUnparkGoalRejectsNonParkedGoalsWithNonzeroExit()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var active = kernel.CreateGoal("Active unpark rejection", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        var completed = kernel.CreateGoal("Completed unpark rejection", [new TaskSpec(TaskId.New(), "Done work", AgentRole.Developer)]);
        var agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(active.Id, agents);
        kernel.ActivateGoal(completed.Id, agents);
        kernel = WithGoalStatus(kernel, completed.Id, GoalStatus.Completed);
        await repository.SaveAsync(kernel);

        var activeResult = RunAppCommand(root, "unpark-goal", active.Id.Value[..8], "resume", "--confirm-goal-unpark");
        var completedResult = RunAppCommand(root, "unpark-goal", completed.Id.Value[..8], "resume", "--confirm-goal-unpark");

        var restored = await repository.LoadAsync();
        Xunit.Assert.Equal(1, activeResult.ExitCode);
        Xunit.Assert.Equal(1, completedResult.ExitCode);
        Xunit.Assert.Contains("unpark-goal only applies to Parked goals", activeResult.Stderr);
        Xunit.Assert.Contains("is Active", activeResult.Stderr);
        Xunit.Assert.Contains("is Completed", completedResult.Stderr);
        Xunit.Assert.Equal(GoalStatus.Active, restored.GetGoal(active.Id).Status);
        Xunit.Assert.Equal(GoalStatus.Completed, restored.GetGoal(completed.Id).Status);
        Xunit.Assert.DoesNotContain(restored.GetGoal(active.Id).Timeline, evt => evt.Message.StartsWith("Goal unparked:", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(restored.GetGoal(completed.Id).Timeline, evt => evt.Message.StartsWith("Goal unparked:", StringComparison.Ordinal));
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_routes_bare_goals_to_metadata_only_listing")]
    public void RunnerRoutesBareGoalsToMetadataOnlyListing()
    {
        Xunit.Assert.True(CliPersistentStateRunner.IsMetadataOnlyListing(["goals"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsMetadataOnlyListing(["GOALS"]));
        // Anything beyond the bare verb must fall through to the normal (hydrating) path.
        Xunit.Assert.False(CliPersistentStateRunner.IsMetadataOnlyListing(["goals", "extra"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsMetadataOnlyListing(["next"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsMetadataOnlyListing([]));
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_routes_acceptance_workflows_outside_command_transaction")]
    public void RunnerRoutesAcceptanceWorkflowsOutsideCommandTransaction()
    {
        Xunit.Assert.True(CliPersistentStateRunner.IsAcceptanceCommand(["acceptance"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsAcceptanceCommand(["accept"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsAcceptanceCommand(["acceptance-queue", "--apply"]));

        Xunit.Assert.False(CliPersistentStateRunner.IsAcceptanceCommand(["next"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsAcceptanceCommand([]));
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_routes_single_goal_conduct_outside_command_transaction")]
    public void RunnerRoutesSingleGoalConductOutsideCommandTransaction()
    {
        Xunit.Assert.True(CliPersistentStateRunner.IsSingleGoalConductCommand(["conduct", "abc123"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsSingleGoalConductCommand(["CONDUCT", "abc123", "--policy", "Permissive"]));

        Xunit.Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand(["conduct", "--loop"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand(["conduct", "abc123", "--watch"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand(["conduct", "--help"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand(["conduct"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand(["next"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand([]));
    }


    [Xunit.Theory(DisplayName = "Cli_dispatcher_help_prints_command_specific_usage")]
    [Xunit.InlineData(new[] { "conduct", "--help" }, CliCommandHelp.ConductUsage)]
    [Xunit.InlineData(new[] { "conduct", "-h" }, CliCommandHelp.ConductUsage)]
    [Xunit.InlineData(new[] { "conduct", "--loop", "--help" }, CliCommandHelp.ConductUsage)]
    [Xunit.InlineData(new[] { "goals", "subscribe", "--help" }, GoalMonitoringSubscriptionCommand.GoalsSubscribeUsage)]
    [Xunit.InlineData(new[] { "help", "goals", "subscribe" }, GoalMonitoringSubscriptionCommand.GoalsSubscribeUsage)]
    [Xunit.InlineData(new[] { "reassign-agent", "--help" }, CliCommandHelp.ReassignAgentUsage)]
    [Xunit.InlineData(new[] { "workspace", "--help" }, CliCommandHelp.WorkspaceUsage)]
    [Xunit.InlineData(new[] { "workspace", "-h" }, CliCommandHelp.WorkspaceUsage)]
    [Xunit.InlineData(new[] { "workspace", "create", "-h" }, CliCommandHelp.WorkspaceCreateUsage)]
    public void CliDispatcherHelpPrintsCommandSpecificUsage(string[] args, string expectedUsage)
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                args,
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);

            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains(expectedUsage, output);
    }


    [Xunit.Theory(DisplayName = "Cli_startup_help_exits_before_state_repository_creation")]
    [Xunit.InlineData(new[] { "conduct", "--help" }, CliCommandHelp.ConductUsage)]
    [Xunit.InlineData(new[] { "conduct", "-h" }, CliCommandHelp.ConductUsage)]
    [Xunit.InlineData(new[] { "conduct", "--loop", "--help" }, CliCommandHelp.ConductUsage)]
    [Xunit.InlineData(new[] { "goals", "subscribe", "--help" }, GoalMonitoringSubscriptionCommand.GoalsSubscribeUsage)]
    [Xunit.InlineData(new[] { "help", "goals", "subscribe" }, GoalMonitoringSubscriptionCommand.GoalsSubscribeUsage)]
    [Xunit.InlineData(new[] { "reassign-agent", "--help" }, CliCommandHelp.ReassignAgentUsage)]
    [Xunit.InlineData(new[] { "workspace", "--help" }, CliCommandHelp.WorkspaceUsage)]
    [Xunit.InlineData(new[] { "workspace", "-h" }, CliCommandHelp.WorkspaceUsage)]
    [Xunit.InlineData(new[] { "workspace", "create", "-h" }, CliCommandHelp.WorkspaceCreateUsage)]
    public void CliStartupHelpExitsBeforeStateRepositoryCreation(string[] args, string expectedUsage)
    {
        var root = CreateTempDirectory();
        var result = RunAppCli(root, args);

        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.Contains(expectedUsage, result.StandardOutput);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(result.StandardError), result.StandardError);
        Xunit.Assert.False(File.Exists(Path.Combine(root, ".orchestrator", "state.db")));
        Xunit.Assert.False(Directory.Exists(Path.Combine(root, ".orchestrator")));
    }


    [Xunit.Fact(DisplayName = "Cli_startup_short_read_command_exits_within_two_seconds_and_releases_state")]
    public async Task CliStartupShortReadCommandExitsWithinTwoSecondsAndReleasesState()
    {
        var root = CreateTempDirectory();

        var result = await RunAppCliWithExitTimeout(root, ["next", "--full"], TimeSpan.FromSeconds(2));

        Xunit.Assert.True(
            result.ExitedWithinTimeout,
            $"CLI did not exit within 2 seconds. stdout: {result.StandardOutput} stderr: {result.StandardError}");
        Xunit.Assert.Equal(1, result.ExitCode);
        Xunit.Assert.Contains("Create a goal first", result.StandardError);

        var statePath = Path.Combine(root, ".orchestrator", "state.db");
        Xunit.Assert.True(File.Exists(statePath));
        using var stateLockProbe = File.Open(statePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Xunit.Assert.True(stateLockProbe.CanWrite);
    }


    [Xunit.Fact(DisplayName = "ConsoleViews_PrintGoals_renders_metadata_summaries")]
    public void PrintGoalsRendersMetadataSummaries()
    {
        IReadOnlyList<GoalSummary> summaries =
        [
            new GoalSummary("0123456789abcdef0123456789abcdef", "Active", "Build the widget", "2026-06-15T00:00:00.0000000+00:00"),
            new GoalSummary("fedcba98", "Completed", "Ship the gadget", "2026-06-14T00:00:00.0000000+00:00")
        ];

        var output = CaptureConsole(() => ConsoleViews.PrintGoals(summaries));

        Xunit.Assert.Contains("01234567 Active: Build the widget", output);
        Xunit.Assert.Contains("fedcba98 Completed: Ship the gadget", output);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_acceptance_ignores_volatile_snapshot_churn")]
    public void PersistentRunnerAcceptanceIgnoresVolatileSnapshotChurn()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Land despite volatile metadata churn", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-06-25T15:00:00Z")));
        var worktree = CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var fullSnapshotChanged = false;
        var stableProjectionChanged = true;
        repository.BeforeNextTransaction = stored =>
        {
            var before = stored.ExportSnapshot().Goals.Single(candidate => candidate.Id == goal.Id.Value);
            stored.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed again.", root, DateTimeOffset.Parse("2026-06-25T15:01:00Z")));
            var after = stored.ExportSnapshot().Goals.Single(candidate => candidate.Id == goal.Id.Value);
            fullSnapshotChanged = JsonSerializer.Serialize(before) != JsonSerializer.Serialize(after);
            stableProjectionChanged = BuildTaskStatusProjectionJson(before) != BuildTaskStatusProjectionJson(after);
        };

        CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["acceptance", "--skip-verify", "--keep-workspace"],
            repository,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.True(fullSnapshotChanged);
        Xunit.Assert.False(stableProjectionChanged);
        Xunit.Assert.Equal("main", RunGitOutput(root, "branch", "--show-current").Trim());
        Xunit.Assert.Equal("goal work", File.ReadAllText(Path.Combine(root, "feature.txt")));
        Xunit.Assert.True(Directory.Exists(worktree));
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_acceptance_verifier_runs_outside_state_write_transaction")]
    public void PersistentRunnerAcceptanceVerifierRunsOutsideStateWriteTransaction()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Verify outside transaction", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-06-25T15:00:00Z")));
        CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var verifierObservedUnlockedState = false;
        var verifier = new ProbeAcceptanceVerifier(() =>
        {
            Xunit.Assert.False(repository.IsInTransaction);
            verifierObservedUnlockedState = true;
        });

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["acceptance", "--keep-workspace"],
            repository,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal,
            acceptanceVerifier: verifier));

        Xunit.Assert.True(verifierObservedUnlockedState);
        Xunit.Assert.Equal(1, verifier.RunCount);
        Xunit.Assert.Equal(1, repository.TransactionCount);
        Xunit.Assert.Equal(0, repository.LoadCount);
        Xunit.Assert.Equal(1, repository.LoadGoalCount);
        Xunit.Assert.Equal([goal.Id.Value], repository.LoadedGoalIds);
        Xunit.Assert.False(repository.IsInTransaction);
        Xunit.Assert.Equal("goal work", File.ReadAllText(Path.Combine(root, "feature.txt")));
        Xunit.Assert.Contains("PHASE_TIMING command=acceptance phase=startup-goal-resolve", output);
        Xunit.Assert.Contains("PHASE_TIMING command=acceptance phase=startup-load-target-goal", output);
        Xunit.Assert.Contains("PHASE_TIMING command=acceptance phase=reconcile-sweep", output);
        Xunit.Assert.Contains("PHASE_TIMING command=acceptance phase=workspace-rebase", output);
        Xunit.Assert.Contains("PHASE_TIMING command=acceptance phase=verification-suite", output);
        Xunit.Assert.Contains("PHASE_TIMING command=acceptance phase=verification-check", output);
        Xunit.Assert.Contains("PHASE_TIMING command=acceptance phase=workspace-merge", output);
        Xunit.Assert.Matches(@"PHASE_TIMING command=acceptance phase=verification-check elapsedMs=\d+ .*name=""probe verifier""", output);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_acceptance_target_scoped_reconcile_preserves_target_dispatch_refresh")]
    public async Task PersistentRunnerAcceptanceTargetScopedReconcilePreservesTargetDispatchRefresh()
    {
        var root = CreateShortAcceptanceRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var currentTask = new TaskSpec(TaskId.New(), "Current work", AgentRole.Developer);
            var targetTask = new TaskSpec(TaskId.New(), "Target work", AgentRole.Developer);
            var current = kernel.CreateGoal("Current acceptance context", [currentTask]);
            var target = kernel.CreateGoal("Target acceptance context", [targetTask]);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = current;
            kernel.ActivateGoal(current.Id, agents);
            kernel.ActivateGoal(target.Id, agents);
            RecordRunningProcess(kernel, target, targetTask, root);
            File.WriteAllText(targetTask.LastProcess!.ExitCodePath, "0");
            File.WriteAllText(targetTask.LastProcess.StandardOutputPath, "done");
            var repository = new InMemoryTransactionalStateRepository(kernel);

            var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["acceptance", target.Id.Value[..8], "--skip-verify", "--keep-workspace", "--no-record"],
                repository,
                CreateRefinedWorkspace(root),
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Contains("PHASE_TIMING command=acceptance phase=reconcile-sweep", output);
            Xunit.Assert.Contains("mode=target-scoped-fast-path", output);
            Xunit.Assert.Contains("goalsWalked=1", output);
            Xunit.Assert.Equal(0, repository.LoadCount);
            Xunit.Assert.Equal(1, repository.LoadGoalCount);
            Xunit.Assert.Contains(target.Id.Value, repository.LoadedGoalIds);
            Xunit.Assert.DoesNotContain(current.Id.Value, repository.LoadedGoalIds);
            Xunit.Assert.Equal(target.Id, currentGoal!.Id);

            var currentSnapshot = await repository.LoadGoalAsync(current.Id);
            var targetSnapshot = await repository.LoadGoalAsync(target.Id);
            var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([currentSnapshot!, targetSnapshot!], []));
            Xunit.Assert.Equal(WorkTaskStatus.Assigned, restored.GetTask(current.Id, currentTask.Id).Status);
            var restoredTargetTask = restored.GetTask(target.Id, targetTask.Id);
            Xunit.Assert.Equal(WorkTaskStatus.Completed, restoredTargetTask.Status);
            Xunit.Assert.Equal(0, restoredTargetTask.LastProcess!.ExitCode);
            Xunit.Assert.NotNull(restoredTargetTask.LastVerification);
        }
        finally
        {
            CleanupAcceptanceRepository(root, null);
        }
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_acceptance_final_state_is_persisted_by_goal_cas")]
    public async Task PersistentRunnerAcceptanceFinalStateIsPersistedByGoalCas()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
            var goal = kernel.CreateGoal("Persist acceptance final state with CAS", [task]);
            cleanupGoalId = goal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;
            kernel.ActivateGoal(goal.Id, agents);
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-06-25T15:00:00Z")));
            kernel.RecordAcceptanceFailure(goal.Id, ["previous acceptance failure"]);
            CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
            var repository = new InMemoryTransactionalStateRepository(kernel);

            var changed = false;
            var output = CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
                ["acceptance", "--skip-verify", "--keep-workspace"],
                repository,
                CreateRefinedWorkspace(root),
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Equal(1, repository.TransactionCount);
            Xunit.Assert.Equal(2, repository.SaveGoalSnapshotsCount);
            Xunit.Assert.False(changed);
            Xunit.Assert.Contains("Acceptance evidence bundle: passed", output);
            Xunit.Assert.Contains($"Goal {goal.Id.Value[..8]} acceptance: accepted", output);
            var storedGoal = (await repository.LoadAsync()).GetGoal(goal.Id);
            Xunit.Assert.Null(storedGoal.LatestAcceptanceFailure);
            Xunit.Assert.Equal("goal work", File.ReadAllText(Path.Combine(root, "feature.txt")));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_acceptance_journals_backlog_close_noop_and_persists_missing_warning")]
    public async Task PersistentRunnerAcceptanceJournalsBacklogCloseNoopAndPersistsMissingWarning()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var store = new BacklogStore(workspace.BacklogStorePath);
            var closedItem = await store.AddAsync("Already closed linked item");
            await store.CloseAsync(closedItem.Id);

            var closedKernel = new AgentOrchestratorKernel();
            var closedTask = new TaskSpec(TaskId.New(), "Implement closed-item landing", AgentRole.Developer);
            var closedGoal = closedKernel.CreateGoal("Land with already closed source item", [closedTask]);
            cleanupGoalId = closedGoal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = closedGoal;
            closedKernel.ActivateGoal(closedGoal.Id, agents);
            closedKernel.RecordTaskVerification(
                closedGoal.Id,
                closedTask.Id,
                ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-06-25T15:00:00Z")));
            closedKernel.SetGoalSourceBacklogItemId(closedGoal.Id, closedItem.Id);
            CommitGoalWork(root, closedGoal.Id, "closed-item.txt", "goal work");
            var closedRepository = new InMemoryTransactionalStateRepository(closedKernel);

            var closedOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["acceptance", "--skip-verify", "--keep-workspace", "--no-record"],
                closedRepository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Contains("already closed", closedOutput, StringComparison.OrdinalIgnoreCase);
            var closeJournal = GoalOperationJournal.Read(root, closedGoal.Id);
            Xunit.Assert.Contains(closeJournal.Entries, entry =>
                entry.Operation == "conductor:backlog-close" &&
                entry.Status == GoalOperationStatus.Completed &&
                entry.Detail.Contains("No linked source backlog item closed.", StringComparison.Ordinal));

            CleanupAcceptanceRepository(root, cleanupGoalId);
            cleanupGoalId = null;
            root = CreateShortAcceptanceRepository();
            workspace = CreateRefinedWorkspace(root);

            var missingKernel = new AgentOrchestratorKernel();
            var missingTask = new TaskSpec(TaskId.New(), "Implement missing-item landing", AgentRole.Developer);
            var missingGoal = missingKernel.CreateGoal("Land with missing source item", [missingTask]);
            cleanupGoalId = missingGoal.Id;
            agents = AgentCatalog.Default().Agents;
            profiles = WorkerProfileCatalog.Default();
            currentGoal = missingGoal;
            missingKernel.ActivateGoal(missingGoal.Id, agents);
            missingKernel.RecordTaskVerification(
                missingGoal.Id,
                missingTask.Id,
                ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-06-25T15:00:00Z")));
            missingKernel.SetGoalSourceBacklogItemId(missingGoal.Id, "deadbeefdeadbeefdeadbeefdeadbeef");
            CommitGoalWork(root, missingGoal.Id, "missing-item.txt", "goal work");
            var missingRepository = new InMemoryTransactionalStateRepository(missingKernel);

            var missingOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["acceptance", "--skip-verify", "--keep-workspace", "--no-record"],
                missingRepository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Contains("not found", missingOutput, StringComparison.OrdinalIgnoreCase);
            var restoredGoal = (await missingRepository.LoadAsync()).GetGoal(missingGoal.Id);
            Xunit.Assert.Contains(restoredGoal.Timeline, entry =>
                entry.Message.Contains("linked backlog item deadbeefdeadbeefdeadbeefdeadbeef was not found", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_acceptance_blocks_task_status_change")]
    public void PersistentRunnerAcceptanceBlocksTaskStatusChange()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Block stale task state", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-06-25T15:00:00Z")));
        CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        repository.BeforeNextTransaction = stored =>
            stored.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Task moved during acceptance.");

        InvalidOperationException? caught = null;
        var output = CaptureConsole(() =>
        {
            try
            {
                CliPersistentStateRunner.ExecuteCommand(
                    ["acceptance", "--skip-verify", "--keep-workspace"],
                    repository,
                    CreateRefinedWorkspace(root),
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);
            }
            catch (InvalidOperationException ex)
            {
                caught = ex;
            }
        });

        Xunit.Assert.NotNull(caught);
        Xunit.Assert.Contains("changed during acceptance verification", caught!.Message);
        Xunit.Assert.Contains("BLOCKER step=acceptance-state-guard", output);
        Xunit.Assert.Contains("state changed during acceptance verification", output);
        Xunit.Assert.Contains($"Goal {goal.Id.Value[..8]} acceptance: not accepted", output);
        var storedGoal = repository.LoadAsync().GetAwaiter().GetResult().GetGoal(goal.Id);
        Xunit.Assert.NotNull(storedGoal.LatestAcceptanceFailure);
        Xunit.Assert.Contains(storedGoal.LatestAcceptanceFailure.FailedChecks, check =>
            check.Contains("state changed during acceptance verification", StringComparison.Ordinal));
        var acceptance = repository.LoadAsync().GetAwaiter().GetResult().BuildGoalAcceptanceSummary(goal.Id);
        Xunit.Assert.Contains(acceptance.Blockers, blocker =>
            blocker.Kind == GoalAcceptanceBlockerKind.AcceptanceFailed &&
            blocker.Message.Contains("state changed during acceptance verification", StringComparison.Ordinal));
        Xunit.Assert.Equal("main", RunGitOutput(root, "branch", "--show-current").Trim());
        Xunit.Assert.False(File.Exists(Path.Combine(root, "feature.txt")));
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_recover_reconciles_terminal_running_exit_file")]
    public async Task PersistentRunnerRecoverReconcilesTerminalRunningExitFile()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("No implicit reconcile", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
        goal = kernel.GetGoal(goal.Id);
        currentGoal = goal;
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, "done");
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var changed = false;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["recover", goal.Id.Value[..8], "operator note"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restoredTask = (await repository.LoadAsync()).GetTask(goal.Id, task.Id);
        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, restoredTask.Status);
        Xunit.Assert.Equal(0, restoredTask.LastProcess!.ExitCode);
        Xunit.Assert.NotNull(restoredTask.LastVerification);
        Xunit.Assert.Equal(1, repository.TransactionCount);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_next_persists_terminal_sweep_repairs")]
    public async Task PersistentRunnerNextPersistsTerminalSweepRepairs()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Premature completion", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
        currentGoal = kernel.GetGoal(goal.Id);
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var changed = false;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["next", goal.Id.Value[..8]],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restored = (await repository.LoadAsync()).GetGoal(goal.Id);
        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(GoalStatus.Active, restored.Status);
        Xunit.Assert.Equal(0, repository.TransactionCount);
        Xunit.Assert.Equal(1, repository.LoadGoalCount);
        Xunit.Assert.Equal(1, repository.SaveGoalSnapshotsCount);
        Xunit.Assert.Equal([goal.Id.Value], repository.LoadedGoalIds);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_reconcile_applies_exit_file_outside_command_transaction")]
    public async Task PersistentRunnerReconcileAppliesExitFileOutsideCommandTransaction()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Explicit reconcile", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, "done");
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var changed = false;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["reconcile"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restoredTask = (await repository.LoadAsync()).GetTask(goal.Id, task.Id);
        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, restoredTask.Status);
        Xunit.Assert.Equal(0, restoredTask.LastProcess!.ExitCode);
        Xunit.Assert.NotNull(restoredTask.LastVerification);
        Xunit.Assert.Equal(1, repository.TransactionCount);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_refresh_dispatch_uses_goal_scoped_state")]
    public async Task PersistentRunnerRefreshDispatchUsesGoalScopedState()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Explicit refresh", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, "done");
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var changed = false;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["refresh-dispatch", goal.Id.Value[..8], "1"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(0, repository.LoadCount);
        Xunit.Assert.Equal(1, repository.LoadGoalCount);
        Xunit.Assert.Equal(1, repository.TransactionCount);
        Xunit.Assert.Equal(1, repository.SaveGoalSnapshotsCount);
        Xunit.Assert.Equal([goal.Id.Value], repository.LoadedGoalIds);

        var restoredSnapshot = await repository.LoadGoalAsync(goal.Id);
        var restoredTask = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([restoredSnapshot!], [])).GetTask(goal.Id, task.Id);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, restoredTask.Status);
        Xunit.Assert.Equal(0, restoredTask.LastProcess!.ExitCode);
        Xunit.Assert.NotNull(restoredTask.LastVerification);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_refresh_dispatches_goal_flag_uses_requested_goal_scoped_state")]
    public async Task PersistentRunnerRefreshDispatchesGoalFlagUsesRequestedGoalScopedState()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var currentTask = new TaskSpec(TaskId.New(), "Current work", AgentRole.Developer);
        var targetTask = new TaskSpec(TaskId.New(), "Target work", AgentRole.Developer);
        var current = kernel.CreateGoal("Current refresh", [currentTask]);
        var target = kernel.CreateGoal("Target refresh", [targetTask]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = current;
        kernel.ActivateGoal(current.Id, agents);
        kernel.ActivateGoal(target.Id, agents);
        RecordRunningProcess(kernel, target, targetTask, root);
        File.WriteAllText(targetTask.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(targetTask.LastProcess.StandardOutputPath, "done");
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var changed = false;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["refresh-dispatches", "--goal", target.Id.Value[..8]],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(0, repository.LoadCount);
        Xunit.Assert.Equal(1, repository.LoadGoalCount);
        Xunit.Assert.Equal(1, repository.TransactionCount);
        Xunit.Assert.Equal(1, repository.SaveGoalSnapshotsCount);
        Xunit.Assert.Equal([target.Id.Value], repository.LoadedGoalIds);
        Xunit.Assert.Equal(target.Id, currentGoal!.Id);

        var currentSnapshot = await repository.LoadGoalAsync(current.Id);
        var targetSnapshot = await repository.LoadGoalAsync(target.Id);
        var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([currentSnapshot!, targetSnapshot!], []));
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, restored.GetTask(current.Id, currentTask.Id).Status);
        var restoredTargetTask = restored.GetTask(target.Id, targetTask.Id);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, restoredTargetTask.Status);
        Xunit.Assert.Equal(0, restoredTargetTask.LastProcess!.ExitCode);
        Xunit.Assert.NotNull(restoredTargetTask.LastVerification);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_reconcile_discards_stale_process_identity")]
    public async Task PersistentRunnerReconcileDiscardsStaleProcessIdentity()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Stale reconcile", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, "done");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        repository.BeforeNextTransaction = stored =>
        {
            var current = stored.GetTask(goal.Id, task.Id);
            var replacement = current.LastProcess! with
            {
                ProcessId = 424242,
                StartedAt = current.LastProcess.StartedAt.AddSeconds(1),
                ExitCodePath = current.LastProcess.ExitCodePath + ".next"
            };
            stored.RecordTaskProcessRefreshed(goal.Id, task.Id, replacement, null);
        };

        var changed = false;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["reconcile"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restoredTask = (await repository.LoadAsync()).GetTask(goal.Id, task.Id);
        Xunit.Assert.False(changed);
        Xunit.Assert.Equal(WorkTaskStatus.Running, restoredTask.Status);
        Xunit.Assert.Equal(424242, restoredTask.LastProcess!.ProcessId);
        Xunit.Assert.Null(restoredTask.LastProcess.ExitCode);
        Xunit.Assert.Equal(1, repository.TransactionCount);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_excludes_terminal_goals_from_hydration")]
    public void PersistentRunnerConductLoopExcludesTerminalGoalsFromHydration()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var terminalGoalIds = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var completed = kernel.CreateGoal($"Completed audit goal {i}", [new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)]);
            kernel.ActivateGoal(completed.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(completed.Id, completed.Tasks.Single().Id,
                new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            kernel = WithGoalStatus(kernel, completed.Id, GoalStatus.Completed);
            terminalGoalIds.Add(completed.Id.Value);
        }

        var cleanedUp = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Cleaned-up audit goal");
        var cancelled = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Cancelled audit goal");
        var superseded = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Superseded audit goal");
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Active conductor goal");
        var failed = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Failed conductor goal");
        kernel.ReportTaskProgress(failed.Id, failed.Tasks.Single().Id, WorkTaskStatus.Failed, "Still needs conductor/operator attention");
        kernel = WithGoalStatus(kernel, cancelled.Id, GoalStatus.Cancelled);
        kernel = WithGoalStatus(kernel, superseded.Id, GoalStatus.Superseded);
        var repository = new InMemoryTransactionalStateRepository(kernel);
        repository.CleanedUpGoalIds.Add(cleanedUp.Id.Value);
        terminalGoalIds.AddRange([cleanedUp.Id.Value, cancelled.Id.Value, superseded.Id.Value]);

        var loaded = CliPersistentStateRunner.LoadConductLoopKernel(repository);

        Xunit.Assert.Equal(0, repository.LoadCount);
        Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        Xunit.Assert.All(terminalGoalIds, id => Xunit.Assert.DoesNotContain(id, repository.LoadedGoalIds));
        Xunit.Assert.Contains(active.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.Contains(failed.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.All(terminalGoalIds, id => Xunit.Assert.DoesNotContain(loaded.Goals, goal => goal.Id.Value == id));
        Xunit.Assert.Contains(loaded.Goals, goal => goal.Id == active.Id);
        Xunit.Assert.Contains(loaded.Goals, goal => goal.Id == failed.Id);
        Xunit.Assert.All(terminalGoalIds, id => Xunit.Assert.True(loaded.IsKnownCompletedDependencyGoal(new GoalId(id))));
        var expectedLoadedIds = new[] { active.Id.Value, failed.Id.Value }
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Xunit.Assert.Equal(
            expectedLoadedIds,
            repository.LoadGoalBatches.Single().OrderBy(id => id, StringComparer.Ordinal).ToArray());
        var terminalSnapshot = repository.LoadGoalAsync(new GoalId(terminalGoalIds[0])).GetAwaiter().GetResult();
        Xunit.Assert.NotNull(terminalSnapshot);
        Xunit.Assert.Equal(terminalGoalIds[0], terminalSnapshot.Id);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_sweeps_terminal_candidates_loaded_on_demand")]
    public void PersistentRunnerConductLoopSweepsTerminalCandidatesLoadedOnDemand()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement terminal cleanup", AgentRole.Developer);
            var goal = kernel.CreateGoal("Terminal cleanup candidate", [task]);
            cleanupGoalId = goal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            kernel.ActivateGoal(goal.Id, agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/terminal-cleanup.txt", "goal work");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
            var repository = new InMemoryTransactionalStateRepository(kernel);

            var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["conduct", "--loop", "--max-iterations", "0"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            var restored = repository.LoadGoalAsync(goal.Id).GetAwaiter().GetResult()!;
            Xunit.Assert.Equal(0, repository.LoadCount);
            Xunit.Assert.True(repository.LoadGoalsCount >= 2);
            Xunit.Assert.Contains(goal.Id.Value, repository.LoadedGoalIds);
            Xunit.Assert.Equal(GoalStatus.Verified, restored.Status);
            Xunit.Assert.Contains("completed-branch-normalized", output, StringComparison.Ordinal);
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_timeline_loads_terminal_goal_on_demand")]
    public void PersistentRunnerTimelineLoadsTerminalGoalOnDemand()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var completed = kernel.CreateGoal("Completed timeline goal", [new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)]);
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Active goal");
        kernel.ActivateGoal(completed.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskVerification(completed.Id, completed.Tasks.Single().Id,
            new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        kernel = WithGoalStatus(kernel, completed.Id, GoalStatus.Completed);
        var repository = new InMemoryTransactionalStateRepository(kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["timeline", completed.Id.Value[..8]],
            repository,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Equal(0, repository.LoadCount);
        Xunit.Assert.Equal(1, repository.LoadGoalCount);
        Xunit.Assert.Contains(completed.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.DoesNotContain(active.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.Equal(completed.Id, currentGoal!.Id);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_single_goal_reports_load_terminal_goal_on_demand")]
    public void PersistentRunnerSingleGoalReportsLoadTerminalGoalOnDemand()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        IReadOnlyList<Func<string, string[]>> commands =
        [
            prefix => ["status", prefix],
            prefix => ["monitor", prefix],
            prefix => ["readiness", prefix],
            prefix => ["next", prefix],
            prefix => ["next", prefix, "--full"],
            prefix => ["evidence", prefix],
            prefix => ["stages", prefix],
            prefix => ["gates", prefix],
            prefix => ["verify-needed", prefix],
            prefix => ["input-needed", prefix],
            prefix => ["goal-diagnostics", prefix],
            prefix => ["subscription-plan", prefix],
            prefix => ["failure-triage", prefix],
            prefix => ["retention-plan", prefix]
        ];

        foreach (var command in commands)
        {
            var kernel = new AgentOrchestratorKernel();
            var completed = kernel.CreateGoal("Completed report goal", [new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)]);
            var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Active report bystander");
            kernel.ActivateGoal(completed.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(completed.Id, completed.Tasks.Single().Id,
                new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            kernel = WithGoalStatus(kernel, completed.Id, GoalStatus.Completed);
            var repository = new InMemoryTransactionalStateRepository(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;

            CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                command(completed.Id.Value[..8]),
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Equal(0, repository.LoadCount);
            Xunit.Assert.Equal(1, repository.LoadGoalCount);
            Xunit.Assert.Contains(completed.Id.Value, repository.LoadedGoalIds);
            Xunit.Assert.DoesNotContain(active.Id.Value, repository.LoadedGoalIds);
            Xunit.Assert.Equal(completed.Id, currentGoal!.Id);
        }
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_provenance_loads_completed_goals_on_demand")]
    public void PersistentRunnerProvenanceLoadsCompletedGoalsOnDemand()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var completed = kernel.CreateGoal("Backed completed goal", [new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)]);
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Active provenance bystander");
        kernel.ActivateGoal(completed.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskVerification(completed.Id, completed.Tasks.Single().Id,
            new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        kernel = WithGoalStatus(kernel, completed.Id, GoalStatus.Completed);
        var repository = new InMemoryTransactionalStateRepository(kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = active;

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["provenance"],
            repository,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Equal(0, repository.LoadCount);
        Xunit.Assert.Equal(1, repository.LoadGoalCount);
        Xunit.Assert.Contains(completed.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.DoesNotContain(active.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.Contains("BACKED", output, StringComparison.Ordinal);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_tick_merge_skip_formats_receipt")]
    public void PersistentRunnerTickMergeSkipFormatsReceipt()
    {
        var receipt = CliPersistentStateRunner.FormatTickMergeReceipt(new GoalSnapshotSaveResult(
            "abcdef123456",
            GoalSnapshotSaveDisposition.Skipped,
            null,
            "stored goal no longer contains task 12345678 changed by tick"));

        Xunit.Assert.Equal(
            "TICK_MERGE goal=abcdef12 disposition=SKIPPED stored goal no longer contains task 12345678 changed by tick",
            receipt);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_preserves_terminal_dependency_readiness_without_loading_dependency")]
    public void PersistentRunnerConductLoopPreservesTerminalDependencyReadinessWithoutLoadingDependency()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var completed = kernel.CreateGoal("Completed dependency", [new TaskSpec(TaskId.New(), "Done", AgentRole.Planner)]);
        var active = kernel.CreateGoal("Ready dependent goal", [new TaskSpec(TaskId.New(), "Plan src/Ready.cs", AgentRole.Planner)]);
        var agents = new[] { SubscriptionPlanner("codex-cli", "Planner Codex") };
        kernel.ActivateGoal(completed.Id, agents);
        kernel.ActivateGoal(active.Id, agents);
        kernel.RecordTaskVerification(completed.Id, completed.Tasks.Single().Id,
            new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        kernel = WithGoalStatus(kernel, completed.Id, GoalStatus.Completed);
        completed = kernel.GetGoal(completed.Id);
        kernel.SetGoalDependency(active.Id, completed.Id);
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var loaded = CliPersistentStateRunner.LoadConductLoopKernel(repository);
        var plan = CrossGoalSubscriptionStartPlanner.Build(loaded, agents, WorkerProfileCatalog.Default());

        Xunit.Assert.DoesNotContain(completed.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.Contains(active.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.Contains(loaded.Goals, goal => goal.Id == active.Id);
        Xunit.Assert.DoesNotContain(loaded.Goals, goal => goal.Id == completed.Id);
        Xunit.Assert.True(loaded.IsKnownCompletedDependencyGoal(completed.Id));
        Xunit.Assert.Single(plan.Candidates);
        Xunit.Assert.Contains(active.Id.Value, plan.FirstBatchCandidates.Select(candidate => candidate.GoalId));
        Xunit.Assert.DoesNotContain(plan.ParallelPlan.Decisions.SelectMany(decision => decision.Reasons),
            reason => reason.Equals("dependency could not be scheduled", StringComparison.OrdinalIgnoreCase));
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_mark_landed_carries_prompt_budget_through_state_commit")]
    public void PersistentRunnerGoalMarkLandedCarriesPromptBudgetThroughStateCommit()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Developer);
            var goal = kernel.CreateGoal("Already landed persistent cleanup", [task]);
            cleanupGoalId = goal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;
            kernel.ActivateGoal(goal.Id, agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "dotnet test", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/landed.txt", "goal work");
            RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));
            var repository = new InMemoryTransactionalStateRepository(kernel);

            var output = CaptureConsole(() =>
            {
                var changed = CliPersistentStateRunner.ExecuteCommand(
                    ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed"],
                    repository,
                    CreateRefinedWorkspace(root),
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);
                Xunit.Assert.True(changed);
            });

            Xunit.Assert.Equal(0, repository.TransactionCount);
            Xunit.Assert.Contains("Workspace cleanup deferred", output);
            Xunit.Assert.NotNull(GoalWorktrees.TryResolve(root, goal.Id));
            Xunit.Assert.Contains(GoalWorktrees.BranchName(goal.Id), RunGitOutput(root, "branch", "--list", GoalWorktrees.BranchName(goal.Id)), StringComparison.Ordinal);
            Xunit.Assert.NotNull(GoalWorktrees.TryGetCleanupBackoff(root, goal.Id));
            var cleanupEntry = GoalOperationJournal.Read(root, goal.Id).LatestByOperation.FirstOrDefault(e =>
                e.Operation == "conductor:cleanup" && e.Status == GoalOperationStatus.Failed);
            Xunit.Assert.NotNull(cleanupEntry);
            Xunit.Assert.Contains("Deferred cleanup after goal-mark-landed", cleanupEntry.Detail, StringComparison.Ordinal);
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_mark_landed_returns_success_after_durable_deferred_cleanup")]
    public void PersistentRunnerGoalMarkLandedReturnsSuccessAfterDurableDeferredCleanup()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Developer);
            var goal = kernel.CreateGoal("Already landed deferred cleanup", [task]);
            cleanupGoalId = goal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;
            kernel.ActivateGoal(goal.Id, agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "dotnet test", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/landed.txt", "goal work");
            RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));
            GoalOperationJournal.Completed(root, goal, "conductor:land", "landed");
            GoalOperationJournal.Completed(root, goal, "conductor:record", "recorded");
            GoalOperationJournal.Failed(root, goal, "conductor:cleanup", "Deferred cleanup after landing: cleanup-needed");
            GoalWorktrees.RecordGoalCleanupNeeded(root, goal.Id, "remove:cleanup-budget-exhausted");
            var repository = new InMemoryTransactionalStateRepository(kernel);

            var stderr = CaptureConsoleError(() =>
            {
                var output = CaptureConsole(() =>
                {
                    var changed = CliPersistentStateRunner.ExecuteCommand(
                        ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed"],
                        repository,
                        CreateRefinedWorkspace(root),
                        ref agents,
                        providers,
                        ref profiles,
                        ref currentGoal);
                    Xunit.Assert.True(changed);
                });
                Xunit.Assert.Contains("Workspace cleanup deferred", output);
            });

            Xunit.Assert.DoesNotContain("warning: goal-mark-landed state commit failed", stderr);
            Xunit.Assert.Equal(0, repository.TransactionCount);
            Xunit.Assert.NotNull(GoalWorktrees.TryGetCleanupBackoff(root, goal.Id));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_mark_landed_returns_success_with_deferred_cleanup_backoff")]
    public void PersistentRunnerGoalMarkLandedReturnsSuccessWithDeferredCleanupBackoff()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Developer);
            var goal = kernel.CreateGoal("Already landed cleanup-needed without backoff", [task]);
            cleanupGoalId = goal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;
            kernel.ActivateGoal(goal.Id, agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "dotnet test", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/landed.txt", "goal work");
            RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));
            GoalOperationJournal.Completed(root, goal, "conductor:land", "landed");
            GoalOperationJournal.Completed(root, goal, "conductor:record", "recorded");
            GoalOperationJournal.Failed(root, goal, "conductor:cleanup", "Deferred cleanup after landing: cleanup-needed");
            GoalWorktrees.RecordGoalCleanupNeeded(root, goal.Id, "remove:cleanup-budget-exhausted");
            var repository = new InMemoryTransactionalStateRepository(kernel);

            var stderr = CaptureConsoleError(() =>
            {
                var output = CaptureConsole(() =>
                {
                    var changed = CliPersistentStateRunner.ExecuteCommand(
                        ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed"],
                        repository,
                        CreateRefinedWorkspace(root),
                        ref agents,
                        providers,
                        ref profiles,
                        ref currentGoal);
                    Xunit.Assert.True(changed);
                });
                Xunit.Assert.Contains("Cleanup backoff:", output);
            });

            Xunit.Assert.DoesNotContain("warning: goal-mark-landed state commit failed", stderr);
            Xunit.Assert.Equal(0, repository.TransactionCount);
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_mark_landed_state_commit_timeout_requires_recorded_evidence")]
    public void PersistentRunnerGoalMarkLandedStateCommitTimeoutRequiresRecordedEvidence()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Developer);
            var goal = kernel.CreateGoal("Already landed missing record evidence", [task]);
            cleanupGoalId = goal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;
            kernel.ActivateGoal(goal.Id, agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "dotnet test", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/landed.txt", "goal work");
            RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));
            var journalPath = GoalOperationJournal.PathFor(root, goal.Id);
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            GoalOperationJournal.Completed(root, goal, "conductor:land", "landed");
            GoalOperationJournal.Failed(root, goal, "conductor:cleanup", "Deferred cleanup after landing: cleanup-needed");
            GoalWorktrees.RecordGoalCleanupNeeded(root, goal.Id, "remove:cleanup-budget-exhausted");
            var repository = new InMemoryTransactionalStateRepository(kernel);

            var stderr = CaptureConsoleError(() =>
            {
                var output = CaptureConsole(() =>
                {
                    var changed = CliPersistentStateRunner.ExecuteCommand(
                        ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed"],
                        repository,
                        CreateRefinedWorkspace(root),
                        ref agents,
                        providers,
                        ref profiles,
                        ref currentGoal);
                    Xunit.Assert.True(changed);
                });
                Xunit.Assert.Contains("Workspace cleanup deferred", output);
            });

            Xunit.Assert.DoesNotContain("warning: goal-mark-landed state commit failed", stderr);
            Xunit.Assert.Equal(0, repository.TransactionCount);
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


}
