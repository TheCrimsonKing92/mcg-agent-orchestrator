using Mcg.AgentOrchestrator.Infrastructure;
using System.Reflection;

public sealed class BackgroundDispatchRunnerProcessSnapshotTests
{
    [Xunit.Fact]
    public void BuildDaemonKillReceivesDiscoveredIdentity()
    {
        var method = typeof(BackgroundDispatchRunner).GetMethod(
            "TryKillBuildDaemonProcess",
            BindingFlags.NonPublic | BindingFlags.Static);

        Xunit.Assert.NotNull(method);
        var parameter = Xunit.Assert.Single(method!.GetParameters());
        Xunit.Assert.Equal(typeof(ProcessInspectionRecord), parameter.ParameterType);
    }

    [Xunit.Fact]
    public void RecycledBuildDaemonIdentityRefusesKill()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-26T12:00:00Z");
        var discovered = AvailableRecord(startedAt);
        var recycled = AvailableRecord(startedAt.AddSeconds(1));
        var killCalls = 0;

        var killed = BackgroundDispatchRunner.TryKillRevalidatedBuildDaemon(
            discovered,
            recycled,
            _ =>
            {
                killCalls++;
                return true;
            });

        Xunit.Assert.False(killed);
        Xunit.Assert.Equal(0, killCalls);
    }

    [Xunit.Fact]
    public void UnavailableBuildDaemonIdentityRefusesKill()
    {
        var discovered = AvailableRecord(DateTimeOffset.Parse("2026-08-26T12:00:00Z"));
        var unavailable = discovered with
        {
            CommandLine = null,
            Status = ProcessInspectionStatus.AccessDenied
        };
        var killCalls = 0;

        var killed = BackgroundDispatchRunner.TryKillRevalidatedBuildDaemon(
            discovered,
            unavailable,
            _ =>
            {
                killCalls++;
                return true;
            });

        Xunit.Assert.False(killed);
        Xunit.Assert.Equal(0, killCalls);
    }

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

    private static ProcessInspectionRecord AvailableRecord(DateTimeOffset startedAt) =>
        new(
            17,
            1,
            "MSBuild",
            "C:\\Program Files\\dotnet\\sdk\\MSBuild.exe",
            startedAt,
            "MSBuild.exe C:\\repo\\project.csproj",
            ProcessInspectionStatus.Available);
}
