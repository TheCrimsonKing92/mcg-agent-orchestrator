// Parallel-safe: repository source reads only; no filesystem state is modified.
public sealed class LegacySparkNameSourceGuardTests
{
    [Fact]
    public void SourceFiles_RetiredSpellings_AppearOnlyInReadAliasHome()
    {
        var root = VerifiedRepositoryRoot.Find();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(root, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj" or ".scratch" or ".orchestrator-prototype"
                    or ".orchestrator-worktrees" or "TestResults" or "playwright-report"))
            .ToDictionary(file => Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText);

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
}
