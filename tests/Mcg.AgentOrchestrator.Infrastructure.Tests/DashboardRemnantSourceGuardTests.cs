// Parallel-safe: repository source reads only; no shared state is changed.
public sealed class DashboardRemnantSourceGuardTests
{
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
        var snapshot = RepositorySourceSnapshotCache.Get(root);
        var files = SourceFiles(root).ToArray();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var source = snapshot.GetText(file);
            foreach (var literal in forbidden)
                Assert.False(source.Contains(literal, StringComparison.OrdinalIgnoreCase),
                    $"Removed literal '{literal}' in {Path.GetRelativePath(root, file)}");
        }
    }
    internal static IEnumerable<string> SourceFiles(string root) =>
        RepositorySourceSnapshotCache.Get(root).EnumerateFiles(Path.Combine(root, "src"))
            .Where(file => !SourceGuardFileFilter.WorkspaceAndReports.Excludes(Path.GetRelativePath(root, file)));
}
