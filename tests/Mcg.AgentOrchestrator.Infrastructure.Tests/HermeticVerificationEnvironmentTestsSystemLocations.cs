using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class HermeticVerificationEnvironmentTestsSystemLocations : GoalAcceptanceVerifierTestBase
{
    private static readonly string[] SystemLocations =
    [
        "SystemDrive", "ProgramData", "ALLUSERSPROFILE", "PUBLIC",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432"
    ];

    [Xunit.Fact]
    public void AllSystemLocationsAreDeclaredAndCopiedFromTheSuppliedEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SystemLocations)
            environment[name] = @"Q:\probe-" + name;

        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(environment, Path.GetTempPath());

        foreach (var name in SystemLocations)
        {
            Assert.True(GoalAcceptanceVerifier.IsHermeticVerificationEnvironmentVariable(name), name);
            Assert.Equal(@"Q:\probe-" + name, environment[name]);
        }
    }

    [Xunit.Fact]
    public async Task RealGateChildExpandsSystemDriveOutsideItsWorkingDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateTempDirectory();
        var originalDrive = Environment.GetEnvironmentVariable("SystemDrive");
        var expectedDrive = originalDrive ?? Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        try
        {
            Environment.SetEnvironmentVariable("SystemDrive", expectedDrive);
            var observed = await GoalAcceptanceVerifier.RunProcessForTestsAsync(
                ["powershell", "-NoProfile", "-NonInteractive", "-Command",
                    "[Console]::Write([Environment]::GetEnvironmentVariable('SystemDrive'))"],
                root, TimeSpan.FromSeconds(20));
            var expanded = await GoalAcceptanceVerifier.RunProcessForTestsAsync(
                ["powershell", "-NoProfile", "-NonInteractive", "-Command",
                    @"[Console]::Write([Environment]::ExpandEnvironmentVariables('%SystemDrive%\ProgramData'))"],
                root, TimeSpan.FromSeconds(20));

            Assert.Equal(0, observed.ExitCode);
            Assert.Equal(0, expanded.ExitCode);
            Assert.Equal(expectedDrive, observed.Output.Trim(), ignoreCase: true);
            var path = expanded.Output.Trim();
            Assert.Equal(Path.Combine(expectedDrive + @"\", "ProgramData"), path, ignoreCase: true);
            Assert.True(Path.IsPathFullyQualified(path), path);
            Assert.False(path.StartsWith(root, StringComparison.OrdinalIgnoreCase), path);
            Assert.DoesNotContain('%', path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SystemDrive", originalDrive);
            DeleteDirectoryWithRetry(root);
        }
    }
}
