// Parallel-safe: repository source reads only; no filesystem state is modified.
public sealed class LegacySparkNameSourceGuardTests
{
    [Fact]
    public void SourceFiles_RetiredSpellings_AppearOnlyInReadAliasHome()
    {
        var root = VerifiedRepositoryRoot.Find();
        var snapshot = RepositorySourceSnapshotCache.Get(root);
        var files = SourceFiles(root)
            .ToDictionary(file => Path.GetRelativePath(root, file).Replace('\\', '/'), snapshot.GetText);

        const string aliasPath = "src/Mcg.AgentOrchestrator.Core/Domain/LunaLaneNames.cs";
        const string runnerPath = "src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs";
        Assert.NotEmpty(files);
        Assert.True(files.ContainsKey(aliasPath), $"Source enumeration missed {aliasPath}");
        Assert.True(files.ContainsKey(runnerPath), $"Source enumeration missed {runnerPath}");
        Assert.Contains("codex-spark", files[aliasPath], StringComparison.Ordinal);
        Assert.Contains("OpenAICodexSpark", files[aliasPath], StringComparison.Ordinal);
        Assert.Contains("IsParkedGoalSafetyNetSweepTick", files[runnerPath], StringComparison.Ordinal);

        foreach (var (path, source) in files)
        {
            if (path == aliasPath)
                continue;
            foreach (var spelling in new[] { "spark", "Spark", "SPARK" })
                Assert.False(source.Contains(spelling, StringComparison.Ordinal), $"Retired spelling '{spelling}' in {path}");
        }
    }
    internal static IEnumerable<string> SourceFiles(string root) =>
        RepositorySourceSnapshotCache.Get(root).EnumerateFiles(Path.Combine(root, "src"))
            .Where(file => !SourceGuardFileFilter.WorkspaceAndReports.Excludes(Path.GetRelativePath(root, file)));
}
