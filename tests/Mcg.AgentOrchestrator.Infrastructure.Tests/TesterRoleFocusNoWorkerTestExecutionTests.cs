using System.Reflection;
using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: generates a digest from task-local state without writing artifacts.
public sealed class TesterRoleFocusNoWorkerTestExecutionTests
{
    [Fact]
    public void BuildDigest_TesterTask_DefersRoleFocusExecutionToConductor()
    {
        var repositoryRoot = FindRepositoryRoot();
        var task = new TaskSpec(TaskId.New(), "Verify the scoped Tester change.", AgentRole.Tester);
        var goal = new AgentOrchestratorKernel().CreateGoal(
            new GoalId("role-focus-tester"),
            "Keep Tester guidance aligned.",
            [task]);
        var method = typeof(WorkerArtifactWriter).GetMethod(
            "BuildDigest",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(WorkerArtifactWriter).FullName, "BuildDigest");

        var digest = Assert.IsType<string>(method.Invoke(
            null, [goal, task, repositoryRoot, null, false, false, false]));
        var lines = digest.Split(Environment.NewLine);
        var roleFocusIndex = Assert.Single(
            Enumerable.Range(0, lines.Length), index => lines[index] == "## Role Focus");
        var section = string.Join(Environment.NewLine,
            lines.Skip(roleFocusIndex + 1).TakeWhile(line =>
                !line.StartsWith("## ", StringComparison.Ordinal))).Trim();

        Assert.Equal(
            "Strengthen verification and report exact failures or coverage gaps; name test classes for the conductor to run through evidence_request.",
            section);
        Assert.DoesNotContain("run focused verification", digest, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
