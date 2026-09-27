using Mcg.AgentOrchestrator.App.Cli;

public sealed class ConductorVerbStopTests
{
    [Fact]
    public void StopCreatesRequestOnlyForRunningOwnerAndLeavesGoalAliasesDistinct()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = Path.Combine(fixture.Workspace.ExecutionDirectory, ".conduct-stop");
        using var output = new StringWriter();
        Assert.Equal(0, CliConductorCommand.Run(["conductor", "stop"], fixture.Workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(null), output: output));
        Assert.False(File.Exists(path));
        Assert.Contains("No conductor is running", output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, CliConductorCommand.Run(["conductor", "stop"], fixture.Workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(7654), output: output));
        Assert.True(File.Exists(path));
        Assert.Contains("7654", output.ToString());
        Assert.Contains("detach, not a drain", output.ToString());
        Assert.False(CliConductorCommand.IsCommand(["status", "abcdef12"]));
        Assert.False(CliConductorCommand.IsCommand(["stop", "abcdef12", "r", "--as", "cancel"]));
    }
}
