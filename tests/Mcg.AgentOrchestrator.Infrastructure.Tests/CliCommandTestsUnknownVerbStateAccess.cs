using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class CliCommandTestsUnknownVerbStateAccess : CliCommandTestBase
{
    [Theory(DisplayName = "Unknown startup verbs exit with usage before creating state")]
    [InlineData(new[] { "stauts" }, "stauts")]
    [InlineData(new[] { "goal-show", "abc" }, "goal-show")]
    [InlineData(new[] { "backlog", "list" }, "backlog list")]
    public void UnknownVerbDoesNotCreateState(string[] args, string commandName)
    {
        var root = CreateTempDirectory();
        var statePath = OrchestratorWorkspace.ForDirectory(root).SqliteStatePath;
        Assert.False(File.Exists(statePath));

        var result = RunAppCli(root, args);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains($"Error: Unknown command '{commandName}'.", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("--help", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(statePath), $"Unknown verb created state at {statePath}");
    }

    [Fact(DisplayName = "Handled goals command retains state initialization")]
    public void GoalsStillCreatesState()
    {
        var root = CreateTempDirectory();
        var statePath = OrchestratorWorkspace.ForDirectory(root).SqliteStatePath;
        Assert.False(File.Exists(statePath));

        var result = RunAppCli(root, ["goals"]);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.True(File.Exists(statePath), $"Handled goals command did not create state at {statePath}");
    }
}
