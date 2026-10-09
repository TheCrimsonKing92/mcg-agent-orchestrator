using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: process queries use injected snapshots.
public sealed class RemovedDashboardKeywordFallthroughTests
{
    [Theory]
    [InlineData("status", "app-host")]
    [InlineData("serve-dashboard", "app-host")]
    [InlineData("-dashboard", "app-host")]
    [InlineData("conduct --loop serve-dashboard", "conduct-loop")]
    [InlineData("__dispatch-run serve-dashboard", "dispatch-host")]
    [InlineData("DispatchProcessHost serve-dashboard", "dispatch-host")]
    public void LockReport_UsesExistingProcessKinds(string arguments, string expectedKind)
    {
        var snapshot = new RepoProcessCliCommand.ProcessSnapshot(
            4242,
            1,
            "dotnet",
            @"C:\dotnet.exe",
            DateTimeOffset.Parse("2026-08-26T12:00:00Z"),
            $"dotnet Mcg.AgentOrchestrator.App.dll {arguments}",
            ProcessInspectionStatus.Available);
        using var output = new StringWriter();

        RepoProcessCliCommand.PrintInfo(
            ["repo-process-info", "--locks"],
            output,
            _ => [snapshot]);

        Assert.Contains($"LOCK id=4242 kind={expectedKind} parent=1", output.ToString());
        Assert.DoesNotContain("kind=dashboard", output.ToString());
    }
}
