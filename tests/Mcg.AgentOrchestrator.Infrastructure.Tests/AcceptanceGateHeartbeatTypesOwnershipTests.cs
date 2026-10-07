using System.Runtime.CompilerServices;
using Xunit;

// Parallel-safe: reads source from the explicitly verified repository without changing it.
public sealed class AcceptanceGateHeartbeatTypesOwnershipTests
{
    private const string WorkspaceDirectory = "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces";

    [Theory]
    [InlineData("GateHeartbeatContext.cs", "internal sealed record GateHeartbeatContext(",
        "RunClass { get; init; }", "RunId { get; init; }")]
    [InlineData("GateHeartbeatRuntime.cs", "internal sealed class GateHeartbeatRuntime")]
    [InlineData("CaptureLimitStop.cs", "internal sealed class CaptureLimitStop(")]
    public void Types_AfterExtraction_HaveIndependentOwners(string filename, params string[] declarations)
    {
        var path = Path.Combine(FindRepositoryRoot(), WorkspaceDirectory, filename);
        Assert.True(File.Exists(path), $"Acceptance gate type owner source is missing: {path}");
        var source = ReadSource(filename);

        foreach (var declaration in declarations)
            Assert.Contains(declaration, source, StringComparison.Ordinal);

        Assert.DoesNotContain("GoalAcceptanceVerifier", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("record GateHeartbeatContext")]
    [InlineData("class GateHeartbeatRuntime")]
    [InlineData("class CaptureLimitStop")]
    public void Verifier_AfterExtraction_ContainsNoMovedTypeDeclarations(string declaration)
    {
        foreach (var filename in new[]
        {
            "GoalAcceptanceVerifier.cs",
            "GoalAcceptanceVerifier.HeartbeatRunClass.cs",
            "GoalAcceptanceVerifier.CaptureLimit.cs"
        })
        {
            Assert.DoesNotContain(declaration, ReadSource(filename), StringComparison.Ordinal);
        }
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
