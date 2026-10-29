using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: fixtures own unique source/journal/TRX roots; all git facts are injected.
public sealed class AcceptancePartitionVerdictCacheDisjointLandingTests
{
    [Fact]
    public void FileDisjointLandingReusesUnreachedLane()
    {
        using var fixture = new Fixture();
        fixture.Seed();
        var requests = new List<DisjointLandingFactsRequest>();
        var cache = fixture.Current(request => { requests.Add(request); return Fixture.Facts(); });

        var result = Assert.IsType<AcceptanceCheckResult>(cache.TryReuse(Fixture.Lane));

        Assert.True(result.Passed);
        Assert.Contains("reuse_rule=disjoint-landing", result.ResultSummary);
        Assert.Contains("source_attempt_id=seed", result.ResultSummary);
        Assert.True(result.TestResultIsExplicitCrossAttemptReuse);
        Assert.Equal("seed", result.TestResultAttemptId);
        Assert.Equal(new[] { fixture.TrxPath }, result.TestResultPaths);
        var decision = Assert.Single(cache.DisjointLandingDecisions);
        Assert.Equal(new DisjointLandingDecision("probe", "reused", "disjoint-unreached", "seed"), decision);
        Assert.Empty(cache.Misses);
        var request = Assert.Single(requests);
        Assert.Equal((fixture.Root, "tree-1", "main-1", "tree-2", "main-2"),
            (request.WorktreePath, request.RecordTree, request.RecordMain, request.CurrentTree, request.CurrentMain));
        var receipt = Assert.IsType<AcceptanceCheckResult>(cache.CompleteAttempt());
        Assert.Contains("reuse_rule=disjoint-landing", receipt.ResultSummary);
    }

    [Theory]
    [InlineData("overlap", "landed-overlaps-goal")]
    [InlineData("named-type", "lane-reached")]
    [InlineData("project", "lane-reached")]
    [InlineData("extra-tree-path", "tree-diff-differs")]
    [InlineData("not-ancestor", "main-not-ancestor")]
    [InlineData("red-lane", "attempt-not-all-green")]
    [InlineData("foreign-goal", "no-qualifying-record")]
    [InlineData("unreadable-trx", "trx-unreadable")]
    [InlineData("throwing-seam", "facts-exception")]
    [InlineData("new-lane-class", "lane-membership-expanded")]
    [InlineData("forced-rerun", null)]
    public void RuleDeclinesAndLeavesMissReceiptUnchanged(string scenario, string? reason)
    {
        using var fixture = new Fixture(scenario == "named-type");
        fixture.Seed(redLane: scenario == "red-lane", foreignGoal: scenario == "foreign-goal");
        if (scenario == "new-lane-class") fixture.AddLandedTestClass();
        if (scenario == "unreadable-trx") File.Delete(fixture.TrxPath);
        if (scenario == "forced-rerun") fixture.SeedCurrentTrigger();
        var facts = scenario switch
        {
            "overlap" => Fixture.Facts() with { RecordGoalDelta = [Fixture.Landed], CurrentGoalDelta = [Fixture.Landed] },
            "project" => Fixture.Facts() with { LandedPaths = ["src/App/App.csproj"], TreeDiff = ["src/App/App.csproj"] },
            "extra-tree-path" => Fixture.Facts() with { TreeDiff = [Fixture.Landed, Fixture.GoalPath] },
            "not-ancestor" => Fixture.Facts() with { RecordMainIsAncestor = false },
            "new-lane-class" => Fixture.Facts() with { LandedPaths = [Fixture.AddedTest], TreeDiff = [Fixture.AddedTest] },
            _ => Fixture.Facts()
        };
        var calls = 0;
        var candidate = fixture.Current(_ =>
        {
            calls++;
            return scenario == "throwing-seam" ? throw new IOException("unavailable") : facts;
        }, force: scenario == "forced-rerun");
        var control = fixture.Current(_ => null, force: scenario == "forced-rerun");

        Assert.Null(candidate.TryReuse(Fixture.Lane));
        Assert.Null(control.TryReuse(Fixture.Lane));

        Assert.NotEmpty(control.Misses);
        Assert.Equal(control.Misses.ToArray(), candidate.Misses.ToArray());
        if (scenario == "forced-rerun")
        {
            Assert.True(candidate.ForceFullRerun);
            Assert.Equal(0, calls);
            Assert.Empty(candidate.DisjointLandingDecisions);
        }
        else
        {
            var decision = Assert.Single(candidate.DisjointLandingDecisions);
            Assert.Equal("declined", decision.Decision);
            Assert.Equal(reason, decision.ReasonCode);
            Assert.Equal(scenario is "foreign-goal" or "red-lane" ? 0 : 1, calls);
        }
    }

