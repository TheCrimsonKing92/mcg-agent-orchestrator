using Mcg.AgentOrchestrator.Infrastructure;

public sealed class TestPowerShellResolutionTests
{
    [Fact]
    public void EveryPwshFixtureUsesARealInstalledShell()
    {
        foreach (var executable in new[]
        {
            TestPowerShell.Executable,
            DotnetBuildEnvironmentManagerTests.ResolvePowerShell(),
            TestPowerShell.ForTheoryToken("pwsh")
        })
        {
            Assert.True(Path.IsPathFullyQualified(executable),
                $"Install a real pwsh 7; resolved shell is not absolute: {executable}");
            Assert.True(File.Exists(executable),
                $"Install a real pwsh 7; resolved shell does not exist: {executable}");
            Assert.False(WorkerShell.IsWindowsAppsPath(executable));
        }

        Assert.Equal("powershell.exe", TestPowerShell.ForTheoryToken("powershell.exe"));
    }
}
