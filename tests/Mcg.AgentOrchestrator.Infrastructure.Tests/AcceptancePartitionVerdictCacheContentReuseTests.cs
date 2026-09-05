using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;

public sealed class AcceptancePartitionVerdictCacheContentReuseTests : GoalAcceptanceVerifierTestBase, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mcg-content-reuse-{Guid.NewGuid():N}");
    private readonly GoalAcceptanceVerifier.AcceptanceManifestCheck _lane = new()
    {
        Name = "infrastructure tests: Content reuse",
        Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", "FullyQualifiedName~AcceptancePartitionVerdictCacheContentReuseTests"]
    };

    [Fact]
    public void GreenLane_IdenticalClosureAcrossDifferentCandidatesAndGoals_IsReused()
    {
        Directory.CreateDirectory(_root);
        CreateRepository();
        var first = CreateCache(new GoalId("11111111111111111111111111111111"), "attempt-one", "tree-a", "main-a");
        first.RecordExecution(_lane, PassedResult());
        Assert.NotNull(first.CompleteAttempt());

        var second = CreateCache(new GoalId("22222222222222222222222222222222"), "attempt-two", "tree-b", "main-b");

        // Against the pre-fix cache this assertion fails because the lookup is goal-scoped and
        // its key embeds both the candidate and main SHAs even though this worktree is unchanged.
        var reused = Assert.IsType<AcceptanceCheckResult>(second.TryReuse(_lane));
        Assert.Contains("source_attempt_id=attempt-one", reused.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("reuse_rule=closure", reused.ResultSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("closure_hash=not-applicable", reused.ResultSummary, StringComparison.Ordinal);

        File.AppendAllText(Path.Combine(_root, "src", "TestLibrary", "Probe.cs"), Environment.NewLine + "// changed inside closure");
        RunGit("add", "src/TestLibrary/Probe.cs");
        RunGit("commit", "-m", "change closure");
        var changed = CreateCache(new GoalId("33333333333333333333333333333333"), "attempt-three", "tree-c", "main-c");
        Assert.Null(changed.TryReuse(_lane));
    }

    public void Dispose()
    {
        DeleteDirectoryWithRetry(_root);
    }

    private AcceptancePartitionVerdictCache CreateCache(
        GoalId goalId,
        string attemptId,
        string candidateTreeSha,
        string mainSha) =>
        Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(
                goalId,
                _root,
                [_lane],
                5,
                true,
                _ => candidateTreeSha,
                _ => mainSha,
                _ => $"commit-{attemptId}",
                () => attemptId,
                () => "manifest-a",
                () => false)));

    private AcceptanceCheckResult PassedResult() => new(_lane.Name, true, 0, null, TestResultPaths: []);

    private void CreateRepository()
    {
        var testProject = Path.Combine(_root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        var libraryProject = Path.Combine(_root, "src", "TestLibrary");
        Directory.CreateDirectory(testProject);
        Directory.CreateDirectory(libraryProject);
        File.WriteAllText(Path.Combine(testProject, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../../src/TestLibrary/TestLibrary.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(libraryProject, "TestLibrary.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(libraryProject, "Probe.cs"), "internal sealed class Probe { }");
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        RunGit("init");
        RunGit("config", "user.email", "tests@example.invalid");
        RunGit("config", "user.name", "Tests");
        RunGit("add", ".");
        RunGit("commit", "-m", "initial closure");
    }

    private void RunGit(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git.");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
