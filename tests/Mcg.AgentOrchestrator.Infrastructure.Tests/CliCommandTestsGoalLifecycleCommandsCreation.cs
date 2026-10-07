using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text.Json;

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

