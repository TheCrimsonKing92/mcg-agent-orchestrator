using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: every case owns its index, journal and result files; no global hooks or processes.
public sealed class AcceptanceWholeProjectClosureReuseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mcg-core-closure-{Guid.NewGuid():N}");
    private static readonly GoalId Goal = new("12345678123456781234567812345678");
    private static readonly Check Core = TestCheck("core tests",
        "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj");
    private static readonly Check Cli = TestCheck("cli tests",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj");
    private static readonly Check Owner = TestCheck("acceptance execution owner tests",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Acceptance/Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests.csproj");

    [Fact]
    public void GreenCore_DifferentTreeWithIdenticalClosure_ReusesOriginatingResults()
    {
        var paths = new[] { WriteResult("source.trx", "source evidence") };
        SeedCore(paths: paths);
        var candidate = CreateCache("attempt-two", "tree-b", enforceCoverage: true);
        var executions = 0;

        var result = RunCheck(candidate, Core, () => executions++);

        Assert.Equal(0, executions);
        Assert.True(result.Passed);
        Assert.True(result.TestResultIsExplicitCrossAttemptReuse);
        Assert.Equal("attempt-one", result.TestResultAttemptId);
        Assert.Equal(paths, result.TestResultPaths);
        Assert.Contains("source_attempt_id=attempt-one", result.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("reuse_rule=closure closure_hash=closure-x", result.ResultSummary, StringComparison.Ordinal);
        Assert.Empty(candidate.Misses);
        var summary = Assert.IsType<AcceptanceCheckResult>(candidate.CompleteAttempt());
        Assert.Contains("{partition_id=core-tests,source_attempt_id=attempt-one,", summary.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("reuse_rule=closure,closure_hash=closure-x}", summary.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("executed_lanes=0", summary.ResultSummary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("changed", "no-green-verdict-for-whole-project-closure", "closure-y")]
    [InlineData("unavailable", "closure-hash-unavailable", null)]
    [InlineData("red", "no-green-verdict-for-whole-project-closure", "closure-x")]
    [InlineData("remote", "no-green-verdict-for-whole-project-closure", "closure-x")]
    public void IneligibleClosureOrPriorVerdict_ExecutesWithMatchingMiss(
        string scenario, string reason, string? closureHash)
    {
        SeedCore(passed: scenario != "red", source: scenario == "remote" ? "remote_first_run" : "first_run");
        var candidate = CreateCache("attempt-two", "tree-b", closureHash);

        AssertMissAndExecution(candidate, Core, reason, closureHash);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RetriedCoreGreen_IsNotIndexedAndCandidateExecutes(bool probeRan, bool retryListed)
    {
        var index = new AcceptanceClosureVerdictIndex(_root);
        var reuse = new AcceptanceWholeProjectClosureReuse(index);
        var record = new PartitionVerdictRecord(Goal.Value, "attempt-one", "tree-a", "main-a",
            Identity(Core), "core-tests", "source-key", true, "GREEN", [], DateTimeOffset.UtcNow,
            "closure-x", "first_run", ProbeRan: probeRan, IdenticalTree: true);
        var retries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (retryListed) retries.Add("core-tests");
        Assert.False(AcceptanceWholeProjectClosureReuse.IsIndexable(record, retries));

        reuse.AppendGreen("manifest-a", [record], retries);

        Assert.Null(index.FindLatest("manifest-a", Identity(Core), "closure-x"));
        var candidate = CreateCache("attempt-two", "tree-b");
        // The live within-attempt probe seam supports partitioned lanes only.
        Assert.False(candidate.ShouldRerunWithinAttempt(Core, false));
        AssertMissAndExecution(candidate, Core,
            PartitionVerdictMissReasons.NoGreenVerdictForWholeProjectClosure, "closure-x");
    }

    [Fact]
    public void ForcedFullRerun_WithMatchingCoreClosure_ExecutesBeforeFallback()
    {
        SeedCore();
        // A tree-scoped CLI green activates the existing full-rerun cadence at tree B.
        var priorAtCandidate = CreateCache("attempt-cli", "tree-b");
        priorAtCandidate.RecordExecution(Cli, Passed(Cli));
        Assert.NotNull(priorAtCandidate.CompleteAttempt());
        Assert.NotNull(new AcceptanceClosureVerdictIndex(_root).FindLatest("manifest-a", Identity(Core), "closure-x"));
        var candidate = CreateCache("attempt-two", "tree-b", fullRerunEveryN: 1);

        Assert.True(candidate.ForceFullRerun);
        AssertMissAndExecution(candidate, Core, PartitionVerdictMissReasons.ForcedFullRerun, null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OtherWholeProjects_MatchingClosureEntry_StillExecute(bool executionOwner)
    {
        SeedCore();
        var check = executionOwner ? Owner : Cli;
        var index = new AcceptanceClosureVerdictIndex(_root);
        index.AppendGreen("manifest-a", Identity(check), "closure-x", "attempt-one", []);
        Assert.NotNull(index.FindLatest("manifest-a", Identity(check), "closure-x"));
        var candidate = CreateCache("attempt-two", "tree-b");

        AssertMissAndExecution(candidate, check,
            PartitionVerdictMissReasons.NoGreenVerdictForIdenticalTree, "closure-x");
        Assert.Equal("attempt-one", index.FindLatest("manifest-a", Identity(check), "closure-x")!.SourceAttemptId);
    }

    [Theory]
    [InlineData("no-paths")]
    [InlineData("missing-file")]
    [InlineData("empty-file")]
    public void ClosureHit_MissingStructuralEvidence_Executes(string evidence)
    {
        var paths = evidence switch
        {
            "missing-file" => new[] { Path.Combine(_root, "missing.trx") },
            "empty-file" => new[] { WriteResult("empty.trx", "") },
            _ => Array.Empty<string>()
        };
        SeedCore(paths: paths);
        Assert.NotNull(new AcceptanceClosureVerdictIndex(_root).FindLatest("manifest-a", Identity(Core), "closure-x"));
        var candidate = CreateCache("attempt-two", "tree-b", enforceCoverage: true);

        AssertMissAndExecution(candidate, Core,
            PartitionVerdictMissReasons.MissingStructuralCoverageEvidence, "closure-x");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdentityDrift_ManifestOrArguments_Executes(bool argumentsChanged)
    {
        SeedCore();
        var check = argumentsChanged ? new Check
        {
            Name = Core.Name, Type = Core.Type, Project = Core.Project, Arguments = ["--verbosity", "quiet"]
        } : Core;
        var candidate = CreateCache("attempt-two", "tree-b",
            manifest: argumentsChanged ? "manifest-a" : "manifest-b", core: check);

        AssertMissAndExecution(candidate, check,
            PartitionVerdictMissReasons.NoGreenVerdictForWholeProjectClosure, "closure-x");
    }

    private void SeedCore(bool passed = true, string source = "first_run", IReadOnlyList<string>? paths = null)
    {
        var prior = CreateCache("attempt-one", "tree-a");
        prior.RecordExecution(Core, new AcceptanceCheckResult(Core.Name, passed, passed ? 0 : 1, null,
            TestResultPaths: paths ?? []), source);
        Assert.NotNull(prior.CompleteAttempt());
        var entry = new AcceptanceClosureVerdictIndex(_root).FindLatest("manifest-a", Identity(Core), "closure-x");
        if (passed && source == "first_run") Assert.NotNull(entry);
        else Assert.Null(entry);
    }

    private static void AssertMissAndExecution(
        AcceptancePartitionVerdictCache candidate, Check check, string reason, string? closureHash)
    {
        var executions = 0;
        var result = RunCheck(candidate, check, () => executions++);

        Assert.Equal(1, executions);
        Assert.False(result.TestResultIsExplicitCrossAttemptReuse);
        Assert.Equal(new PartitionVerdictMissReceipt(AcceptanceIdenticalTreeReuseRule.PartitionId(check),
            reason, closureHash), Assert.Single(candidate.Misses));
        var completed = candidate.CompleteAttempt();
        if (reason == PartitionVerdictMissReasons.ClosureHashUnavailable)
            Assert.Null(completed);
        else
        {
            var summary = Assert.IsType<AcceptanceCheckResult>(completed);
            Assert.Contains($"missed_lane={AcceptanceIdenticalTreeReuseRule.PartitionId(check)}:{reason}" +
                (closureHash is null ? "" : $":{closureHash}"), summary.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("reused_lanes=0", summary.ResultSummary, StringComparison.Ordinal);
        }
    }

    private static AcceptanceCheckResult RunCheck(AcceptancePartitionVerdictCache cache, Check check, Action execute)
    {
        if (cache.TryReuse(check) is { } reused) return reused;
        execute();
        var result = Passed(check);
        cache.RecordExecution(check, result);
        return result;
    }

    private AcceptancePartitionVerdictCache CreateCache(
        string attempt, string tree, string? closureHash = "closure-x", bool enforceCoverage = false,
        int fullRerunEveryN = 5, string manifest = "manifest-a", Check? core = null)
    {
        Check[] checks = [core ?? Core, Cli, Owner];
        return Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(Goal, _root, checks, fullRerunEveryN, true,
                _ => tree, _ => "main-a", _ => $"commit-{attempt}", () => attempt, () => manifest,
                () => enforceCoverage, ResolveClosureHash: _ => closureHash, IdenticalTreeChecks: checks)));
    }

    private static string Identity(Check check)
    {
        Assert.True(AcceptanceIdenticalTreeReuseRule.TryGetCheckIdentity(check, out var identity));
        return identity;
    }

    private static Check TestCheck(string name, string project) => new()
    {
        Name = name, Type = "dotnet-test", Project = project, Arguments = ["--verbosity", "minimal"]
    };

    private static AcceptanceCheckResult Passed(Check check) => new(check.Name, true, 0, null, TestResultPaths: []);

    private string WriteResult(string file, string text)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, file);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
