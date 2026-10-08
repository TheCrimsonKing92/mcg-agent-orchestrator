using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text.Json;

[Xunit.Collection(TestCollections.IsolatedDotnetRoot)]
public sealed class CliCommandTestsIsolatedBuildLeaseCommands : CliCommandTestBase
{
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
            Xunit.Assert.True(DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal.Id));
        }
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
            Xunit.Assert.True(DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal.Id));
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
            Xunit.Assert.True(DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal.Id));
        }

        Xunit.Assert.Contains("Deleted orphaned build lease", leaseOutput);
        Xunit.Assert.False(Directory.Exists(environment.RootPath));
    }
}

public sealed class CliCommandTestsGoalLifecycleCleanupHooksAbandon : CliCommandTestBase
{
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
                ref currentGoal,
                cleanupContext: CreateIsolatedCleanupContext(workspace));
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


}

public sealed class CliCommandTestsGoalLifecycleCommandsCreation : CliCommandTestBase
{
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
                "--pipeline",
                "five-role",
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

    [Xunit.Fact]
    public void CliGoalTextFileCreatesGoalWithFileContent()
    {
        var root = CreateTempDirectory();
        var briefContent = "Create a goal with the canonical --text-file option.\n\nKeep multiline content intact.";
        var briefPath = Path.Combine(root, "brief.md");
        File.WriteAllText(briefPath, briefContent, System.Text.Encoding.UTF8);
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["goal", "--text-file", briefPath, "--simple"],
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
            CleanupContext = new WorktreeCleanupContext(hooks)
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


}

public sealed class CliCommandTestsGoalLifecycleCleanupHooksAcceptance : CliCommandTestBase
{
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
            ref currentGoal,
                cleanupContext: CreateIsolatedCleanupContext(workspace)));

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
        var isolatedRoot = Path.Combine(Path.GetTempPath(), $"{DotnetBuildEnvironmentManager.RootDirectoryName}-cli-{Guid.NewGuid():N}");
        var storageRoot = new DotnetBuildStorageRoot(isolatedRoot);
        var root = CreateShortAcceptanceRepository();
        try
        {
            var authorityPath = Path.Combine(
                root,
                SourceSizeRatchet.SourcePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(authorityPath)!);
            File.WriteAllText(authorityPath, "new SourceSizeCeiling(\"guarded.cs\", 3)");
            File.WriteAllLines(Path.Combine(root, "guarded.cs"), ["one", "two", "three"]);
            RunGit(root, "add", ".");
            RunGit(root, "commit", "-m", "Add compliant source size authority");
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
                DotnetBuildEnvironmentManager.CreateAttempt(goal.Id, "permit-probe", storageRoot: storageRoot).BuildPermitIndex ?? 0;
            var foreignSlot = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(goalBuildPermit == 0 ? 1 : 0, storageRoot: storageRoot);
            using var foreignSlotLock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(foreignSlot);
            var leaseHeldObserved = false;
            var verifier = new ProbeAcceptanceVerifier(stableSlotLease =>
            {
                Xunit.Assert.True(storageRoot.ContainsPath(stableSlotLease!.Environment.RootPath), $"Acceptance lease root {stableSlotLease.Environment.RootPath} escaped supplied root {storageRoot.RootPath}.");
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
                stableSlotAcquisitionTimeout: TimeSpan.FromSeconds(1),
                cleanupContext: WorktreeCleanupContext.Load(attentionStoreDirectory: workspace.OrchestratorDirectory, buildStorageRoot: storageRoot)));

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
            if (Directory.Exists(isolatedRoot))
            {
                Directory.Delete(isolatedRoot, recursive: true);
            }
        }
    }

    [Xunit.Fact]
    public void Acceptance_ViolatingAuthority_SkipsSlotAndVerifier()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Reject source size breach before CLI slot", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-08-24T12:00:00Z")));
        CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
        var worktree = Assert.IsType<string>(GoalWorktrees.TryResolve(root, goal.Id));
        var authorityPath = Path.Combine(
            worktree,
            SourceSizeRatchet.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(authorityPath)!);
        File.WriteAllText(authorityPath, "new SourceSizeCeiling(\"guarded.cs\", 2)");
        File.WriteAllLines(Path.Combine(worktree, "guarded.cs"), ["one", "two", "three"]);
        RunGit(worktree, "add", ".");
        RunGit(worktree, "commit", "-m", "Breach source size ceiling");
        var verifier = new ProbeAcceptanceVerifier(() => { });
        var slotAttempted = false;

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["acceptance", "--keep-workspace"],
            kernel,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal,
            acceptanceVerifier: verifier,
            stableSlotSelector: (_, _) =>
            {
                slotAttempted = true;
                throw new Xunit.Sdk.XunitException(
                    "The source-size rejection must run before the CLI stable-slot selector.");
            },
                cleanupContext: CreateIsolatedCleanupContext(OrchestratorWorkspace.ForDirectory(root))));

        Xunit.Assert.False(slotAttempted);
        Xunit.Assert.Equal(0, verifier.RunCount);
        Xunit.Assert.Contains("guarded.cs has 3 lines", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("recorded ceiling of 2", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_tests_use_workspace_owned_dotnet_root")]
    public void CliAcceptanceTestsUseFixtureIsolatedDotnetRoot()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateShortAcceptanceRepository());
        var context = CreateIsolatedCleanupContext(workspace);
        var storageRoot = Xunit.Assert.IsType<DotnetBuildStorageRoot>(context.Hooks.BuildStorageRoot);
        var attempt = DotnetBuildEnvironmentManager.CreateAttempt(
            GoalId.New(), "ownership-probe", storageRoot: storageRoot);

        Xunit.Assert.StartsWith(
            Path.Combine(workspace.ExecutionDirectory, ".orchestrator", "test-dotnet") + Path.DirectorySeparatorChar,
            attempt.RootPath,
            StringComparison.OrdinalIgnoreCase);
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
                },
                cleanupContext: CreateIsolatedCleanupContext(OrchestratorWorkspace.ForDirectory(root))));
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
                ref currentGoal,
                cleanupContext: CreateIsolatedCleanupContext(workspace)));

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
        GoalOperationJournal.AcceptanceBlocked(
            root,
            goal,
            "acceptance",
            "timeout",
            "branch-head",
            "main-head",
            "A task was canceled");

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["goal-mark-landed", goalPrefix, "--confirm-goal-mark-landed", "--force"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal,
                cleanupContext: CreateIsolatedCleanupContext(workspace));
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
        var dogfoodRecord = new DogfoodLogStore(workspace.DogfoodLogStorePath)
            .GetByGoalIdAsync(goal.Id.Value)
            .GetAwaiter()
            .GetResult();
        Xunit.Assert.NotNull(dogfoodRecord);
        Xunit.Assert.Contains(
            "Manual landing recorded; acceptance not recorded as passed.",
            dogfoodRecord!.RenderedMarkdown);
        Xunit.Assert.Contains("Acceptance inconclusive: blocked:timeout.", dogfoodRecord.RenderedMarkdown);
        Xunit.Assert.DoesNotContain("Acceptance passed", dogfoodRecord.RenderedMarkdown, StringComparison.Ordinal);
        var facts = new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: false);
        Xunit.Assert.Equal(GoalLifecycleState.Recorded, GoalLifecycle.ResolveState(kernel.GetGoal(goal.Id), facts));
        var sweep = TerminalGoalSweep.Run(kernel, root, goal.Id);
        Xunit.Assert.Empty(sweep.Goals);
        Xunit.Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
    }

    [Xunit.Fact]
    public async Task GoalMarkLanded_OutstandingObligation_RefusesWithoutDurableWrites()
    {
        var fixture = await CreateGoalMarkLandedRefusalFixtureAsync("Outstanding landing evidence");
        var (root, workspace, backlogStore, backlogItem, kernel, goal) = fixture;
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            goal.Objective,
            ["Acceptance evidence is recorded"],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.MapCriterionEvidenceOwner(
            goal.Id,
            0,
            1,
            CriterionEvidenceOwner.Acceptance,
            "operator",
            CriterionEvidenceScopes.FullAcceptanceGate,
            expectedCandidateSha: "candidate-a");

        await AssertGoalMarkLandedRefusalHasNoDurableSideEffectsAsync(
            root,
            workspace,
            backlogStore,
            backlogItem,
            kernel,
            goal,
            "cannot complete from merge evidence while criterion evidence obligations remain outstanding",
            force: true);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task GoalMarkLanded_BlockedTask_RefusesWithoutDurableWrites(bool liveProcess)
    {
        var fixture = await CreateGoalMarkLandedRefusalFixtureAsync("Blocked landing task");
        var (root, workspace, backlogStore, backlogItem, kernel, originalGoal) = fixture;
        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = Xunit.Assert.Single(snapshot.Goals);
        var taskSnapshot = Xunit.Assert.Single(goalSnapshot.Tasks);
        var blockedTask = liveProcess
            ? taskSnapshot with
            {
                LastProcess = new TaskProcessSnapshot(
                    1234,
                    "worker",
                    root,
                    Path.Combine(root, "worker.out.log"),
                    Path.Combine(root, "worker.err.log"),
                    Path.Combine(root, "worker.exit.txt"),
                    DateTimeOffset.UtcNow,
                    null,
                    null)
            }
            : taskSnapshot with { Status = WorkTaskStatus.Assigned };
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goalSnapshot with { Tasks = [blockedTask] }]
        });
        var goal = kernel.GetGoal(originalGoal.Id);
        Xunit.Assert.Equal(GoalStatus.Verified, goal.Status);
        Xunit.Assert.Equal(liveProcess, goal.Tasks.Single().LastProcess is { IsRunning: true });
        Xunit.Assert.Equal(
            liveProcess ? WorkTaskStatus.Completed : WorkTaskStatus.Assigned,
            goal.Tasks.Single().Status);

        await AssertGoalMarkLandedRefusalHasNoDurableSideEffectsAsync(
            root,
            workspace,
            backlogStore,
            backlogItem,
            kernel,
            goal,
            "cannot complete from merge evidence while any task is non-terminal or has a live process",
            force: false);
    }

    private static async Task<(
        string Root,
        OrchestratorWorkspace Workspace,
        BacklogStore BacklogStore,
        BacklogItem BacklogItem,
        AgentOrchestratorKernel Kernel,
        Goal Goal)> CreateGoalMarkLandedRefusalFixtureAsync(string backlogTitle)
    {
        var root = CreateTempDirectory();
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "tests@example.invalid");
        RunGit(root, "config", "user.name", "Tests");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, "add", "seed.txt");
        RunGit(root, "commit", "-m", "Seed");
        var workspace = CreateRefinedWorkspace(root);
        var backlogStore = new BacklogStore(workspace.BacklogStorePath);
        var backlogItem = await backlogStore.AddAsync(backlogTitle);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Developer);
        var goal = kernel.CreateGoal("Refuse unsafe landing", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        Xunit.Assert.Equal(GoalStatus.Verified, goal.Status);
        kernel.SetGoalSourceBacklogItemLink(goal.Id, backlogItem.Id, SourceBacklogCoverage.Full);
        File.WriteAllText(Path.Combine(root, "landed.txt"), "landed");
        RunGit(root, "add", "landed.txt");
        RunGit(root, "commit", "-m", $"Integrate {GoalWorktrees.BranchName(goal.Id)}");
        return (root, workspace, backlogStore, backlogItem, kernel, goal);
    }

    private static async Task AssertGoalMarkLandedRefusalHasNoDurableSideEffectsAsync(
        string root,
        OrchestratorWorkspace workspace,
        BacklogStore backlogStore,
        BacklogItem backlogItem,
        AgentOrchestratorKernel kernel,
        Goal goal,
        string expectedMessage,
        bool force)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        var parts = force
            ? new[] { "goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed", "--force" }
            : ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed"];
        var error = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            parts,
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal,
            cleanupContext: CreateIsolatedCleanupContext(workspace)));

        Xunit.Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
        var journal = GoalOperationJournal.Read(root, goal.Id);
        Xunit.Assert.DoesNotContain(journal.Entries, entry =>
            entry.Operation == GoalOperationJournal.LandingIntentOperation);
        Xunit.Assert.DoesNotContain(journal.Entries, entry => entry.Operation == "conductor:land");
        Xunit.Assert.DoesNotContain(journal.Entries, entry =>
            entry.Operation == GoalOperationJournal.TerminalDispositionOperation);
        Xunit.Assert.Null(await new DogfoodLogStore(workspace.DogfoodLogStorePath)
            .GetByGoalIdAsync(goal.Id.Value));
        var unchangedBacklogItem = await backlogStore.GetByExactIdAsync(backlogItem.Id);
        Xunit.Assert.NotNull(unchangedBacklogItem);
        Xunit.Assert.Equal(BacklogItemStatus.Open, unchangedBacklogItem!.Status);
        Xunit.Assert.Equal(GoalStatus.Verified, goal.Status);
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
                ref currentGoal,
                cleanupContext: CreateIsolatedCleanupContext(workspace));
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
                ref currentGoal,
                cleanupContext: CreateIsolatedCleanupContext(workspace));

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
            ref currentGoal,
                cleanupContext: CreateIsolatedCleanupContext(workspace)));

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
            stableSlotAcquisitionTimeout: TimeSpan.FromSeconds(1),
                cleanupContext: CreateIsolatedCleanupContext(workspace)));

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

    [Xunit.Fact(DisplayName = "Cli_acceptance_infrastructure_deferral_stays_ready_without_failure_or_worker_retry")]
    public void CliAcceptanceInfrastructureDeferralStaysReadyWithoutFailureOrWorkerRetry()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Trusted baseline infrastructure deferral", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-08-10T12:00:00Z")));
        CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
        var verifier = new ProbeAcceptanceVerifier(_ => throw new AcceptanceInfrastructureDeferredException(
            "trusted-main-build-failed",
            1,
            "baseline could not produce a usable assembly"));
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
            stableSlotAcquisitionTimeout: TimeSpan.FromSeconds(1),
                cleanupContext: CreateIsolatedCleanupContext(workspace)));

        Xunit.Assert.Contains("ACCEPTANCE_INFRASTRUCTURE_DEFERRED", output);
        Xunit.Assert.Contains("trusted-main-build-failed", output);
        Xunit.Assert.Contains("goal remains ready", output);
        Xunit.Assert.Equal(1, verifier.RunCount);
        var held = kernel.GetGoal(goal.Id)!;
        Xunit.Assert.Equal(GoalStatus.Verified, held.Status);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, held.Tasks.Single().Status);
        Xunit.Assert.Null(held.LatestAcceptanceFailure);

        var conductEvent = File.ReadAllLines(workspace.ConductEventsLogPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .Single(record => record.EventKind == "infrastructure-deferral");
        Xunit.Assert.Equal(goal.Id.Value[..8], conductEvent.GoalId);
        Xunit.Assert.Contains("trusted-main-build-failed", conductEvent.Detail, StringComparison.Ordinal);

        var journal = GoalOperationJournal.Read(root, goal.Id);
        var blockedOutcome = journal.Entries.LastOrDefault(entry =>
            entry.AcceptanceOutcome == "blocked:INFRASTRUCTURE_DEFERRED:trusted-main-build-failed");
        Xunit.Assert.NotNull(blockedOutcome);
        Xunit.Assert.Equal(GoalOperationStatus.Failed, blockedOutcome.Status);
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
            stableSlotSelector: (_, _) => throw new DotnetBuildSlotsBusyException(busy),
                cleanupContext: CreateIsolatedCleanupContext(OrchestratorWorkspace.ForDirectory(root))));

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
            stableSlotAcquisitionTimeout: TimeSpan.FromSeconds(1),
                cleanupContext: CreateIsolatedCleanupContext(OrchestratorWorkspace.ForDirectory(root))));

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
            stableSlotAcquisitionTimeout: TimeSpan.FromSeconds(1),
                cleanupContext: CreateIsolatedCleanupContext(OrchestratorWorkspace.ForDirectory(root))));

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
            stableSlotAcquisitionTimeout: TimeSpan.FromSeconds(1),
                cleanupContext: CreateIsolatedCleanupContext(OrchestratorWorkspace.ForDirectory(root))));

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
            stableSlotAcquisitionTimeout: TimeSpan.FromSeconds(1),
                cleanupContext: CreateIsolatedCleanupContext(OrchestratorWorkspace.ForDirectory(root))));

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

    [Xunit.Fact]
    public void AcceptanceFailed_OldCandidatePair_RunsFreshGate()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Retry a stale failed acceptance verdict", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-09-03T12:00:00Z")));
        var oldMain = RunGitOutput(root, "rev-parse", "HEAD").Trim();
        var worktree = CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
        var oldBranch = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
        Xunit.Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "Run acceptance."));
        Xunit.Assert.True(kernel.ReconcileGoalAcceptanceFailed(
            goal.Id,
            ["old acceptance failure"],
            "Acceptance failed against the old candidate pair.",
            oldBranch,
            oldMain));
        GoalOperationJournal.AcceptanceFailed(
            root,
            goal,
            "acceptance",
            oldBranch,
            oldMain,
            "old acceptance failure");
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
            stableSlotAcquisitionTimeout: TimeSpan.FromSeconds(1),
                cleanupContext: CreateIsolatedCleanupContext(OrchestratorWorkspace.ForDirectory(root))));

        Xunit.Assert.Contains("superseded failure is historical", output);
        Xunit.Assert.Equal(1, verifier.RunCount);
        Xunit.Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
        Xunit.Assert.Null(kernel.GetGoal(goal.Id).LatestAcceptanceFailure);
    }

    [Xunit.Fact]
    public void AcceptanceFailed_CurrentCandidatePair_StaysBlocked()
    {
        var root = CreateShortAcceptanceRepository();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement something", AgentRole.Developer);
        var goal = kernel.CreateGoal("Keep a current failed acceptance verdict", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root, DateTimeOffset.Parse("2026-09-03T12:00:00Z")));
        var mainHead = RunGitOutput(root, "rev-parse", "HEAD").Trim();
        var worktree = CommitGoalWork(root, goal.Id, "feature.txt", "goal work");
        var branchHead = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
        Xunit.Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "Run acceptance."));
        Xunit.Assert.True(kernel.ReconcileGoalAcceptanceFailed(
            goal.Id,
            ["current acceptance failure"],
            "Acceptance failed against the current candidate pair.",
            branchHead,
            mainHead));

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
            stableSlotAcquisitionTimeout: TimeSpan.FromSeconds(1),
                cleanupContext: CreateIsolatedCleanupContext(OrchestratorWorkspace.ForDirectory(root))));

        Xunit.Assert.Contains("acceptance: not accepted", output);
        Xunit.Assert.Equal(0, verifier.RunCount);
        Xunit.Assert.Equal(GoalStatus.AcceptanceFailed, kernel.GetGoal(goal.Id).Status);
        Xunit.Assert.NotNull(kernel.GetGoal(goal.Id).LatestAcceptanceFailure);
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

