using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: reads the verified source root without mutating files or process state.
public sealed class ExecutionOwnedGroupCloseSourceGuardTests
{
    [Fact]
    public void OwnedGroupClose_UsesAccountingHelperAcrossMovedSources()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var productionRoot = Path.Combine(root, "src", "Mcg.AgentOrchestrator.Execution");
        Assert.True(Directory.Exists(productionRoot), productionRoot);
        var snapshot = RepositorySourceSnapshotCache.Get(root);
        var files = SourceFiles(root).ToArray();
        Assert.NotEmpty(files);
        Assert.Contains(files, path => path.EndsWith(Path.Combine("Processes", "WorkerProcessJobs.cs"),
            StringComparison.OrdinalIgnoreCase));
        string[] closeTokens =
        [
            "workerGroup?.Dispose()", "workerGroup?.Kill()", "processGroup?.Dispose()",
            "processGroup?.Kill()", "group.Dispose()", "group.Kill()"
        ];
        var offenders = new List<string>();
        foreach (var path in files)
        {
            if (path.EndsWith("OwnedProcessGroup.cs", StringComparison.OrdinalIgnoreCase))
                continue;
            var lines = snapshot.GetLines(path).ToArray();
            var isJobs = path.EndsWith(Path.Combine("Processes", "WorkerProcessJobs.cs"),
                StringComparison.OrdinalIgnoreCase);
            var start = Array.FindIndex(lines, line => line.Contains(
                "internal static bool ReadAccountingAndDispose(", StringComparison.Ordinal));
            var end = Array.FindIndex(lines, line => line.Contains(
                "internal static bool HasRegisteredJob", StringComparison.Ordinal));
            if (isJobs)
                Assert.True(start >= 0 && end > start, "Missing accounting helper boundaries");
            for (var index = 0; index < lines.Length; index++)
            {
                if (isJobs && index >= start && index < end)
                    continue;
                if (closeTokens.Any(token => lines[index].Contains(token, StringComparison.Ordinal)) &&
                    !lines[index].Contains("ReadAccountingAndDispose", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetRelativePath(root, path)}:{index + 1}:{lines[index].Trim()}");
            }
        }
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }
    internal static IEnumerable<string> SourceFiles(string root)
    {
        var productionRoot = Path.Combine(root, "src", "Mcg.AgentOrchestrator.Execution");
        return RepositorySourceSnapshotCache.Get(root).EnumerateFiles(productionRoot)
            .Where(path => !SourceGuardFileFilter.BuildOutputAndArtifacts.Excludes(Path.GetRelativePath(productionRoot, path)));
    }
}
