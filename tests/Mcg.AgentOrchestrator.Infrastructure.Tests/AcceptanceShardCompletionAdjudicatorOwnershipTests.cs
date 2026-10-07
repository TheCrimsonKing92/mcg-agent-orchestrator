using System.Runtime.CompilerServices;
using Xunit;

// Parallel-safe: reads source from the explicitly verified repository without changing it.
public sealed class AcceptanceShardCompletionAdjudicatorOwnershipTests
{
    private const string WorkspaceDirectory = "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces";

    [Fact]
    public void ShardCompletionHasAnIndependentOwner()
    {
        var path = Path.Combine(FindRepositoryRoot(), WorkspaceDirectory, "GoalAcceptanceVerifier.ShardCompletionAdjudication.cs");
        Assert.True(File.Exists(path), $"Shard-completion owner source is missing: {path}");
        var source = File.ReadAllText(path);

        Assert.Contains("static class AcceptanceShardCompletionAdjudicator", source, StringComparison.Ordinal);
        Assert.Contains("record TrxCompletionEvidence(", source, StringComparison.Ordinal);
        foreach (var forbidden in new[]
        {
            "partial class GoalAcceptanceVerifier",
            "GoalAcceptanceVerifier",
            "CommandResult",
            "AcceptanceManifestCheck",
            "_testOverrides"
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("record TrxCompletionEvidence(")]
    [InlineData("new AcceptanceShardCompletionDecision(")]
    public void VerifierDelegatesCompletionOwnership(string forbidden)
    {
        var source = ReadSource("GoalAcceptanceVerifier.cs");
        Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("new AcceptanceShardCompletionDecision(")]
    [InlineData("IsPassingTrxReceipt")]
    [InlineData("TryReadTrxCounter")]
    [InlineData("GoalAcceptanceVerifier.TrxCompletionEvidence")]
    public void TelemetryDelegatesCompletionOwnership(string forbidden)
    {
        var source = ReadSource("GoalAcceptanceVerifier.TestTelemetry.cs");
        Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    private static string ReadSource(string filename) =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), WorkspaceDirectory, filename));

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
