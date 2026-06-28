using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliHelpTests
{
    [Xunit.Theory(DisplayName = "Cli_help_prints_usage_without_executing_command")]
    [Xunit.InlineData(new[] { "backlog-list", "--help" }, "backlog-list", "--all")]
    [Xunit.InlineData(new[] { "backlog-add", "-h" }, "backlog-add", "--body-file")]
    [Xunit.InlineData(new[] { "backlog-show", "--help" }, "backlog-show", "-h")]
    [Xunit.InlineData(new[] { "backlog-close", "-h" }, "backlog-close", "--reason-file")]
    [Xunit.InlineData(new[] { "conduct", "--help" }, "conduct", "--loop")]
    [Xunit.InlineData(new[] { "workspace", "create", "-h" }, "workspace create", "--help")]
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

    [Xunit.Theory(DisplayName = "Cli_help_startup_exits_zero_before_state_creation")]
    [Xunit.InlineData(new[] { "backlog-list", "--help" }, "backlog-list", "--all")]
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

        var ex = Xunit.Assert.Throws<ArgumentException>(() =>
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
        Xunit.Assert.Contains("Usage: backlog-list [--all]", result.StandardError);
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
                ["backlog-list", "--all"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);

            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains("No backlog items.", output);
    }

    private static string CaptureConsole(Action action)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-cli-help-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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
