using System.Runtime.CompilerServices;
using Xunit;

// Parallel-safe: reads source from the explicitly verified repository without changing it.
public sealed class AcceptanceDotnetBuildPhaseOwnershipTests
{
    private const string WorkspaceDirectory = "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces";

    [Fact]
    public void BuildPhaseHasAnIndependentOwner()
    {
        var path = Path.Combine(FindRepositoryRoot(), WorkspaceDirectory, "GoalAcceptanceVerifier.DotnetBuildPhase.cs");
        Assert.True(File.Exists(path), $"Build-phase owner source is missing: {path}");
        var source = File.ReadAllText(path);

        Assert.Contains("class AcceptanceDotnetBuildPhase", source, StringComparison.Ordinal);
        foreach (var forbidden in new[]
        {
            "partial class GoalAcceptanceVerifier",
            "GoalAcceptanceVerifier",
            "GoalAcceptanceVerifierTestOverrides",
            "_testOverrides"
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("record DotnetBaseBuildCachePlan")]
    [InlineData("class DotnetTestBuildPhase")]
    [InlineData("TryRunCachedDotnetTestBuildPhaseAsync")]
    [InlineData("TryCreateBaseBuildCachePlan")]
    [InlineData("BuildBaseBuildCacheSummary")]
    [InlineData("BuildDotnetProjectBuildArguments")]
    [InlineData("CacheableProjects")]
    public void VerifierDelegatesBuildPhaseOwnership(string forbidden)
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), WorkspaceDirectory, "GoalAcceptanceVerifier.cs"));
        Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate repository root from source file path '{sourceFilePath}'.");
    }
}
