using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;

[Xunit.Collection("IsolatedProcessSpawning")]
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

    [Fact]
    public void ScriptsOnlyChange_DoesNotReuseGreenLane()
    {
        Directory.CreateDirectory(_root);
        CreateRepository();
        var first = CreateCache(new GoalId("11111111111111111111111111111111"), "attempt-one", "tree-a", "main-a");
        first.RecordExecution(_lane, PassedResult());
        Assert.NotNull(first.CompleteAttempt());

        File.AppendAllText(Path.Combine(_root, "scripts", "probe.ps1"), Environment.NewLine + "# changed runtime input");
        RunGit("add", "scripts/probe.ps1");
        RunGit("commit", "-m", "change runtime script");

        var changed = CreateCache(new GoalId("22222222222222222222222222222222"), "attempt-two", "tree-b", "main-b");

        // Against the pre-fix closure this assertion fails because scripts are omitted from its hash.
        Assert.Null(changed.TryReuse(_lane));
    }

    [Fact]
    public void WithinAttemptRerun_PassingRecord_IsNeverExportedToClosureIndex()
    {
        Directory.CreateDirectory(_root);
        CreateRepository();
        var first = CreateCache(new GoalId("11111111111111111111111111111111"), "attempt-one", "tree-a", "main-a");
        var firstRun = new AcceptanceCheckResult(
            _lane.Name,
            false,
            1,
            "first run failed",
            CompletionDecision: new AcceptanceShardCompletionDecision(
                false,
                AcceptanceShardCompletionPredicates.NonzeroExit,
                false,
                1,
                1,
                1,
                "failed"));
        first.RecordWithinAttemptRetry(
            _lane,
            firstRun,
            "attempt-one:content-reuse:0",
            "attempt-one:content-reuse:1",
            new AcceptanceRetainedDiagnostic("first-run.err", "first-run-sha"));
        first.RecordExecution(_lane, PassedResult());
        Assert.NotNull(first.CompleteAttempt());

        var second = CreateCache(new GoalId("22222222222222222222222222222222"), "attempt-two", "tree-b", "main-b");

        Assert.Null(second.TryReuse(_lane));
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
        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        File.WriteAllText(Path.Combine(testProject, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../../src/TestLibrary/TestLibrary.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(libraryProject, "TestLibrary.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(libraryProject, "Probe.cs"), "internal sealed class Probe { }");
        File.WriteAllText(Path.Combine(_root, "scripts", "probe.ps1"), "Write-Output probe");
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        RunGit("init");
        RunGit("config", "user.email", "tests@example.invalid");
        RunGit("config", "user.name", "Tests");
        RunGit("add", ".");
        RunGit("commit", "-m", "initial closure");
    }

    private void RunGit(params string[] arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(_root, arguments);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)} exit={result.ExitCode}: {result.StandardError}; {result}");
    }
}
