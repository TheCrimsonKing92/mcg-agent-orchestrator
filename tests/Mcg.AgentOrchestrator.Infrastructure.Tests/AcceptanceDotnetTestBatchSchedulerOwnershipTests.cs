using System.Runtime.CompilerServices;
using Xunit;

// Parallel-safe: only reads source from the explicitly verified repository.
public sealed class AcceptanceDotnetTestBatchSchedulerOwnershipTests
{
    private const string WorkspaceDirectory = "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces";

    [Fact]
    public void SchedulerHasAnIndependentOwner()
    {
        Assert.Contains("static class AcceptanceDotnetTestBatchScheduler", ReadOwner(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("partial class GoalAcceptanceVerifier")]
    [InlineData("GoalAcceptanceVerifier")]
    [InlineData("AcceptanceManifestCheck")]
    [InlineData("CheckBatchResult")]
    [InlineData("AcceptancePartitionVerdictCache")]
    [InlineData("DotnetTestBuildPhase")]
    [InlineData("_testOverrides")]
    public void OwnerDoesNotNameVerifierState(string forbidden)
    {
        Assert.DoesNotContain(forbidden, ReadOwner(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AcceptanceDotnetTestBatchScheduler.RunAsync(")]
    [InlineData("AcceptanceDotnetTestBatchScheduler.CanOverlap(")]
    public void VerifierDelegatesScheduling(string required)
    {
        Assert.Contains(required, ReadAdapter(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("new SemaphoreSlim(")]
    [InlineData("ConcurrentQueue")]
    [InlineData("ExceptionDispatchInfo")]
    [InlineData("Task.WhenAny(")]
    public void VerifierDoesNotOwnBatchExecution(string forbidden)
    {
        Assert.DoesNotContain(forbidden, ReadAdapter(), StringComparison.Ordinal);
    }

    private static string ReadOwner()
    {
        var path = Path.Combine(FindRepositoryRoot(), WorkspaceDirectory, "GoalAcceptanceVerifier.DotnetTestBatchScheduler.cs");
        Assert.True(File.Exists(path), $"Scheduler owner source is missing: {path}");
        return File.ReadAllText(path);
    }

    private static string ReadAdapter() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(), WorkspaceDirectory, "GoalAcceptanceVerifier.DotnetTestBatchScheduling.cs"));

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
