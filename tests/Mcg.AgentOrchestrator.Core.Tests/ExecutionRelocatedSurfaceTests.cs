using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: source reads and deterministic path classification; no shared state changes.
public sealed class ExecutionRelocatedSurfaceTests
{
    private const string ExecutionRoot = "src/Mcg.AgentOrchestrator.Execution/";

    [Xunit.Fact]
    public void MovedCanaryAndDispatchSources_ExistAndRetainTriggers()
    {
        var root = FindRepositoryRoot();
        string[] canaryPaths =
        [
            "Processes/TempRootApparatusLossReceipts.cs",
            "Processes/AcceptanceTempRootNames.cs",
            "Verification/AcceptanceTrxOutcomeTaxonomy.cs",
            "Verification/AcceptanceTrxTestIdentityResolver.cs",
            "Processes/DotnetBuildStorageRoot.cs",
            "Processes/DotnetBuildStorageLayout.cs"
        ];
        foreach (var relativePath in canaryPaths)
        {
            var path = ExecutionRoot + relativePath;
            Assert.True(File.Exists(Path.Combine(root, path)), path);
            Assert.True(PostLandingCanaryTrigger.Evaluate([path]).ShouldRun, path);
        }

        string[] dispatchPaths =
        [
            "Processes/BackgroundDispatchRunner.cs",
            "Processes/DispatchProcessHost.cs",
            "Processes/GracefulDispatchDetacher.cs",
            "Processes/ProcessLogReader.Decisions.cs",
            "Processes/WorkerProcessJobs.cs",
            "Workers/WorkerResultParser.cs"
        ];
        foreach (var relativePath in dispatchPaths)
        {
            var path = ExecutionRoot + relativePath;
            Assert.True(File.Exists(Path.Combine(root, path)), path);
            Assert.True(RepositoryChangeClassifier.TouchesDispatchResultHandling([path]), path);
        }
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "") =>
        VerifiedRepositoryRoot.TryGetVerifiedRoot(out var root)
            ? root
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
