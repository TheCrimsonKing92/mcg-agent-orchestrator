using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BackgroundDispatchRunnerProcessSnapshotTests
{
    [Xunit.Fact]
    public void BuildDaemonDiscoveryCreatesOneCandidateSnapshot()
    {
        var calls = 0;
        IReadOnlyCollection<string>? requestedNames = null;
        var workingDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mcg-background-snapshot"));

        var matches = BackgroundDispatchRunner.FindBuildDaemons(
            workingDirectory,
            names =>
            {
                calls++;
                requestedNames = names;
                return new ProcessCommandLineSnapshot(
                    new Dictionary<int, ProcessInspectionRecord>
                    {
                        [17] = new ProcessInspectionRecord(
                            17,
                            1,
                            "MSBuild",
                            "C:\\Program Files\\dotnet\\sdk\\MSBuild.exe",
                            DateTimeOffset.UtcNow,
                            $"MSBuild.exe \"{workingDirectory}\\project.csproj\"",
                            ProcessInspectionStatus.Available)
                    });
            });

        Xunit.Assert.Equal(1, calls);
        Xunit.Assert.NotNull(requestedNames);
        Xunit.Assert.Contains("MSBuild", requestedNames, StringComparer.OrdinalIgnoreCase);
        Xunit.Assert.Single(matches);
        Xunit.Assert.Equal(17, matches[0].ProcessId);
    }
}
