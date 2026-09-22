using Xunit;
using Mcg.AgentOrchestrator.App.Cli;

public sealed class OptionalDashboardLaunchTests
{
    [Fact]
    public void MissingDashboardComponentReturnsActionableNonDestructiveResult()
    {
        var directory = Path.Combine(Path.GetTempPath(), "missing-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var error = new StringWriter();
        try
        {
            var exitCode = OptionalDashboardHostLauncher.Run(["open-dashboard"], directory, error);

            Assert.Equal(OptionalDashboardHostLauncher.MissingComponentExitCode, exitCode);
            Assert.Contains("scripts/publish-dashboard.ps1", error.ToString(), StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void InstalledDashboardPropagatesChildExitCode()
    {
        var directory = Path.Combine(Path.GetTempPath(), "present-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            File.Copy(commandInterpreter, Path.Combine(directory, "Mcg.AgentOrchestrator.Dashboard.exe"));

            var exitCode = OptionalDashboardHostLauncher.Run(["/d", "/c", "exit", "23"], directory);

            Assert.Equal(23, exitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InstalledButUnlaunchableDashboardReturnsActionableFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "broken-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var error = new StringWriter();
        try
        {
            var componentPath = Path.Combine(directory, "Mcg.AgentOrchestrator.Dashboard.exe");
            File.WriteAllText(componentPath, "not an executable");

            var exitCode = OptionalDashboardHostLauncher.Run(["open-dashboard"], directory, error);

            Assert.Equal(OptionalDashboardHostLauncher.MissingComponentExitCode, exitCode);
            Assert.Contains(componentPath, error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("could not be started", error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
