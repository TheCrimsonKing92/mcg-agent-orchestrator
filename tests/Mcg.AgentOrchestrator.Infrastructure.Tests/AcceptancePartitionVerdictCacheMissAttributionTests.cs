using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Each fixture owns its temporary stores, instance overrides and async console capture.
public sealed class AcceptancePartitionVerdictCacheMissAttributionTests
{
    [Fact]
    public void TryReuse_NoPriorGreen_ReportsNoPriorGreenForLane()
    {
        using var context = new CacheContext();
        AssertMiss(context, "no-prior-green-for-lane");
    }

    [Fact]
    public void TryReuse_ManifestDiffers_ReportsManifestIdentityDiffers()
    {
        using var context = new CacheContext();
        context.Seed("Manifest-a", "CLOSURE-A");
        AssertMiss(context, "manifest-identity-differs");
    }

    [Fact]
    public void TryReuse_ClosureDiffers_ReportsClosureHashDiffers()
    {
        using var context = new CacheContext();
        context.Seed("manifest-a", "closure-b");
        AssertMiss(context, "closure-hash-differs");
    }

    [Fact]
    public void TryReuse_ManifestAndClosureDiffer_ReportsBothDifferences()
    {
        using var context = new CacheContext();
        context.Seed("manifest-b", "closure-b");
        AssertMiss(context, "manifest-and-closure-differ");
    }