public sealed class CliCommandTestsGoalLifecycleCleanupHooks : CliCommandTestBase
{
    [Xunit.Fact(DisplayName = "Cli_status_prints_cleanup_backoff_for_snapshot_visibility")]
    public void CliStatusPrintsCleanupBackoffForSnapshotVisibility()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var now = DateTimeOffset.Parse("2026-07-03T12:00:00Z");
        var hooks = new GoalWorktreeCleanupHooks
        {
            CleanupUtcNow = () => now,
            CleanupBackoffDuration = static () => TimeSpan.FromMinutes(15)
        };
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Cleanup debt visible in status");
        GoalWorktrees.RecordGoalCleanupNeeded(
            workspace.ExecutionDirectory,
            goal.Id,
            "remove:branch-delete-failed",
            hooks);
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
            ref currentGoal,
            cleanupContext: CreateIsolatedCleanupContext(workspace, hooks)));

        Xunit.Assert.Contains("Cleanup backoff:", output);
        Xunit.Assert.Contains("reason=remove:branch-delete-failed", output);
        Xunit.Assert.Contains("skip_until_utc=2026-07-03T12:15:00.0000000+00:00", output);
        Xunit.Assert.Contains("remaining_wait=00:15:00", output);
        Xunit.Assert.Contains($"Cleanup retry: conduct {goal.Id.Value[..8].ToLowerInvariant()} --loop", output);
    }
}
