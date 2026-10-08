using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using static InfrastructureTestSupport;

// Parallel-safe: each backlog owns a unique temporary root; process queries use injected snapshots.
public sealed class RemovedDashboardKeywordFallthroughTests
{
    [Theory]
    [InlineData("dashboard")]
    [InlineData("operator inbox")]
    [InlineData("work-summary")]
    public void BacklogIntake_OmitsRetiredRenderingCheck(string surface)
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, $"## Polish {surface}\n\nPolish the {surface} view. Done when focused checks pass.");

        var plan = BacklogIntakePlanner.Build(BacklogStorePathFor(root), maxItems: 1);

        var item = Assert.Single(plan.Items);
        Assert.Equal($"Polish {surface}", item.Heading);
        Assert.Equal(["Focused unit tests for changed planner/command/mapper behavior."], item.Verification);
        Assert.DoesNotContain(item.Verification, check =>
            check.Contains("Dashboard rendering", StringComparison.OrdinalIgnoreCase));
    }

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
