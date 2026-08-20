using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptancePartitionVerdictCacheTests : IDisposable
{
    private static readonly GoalId GoalId = new("12345678123456781234567812345678");
    private readonly string _root = CreateTempDirectory();
    private readonly GoalAcceptanceVerifier.AcceptanceManifestCheck _partition = new()
    {
        Name = "infrastructure tests: Cache",
        Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", "FullyQualifiedName~AcceptancePartitionVerdictCacheTests"]
    };

    [Fact]
    public void Reuse_MatchingPairKeyWithGreenJournalRecord_ReusesSourceAttemptVerdict()
    {
        var first = CreateCache("attempt-one");
        first.RecordExecution(_partition, Result(passed: true));
        Assert.NotNull(first.CompleteAttempt());

        var second = CreateCache("attempt-two");
        var reused = Assert.IsType<AcceptanceCheckResult>(second.TryReuse(_partition));

        Assert.True(reused.Passed);
        Assert.Equal("attempt-one", reused.TestResultAttemptId);
        Assert.True(reused.TestResultIsExplicitCrossAttemptReuse);
        Assert.Contains("source_attempt_id=attempt-one", reused.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Reuse_ChangedCandidateTreeSha_ForcesRerun()
    {
        var first = CreateCache("attempt-one", candidateTreeSha: "tree-a");
        first.RecordExecution(_partition, Result(passed: true));
        Assert.NotNull(first.CompleteAttempt());

        var second = CreateCache("attempt-two", candidateTreeSha: "tree-b");

        Assert.Null(second.TryReuse(_partition));
    }

    [Fact]
    public void Backstop_AtConfiguredInterval_ForcesFullRerunAndResetsReuseCounter()
    {
        var first = CreateCache("attempt-one", fullRerunEveryN: 2);
        first.RecordExecution(_partition, Result(passed: true));
        Assert.NotNull(first.CompleteAttempt());

        var second = CreateCache("attempt-two", fullRerunEveryN: 2);
        Assert.NotNull(second.TryReuse(_partition));
        Assert.NotNull(second.CompleteAttempt());

        var third = CreateCache("attempt-three", fullRerunEveryN: 2);
        Assert.True(third.ForceFullRerun);
        Assert.Null(third.TryReuse(_partition));
        third.RecordExecution(_partition, Result(passed: true));
        Assert.NotNull(third.CompleteAttempt());

        var fourth = CreateCache("attempt-four", fullRerunEveryN: 2);
        Assert.False(fourth.ForceFullRerun);
        Assert.NotNull(fourth.TryReuse(_partition));
    }

    [Fact]
    public void WithinAttemptRerun_GreenRerunAfterRedFirstRun_DoesNotPoisonNextAttemptVerdict()
    {
        var first = CreateCache("attempt-one", withinAttemptRerunEnabled: true);
        Assert.True(first.ShouldRerunWithinAttempt(_partition, passed: false));
        first.RecordExecution(_partition, Result(passed: true));
        Assert.NotNull(first.CompleteAttempt());

        var second = CreateCache("attempt-two");
        var reused = Assert.IsType<AcceptanceCheckResult>(second.TryReuse(_partition));

        Assert.True(reused.Passed);
        Assert.Equal("attempt-one", reused.TestResultAttemptId);
    }

    [Fact]
    public void Reuse_LatestJournalRecordRed_DoesNotReuseOlderGreen()
    {
        var first = CreateCache("attempt-one", fullRerunEveryN: 10);
        first.RecordExecution(_partition, Result(passed: true));
        Assert.NotNull(first.CompleteAttempt());

        var second = CreateCache("attempt-two", fullRerunEveryN: 1);
        Assert.True(second.ForceFullRerun);
        second.RecordExecution(_partition, Result(passed: false));
        Assert.NotNull(second.CompleteAttempt());

        var third = CreateCache("attempt-three", fullRerunEveryN: 10);
        Assert.Null(third.TryReuse(_partition));
    }

    [Fact]
    public void Reuse_StructuralCoverageEnforcedWithMissingTrx_ExecutesInsteadOfReusing()
    {
        var missingTrx = Path.Combine(_root, "missing.trx");
        var first = CreateCache("attempt-one", enforceStructuralCoverage: false);
        first.RecordExecution(_partition, Result(passed: true, testResultPaths: [missingTrx]));
        Assert.NotNull(first.CompleteAttempt());

        var enforced = CreateCache("attempt-two", enforceStructuralCoverage: true);
        Assert.Null(enforced.TryReuse(_partition));

        var notEnforced = CreateCache("attempt-three", enforceStructuralCoverage: false);
        Assert.NotNull(notEnforced.TryReuse(_partition));
    }

    [Fact]
    public void ShouldRerunWithinAttempt_DisabledOrNonPartitionOrPassed_ReturnsFalse()
    {
        var enabled = CreateCache("attempt-one", withinAttemptRerunEnabled: true);
        var disabled = CreateCache("attempt-two", withinAttemptRerunEnabled: false);
        var nonPartition = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "not a partition",
            Type = "command",
            Command = "git",
            Arguments = ["diff", "--check"]
        };

        Assert.False(disabled.ShouldRerunWithinAttempt(_partition, passed: false));
        Assert.False(enabled.ShouldRerunWithinAttempt(nonPartition, passed: false));
        Assert.False(enabled.ShouldRerunWithinAttempt(_partition, passed: true));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AcceptancePartitionVerdictCache CreateCache(
        string attemptId,
        string candidateTreeSha = "tree-a",
        string mainSha = "main-a",
        int fullRerunEveryN = 5,
        bool withinAttemptRerunEnabled = true,
        bool enforceStructuralCoverage = false)
    {
        var cache = AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(
                GoalId,
                _root,
                [_partition],
                fullRerunEveryN,
                withinAttemptRerunEnabled,
                _ => candidateTreeSha,
                _ => mainSha,
                _ => $"commit-{attemptId}",
                () => attemptId,
                () => "manifest-a",
                () => enforceStructuralCoverage));
        return Assert.IsType<AcceptancePartitionVerdictCache>(cache);
    }

    private AcceptanceCheckResult Result(
        bool passed,
        IReadOnlyList<string>? testResultPaths = null) =>
        new(
            _partition.Name,
            passed,
            passed ? 0 : 1,
            passed ? null : "failed",
            TestResultPaths: testResultPaths ?? []);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-partition-verdict-cache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