    [Theory]
    [InlineData("empty-landings", "landed-set-empty")]
    [InlineData("goal-delta", "goal-delta-differs")]
    [InlineData("unknown-class", "class-unresolved")]
    [InlineData("no-class", "trx-no-class")]
    [InlineData("failed-trx", "trx-not-green")]
    [InlineData("different-manifest", "no-qualifying-record")]
    public void IncompleteProofDeclines(string scenario, string reason)
    {
        using var fixture = new Fixture();
        fixture.Seed();
        if (scenario == "unknown-class") File.WriteAllText(fixture.TrxPath, Fixture.Trx("MissingTests"));
        if (scenario == "no-class") File.WriteAllText(fixture.TrxPath, "<TestRun />");
        if (scenario == "failed-trx") File.WriteAllText(fixture.TrxPath, Fixture.Trx().Replace("Passed", "Failed"));
        var cache = fixture.Current(_ => scenario switch
        {
            "empty-landings" => Fixture.Facts() with { LandedPaths = [], TreeDiff = [] },
            "goal-delta" => Fixture.Facts() with { CurrentGoalDelta = ["src/Core/Another.cs"] },
            _ => Fixture.Facts()
        }, manifest: scenario == "different-manifest" ? "manifest-b" : "manifest-a");

        Assert.Null(cache.TryReuse(Fixture.Lane));
        Assert.Equal(reason, Assert.Single(cache.DisjointLandingDecisions).ReasonCode);
    }

    [Fact]
    public void TwoLanesAtSamePairResolveFactsOnce()
    {
        using var fixture = new Fixture();
        fixture.Seed(secondLane: true);
        var calls = 0;
        var cache = fixture.Current(_ => { calls++; return Fixture.Facts(); });

        Assert.NotNull(cache.TryReuse(Fixture.Lane));
        Assert.NotNull(cache.TryReuse(Fixture.OtherLane));

        Assert.Equal(1, calls);
        Assert.Equal(2, cache.DisjointLandingDecisions.Count);
        Assert.All(cache.DisjointLandingDecisions, decision => Assert.Equal("reused", decision.Decision));
    }

    [Fact]
    public void NewestQualifyingRecordWinsAndStructuralEvidenceIsRequired()
    {
        using var fixture = new Fixture();
        var old = new PartitionVerdictRecord("goal", "old", "old-tree", "main-1", "filter", "probe",
            "key", true, "GREEN", [fixture.TrxPath], DateTimeOffset.UnixEpoch);
        var newest = old with { AttemptId = "newest", CandidateTreeSha = "newest-tree", RecordedAt = old.RecordedAt.AddDays(1) };
        var requests = new List<string>();
        var session = new AcceptanceDisjointLandingLaneReuse.Session(fixture.Root, "goal", "current", "tree-2", "main-2",
            [old, newest], request => { requests.Add(request.RecordTree); return Fixture.Facts(); });

        var reused = session.Decide("probe", "filter", "FullyQualifiedName~LaneProbeTests", _ => true, _ => true);

        Assert.Equal("newest", reused.Record?.AttemptId);
        Assert.Equal(new[] { "newest-tree" }, requests);
        var missingCoverage = session.Decide("probe", "filter", "FullyQualifiedName~LaneProbeTests", _ => true, _ => false);
        Assert.Null(missingCoverage.Record);
        Assert.Equal("missing-structural-coverage-evidence", missingCoverage.Decision.ReasonCode);
    }

