// Parallel-safe: repository source reads only; no shared state is changed.
public sealed class ExecutionWorkspacesReferenceGuardTests
{
    [Fact]
    public void ProcessesAndWorkersSources_DoNotReferenceBuildEnvironmentManagerOrAcceptanceVerifier()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var offenders = new List<string>();
        foreach (var area in new[] { "Processes", "Workers" })
        {
            var directory = Path.Combine(root, "src", "Mcg.AgentOrchestrator.Infrastructure", area);
            Assert.True(Directory.Exists(directory), $"Missing source directory: {directory}");
            var files = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(file => !Path.GetRelativePath(root, file)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment is "bin" or "obj" or ".scratch" or ".orchestrator-prototype"
                        or ".orchestrator-worktrees" or "TestResults" or "playwright-report"))
                .ToArray();
            Assert.NotEmpty(files);
            offenders.AddRange(files
                .Where(file => Path.GetRelativePath(root, file).Replace('\\', '/') !=
                    "src/Mcg.AgentOrchestrator.Infrastructure/Processes/LocalProcessVerifier.cs")
                .Where(file =>
                {
                    var source = File.ReadAllText(file);
                    return source.Contains("DotnetBuildEnvironmentManager.", StringComparison.Ordinal) ||
                        source.Contains("GoalAcceptanceVerifier.", StringComparison.Ordinal);
                })
                .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/')));
        }

        Assert.True(offenders.Count == 0,
            $"Processes and Workers must not reference DotnetBuildEnvironmentManager or GoalAcceptanceVerifier. Offending files: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void ProcessesAndWorkersSources_DoNotReferenceGoalWorktrees()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var offenders = new List<string>();
        foreach (var area in new[] { "Processes", "Workers" })
        {
            var directory = Path.Combine(root, "src", "Mcg.AgentOrchestrator.Infrastructure", area);
            Assert.True(Directory.Exists(directory), $"Missing source directory: {directory}");
            var files = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(file => !Path.GetRelativePath(root, file)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment is "bin" or "obj" or ".scratch" or ".orchestrator-prototype"
                        or ".orchestrator-worktrees" or "TestResults" or "playwright-report"))
                .ToArray();
            Assert.NotEmpty(files);
            offenders.AddRange(files
                .Where(file => File.ReadAllText(file).Contains("GoalWorktrees.", StringComparison.Ordinal))
                .Select(file => Path.GetRelativePath(root, file)));
        }

        Assert.True(offenders.Count == 0,
            $"Processes and Workers must not reference GoalWorktrees. Offending files: {string.Join(", ", offenders)}");
    }
}
