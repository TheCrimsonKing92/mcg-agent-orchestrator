using System.Reflection;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: deterministic classification and repository source reads only.
public sealed class RetiredInfrastructureFolderKeyTests
{
    private const string InfrastructureRoot = "src/Mcg.AgentOrchestrator.Infrastructure/";
    private const string ExecutionRoot = "src/Mcg.AgentOrchestrator.Execution/";

    [Xunit.Fact]
    public void OldPaths_DoNotTrigger_ExecutionTwinsRetainTheirSurfaces()
    {
        string[] dispatchPaths =
        [
            "Processes/BackgroundDispatchRunner.cs",
            "Processes/DispatchProcessHost.cs",
            "Processes/GracefulDispatchDetacher.cs",
            "Processes/ProcessLogReader.Decisions.cs",
            "Processes/WorkerProcessJobs.cs",
            "Workers/WorkerResultParser.cs"
        ];
        (string Path, string Surface)[] canaryPaths =
        [
            ("Processes/TempRootApparatusLossReceipts.cs", "acceptance-verifier"),
            ("Processes/AcceptanceTempRootNames.cs", "acceptance-verifier"),
            ("Verification/AcceptanceTrxOutcomeTaxonomy.cs", "acceptance-verifier"),
            ("Verification/AcceptanceTrxTestIdentityResolver.cs", "acceptance-verifier"),
            ("Processes/DotnetBuildStorageRoot.cs", "build-environment"),
            ("Processes/DotnetBuildStorageLayout.cs", "build-environment")
        ];

        foreach (var relativePath in dispatchPaths.Concat(canaryPaths.Select(entry => entry.Path)))
        {
            var oldPath = InfrastructureRoot + relativePath;
            Assert.False(RepositoryChangeClassifier.TouchesDispatchResultHandling([oldPath]), oldPath);
            Assert.False(PostLandingCanaryTrigger.Evaluate([oldPath]).ShouldRun, oldPath);
        }

        foreach (var relativePath in dispatchPaths)
        {
            var executionPath = ExecutionRoot + relativePath;
            Assert.True(RepositoryChangeClassifier.TouchesDispatchResultHandling([executionPath]), executionPath);
        }

        foreach (var (relativePath, surfaceName) in canaryPaths)
        {
            var executionPath = ExecutionRoot + relativePath;
            Assert.Equal(executionPath, Assert.Single(PostLandingCanaryTrigger.Evaluate([executionPath]).TriggeringPaths));
            var surface = Assert.Single(AcceptanceEngineSurfaceRegistry.Surfaces, entry => entry.Name == surfaceName);
            Assert.Contains(executionPath[..^3], surface.PathPrefixes);
        }
    }

    [Xunit.Fact]
    public void RetiredFolders_AreEmpty_AndRemainingKeysMatchRepositoryFiles()
    {
        var candidate = VerifiedRepositoryRoot.Find();
        Assert.True(VerifiedRepositoryRoot.TryGetVerifiedRoot(candidate, out var root),
            $"Could not verify repository root: {candidate}");

        foreach (var folder in new[] { "Processes", "Workers", "Persistence", "Verification" })
        {
            var directory = Path.Combine(root, InfrastructureRoot + folder);
            Assert.True(!Directory.Exists(directory) || !Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any(),
                $"Retired Infrastructure folder contains files: {directory}");
        }

        var field = typeof(RepositoryChangeClassifier).GetField(
            "DispatchResultHandlingPaths", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var rows = Assert.IsType<(string Directory, string Stem)[]>(field.GetValue(null));
        Assert.NotEmpty(rows);
        foreach (var (directory, stem) in rows)
        {
            var absoluteDirectory = Path.Combine(root, directory);
            Assert.True(Directory.Exists(absoluteDirectory) &&
                Directory.EnumerateFiles(absoluteDirectory, "*.cs", SearchOption.TopDirectoryOnly)
                    .Any(file => Path.GetFileName(file).StartsWith(stem, StringComparison.OrdinalIgnoreCase)),
                $"Dispatch-handling key matches no source file: {directory}{stem}");
        }

        Assert.NotEmpty(PostLandingCanaryTrigger.EnginePathPrefixes);
        foreach (var prefix in PostLandingCanaryTrigger.EnginePathPrefixes)
        {
            Assert.True(PrefixMatchesFile(root, prefix), $"Canary prefix matches no repository file: {prefix}");
        }
    }

    private static bool PrefixMatchesFile(string root, string prefix)
    {
        var absolutePath = Path.Combine(root, prefix);
        if (File.Exists(absolutePath))
            return true;
        if (Directory.Exists(absolutePath) && Directory.EnumerateFiles(absolutePath, "*", SearchOption.AllDirectories).Any())
            return true;
        // Keys may name partial-file stems containing dots, such as CandidateTree.
        var parent = Path.GetDirectoryName(absolutePath)!;
        return Directory.Exists(parent) && Directory.EnumerateFiles(parent, "*", SearchOption.TopDirectoryOnly)
            .Any(file => Path.GetFileName(file).StartsWith(Path.GetFileName(absolutePath), StringComparison.OrdinalIgnoreCase));
    }
}
