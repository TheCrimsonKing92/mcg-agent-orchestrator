using System.Reflection;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: reflection and repository source reads only; no shared state is changed.
public sealed class DashboardRemnantSourceGuardTests
{
    [Fact]
    public void RemovedMembers_Reflection_HasNoDashboardContracts()
    {
        Assert.DoesNotContain("DashboardApi", Enum.GetNames<RepositoryOwnershipArea>());
        Assert.DoesNotContain("DashboardUi", Enum.GetNames<RepositoryOwnershipArea>());
        foreach (var guard in new[] { typeof(ApiPromptCostGuard), typeof(SubscriptionPromptCostGuard) })
            AssertMembersAbsent(guard, "DashboardConfirmationQueryName", "BuildDashboardMessage");
        AssertMembersAbsent(typeof(OrchestratorWorkspace), "DashboardUrlFilePath");
        AssertMembersAbsent(typeof(RepositoryChangeClassifier), "ConductorExcludedPathPrefixes");

        var assembly = typeof(OrchestratorWorkspace).Assembly;
        var architecture = assembly.GetType("Mcg.AgentOrchestrator.App.Application.DistributedArchitectureReport");
        var monitoring = assembly.GetType("Mcg.AgentOrchestrator.App.Application.GoalMonitoringBatch");
        Assert.NotNull(architecture);
        Assert.NotNull(monitoring);
        AssertMembersAbsent(architecture!, "ApiSurfaces", "HostModes", "OperatorControlsEnabled");
        AssertMembersAbsent(monitoring!, "StreamPath");
        AssertMembersAbsent(typeof(NextActionControl), "Label", "Method", "Url");
        foreach (var name in new[]
        {
            "GetRunActionLabel", "GetExplicitApiRunActionLabel", "BuildTaskRunUrl", "BuildExplicitApiRunUrl"
        })
            Assert.Empty(typeof(NextActionControls).GetMember(name));
    }

    [Fact]
    public void SourceFiles_RemovedLiterals_HaveNoDashboardRemnants()
    {
        string[] forbidden =
        [
            "src/Mcg.AgentOrchestrator.App/Dashboard/", "src/Mcg.AgentOrchestrator.Dashboard/",
            "excluded-dashboard", ".dashboard-url", "/api/source-survey", "/api/system/",
            "/api/goals/", "/api/task/", "/api/monitor", "hosted-dashboard",
            "Invoke-DashboardBuildTestCycle", "Run-DashboardBrowserScript",
            "dashboard source survey", "dashboard APIs"
        ];
        var root = VerifiedRepositoryRoot.Find();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(root, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj" or ".scratch" or ".orchestrator-prototype"
                    or ".orchestrator-worktrees" or "TestResults" or "playwright-report"))
            .ToArray();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (var literal in forbidden)
                Assert.False(source.Contains(literal, StringComparison.OrdinalIgnoreCase),
                    $"Removed literal '{literal}' in {Path.GetRelativePath(root, file)}");
        }
    }

    private static void AssertMembersAbsent(Type type, params string[] names)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance;
        foreach (var name in names)
            Assert.Empty(type.GetMember(name, flags));
    }
}
