using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptancePartitionVerdictCacheWithinAttemptRerunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "partition-rerun-" + Guid.NewGuid().ToString("N"));
    private readonly GoalAcceptanceVerifier.AcceptanceManifestCheck _partition = new()
    {
        Name = "infrastructure tests: Process spawning",
        Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", "FullyQualifiedName~ProcessSpawning"]
    };

    [Theory]
    [InlineData(AcceptanceShardCompletionPredicates.MissingTrx, true, 351, false, true)]
    [InlineData(AcceptanceShardCompletionPredicates.MalformedTrx, true, 351, false, true)]
    [InlineData(AcceptanceShardCompletionPredicates.MissingTrx, false, 351, false, false)]
    [InlineData(AcceptanceShardCompletionPredicates.MissingTrx, true, 0, false, false)]
    [InlineData(AcceptanceShardCompletionPredicates.MissingTrx, true, 351, true, false)]
    [InlineData(AcceptanceShardCompletionPredicates.NonzeroExit, true, 351, false, false)]
    public void OnlyParsedIdentityFreeTrxRerunOfNoTrxFailureCarriesApparatusReceipt(
        string predicate, bool rerunPassed, int executed, bool hasFailingIdentity, bool expected)
    {
        Directory.CreateDirectory(_root);
        var cache = Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(
                new GoalId("12345678123456781234567812345678"), _root, [_partition], 5, true,
                _ => "candidate", _ => "main", _ => "commit", () => "attempt", () => "manifest",
                () => false)));
        var first = new AcceptanceCheckResult(
            _partition.Name, false, 0, "first run crashed",
            FailureClassification: predicate,
            CompletionDecision: new AcceptanceShardCompletionDecision(false, predicate, false, 0, 351, 0, "missing"));
        cache.RecordWithinAttemptRetry(
            _partition, first, "attempt:partition:0", "attempt:partition:1",
            new AcceptanceRetainedDiagnostic("first-run.err", "first-run-sha"));
        var probe = new AcceptanceCheckResult(
            _partition.Name, rerunPassed, rerunPassed ? 0 : 1, null,
            TestResultPaths: ["rerun.trx"],
            FailingTestIdentities: hasFailingIdentity ? ["Failing.Test"] : [],
            ExecutedTestCount: executed,
            CompletionDecision: new AcceptanceShardCompletionDecision(
                rerunPassed, rerunPassed ? null : AcceptanceShardCompletionPredicates.FailingTrx,
                false, rerunPassed ? 0 : 1, executed, executed, "parsed"));

        var verdict = cache.SelectPartitionVerdict(_partition, first, probe);

        Assert.False(verdict.Passed);
        Assert.Equal(expected, verdict.WithinAttemptRerun is not null);
        if (expected)
        {
            var evidence = Assert.IsType<AcceptanceWithinAttemptRerunEvidence>(verdict.WithinAttemptRerun);
            Assert.Equal(predicate, evidence.FailedPredicate);
            Assert.Equal("attempt:partition:0", evidence.OriginalInvocationId);
            Assert.Equal("attempt:partition:1", evidence.RetryInvocationId);
            Assert.Equal(351, evidence.ExecutedTestCount);
            Assert.Equal(["rerun.trx"], evidence.RerunTestResultPaths);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
