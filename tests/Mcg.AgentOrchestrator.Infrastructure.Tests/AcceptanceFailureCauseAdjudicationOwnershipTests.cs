using System.Runtime.CompilerServices;
using Xunit;

// Parallel-safe: reads source from the explicitly verified repository without changing it.
public sealed class AcceptanceFailureCauseAdjudicationOwnershipTests
{
    private const string WorkspaceDirectory = "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces";

    [Fact]
    public void FailureCauseAdjudicationHasAnIndependentOwner()
    {
        var path = Path.Combine(FindRepositoryRoot(), WorkspaceDirectory, "GoalAcceptanceVerifier.FailureCauseAdjudication.cs");
        Assert.True(File.Exists(path), $"Failure-cause owner source is missing: {path}");
        var source = File.ReadAllText(path);

        Assert.Contains("static class AcceptanceFailureCauseAdjudicator", source, StringComparison.Ordinal);
        Assert.Contains("record AcceptanceFailureCauseReceiptV1", source, StringComparison.Ordinal);
        Assert.Contains("static class AcceptanceFailureCauseReceiptCodec", source, StringComparison.Ordinal);
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

    [Fact]
    public void VerifierDelegatesFailureCauseAdjudication()
    {
        var path = Path.Combine(FindRepositoryRoot(), WorkspaceDirectory, "GoalAcceptanceVerifier.cs");
        Assert.True(File.Exists(path), $"Verifier source is missing: {path}");
        var source = File.ReadAllText(path);

        Assert.Contains("AcceptanceFailureCauseAdjudicator.Attach(", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("record AcceptanceFailureCauseReceiptV1")]
    [InlineData("class AcceptanceFailureCauseReceiptCodec")]
    [InlineData("ExtractTrxFailureCauseEvidence")]
    [InlineData("MCG_ACCEPTANCE_CAUSE_V1")]
    public void VerifierNoLongerOwnsFailureCauseAdjudication(string forbidden)
    {
        var path = Path.Combine(FindRepositoryRoot(), WorkspaceDirectory, "GoalAcceptanceVerifier.cs");
        Assert.True(File.Exists(path), $"Verifier source is missing: {path}");
        var source = File.ReadAllText(path);

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
