using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptancePartitionVerdictCacheMissReasonTests
{
    [Fact]
    public void TryReuse_RecordsEachMissReasonAndPreservesPairReuse()
    {
        using var context = new PartitionVerdictCacheMeasurementContext();
        var lane = context.Lane("Alpha");
        var noKey = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "check with spaces: no key",
            Type = "command",
            Command = "git",
            Arguments = ["diff", "--check"]
        };

        var missingKey = context.Create("missing-key", [lane]);
        Assert.Null(missingKey.TryReuse(noKey));
        Assert.Equal(
            new PartitionVerdictMissReceipt("check%20with%20spaces%3A%20no%20key",
                PartitionVerdictMissReasons.CacheKeyUnavailable, null),
            Assert.Single(missingKey.Misses));

        var first = context.Create("first", [lane]);
        first.RecordExecution(lane, context.Result(lane, duration: 12));
        Assert.NotNull(first.CompleteAttempt());

        var forced = context.Create("forced", [lane], fullRerunEveryN: 1);
        Assert.True(forced.ForceFullRerun);
        Assert.Null(forced.TryReuse(lane));
        Assert.Equal(new PartitionVerdictMissReceipt("alpha",
            PartitionVerdictMissReasons.ForcedFullRerun, null), Assert.Single(forced.Misses));

        var reusable = context.Create("reusable", [lane]);
        var reused = Assert.IsType<AcceptanceCheckResult>(reusable.TryReuse(lane));
        Assert.True(reused.Passed);
        Assert.Equal("first", reused.TestResultAttemptId);
        Assert.Equal(
            $"partition-verdict-cache reused source_attempt_id=first cache_key={reusable.GoalId.ToLowerInvariant()}:tree-a:main-a:manifest-a:{GoalAcceptanceVerifier.ShortHash("FullyQualifiedName~Alpha")}" +
            " reuse_rule=pair closure_hash=not-applicable",
            reused.ResultSummary);
        Assert.Empty(reusable.Misses);

        var noHash = context.Create("no-hash", [lane], candidateTreeSha: "tree-b");
        Assert.Null(noHash.TryReuse(lane));
        Assert.Equal(new PartitionVerdictMissReceipt("alpha",
            PartitionVerdictMissReasons.ClosureHashUnavailable, null), Assert.Single(noHash.Misses));

        var noVerdict = context.Create("no-verdict", [lane], candidateTreeSha: "tree-b",
            closureHash: "closure-x");
        Assert.Null(noVerdict.TryReuse(lane));
        Assert.Equal(new PartitionVerdictMissReceipt("alpha",
            PartitionVerdictMissReasons.NoGreenVerdictForClosure, "closure-x"),
            Assert.Single(noVerdict.Misses));

        var noCoverage = context.Create("no-coverage", [lane], enforceStructuralCoverage: true);
        Assert.Null(noCoverage.TryReuse(lane));
        Assert.Equal(new PartitionVerdictMissReceipt("alpha",
            PartitionVerdictMissReasons.MissingStructuralCoverageEvidence, null),
            Assert.Single(noCoverage.Misses));
    }
}

internal sealed class PartitionVerdictCacheMeasurementContext : IDisposable
{
    private static readonly GoalId GoalId = new("12345678123456781234567812345678");
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "mcg-partition-measurement-tests", Guid.NewGuid().ToString("N"));

    internal GoalAcceptanceVerifier.AcceptanceManifestCheck Lane(string name) => new()
    {
        Name = $"infrastructure tests: {name}",
        Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", $"FullyQualifiedName~{name}"]
    };

    internal AcceptancePartitionVerdictCache Create(
        string attemptId,
        IReadOnlyList<GoalAcceptanceVerifier.AcceptanceManifestCheck> lanes,
        string candidateTreeSha = "tree-a",
        int fullRerunEveryN = 5,
        string? closureHash = null,
        bool enforceStructuralCoverage = false) =>
        Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(
                GoalId, _root, lanes, fullRerunEveryN, true,
                _ => candidateTreeSha, _ => "main-a", _ => $"commit-{attemptId}",
                () => attemptId, () => "manifest-a", () => enforceStructuralCoverage,
                ResolveClosureHash: _ => closureHash)));

    internal AcceptanceCheckResult Result(
        GoalAcceptanceVerifier.AcceptanceManifestCheck lane,
        long? duration = null) =>
        new(lane.Name, true, 0, null, DurationMilliseconds: duration, TestResultPaths: []);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
