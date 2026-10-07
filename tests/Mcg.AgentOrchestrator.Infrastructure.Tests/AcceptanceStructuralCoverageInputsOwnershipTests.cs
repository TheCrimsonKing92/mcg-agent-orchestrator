using System.Runtime.CompilerServices;
using Xunit;

// Parallel-safe: reads source from the explicitly verified repository without changing it.
public sealed class AcceptanceStructuralCoverageInputsOwnershipTests
{
    private const string WorkspaceDirectory = "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces";

    [Fact]
    public void InputsHaveAnIndependentOwner()
    {
        var path = Path.Combine(FindRepositoryRoot(), WorkspaceDirectory, "GoalAcceptanceVerifier.StructuralCoverageInputs.cs");
        Assert.True(File.Exists(path), $"Structural-coverage input owner source is missing: {path}");
        var source = File.ReadAllText(path);

        Assert.Contains("static class AcceptanceStructuralCoverageInputs", source, StringComparison.Ordinal);
        Assert.Contains("record UnresolvedRenameDestination(", source, StringComparison.Ordinal);
        Assert.Contains("record DeletedTestFileParse(", source, StringComparison.Ordinal);
        foreach (var forbidden in new[]
        {
            "partial class GoalAcceptanceVerifier",
            "GoalAcceptanceVerifier",
            "AcceptanceManifestCheck",
            "CommandResult",
            "_testOverrides"
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("record UnresolvedRenameDestination(")]
    [InlineData("record DeletedTestFileParse(")]
    [InlineData("new UnresolvedRenameDestination(")]
    [InlineData("\"--name-status\"")]
    [InlineData("IsTestProject")]
    [InlineData("EndsWith(\".Tests\"")]
    public void VerifierDelegatesInputOwnership(string forbidden)
    {
        Assert.DoesNotContain(forbidden, ReadSource("GoalAcceptanceVerifier.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluatorUsesTheIndependentRenameRecord()
    {
        Assert.DoesNotContain("GoalAcceptanceVerifier.UnresolvedRenameDestination",
            ReadSource("AcceptanceStructuralCoverageEvaluator.cs"), StringComparison.Ordinal);
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
