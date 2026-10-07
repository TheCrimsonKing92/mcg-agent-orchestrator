using System.Runtime.CompilerServices;

public sealed class OperatorIntentGoalApplicationBoundaryTests
{
    [Xunit.Fact]
    public void TickDelegatesToSharedApplicationWithoutReverseDependency()
    {
        var root = FindRepositoryRoot();
        var loopPath = Path.Combine(root, "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs");
        var applicationPath = Path.Combine(root, "src/Mcg.AgentOrchestrator.App/Orchestration/OperatorIntentGoalApplication.cs");
        Assert.True(File.Exists(loopPath), $"Missing source: {loopPath}");
        Assert.True(File.Exists(applicationPath), $"Missing source: {applicationPath}");

        var loop = File.ReadAllText(loopPath);
        Assert.Contains("OperatorIntentGoalApplication.ApplyPending(", loop, StringComparison.Ordinal);
        Assert.DoesNotContain("_operatorIntents.ExecutePending(", loop, StringComparison.Ordinal);
        Assert.DoesNotContain("ConductorBatchLoop", File.ReadAllText(applicationPath), StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")) ||
                (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) &&
                 File.Exists(Path.Combine(directory.FullName, "CLAUDE.md"))))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate repository root from '{sourceFilePath}'.");
    }
}
