using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliCommandTestsDashboardRemoval : CliCommandTestBase
{
    [Theory]
    [InlineData("dashboard")]
    [InlineData("serve-dashboard")]
    [InlineData("hosted-dashboard")]
    [InlineData("simple-hosted-dashboard")]
    [InlineData("open-dashboard")]
    [InlineData("prototype-ui")]
    [InlineData("transcript")]
    public void RemovedCommandsAndTheirHelpReportUnknownCommand(string command)
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        foreach (var args in new[] { new[] { command }, new[] { "help", command }, new[] { command, "--help" } })
        {
            var error = Assert.ThrowsAny<ArgumentException>(() => ExecuteCliAndCapture(args, kernel, workspace));
            Assert.Contains($"Unknown command '{command}'.", error.Message, StringComparison.Ordinal);
        }
        var help = ExecuteCliAndCapture(["--help"], kernel, workspace);
        Assert.DoesNotContain(command, help.Split([' ', '\r', '\n', ',', '|'], StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("http://localhost:5087")]
    [InlineData("https://localhost:5087")]
    public void MonitorGoalRejectsHttpUrlAsInvalidGoal(string url)
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var error = Assert.ThrowsAny<ArgumentException>(() =>
            ExecuteCliAndCapture(["monitor-goal", url, "abc123"], new AgentOrchestratorKernel(), workspace));
        Assert.Contains($"Invalid goal argument '{url}'.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MonitorGoalRetainsLocalSnapshotAndLifecycleEvents()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Local lifecycle", AgentRole.Developer);
        var goal = kernel.CreateGoal("Local monitor", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Started local work.");

        var output = ExecuteCliAndCapture(["monitor-goal", goal.Id.Value, "--once"], kernel, workspace);

        Assert.Contains("event: goal.snapshot", output, StringComparison.Ordinal);
        Assert.Contains("event: task.status", output, StringComparison.Ordinal);
        Assert.Contains("Started local work.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorChannelRejectsDashboardUrlAndOmitsItFromHelp()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var error = Assert.ThrowsAny<ArgumentException>(() =>
            ExecuteCliAndCapture(["operator-channel", "set", "discord", "--dashboard-url", "http://localhost:5087"], kernel, workspace));
        Assert.Contains("Unknown option '--dashboard-url'", error.Message, StringComparison.Ordinal);
        var help = ExecuteCliAndCapture(["operator-channel", "--help"], kernel, workspace);
        Assert.DoesNotContain("--dashboard-url", help, StringComparison.Ordinal);
    }

    [Fact]
    public void InteractiveCommandBannerOmitsRemovedCommands()
    {
        var program = File.ReadAllText(Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(),
            "src", "Mcg.AgentOrchestrator.App", "Program.cs"));
        foreach (var command in new[]
        {
            "dashboard", "serve-dashboard", "hosted-dashboard", "simple-hosted-dashboard",
            "open-dashboard", "prototype-ui", "transcript"
        })
            Assert.DoesNotMatch($"Console\\.WriteLine\\(\"[^\"\\r\\n]*\\b{command}\\b", program);
    }
}