    [Fact]
    public void CumulativeBudgetOverrunDeclinesCurrentAndLaterLanes()
    {
        var clock = new ManualClock();
        var calls = 0;
        var record = new PartitionVerdictRecord("goal", "seed", "tree-1", "main-1", "filter", "probe",
            "key", true, "GREEN", [], DateTimeOffset.UnixEpoch);
        var session = new AcceptanceDisjointLandingLaneReuse.Session("unused", "goal", "current", "tree-2", "main-2",
            [record], _ => { calls++; clock.Advance(TimeSpan.FromSeconds(11)); return Fixture.Facts(); }, clock);

        var first = session.Decide("probe", "filter", "FullyQualifiedName~LaneProbeTests", _ => true, _ => true);
        var second = session.Decide("probe", "filter", "FullyQualifiedName~LaneProbeTests", _ => true, _ => true);

        Assert.Null(first.Record);
        Assert.Null(second.Record);
        Assert.Equal("budget-exceeded", first.Decision.ReasonCode);
        Assert.Equal("budget-exceeded", second.Decision.ReasonCode);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("FullyQualifiedName~LaneProbeTests", true)]
    [InlineData("FullyQualifiedName!~OtherProbeTests", true)]
    [InlineData("FullyQualifiedName~LaneProbeTests&FullyQualifiedName!~LaneProbeTestsAdded", false)]
    public void LandedClassMembership_UsesCurrentLaneFilter(string filter, bool included)
    {
        using var fixture = new Fixture();
        fixture.AddLandedTestClass();
        var record = new PartitionVerdictRecord("goal", "seed", "tree-1", "main-1", "filter", "probe",
            "key", true, "GREEN", [fixture.TrxPath], DateTimeOffset.UnixEpoch);
        var session = new AcceptanceDisjointLandingLaneReuse.Session(fixture.Root, "goal", "current", "tree-2", "main-2",
            [record], _ => Fixture.Facts() with { LandedPaths = [Fixture.AddedTest], TreeDiff = [Fixture.AddedTest] });

        var result = session.Decide("probe", "filter", filter, _ => true, _ => true);

        if (included)
        {
            Assert.Null(result.Record);
            Assert.Equal(new DisjointLandingDecision("probe", "declined", "lane-membership-expanded", "seed"), result.Decision);
        }
        else
        {
            Assert.Same(record, result.Record);
            Assert.Equal(new DisjointLandingDecision("probe", "reused", "disjoint-unreached", "seed"), result.Decision);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Landed = "src/Mcg.AgentOrchestrator.App/LandedWidget.cs";
        internal const string GoalPath = "src/Mcg.AgentOrchestrator.Core/GoalOwned.cs";
        internal const string AddedTest = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LaneProbeTestsAdded.cs";
        private static readonly GoalId Goal = new("11111111111111111111111111111111");
        internal static readonly AcceptanceManifestCheck Lane = Check("Probe", "LaneProbeTests");
        internal static readonly AcceptanceManifestCheck OtherLane = Check("Other", "OtherProbeTests");
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"mcg-disjoint-{Guid.NewGuid():N}");
        internal string TrxPath => Path.Combine(Root, "lane.trx");

        internal Fixture(bool namesLandedType = false)
        {
            Write("config/acceptance-manifest.json",
                """{"engine":{"laneTestSourceRoots":["tests/Mcg.AgentOrchestrator.Infrastructure.Tests"]}}""");
            Write(Landed, "internal sealed class LandedWidget { }");
            Write(GoalPath, "internal sealed class GoalOwned { }");
            Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LaneProbeTests.cs",
                "using Xunit; public class LaneProbeTests { [Fact] public void Probe() { " +
                (namesLandedType ? "LandedWidget probe = null;" : "GoalOwned probe = null;") + " } }");
            Write("lane.trx", Trx());
        }

        internal static DisjointLandingFacts Facts() => new(true, [Landed], [GoalPath], [GoalPath], [Landed]);
        internal void AddLandedTestClass() => Write(AddedTest,
            "using Xunit; public class LaneProbeTestsAdded { [Fact] public void Probe() { GoalOwned probe = null; } }");
        internal static string Trx(string name = "LaneProbeTests") =>
            $"<TestRun><TestDefinitions><UnitTest id=\"t\"><TestMethod className=\"{name}\" /></UnitTest></TestDefinitions>" +
            "<Results><UnitTestResult testId=\"t\" outcome=\"Passed\" /></Results></TestRun>";

        internal void Seed(bool redLane = false, bool foreignGoal = false, bool secondLane = false)
        {
            var seed = Cache("seed", "tree-1", "main-1", _ => null,
                goal: foreignGoal ? new GoalId("22222222222222222222222222222222") : Goal);
            seed.RecordExecution(Lane, new(Lane.Name, true, 0, null, TestResultPaths: [TrxPath]));
            if (redLane || secondLane)
                seed.RecordExecution(OtherLane, new(OtherLane.Name, !redLane, redLane ? 1 : 0, null, TestResultPaths: [TrxPath]));
            Assert.NotNull(seed.CompleteAttempt());
            if (foreignGoal)
                File.Copy(seed.JournalPath, Path.Combine(Path.GetDirectoryName(seed.JournalPath)!, $"{Goal.Value}.jsonl"));
        }

        internal void SeedCurrentTrigger()
        {
            var cache = Cache("trigger", "tree-2", "main-2", _ => null);
            cache.RecordExecution(OtherLane, new(OtherLane.Name, true, 0, null, TestResultPaths: [TrxPath]));
            Assert.NotNull(cache.CompleteAttempt());
        }

        internal AcceptancePartitionVerdictCache Current(Func<DisjointLandingFactsRequest, DisjointLandingFacts?> resolver,
            bool force = false, string manifest = "manifest-a") =>
            Cache("current", "tree-2", "main-2", resolver, force ? 1 : 5, manifest: manifest);

        private AcceptancePartitionVerdictCache Cache(string attempt, string tree, string main,
            Func<DisjointLandingFactsRequest, DisjointLandingFacts?> resolver, int fullRerunEveryN = 5,
            GoalId? goal = null, string manifest = "manifest-a") =>
            Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
                new AcceptancePartitionVerdictCacheOptions(goal ?? Goal, Root, [Lane, OtherLane], fullRerunEveryN, false,
                    _ => tree, _ => main, _ => "commit-" + attempt, () => attempt, () => manifest, () => true,
                    ResolveClosureHash: _ => "closure-" + tree,
                    ResolveChangedFiles: (_, _, _) => [], ResolveLaneCarryChangedFiles: (_, _, _) => [],
                    ResolveDisjointLandingFacts: resolver)));

        private static AcceptanceManifestCheck Check(string lane, string filter) => new()
        {
            Name = "infrastructure tests: " + lane, Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~" + filter]
        };

        private void Write(string relative, string content)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
