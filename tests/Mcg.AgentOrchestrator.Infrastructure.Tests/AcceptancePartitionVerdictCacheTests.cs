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

    [Theory]
    [InlineData(true, -1, 1, 1, "passed", null, null, AcceptanceShardCompletionPredicates.TimedOut)]
    [InlineData(false, 1, 1, 1, "passed", null, null, AcceptanceShardCompletionPredicates.NonzeroExit)]
    [InlineData(false, 0, null, null, "missing", AcceptanceShardCompletionPredicates.MissingTrx, null, AcceptanceShardCompletionPredicates.MissingTrx)]
    [InlineData(false, 0, null, null, "malformed", AcceptanceShardCompletionPredicates.MalformedTrx, null, AcceptanceShardCompletionPredicates.MalformedTrx)]
    [InlineData(false, 0, 0, 0, "passed", null, null, AcceptanceShardCompletionPredicates.ZeroTests)]
    [InlineData(false, 0, 2, 1, "passed", null, null, AcceptanceShardCompletionPredicates.IncompleteExecution)]
    [InlineData(false, 0, 1, 1, "failed", null, null, AcceptanceShardCompletionPredicates.FailingTrx)]
    [InlineData(false, 0, 1, 1, "passed", null, AcceptanceFailureClassifications.FocusedSelectionApparatusFailure, AcceptanceFailureClassifications.FocusedSelectionApparatusFailure)]
    public void CompletionDecision_FailedPredicate_DrivesTypedPartitionRetry(
        bool timedOut,
        int exitCode,
        int? discovered,
        int? executed,
        string trxOutcome,
        string? trxFailedPredicate,
        string? policyFailure,
        string expectedPredicate)
    {
        var cache = CreateCache("typed-retry", withinAttemptRerunEnabled: true);
        var decision = GoalAcceptanceVerifier.DecideTestShardCompletionForTests(
            new GoalAcceptanceVerifier.CommandResult(exitCode, string.Empty, TimedOut: timedOut),
            discovered,
            executed,
            trxOutcome,
            trxFailedPredicate,
            policyFailure);

        Assert.False(decision.Passed);
        Assert.Equal(expectedPredicate, decision.FailedPredicate);
        Assert.True(cache.ShouldRerunWithinAttempt(_partition, decision));
    }

    [Fact]
    public void CompletionDecision_CompletedTrxCountsSkippedTestsWithoutHidingMissingExecution()
    {
        var completePath = Path.Combine(_root, "completed-with-skip.trx");
        File.WriteAllText(
            completePath,
            "<TestRun><Results><UnitTestResult outcome=\"Passed\"/><UnitTestResult outcome=\"NotExecuted\"/></Results>" +
            "<ResultSummary outcome=\"Completed\"><Counters total=\"2\" executed=\"1\" passed=\"1\" failed=\"0\" notExecuted=\"1\"/></ResultSummary></TestRun>");
        var complete = GoalAcceptanceVerifier.DecideTestShardCompletionFromTrxForTests(
            new GoalAcceptanceVerifier.CommandResult(0, string.Empty),
            [completePath]);

        Assert.True(complete.Passed);
        Assert.Equal("Completed", complete.TrxOutcome);
        Assert.Equal(2, complete.DiscoveredTestCount);
        Assert.Equal(1, complete.ExecutedTestCount);
        Assert.Equal(1, complete.NotExecutedTestCount);

        var incompletePath = Path.Combine(_root, "completed-but-incomplete.trx");
        File.WriteAllText(
            incompletePath,
            "<TestRun><Results><UnitTestResult outcome=\"Passed\"/></Results>" +
            "<ResultSummary outcome=\"Completed\"><Counters total=\"2\" executed=\"1\" passed=\"1\" failed=\"0\" notExecuted=\"0\"/></ResultSummary></TestRun>");
        var incomplete = GoalAcceptanceVerifier.DecideTestShardCompletionFromTrxForTests(
            new GoalAcceptanceVerifier.CommandResult(0, string.Empty),
            [incompletePath]);

        Assert.False(incomplete.Passed);
        Assert.Equal(AcceptanceShardCompletionPredicates.IncompleteExecution, incomplete.FailedPredicate);
    }

    [Fact]
    public void WithinAttemptRetryReceipt_RetainsCapturedStderrAndTypedDecision()
    {
        const string stderr = "retry-driving stderr from the original process";
        var retained = AcceptanceAttemptArtifactCustody.RetainRetryDiagnostic(
            Path.Combine(_root, "attempt", "acceptance"),
            fallbackArtifactsPath: null,
            "cache-run-0",
            Path.Combine(_root, "deleted-temp.err"),
            stderr,
            "{\"fallback\":true}");
        var retainedBytes = File.ReadAllBytes(retained.Path);
        var expectedHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(retainedBytes));
        Assert.EndsWith(".err", retained.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(stderr, File.ReadAllText(retained.Path));
        Assert.Equal(expectedHash, retained.Sha256);

        var cache = CreateCache("receipt-attempt", withinAttemptRerunEnabled: true);
        var decision = new AcceptanceShardCompletionDecision(
            false,
            AcceptanceShardCompletionPredicates.IncompleteExecution,
            false,
            0,
            110,
            109,
            "passed",
            NotExecutedTestCount: 0);
        var original = new AcceptanceCheckResult(
            _partition.Name,
            false,
            0,
            "incomplete execution",
            CompletionDecision: decision,
            ProcessStderrPath: Path.Combine(_root, "deleted-temp.err"),
            ProcessStderr: stderr);

        cache.RecordWithinAttemptRetry(
            _partition,
            original,
            "receipt-attempt:cache:0",
            "receipt-attempt:cache:1",
            retained);

        var retryLine = SharedJsonlFile.ReadAllLines(cache.JournalPath)
            .Single(line => line.Contains("acceptance:partition-within-attempt-retry", StringComparison.Ordinal));
        using var document = System.Text.Json.JsonDocument.Parse(retryLine);
        var receipt = document.RootElement.GetProperty("partitionRetryReceipt");
        Assert.Equal(AcceptanceShardCompletionPredicates.IncompleteExecution, receipt.GetProperty("failedPredicate").GetString());
        Assert.Equal("receipt-attempt:cache:0", receipt.GetProperty("originalInvocationId").GetString());
        Assert.Equal("receipt-attempt:cache:1", receipt.GetProperty("retryInvocationId").GetString());
        Assert.Equal(0, receipt.GetProperty("exitCode").GetInt32());
        Assert.Equal(110, receipt.GetProperty("discoveredTestCount").GetInt32());
        Assert.Equal(109, receipt.GetProperty("executedTestCount").GetInt32());
        Assert.Equal("passed", receipt.GetProperty("trxOutcome").GetString());
        Assert.Equal(0, receipt.GetProperty("notExecutedTestCount").GetInt32());
        Assert.Equal(retained.Path, receipt.GetProperty("diagnosticPath").GetString());
        Assert.Equal(expectedHash, receipt.GetProperty("diagnosticSha256").GetString());
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
