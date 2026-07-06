using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliHelpTests
{
    [Xunit.Theory(DisplayName = "Cli_help_prints_usage_without_executing_command")]
    [Xunit.InlineData(new[] { "backlog-list", "--help" }, "backlog-list", "--limit <n>")]
    [Xunit.InlineData(new[] { "backlog-add", "-h" }, "backlog-add", "--body-file")]
    [Xunit.InlineData(new[] { "backlog-show", "--help" }, "backlog-show", "-h")]
    [Xunit.InlineData(new[] { "backlog-close", "-h" }, "backlog-close", "--reason-file")]
    [Xunit.InlineData(new[] { "backlog-reopen", "--help" }, "backlog-reopen", "-h")]
    [Xunit.InlineData(new[] { "conduct", "--help" }, "conduct", "--loop")]
    [Xunit.InlineData(new[] { "workspace", "create", "-h" }, "workspace create", "--help")]
    [Xunit.InlineData(new[] { "status", "--help" }, "status", "-h")]
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
    [Xunit.InlineData(new[] { "goal", "--help" }, "goal", "--simple")]
    [Xunit.InlineData(new[] { "backlog-list", "--help" }, "backlog-list", "--limit <n>")]
    [Xunit.InlineData(new[] { "backlog-add", "-h" }, "backlog-add", "--body-file")]
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
                Xunit.Assert.Contains("No matching open backlog items.", result.StandardOutput);
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

    [Xunit.Fact(DisplayName = "Cli_invalid_flag_startup_exits_one_with_usage_on_stderr")]
    public void CliInvalidFlagStartupExitsOneWithUsageOnStderr()
    {
        var root = CreateTempDirectory();

        var result = RunAppCli(root, ["backlog-list", "--frobnitz"]);

        Xunit.Assert.Equal(1, result.ExitCode);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(result.StandardOutput), result.StandardOutput);
        Xunit.Assert.Contains("Error: Unknown option '--frobnitz'.", result.StandardError);
        Xunit.Assert.Contains("Usage: backlog-list [--all] [--limit <n>] [--status <value>] [--text <pattern>]", result.StandardError);
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

        Xunit.Assert.Contains("No matching backlog items.", output);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-cli-help-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10000) || process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git init failed. stdout={output} stderr={error}");
        }
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunAppCli(
        string workingDirectory,
        IReadOnlyList<string> args)
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
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll"));
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start app CLI.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"CLI did not exit for: {string.Join(' ', args)}");
        }

        return (process.ExitCode, output, error);
    }
}