    [Theory]
    [InlineData(null, "closure-a", "manifest-identity-differs")]
    [InlineData("manifest-a", null, "closure-hash-differs")]
    [InlineData(null, null, "manifest-and-closure-differ")]
    public void TryReuse_NullStoredLookupFields_ReportsMissAndAllowsExecution(
        string? manifest, string? closure, string code)
    {
        using var context = new CacheContext();
        var record = new AcceptanceClosureVerdictRecord(1, manifest!, context.FilterHash,
            closure!, "malformed-green", [], DateTimeOffset.UtcNow);
        var path = Path.Combine(context.Root, ".orchestrator", "acceptance-closure-verdicts.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        AssertMiss(context, code);
    }

    [Fact]
    public void TryReuse_MatchingGreen_ReusesClosureAcrossCandidateKeys()
    {
        using var context = new CacheContext();
        var first = Assert.IsType<AcceptancePartitionVerdictCache>(
            AcceptancePartitionVerdictCache.Create(context.Options));
        first.RecordExecution(context.Lane, new AcceptanceCheckResult(
            context.Lane.Name, true, 0, null, TestResultPaths: []));
        Assert.NotNull(first.CompleteAttempt());

        var second = Assert.IsType<AcceptancePartitionVerdictCache>(
            AcceptancePartitionVerdictCache.Create(context.Options with
            {
                ResolveCandidateTreeSha = _ => "tree-b",
                ResolveAttemptId = () => "second"
            }, out var reason));
        Assert.Null(reason);
        var reused = Assert.IsType<AcceptanceCheckResult>(second.TryReuse(context.Lane));
        Assert.True(reused.Passed);
        Assert.Equal("first", reused.TestResultAttemptId);
        Assert.Contains("reuse_rule=closure closure_hash=closure-a", reused.ResultSummary);
        Assert.Empty(second.Misses);
    }

    [Fact]
    public void FindLatestForFilter_UsesNewestCurrentVersionForSameLane()
    {
        using var context = new CacheContext();
        var time = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var newest = new AcceptanceClosureVerdictRecord(1, "manifest-b",
            context.FilterHash.ToUpperInvariant(), "closure-b", "newest", [], time.AddMinutes(1));
        var records = new[]
        {
            newest,
            newest with { SourceAttemptId = "old", RecordedAt = time },
            newest with { Version = 2, SourceAttemptId = "wrong-version", RecordedAt = time.AddMinutes(3) },
            newest with { PartitionFilterHash = "other-filter", SourceAttemptId = "other-lane", RecordedAt = time.AddMinutes(4) }
        };
        var path = Path.Combine(context.Root, ".orchestrator", "acceptance-closure-verdicts.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, records.Select(record =>
            JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web))));

        var index = new AcceptanceClosureVerdictIndex(context.Root);
        Assert.Equal("newest", Assert.IsType<AcceptanceClosureVerdictRecord>(
            index.FindLatestForFilter(context.FilterHash)).SourceAttemptId);
        Assert.Equal("newest", Assert.IsType<AcceptanceClosureVerdictRecord>(
            index.FindLatest("manifest-b", context.FilterHash, "CLOSURE-B")).SourceAttemptId);
        AssertMiss(context, "manifest-and-closure-differ");
    }

    [Fact]
    public void Create_UnknownGoal_ReportsGoalIdUnknown()
    {
        using var context = new CacheContext();
        AssertUnavailable(context.Options with { GoalId = null }, "goal-id-unknown");
    }

    [Fact]
    public void Create_NoCacheableChecks_ReportsNoCacheableChecks()
    {
        using var context = new CacheContext();
        AssertUnavailable(context.Options with { EffectiveChecks = [] }, "no-cacheable-checks");
    }

    [Fact]
    public void Create_UnresolvedCandidate_ReportsCandidateTreeUnresolved()
    {
        using var context = new CacheContext();
        var resolved = new List<string>();
        AssertUnavailable(context.Options with
        {
            ResolveCandidateTreeSha = _ => { resolved.Add("candidate"); return null; },
            ResolveMainSha = _ => { resolved.Add("main"); return null; },
            ResolveVerifyingCommitSha = _ => { resolved.Add("verifying"); return null; }
        }, "candidate-tree-unresolved");
        Assert.Equal(new[] { "candidate", "main", "verifying" }, resolved);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Create_UnresolvedRevision_ReportsRevisionUnresolved(bool missingMain)
    {
        using var context = new CacheContext();
        AssertUnavailable(context.Options with
        {
            ResolveMainSha = _ => missingMain ? null : "main-a",
            ResolveVerifyingCommitSha = _ => missingMain ? "commit-a" : null
        }, "revision-unresolved");
    }

    [Fact]
    public async Task RunAsync_NoCacheableChecks_PrintsOneUnavailableLine()
    {
        using var context = new CacheContext();
        context.WriteGateManifest(cacheable: false);
        var executions = 0;
        var verifier = context.CreateVerifier((arguments, _, _) =>
        {
            Assert.Equal("probe", arguments[0]);
            executions++;
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "probe passed"));
        });
        var output = await AsyncLocalConsoleRouter.Out.CaptureLocalAsync(async () =>
        {
            var result = await verifier.RunAsync(context.Root, CacheContext.Goal);
            Assert.True(result.Passed, result.OutputTail);
            Assert.DoesNotContain(result.Checks!, check => check.Name == "infrastructure partition verdict cache");
        });

        Assert.Equal(1, executions);
        Assert.Equal("PARTITION_VERDICT_CACHE status=unavailable reason=no-cacheable-checks",
            Assert.Single(CacheLines(output)));
    }

    [Fact]
    public async Task RunAsync_CreatedCache_PrintsNoUnavailableLine()
    {
        using var context = new CacheContext();
        context.WriteGateManifest(cacheable: true);
        var executions = 0;
        var verifier = context.CreateVerifier((arguments, _, _) =>
        {
            if (arguments.Length >= 2 && arguments[0] == "dotnet" && arguments[1] == "test")
            {
                executions++;
                WritePassedTrx(arguments);
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
        });
        var output = await AsyncLocalConsoleRouter.Out.CaptureLocalAsync(async () =>
        {
            var result = await verifier.RunAsync(context.Root, CacheContext.Goal);
            Assert.True(result.Passed, result.OutputTail);
            Assert.Single(result.Checks!, check => check.Name == "infrastructure partition verdict cache");
        });

        Assert.Equal(1, executions);
        var line = Assert.Single(CacheLines(output));
        Assert.DoesNotContain("status=unavailable", output);
        Assert.Contains("executed_lanes=1", line);
    }

    private static void AssertMiss(CacheContext context, string code)
    {
        var cache = Assert.IsType<AcceptancePartitionVerdictCache>(
            AcceptancePartitionVerdictCache.Create(context.Options));
        Assert.Null(cache.TryReuse(context.Lane));
        Assert.Equal(new PartitionVerdictMissReceipt("alpha", "no-green-verdict-for-closure",
            "closure-a", code), Assert.Single(cache.Misses));
        cache.RecordExecution(context.Lane, new AcceptanceCheckResult(
            context.Lane.Name, true, 0, null, TestResultPaths: []));
        var output = AsyncLocalConsoleRouter.Capture(() => Assert.NotNull(cache.CompleteAttempt()));
        Assert.Contains($"missed_lane=alpha:no-green-verdict-for-closure:closure-a:{code}",
            Assert.Single(CacheLines(output)));
    }

    private static void AssertUnavailable(AcceptancePartitionVerdictCacheOptions options, string expected)
    {
        Assert.Null(AcceptancePartitionVerdictCache.Create(options, out var reason));
        Assert.Equal(expected, reason);
    }

    private static string[] CacheLines(string output) => output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.StartsWith("PARTITION_VERDICT_CACHE ", StringComparison.Ordinal)).ToArray();

    private static void WritePassedTrx(string[] arguments)
    {
        var resultsIndex = Array.IndexOf(arguments, "--results-directory");
        var loggerIndex = Array.IndexOf(arguments, "--logger");
        Assert.True(resultsIndex >= 0 && loggerIndex >= 0);
        const string prefix = "trx;LogFileName=";
        Assert.StartsWith(prefix, arguments[loggerIndex + 1]);
        var directory = arguments[resultsIndex + 1];
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, arguments[loggerIndex + 1][prefix.Length..]), """
            <TestRun>
              <TestDefinitions><UnitTest id="1" name="AlphaTests.Runs"><TestMethod className="AlphaTests" name="Runs" /></UnitTest></TestDefinitions>
              <Results><UnitTestResult testId="1" testName="AlphaTests.Runs" outcome="Passed" /></Results>
              <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary>
            </TestRun>
            """);
    }

    private sealed class CacheContext : IDisposable
    {
        internal static readonly GoalId Goal = new("12345678123456781234567812345678");
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-miss-attribution", Guid.NewGuid().ToString("N"));
        internal GoalAcceptanceVerifier.AcceptanceManifestCheck Lane { get; } = new()
        {
            Name = "infrastructure tests: alpha",
            Type = "dotnet-test",
            Runner = "vstest",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~AlphaTests"]
        };
        internal string FilterHash => GoalAcceptanceVerifier.ShortHash("FullyQualifiedName~AlphaTests");
        internal AcceptancePartitionVerdictCacheOptions Options => new(
            Goal, Root, [Lane], 5, false,
            _ => "tree-a", _ => "main-a", _ => "commit-a",
            () => "first", () => "manifest-a", () => false,
            ResolveClosureHash: _ => "closure-a", ResolveChangedFiles: (_, _, _) => []);

        internal void Seed(string manifest, string closure) =>
            new AcceptanceClosureVerdictIndex(Root).AppendGreen(
                manifest, FilterHash, closure, "prior-green", []);

        internal GoalAcceptanceVerifier CreateVerifier(
            Func<string[], string, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> runner) =>
            new(new GoalAcceptanceVerifierTestOverrides
            {
                ResolveMainWorktreePathForTests = _ => Root,
                ResolveDeletedTestFilesForTests = _ => [],
                ResolvePartitionVerdictCandidateTreeShaForTests = _ => "tree-a",
                ResolvePartitionVerdictMainShaForTests = _ => "main-a",
                ResolvePartitionVerdictVerifyingCommitShaForTests = _ => "commit-a",
                ResolvePartitionVerdictClosureHashForTests = _ => "closure-a",
                ResolvePartitionVerdictChangedFilesForTests = (_, _, _) => [],
                PartitionVerdictWithinAttemptRerunEnabled = false,
                BuildStorageRootForTests = new DotnetBuildStorageRoot(Path.Combine(Root, "dotnet")),
                ShardPermitRootForTests = Path.Combine(Root, "permits"),
                MainBaselineDiscoveryCacheEnabled = false
            }, runner);

        internal void WriteGateManifest(bool cacheable)
        {
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            var check = cacheable ? Lane : new GoalAcceptanceVerifier.AcceptanceManifestCheck
            {
                Name = "probe check", Type = "command", Command = "probe", Arguments = []
            };
            File.WriteAllText(Path.Combine(Root, "config", "acceptance-manifest.json"),
                JsonSerializer.Serialize(new
                {
                    version = 1,
                    engine = new
                    {
                        enforceStructuralCoverage = false,
                        maxConcurrentShards = 1,
                        infrastructureTestLanes = new[] { new { name = "alpha", filter = "FullyQualifiedName~AlphaTests" } }
                    },
                    checks = new[] { check }
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            if (cacheable)
            {
                var project = Path.Combine(Root, Lane.Project!.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(project)!);
                File.WriteAllText(project, "<Project />");
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
