using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RepoProcessCliCommandTests
{
    [Xunit.Fact]
    public void StopRevalidation_RecycledPid_RefusesAction()
    {
        var recorded = Snapshot(DateTimeOffset.Parse("2026-08-26T12:00:00Z"));
        var recycled = Snapshot(DateTimeOffset.Parse("2026-08-26T12:01:00Z"));

        var reason = RepoProcessCliCommand.EvaluateStopRevalidation(recorded, recycled, ["conduct"]);

        Assert.Equal("identity-mismatch", reason);
    }

    [Xunit.Fact]
    public void StopRevalidation_UnreadableProcess_RefusesAction()
    {
        var recorded = Snapshot(DateTimeOffset.Parse("2026-08-26T12:00:00Z"));
        var unreadable = recorded with
        {
            CommandLine = null,
            InspectionStatus = ProcessInspectionStatus.AccessDenied
        };

        var reason = RepoProcessCliCommand.EvaluateStopRevalidation(recorded, unreadable, ["conduct"]);

        Assert.Equal("inspection-unavailable", reason);
    }

    private static RepoProcessCliCommand.ProcessSnapshot Snapshot(DateTimeOffset startedAt) =>
        new(
            42,
            1,
            "dotnet",
            Path.Combine(Path.GetTempPath(), "dotnet.exe"),
            startedAt,
            "dotnet App.dll conduct --loop",
            ProcessInspectionStatus.Available);
}
