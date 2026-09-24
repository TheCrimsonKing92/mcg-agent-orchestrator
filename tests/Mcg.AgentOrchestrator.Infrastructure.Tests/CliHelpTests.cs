using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class CliHelpTests
{
    [Xunit.Theory(DisplayName = "Cli_help_prints_usage_without_executing_command")]
    [Xunit.InlineData(new[] { "backlog-list", "--help" }, "backlog-list", "--limit <n>")]
    [Xunit.InlineData(new[] { "backlog-add", "-h" }, "backlog-add", "--text-file")]
    [Xunit.InlineData(new[] { "backlog-similar", "--help" }, "backlog-similar", "--excerpt")]
    [Xunit.InlineData(new[] { "backlog-update", "--help" }, "backlog-update", "--description")]
    [Xunit.InlineData(new[] { "backlog-show", "--help" }, "backlog-show", "-h")]
    [Xunit.InlineData(new[] { "backlog-annotate", "--help" }, "backlog-annotate", "--text-file")]
    [Xunit.InlineData(new[] { "backlog-close", "-h" }, "backlog-close", "--text-file")]
    [Xunit.InlineData(new[] { "backlog-supersede", "--help" }, "backlog-supersede", "new-id-prefix")]
    [Xunit.InlineData(new[] { "backlog-unsupersede", "--help" }, "backlog-unsupersede", "id-prefix")]
    [Xunit.InlineData(new[] { "backlog-link", "--help" }, "backlog-link", "--related")]
    [Xunit.InlineData(new[] { "backlog-reopen", "--help" }, "backlog-reopen", "-h")]
    [Xunit.InlineData(new[] { "retry", "--help" }, "retry", "--text-file")]
    [Xunit.InlineData(new[] { "note", "--help" }, "note", "--text-file")]
    [Xunit.InlineData(new[] { "progress", "--help" }, "progress", "--text-file")]
    [Xunit.InlineData(new[] { "verify-manual", "--help" }, "verify-manual", "--text-file")]
    [Xunit.InlineData(new[] { "recover", "--help" }, "recover", "--text-file")]
    [Xunit.InlineData(new[] { "answer", "--help" }, "answer", "--text-file")]
    [Xunit.InlineData(new[] { "supersede", "--help" }, "supersede", "--text-file")]
    [Xunit.InlineData(new[] { "add-task", "--help" }, "add-task", "--text-file")]
    [Xunit.InlineData(new[] { "abandon-goal", "--help" }, "abandon-goal", "--text-file")]
    [Xunit.InlineData(new[] { "unpark-goal", "--help" }, "unpark-goal", "--confirm-goal-unpark")]
    [Xunit.InlineData(new[] { "goal", "--help" }, "goal", "--text-file")]
    [Xunit.InlineData(new[] { "goal-replace", "--help" }, "goal-replace", "--researcher <agent>")]
    [Xunit.InlineData(new[] { "agent", "--help" }, "agent <role>", "--subscription-reasoning")]
    [Xunit.InlineData(new[] { "agent-add", "--help" }, "agent-add <role>", "--subscription-reasoning")]
    [Xunit.InlineData(new[] { "goals", "subscribe", "--help" }, "goals subscribe", "--from-cursor")]
    [Xunit.InlineData(new[] { "goals", "--board", "--help" }, "goals --board", "--limit <n>")]
    [Xunit.InlineData(new[] { "conduct", "--help" }, "conduct", "--loop")]
    [Xunit.InlineData(new[] { "refresh-dispatch", "--help" }, "refresh-dispatch <task-number>", "--history-limit <n>")]
    [Xunit.InlineData(new[] { "workspace", "create", "-h" }, "workspace create", "--help")]
    [Xunit.InlineData(new[] { "status", "--help" }, "status", "-h")]
    [Xunit.InlineData(new[] { "status", "--help" }, "status", "--tasks-only")]
    [Xunit.InlineData(new[] { "flake-census", "--help" }, "flake-census", "--min-goals")]
    public void CliHelpPrintsUsageWithoutExecutingCommand(string[] args, string synopsisToken, string optionToken)
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

        Xunit.Assert.Contains("Usage:", output);
        Xunit.Assert.Contains(synopsisToken, output);
        Xunit.Assert.Contains("Options:", output);
        Xunit.Assert.Contains(optionToken, output);
        Xunit.Assert.False(File.Exists(workspace.BacklogStorePath));
        Xunit.Assert.Empty(kernel.Goals);
    }

    [Xunit.Fact(DisplayName = "CliCommandHelp_GoalsBoard_accepts_board_selector")]
    public void CliCommandHelpGoalsBoardAcceptsBoardSelector()
    {
        var exception = Xunit.Record.Exception(() =>
            CliCommandHelp.ThrowIfInvalidFlags(["goals", "--board", "--all"]));

        Xunit.Assert.Null(exception);
    }

    [Xunit.Fact]
    public void RefreshDispatchHelpDescribesCompactDefaultAndBothHistoryModes()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();

        var output = ExecuteHelpAndCapture(["refresh-dispatch", "--help"], kernel, workspace);

        Xunit.Assert.Contains("compact decision surface", output);
        Xunit.Assert.Contains("--history        Include the complete durable task history.", output);
        Xunit.Assert.Contains("--history-limit <n>    Include only the newest n task-history events", output);
        Xunit.Assert.Empty(kernel.Goals);
    }

    [Xunit.Fact(DisplayName = "Cli_help_command_prints_command_usage_without_executing")]
    public void CliHelpCommandPrintsCommandUsageWithoutExecuting()
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
                ["help", "backlog-list"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);

            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains("Usage: backlog-list", output);
        Xunit.Assert.Contains("--limit <n>", output);
        Xunit.Assert.False(File.Exists(workspace.BacklogStorePath));
    }

    [Xunit.Fact]
    public void BacklogHelp_ListAddClose_HasDescriptions()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();

        var list = ExecuteHelpAndCapture(["backlog-list", "--help"], kernel, workspace);
        var add = ExecuteHelpAndCapture(["backlog-add", "--help"], kernel, workspace);
        var close = ExecuteHelpAndCapture(["backlog-close", "--help"], kernel, workspace);

        Xunit.Assert.Contains("List every backlog item with its status and linked goal id.", list);
        Xunit.Assert.Contains("Add a backlog item.", add);
        Xunit.Assert.Contains("Close a backlog item by id prefix.", close);
        Xunit.Assert.False(File.Exists(workspace.BacklogStorePath));
        Xunit.Assert.Empty(kernel.Goals);
    }

    private static string ExecuteHelpAndCapture(
        IReadOnlyList<string> args,
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            args,
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
    }

    [Xunit.Fact(DisplayName = "Cli_help_goals_subscribe_prints_nested_command_usage_without_executing")]
    public void CliHelpGoalsSubscribePrintsNestedCommandUsageWithoutExecuting()
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
                ["help", "goals", "subscribe"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);

            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains(GoalMonitoringSubscriptionCommand.GoalsSubscribeUsage, output);
        Xunit.Assert.Contains("Monitor goal lifecycle events.", output);
        Xunit.Assert.Contains("Example: goals subscribe --goal-prefix abc123 --event-kind conductor:dispatch --wait-terminal --once --timeout 5m", output);
        Xunit.Assert.Contains("--goal-prefix", output);
        Xunit.Assert.Contains("--from-cursor", output);
        Xunit.Assert.Contains("--since", output);
        Xunit.Assert.Contains("--task", output);
        Xunit.Assert.Contains("--event-kind", output);
        Xunit.Assert.Contains("--once", output);
        Xunit.Assert.Contains("--wait-terminal", output);
        Xunit.Assert.Contains("--format", output);
        Xunit.Assert.Contains("--timeout", output);
        Xunit.Assert.DoesNotContain("Goals:", output);
        Xunit.Assert.False(File.Exists(workspace.BacklogStorePath));
        Xunit.Assert.Empty(kernel.Goals);
    }

    [Xunit.Fact(DisplayName = "Cli_help_unknown_command_suggests_nearest_command")]
    public void CliHelpUnknownCommandSuggestsNearestCommand()
    {
        AssertUnknownCommandSuggestion(
            ["help", "backlog-lits"],
            "backlog-lits",
            "backlog-list");
    }

    [Xunit.Theory(DisplayName = "Cli_unknown_command_suggests_nearest_command")]
    [Xunit.InlineData(new[] { "backlog", "list" }, "backlog list", "backlog-list")]
    [Xunit.InlineData(new[] { "help" }, "help", "--help")]
    [Xunit.InlineData(new[] { "stauts" }, "stauts", "status")]
    public void CliUnknownCommandSuggestsNearestCommand(string[] args, string token, string suggestion)
    {
        AssertUnknownCommandSuggestion(args, token, suggestion);
    }

    private static void AssertUnknownCommandSuggestion(string[] args, string token, string suggestion)
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var ex = Xunit.Assert.ThrowsAny<ArgumentException>(() => CliCommandDispatcher.ExecuteCommand(
            args,
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains($"Unknown command '{token}'.", ex.Message);
        Xunit.Assert.Contains($"Did you mean: {suggestion}", ex.Message);
        Xunit.Assert.Contains("--help", ex.Message);
    }

    [Xunit.Fact(DisplayName = "Cli_conduct_help_documents_poll_seconds")]
    public void CliConductHelpDocumentsPollSeconds()
    {
        foreach (var args in new[]
        {
            new[] { "conduct", "--help" },
            new[] { "conduct", "--loop", "--help" },
            new[] { "conduct", "--watch", "--help" }
        })
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

            Xunit.Assert.Contains("--poll-seconds <n>", output);
            Xunit.Assert.Contains("Positive integer seconds between watch polls", output);
            Xunit.Assert.Contains($"default {ConductorBatchLoop.DefaultWatchIntervalSeconds}", output);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_operator_commands_help_documents_repo_bounded_prefixes")]
    public void CliOperatorCommandsHelpDocumentsRepoBoundedPrefixes()
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
                ["operator-commands"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);

            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains("Usage: operator-commands", output);
        Xunit.Assert.Contains("Approved prefixes:", output);
        Xunit.Assert.Contains("scripts\\Get-OrchestratorSnapshot.ps1", output);
        Xunit.Assert.Contains("scripts\\Wait-ForDispatch.ps1 -ExitFile <path>", output);
        Xunit.Assert.Contains("scripts\\Invoke-Git.ps1", output);
        Xunit.Assert.Contains("scripts\\Invoke-OrchestratorCommand.ps1 backlog-list", output);
        Xunit.Assert.Contains("scripts\\Get-RepoProcessInfo.ps1", output);
        Xunit.Assert.Contains("scripts\\Stop-RepoProcess.ps1", output);
        Xunit.Assert.Contains("scripts\\Invoke-OrchestratorSqliteTool.ps1", output);
        Xunit.Assert.Contains("Logs: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Show-OrchestratorLogArtifacts.ps1 -GoalPrefix <goal> [-TaskPrefix <task>] [-TailLines <n>]", output);
        Xunit.Assert.Contains("Acceptance: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Invoke-OrchestratorCommand.ps1 acceptance <goal>", output);
        Xunit.Assert.DoesNotContain("Get-Process codex", output, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.False(File.Exists(workspace.BacklogStorePath));
        Xunit.Assert.Empty(kernel.Goals);
    }

    [Xunit.Theory(DisplayName = "Cli_help_startup_exits_zero_before_state_creation")]
    [Xunit.InlineData(new[] { "goal", "--help" }, "goal", "--text-file")]
    [Xunit.InlineData(new[] { "goals", "subscribe", "--help" }, "goals subscribe", "--wait-terminal")]
    [Xunit.InlineData(new[] { "help", "goals", "subscribe" }, "goals subscribe", "--wait-terminal")]
    [Xunit.InlineData(new[] { "backlog-list", "--help" }, "backlog-list", "--limit <n>")]
    [Xunit.InlineData(new[] { "backlog-add", "-h" }, "backlog-add", "--text-file")]
    [Xunit.InlineData(new[] { "backlog-update", "--help" }, "backlog-update", "--description")]
    public void CliHelpStartupExitsZeroBeforeStateCreation(string[] args, string synopsisToken, string optionToken)
    {
        var root = CreateTempDirectory();

        var result = RunAppCli(root, args);

        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.Contains("Usage:", result.StandardOutput);
        Xunit.Assert.Contains(synopsisToken, result.StandardOutput);
        Xunit.Assert.Contains(optionToken, result.StandardOutput);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(result.StandardError), result.StandardError);
        Xunit.Assert.False(Directory.Exists(Path.Combine(root, ".orchestrator")));
    }

    [Xunit.Theory]
    [Xunit.InlineData("--help")]
    [Xunit.InlineData("-h")]
    public void CliRootHelpPrintsCommandListBeforeStateCreation(string helpFlag)
    {
        var root = CreateTempDirectory();

        var result = RunAppCli(root, [helpFlag]);

        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.Contains("Usage:", result.StandardOutput, StringComparison.Ordinal);
        Xunit.Assert.Contains("Commands:", result.StandardOutput, StringComparison.Ordinal);
        Xunit.Assert.Contains("backlog-add", result.StandardOutput, StringComparison.Ordinal);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(result.StandardError), result.StandardError);
        Xunit.Assert.False(Directory.Exists(Path.Combine(root, ".orchestrator")));
    }

    [Xunit.Fact(DisplayName = "Cli_startup_help_skips_invalid_worktree_cleanup_config_and_commands_format_the_error")]
    public void CliStartupHelpSkipsInvalidWorktreeCleanupConfigAndCommandsFormatTheError()
    {
        var root = CreateTempDirectory();
        var environment = new Dictionary<string, string?>
        {
            ["WorktreeCleanup__EscalationThreshold"] = "not-an-integer"
        };

        var help = RunAppCli(root, ["goal", "--help"], environment);
        var command = RunAppCli(root, ["goals"], environment);

        Xunit.Assert.Equal(0, help.ExitCode);
        Xunit.Assert.Contains("Usage: goal", help.StandardOutput);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(help.StandardError), help.StandardError);
        Xunit.Assert.Equal(1, command.ExitCode);
        Xunit.Assert.StartsWith("InvalidOperationException:", command.StandardError, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(" at Mcg.", command.StandardError, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_startup_help_and_backlog_commands_skip_orphan_worktree_cleanup")]
    public async Task CliStartupHelpAndBacklogCommandsSkipOrphanWorktreeCleanup()
    {
        await AssertCliSkipsOrphanWorktreeCleanupAsync(
            ["goal", "--help"],
            result =>
            {
                Xunit.Assert.Equal(0, result.ExitCode);
                Xunit.Assert.Contains("Usage: goal", result.StandardOutput);
            });

        await AssertCliSkipsOrphanWorktreeCleanupAsync(
            ["backlog-list", "--limit", "5", "--text", "ACL reset budget"],
            result =>
            {
                Xunit.Assert.Equal(0, result.ExitCode);
                Xunit.Assert.Contains("Backlog list: 0 item(s) from backlog store", result.StandardOutput);
            });

        await AssertCliSkipsOrphanWorktreeCleanupAsync(
            ["backlog-add", "ACL reset budget", "Keep backlog commands isolated."],
            result =>
            {
                Xunit.Assert.Equal(0, result.ExitCode);
                Xunit.Assert.Contains("Added:", result.StandardOutput);
            });

        await AssertCliSkipsOrphanWorktreeCleanupAsync(
            ["backlog-close", "{backlog-id}", "done"],
            result =>
            {
                Xunit.Assert.Equal(0, result.ExitCode);
                Xunit.Assert.Contains("Closed:", result.StandardOutput);
            },
            seedBacklogItem: true);

        await AssertCliSkipsOrphanWorktreeCleanupAsync(
            ["cleanup-status"],
            result =>
            {
                Xunit.Assert.Equal(0, result.ExitCode);
                Xunit.Assert.Equal("Cleanup status: no pending cleanup debt.", result.StandardOutput.Trim());
                Xunit.Assert.True(string.IsNullOrWhiteSpace(result.StandardError), result.StandardError);
            });
    }

    [Xunit.Fact(DisplayName = "Cli_help_unknown_command_startup_exits_one_with_suggestion")]
    public void CliHelpUnknownCommandStartupExitsOneWithSuggestion()
    {
        AssertUnknownCommandStartupExit(
            ["help", "backlog-lits"],
            "backlog-lits",
            "backlog-list");
    }

    [Xunit.Theory(DisplayName = "Cli_unknown_command_startup_exits_one_with_suggestion")]
    [Xunit.InlineData(new[] { "backlog", "list" }, "backlog list", "backlog-list")]
    [Xunit.InlineData(new[] { "help" }, "help", "--help")]
    [Xunit.InlineData(new[] { "stauts" }, "stauts", "status")]
    public void CliUnknownCommandStartupExitsOneWithSuggestion(string[] args, string token, string suggestion)
    {
        AssertUnknownCommandStartupExit(args, token, suggestion);
    }

    private static void AssertUnknownCommandStartupExit(string[] args, string token, string suggestion)
    {
        var root = CreateTempDirectory();

        var result = RunAppCli(root, args);

        Xunit.Assert.Equal(1, result.ExitCode);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(result.StandardOutput), result.StandardOutput);
        Xunit.Assert.Contains($"Error: Unknown command '{token}'.", result.StandardError);
        Xunit.Assert.Contains($"Did you mean: {suggestion}", result.StandardError);
        Xunit.Assert.Contains("--help", result.StandardError);
    }

    [Xunit.Theory(DisplayName = "Cli_invalid_flags_fail_before_handler_execution")]
    [Xunit.InlineData(new[] { "backlog-list", "--frobnitz" }, "backlog-list", "--frobnitz")]
    [Xunit.InlineData(new[] { "backlog-list", "-x" }, "backlog-list", "-x")]
    [Xunit.InlineData(new[] { "backlog-update", "abc123", "--frobnitz" }, "backlog-update", "--frobnitz")]
    [Xunit.InlineData(new[] { "backlog-close", "abc123", "--frobnitz" }, "backlog-close", "--frobnitz")]
    public void CliInvalidFlagsFailBeforeHandlerExecution(string[] args, string usageToken, string invalidFlag)
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var ex = Xunit.Assert.ThrowsAny<ArgumentException>(() =>
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

        Xunit.Assert.Contains($"Unknown option '{invalidFlag}'", ex.Message);
        Xunit.Assert.Contains("Usage:", ex.Message);
        Xunit.Assert.Contains(usageToken, ex.Message);
        Xunit.Assert.False(File.Exists(workspace.BacklogStorePath));
    }

    [Xunit.Fact]
    public void GoalReplaceAcceptsRoleAgentOverrideFlags()
    {
        foreach (var flag in new[] { "--ideation", "--researcher", "--planner", "--developer", "--tester", "--reviewer" })
        {
            var root = CreateTempDirectory();
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;

            var exception = Xunit.Assert.ThrowsAny<Exception>(() => CliCommandDispatcher.ExecuteCommand(
                ["goal-replace", "missing-predecessor", flag, "agent-id", "--confirm-goal-replace"],
                kernel,
                workspace,
                ref agents,
                new InMemoryModelProviderRegistry([]),
                ref profiles,
                ref currentGoal));

            Xunit.Assert.DoesNotContain("Unknown option", exception.Message, StringComparison.Ordinal);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_invalid_flag_startup_exits_one_with_usage_on_stderr")]
    public void CliInvalidFlagStartupExitsOneWithUsageOnStderr()
    {
        var root = CreateTempDirectory();

        var result = RunAppCli(root, ["backlog-list", "--frobnitz"]);

        Xunit.Assert.Equal(1, result.ExitCode);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(result.StandardOutput), result.StandardOutput);
        Xunit.Assert.Contains("Error: Unknown option '--frobnitz'.", result.StandardError);
        Xunit.Assert.Contains("Usage: backlog-list [--all] [--limit <n>] [--status <value>] [--text <pattern>|--text=<leading-dash-pattern>]", result.StandardError);
    }

    [Xunit.Fact(DisplayName = "Cli_backlog_list_valid_flags_still_execute")]
    public void CliBacklogListValidFlagsStillExecute()
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
                ["backlog-list", "--all", "--limit", "10", "--status", "open", "--text", "foo"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);

            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains("Backlog list: 0 item(s) from backlog store", output);
    }

    [Xunit.Fact(DisplayName = "Cli_backlog_list_explicit_leading_dash_text_value_executes")]
    public void CliBacklogListExplicitLeadingDashTextValueExecutes()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        _ = new BacklogStore(workspace.BacklogStorePath)
            .AddAsync("Investigate --goal parsing", "Parser safety")
            .GetAwaiter()
            .GetResult();
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var args = CliArgumentParser.NormalizeArgs(["backlog-list", "--text=--goal"]);

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            args,
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("Investigate --goal parsing", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("Backlog list: 1 item(s)", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_backlog_list_ambiguous_leading_dash_text_value_fails_with_escape_guidance")]
    public void CliBacklogListAmbiguousLeadingDashTextValueFailsWithEscapeGuidance()
    {
        var exception = Xunit.Assert.Throws<ArgumentException>(() =>
            CliCommandHelp.ThrowIfInvalidFlags(["backlog-list", "--text", "--goal"]));

        Xunit.Assert.Contains("Unknown option '--goal'", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("--text=--goal", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_unknown_flag_on_validating_verb_suggests_near_match")]
    public void CliUnknownFlagOnValidatingVerbSuggestsNearMatch()
    {
        var exception = Xunit.Assert.Throws<ArgumentException>(() =>
            CliCommandHelp.ThrowIfInvalidFlags(["backlog-list", "--limt"]));

        Xunit.Assert.Contains("Unknown option '--limt'", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("Did you mean: --limit", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_goal_near_typo_flag_suggests_brief_file")]
    public void CliGoalNearTypoFlagSuggestsBriefFile()
    {
        var exception = Xunit.Assert.Throws<ArgumentException>(() =>
            CliCommandHelp.ThrowIfInvalidFlags(["goal", "Ship the parser", "--brief-fil", "brief.md"]));

        Xunit.Assert.Contains("Unknown option '--brief-fil'", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("Did you mean: --brief-file", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_goal_source_backlog_unknown_flag_fails_without_creating_goal")]
    public void CliGoalSourceBacklogUnknownFlagFailsWithoutCreatingGoal()
    {
        var root = CreateTempDirectory();
        var briefPath = Path.Combine(root, "brief.md");
        File.WriteAllText(briefPath, "Link this goal to a backlog item.");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var exception = Xunit.Assert.ThrowsAny<ArgumentException>(() =>
            CliCommandDispatcher.ExecuteCommand(
                ["goal", "--brief-file", briefPath, "--source-backlog", "759af574"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

        Xunit.Assert.Contains("Unknown option '--source-backlog'", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Empty(kernel.Goals);
        Xunit.Assert.Null(currentGoal);
    }

    [Xunit.Theory(DisplayName = "Cli_shared_flag_seam_rejects_unknown_flags_on_goal_lifecycle_verbs")]
    [Xunit.InlineData("goal")]
    [Xunit.InlineData("simple-goal")]
    [Xunit.InlineData("backlog-intake")]
    [Xunit.InlineData("acceptance")]
    [Xunit.InlineData("run-goal")]
    public void CliSharedFlagSeamRejectsUnknownFlagsOnGoalLifecycleVerbs(string command)
    {
        var exception = Xunit.Assert.Throws<ArgumentException>(() =>
            CliCommandHelp.ThrowIfInvalidFlags([command, "--zz-not-a-real-flag"]));

        Xunit.Assert.Contains("Unknown option '--zz-not-a-real-flag'", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("Usage:", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains(command, exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_all_recognized_commands_reject_unknown_flags_except_documented_passthrough")]
    public void CliAllRecognizedCommandsRejectUnknownFlagsExceptDocumentedPassthrough()
    {
        foreach (var command in CliArgumentParser.RecognizedCommands)
        {
            if (command.Equals("stable-slot-dotnet", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var exception = Xunit.Assert.Throws<ArgumentException>(() =>
                CliCommandHelp.ThrowIfInvalidFlags([command, "--zz-not-a-real-flag"]));
            Xunit.Assert.Contains("Unknown option '--zz-not-a-real-flag'", exception.Message, StringComparison.Ordinal);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_status_rejects_unknown_option")]
    public void CliStatusRejectsUnknownOption()
    {
        var exception = Xunit.Assert.Throws<ArgumentException>(() =>
            CliCommandHelp.ThrowIfInvalidFlags(["status", "--zz-not-a-real-flag"]));

        Xunit.Assert.Contains("Unknown option '--zz-not-a-real-flag'", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("Usage:", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void TrialCompareIsAnOperatorCommandWithDocumentedFlags()
    {
        Xunit.Assert.Contains(
            CliArgumentParser.RecognizedCommands,
            command => command.Equals("trial-compare", StringComparison.OrdinalIgnoreCase));

        CliCommandHelp.ThrowIfInvalidFlags(
            ["trial-compare", "--spec", "harnesses.json", "--receipts", "receipts", "--timeout-seconds", "30"]);
        Xunit.Assert.Contains("canonical workload", CliCommandHelp.TrialCompareUsage, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains("historical", CliCommandHelp.TrialCompareUsage, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "Cli_newly_validated_goal_verbs_accept_existing_flags")]
    public void CliNewlyValidatedGoalVerbsAcceptExistingFlags()
    {
        var commandFlags = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["goal"] =
            [
                "--pipeline", "--simple", "--from-backlog", "--run", "--confirm-batch-start",
                "--backlog-item", "--backlog-coverage", "--request-key", "--text-file", "--brief-file",
                "--ideation", "--researcher", "--planner", "--developer", "--tester", "--reviewer",
                "--dispatch", "--confirm-dispatch-start", "--create-goal", "--create-simple-goal",
                "--force-reclaim", "--autonomy", "--autonomy-policy",
                "--confirm-large-paid-subscription-start", "--confirm-readiness-risk", "--help", "-h"
            ],
            ["simple-goal"] =
            [
                "--pipeline", "--brief-file", "--text-file", "--backlog-item", "--backlog-coverage",
                "--request-key", "--dispatch", "--confirm-dispatch-start",
                "--ideation", "--researcher", "--planner", "--developer", "--tester", "--reviewer", "--help", "-h"
            ],
            ["backlog-intake"] =
            [
                "--create-goal", "--create-simple-goal", "--force-reclaim", "--pipeline",
                "--backlog-item", "--backlog-coverage", "--request-key", "--help", "-h"
            ],
            ["acceptance"] = ["--skip-verify", "--keep-workspace", "--no-record", "--autonomy", "--autonomy-policy", "--help", "-h"],
            ["run-goal"] =
            [
                "--confirm-batch-start", "--confirm-large-paid-subscription-start", "--confirm-readiness-risk",
                "--autonomy", "--autonomy-policy", "--help", "-h"
            ]
        };

        foreach (var (command, flags) in commandFlags)
        {
            foreach (var flag in flags)
            {
                var exception = Xunit.Record.Exception(() =>
                    CliCommandHelp.ThrowIfInvalidFlags([command, flag, "value"]));
                Xunit.Assert.True(exception is null, $"{command} {flag} was rejected: {exception?.Message}");
            }
        }
    }

    [Xunit.Fact(DisplayName = "Cli_generic_vocabulary_accepts_flags_declared_outside_cli_source_tree")]
    public void CliGenericVocabularyAcceptsFlagsDeclaredOutsideCliSourceTree()
    {
        var dashboardCommands = new[]
        {
            "prototype-ui", "serve-dashboard", "hosted-dashboard", "simple-hosted-dashboard", "open-dashboard"
        };
        var dashboardFlags = new[] { "--refresh", "--lan", "--open", "--no-open" };

        foreach (var command in dashboardCommands)
        {
            foreach (var flag in dashboardFlags)
            {
                var exception = Xunit.Record.Exception(() =>
                    CliCommandHelp.ThrowIfInvalidFlags([command, flag, "value"]));
                Xunit.Assert.True(exception is null, $"{command} {flag} was rejected: {exception?.Message}");
            }
        }

        foreach (var command in new[] { "run", "api-run" })
        {
            var exception = Xunit.Record.Exception(() => CliCommandHelp.ThrowIfInvalidFlags(
                [command, "--confirm-paid-api-run", "--confirm-large-paid-api-prompt"]));
            Xunit.Assert.True(
                exception is null,
                $"{command} paid API confirmation flags were rejected: {exception?.Message}");
        }

        Xunit.Assert.Null(Xunit.Record.Exception(() =>
            CliCommandHelp.ThrowIfInvalidFlags(["failure-triage", "--policy", "Conservative"])));
        Xunit.Assert.Null(Xunit.Record.Exception(() =>
            CliCommandHelp.ThrowIfInvalidFlags(
                ["model-function-add", "--subscription", "--subscription-model", "--subscription-reasoning"])));
    }

    [Xunit.Fact(DisplayName = "Cli_stable_slot_dotnet_preserves_unbounded_dotnet_option_passthrough")]
    public void CliStableSlotDotnetPreservesUnboundedDotnetOptionPassthrough()
    {
        var dotnetOptions = new[]
        {
            "-p:Name=Value", "-clp:ErrorsOnly", "--artifacts-path", "--arch", "--os",
            "--filter-method", "--filter-not-trait"
        };

        foreach (var option in dotnetOptions)
        {
            Xunit.Assert.Null(Xunit.Record.Exception(() =>
                CliCommandHelp.ThrowIfInvalidFlags(["stable-slot-dotnet", option, "value"])));
        }
    }

    [Xunit.Theory(DisplayName = "Cli_generic_commands_reject_flags_owned_by_other_commands")]
    [Xunit.InlineData("doctor", "--backlog-item")]
    [Xunit.InlineData("run", "--refresh")]
    [Xunit.InlineData("prototype-ui", "--confirm-paid-api-run")]
    [Xunit.InlineData("durations", "--backlog-coverage")]
    public void CliGenericCommandsRejectFlagsOwnedByOtherCommands(string command, string flag)
    {
        var exception = Xunit.Assert.Throws<ArgumentException>(() =>
            CliCommandHelp.ThrowIfInvalidFlags([command, flag, "value"]));

        Xunit.Assert.Contains($"Unknown option '{flag}'", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_inline_goal_values_and_leading_dash_filter_are_accepted")]
    public void CliInlineGoalValuesAndLeadingDashFilterAreAccepted()
    {
        Xunit.Assert.Null(Xunit.Record.Exception(() => CliCommandHelp.ThrowIfInvalidFlags(
            ["goal", "--backlog-item=759af574", "--backlog-coverage=slice"])));
        Xunit.Assert.Null(Xunit.Record.Exception(() => CliCommandHelp.ThrowIfInvalidFlags(
            ["backlog-list", "--text=-filter"])));
    }

    [Xunit.Fact(DisplayName = "Cli_equals_form_remains_rejected_for_non_inline_flags")]
    public void CliEqualsFormRemainsRejectedForNonInlineFlags()
    {
        var exception = Xunit.Assert.Throws<ArgumentException>(() =>
            CliCommandHelp.ThrowIfInvalidFlags(["conduct", "--poll-seconds=5"]));

        Xunit.Assert.Contains("Unknown option '--poll-seconds=5'", exception.Message, StringComparison.Ordinal);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-cli-help-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Xunit.Fact(DisplayName = "Cli_attention_show_preserves_worker_owned_by_live_external_conductor")]
    public void CliAttentionShowPreservesWorkerOwnedByLiveExternalConductor()
    {
        var root = CreateTempDirectory();
        Process? worker = null;
        try
        {
            InitializeGitRepository(root);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            worker = Process.Start(new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }.WithArguments(WorkerShell.BaseArguments().Concat(["Start-Sleep -Seconds 9999"])))
                ?? throw new InvalidOperationException("Failed to start sentinel worker.");
            Xunit.Assert.True(SpawnProcessIdentityReader.TryReadForRegistration(worker, out var workerIdentity));
            new SpawnRegistry(workspace.SqliteStatePath).Register("external-conductor-dispatch", workerIdentity);

            var result = RunAppCli(root, ["attention", "show"]);

            Xunit.Assert.Equal(0, result.ExitCode);
            Xunit.Assert.False(worker.HasExited);
            var retained = Xunit.Assert.Single(new SpawnRegistry(workspace.SqliteStatePath).ListActive());
            Xunit.Assert.Contains("retain-live-owner", retained.LastDiagnostic, StringComparison.Ordinal);
            Xunit.Assert.Contains("sweeper_pid=", retained.LastDiagnostic, StringComparison.Ordinal);
        }
        finally
        {
            if (worker is not null)
            {
                try { worker.Kill(entireProcessTree: true); } catch { }
                worker.Dispose();
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task AssertCliSkipsOrphanWorktreeCleanupAsync(
        string[] args,
        Action<(int ExitCode, string StandardOutput, string StandardError)> assertResult,
        bool seedBacklogItem = false)
    {
        var root = CreateTempDirectory();
        string? backlogId = null;
        try
        {
            InitializeGitRepository(root);
            using var orphanLock = CreateLockedOrphanWorktree(root);
            if (seedBacklogItem)
            {
                var workspace = OrchestratorWorkspace.ForDirectory(root);
                var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Seed backlog item");
                backlogId = item.Id[..8];
            }

            var resolvedArgs = args
                .Select(arg => arg.Equals("{backlog-id}", StringComparison.Ordinal) ? backlogId ?? arg : arg)
                .ToArray();

            var result = RunAppCli(root, resolvedArgs);

            assertResult(result);
            Xunit.Assert.DoesNotContain("worktree-cleanup", result.StandardError, StringComparison.OrdinalIgnoreCase);
            Xunit.Assert.True(Directory.Exists(orphanLock.OrphanPath), "startup cleanup should not touch orphan worktrees for help/backlog-only commands");
            Xunit.Assert.True(File.Exists(orphanLock.LockPath), "startup cleanup should not touch orphan worktree contents for help/backlog-only commands");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static LockedOrphanWorktree CreateLockedOrphanWorktree(string root)
    {
        var orphanPath = Path.Combine(root, GoalWorktrees.DirectoryName, "9458d180");
        var sandboxPath = Path.Combine(orphanPath, ".mcg-sandbox");
        Directory.CreateDirectory(sandboxPath);
        var lockPath = Path.Combine(sandboxPath, "locked.txt");
        var stream = File.Open(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        return new LockedOrphanWorktree(stream, orphanPath, lockPath);
    }

    private sealed class LockedOrphanWorktree(FileStream stream, string orphanPath, string lockPath) : IDisposable
    {
        private readonly FileStream _stream = stream;

        public string OrphanPath { get; } = orphanPath;

        public string LockPath { get; } = lockPath;

        public void Dispose() => _stream.Dispose();
    }

    private static void InitializeGitRepository(string root)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("init");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git init.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            var termination = TryTerminateProcess(process);
            throw new TimeoutException(
                $"git init did not exit within 10 seconds; {termination}. stdout={CompletedOutput(outputTask)} stderr={CompletedOutput(errorTask)}");
        }

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git init failed. stdout={output} stderr={error}");
        }
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunAppCli(
        string workingDirectory,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.EnvironmentVariables[OrchestratorWorkspace.RepoRootEnvironmentVariable] = workingDirectory;
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll"));
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start app CLI.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            var termination = TryTerminateProcess(process);
            throw new TimeoutException(
                $"CLI did not exit for: {string.Join(' ', args)}; {termination}. stdout={CompletedOutput(outputTask)} stderr={CompletedOutput(errorTask)}");
        }

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        return (process.ExitCode, output, error);
    }

    private static string TryTerminateProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            return process.WaitForExit(5000)
                ? "process tree terminated"
                : "process tree did not exit within 5 seconds after termination";
        }
        catch (InvalidOperationException)
        {
            return "process exited before termination";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return $"process termination failed: {ex.Message}";
        }
    }

    private static string CompletedOutput(Task<string> outputTask) =>
        outputTask.IsCompletedSuccessfully ? outputTask.Result : "<stream still open>";
}
