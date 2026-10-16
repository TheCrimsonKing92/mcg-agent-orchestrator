using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Xunit;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: each fixture owns its host state, source files and TRX files.
public sealed class AcceptanceLaneCarryShadowTests
{
    [Fact]
    public void Complete_WritesRecordAndNoBaselineCannotCarry()
    {
        using var fixture = new Fixture();
        var shadow = fixture.Shadow("current");
        shadow.Observe(Fixture.Check(), "lane-a", fixture.Result());
        shadow.Observe(Fixture.Check(), "lane-b", fixture.Result());
        Assert.False(File.Exists(fixture.RecordPath("current")));
        shadow.Complete();
        using var document = fixture.Read("current");
        var record = document.RootElement;
        Assert.Equal(Fixture.Goal.Value, record.GetProperty("goal_id").GetString());
        Assert.Equal("tree-current", record.GetProperty("candidate_tree").GetString());
        Assert.Equal(JsonValueKind.Number, record.GetProperty("elapsed_ms").ValueKind);
        Assert.Equal(2, record.GetProperty("lanes").GetArrayLength());
        foreach (var lane in record.GetProperty("lanes").EnumerateArray())
        {
            Assert.Equal("no-baseline", lane.GetProperty("baseline_status").GetString());
            Assert.Equal("Ns.CarryFixtureTests", Assert.Single(lane.GetProperty("test_classes").EnumerateArray()).GetString());
            Assert.Empty(lane.GetProperty("unresolved_classes").EnumerateArray());
            AssertTiers(lane, false, false);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnrelatedDiff_CarriesAndCountsActualFailure(bool passed)
    {
        using var fixture = new Fixture();
        fixture.Seed("baseline");
        var calls = 0;
        var shadow = fixture.Shadow("current", (root, baseline, candidate) =>
        {
            calls++;
            Assert.Equal(fixture.Root, root);
            Assert.Equal("tree-baseline", baseline);
            Assert.Equal("tree-current", candidate);
            return ["data/unmentioned.bin"];
        });
        shadow.Observe(Fixture.Check(), "lane", fixture.Result(passed));
        Assert.Equal(0, calls);
        shadow.Complete();
        Assert.Equal(1, calls);
        using var document = fixture.Read("current");
        var lane = OnlyLane(document);
        Assert.Equal("found", lane.GetProperty("baseline_status").GetString());
        Assert.Equal("baseline", lane.GetProperty("baseline_attempt").GetString());
        AssertTiers(lane, true, !passed);
        foreach (var tier in document.RootElement.GetProperty("summary").EnumerateObject())
        {
            Assert.Equal(1, tier.Value.GetProperty("lane_count").GetInt32());
            Assert.Equal(1, tier.Value.GetProperty("would_carry_count").GetInt32());
            Assert.Equal(passed ? 0 : 1, tier.Value.GetProperty("false_carry_count").GetInt32());
        }
    }

    [Theory]
    [InlineData(false, "lane")]
    [InlineData(true, "other-filter")]
    public void FailedOrDifferentFilter_IsNotBaseline(bool passed, string filter)
    {
        using var fixture = new Fixture();
        fixture.Seed("ineligible", passed, filter);
        var calls = 0;
        var shadow = fixture.Shadow("current", (_, _, _) => { calls++; return []; });
        shadow.Observe(Fixture.Check(), "lane", fixture.Result());
        shadow.Complete();
        Assert.Equal(0, calls);
        using var document = fixture.Read("current");
        Assert.Equal("no-baseline", OnlyLane(document).GetProperty("baseline_status").GetString());
        AssertTiers(OnlyLane(document), false, false);
    }

    [Fact]
    public void LatestPassedBaseline_WinsOverNewerFailedRecord()
    {
        using var fixture = new Fixture();
        fixture.Seed("old");
        fixture.Seed("new");
        fixture.Seed("failed", passed: false);
        fixture.SetCompletion("old", DateTimeOffset.UnixEpoch);
        fixture.SetCompletion("new", DateTimeOffset.UnixEpoch.AddDays(1));
        fixture.SetCompletion("failed", DateTimeOffset.UnixEpoch.AddDays(2));
        var shadow = fixture.Shadow("current");
        shadow.Observe(Fixture.Check(), "lane", fixture.Result());
        shadow.Complete();
        using var document = fixture.Read("current");
        Assert.Equal("new", OnlyLane(document).GetProperty("baseline_attempt").GetString());
    }

    [Fact]
    public void MissingTree_LogsAndDisablesCarryWithoutThrowing()
    {
        using var fixture = new Fixture();
        fixture.Seed("baseline");
        var shadow = fixture.Shadow("current", (_, _, _) => throw new IOException("missing tree"));
        shadow.Observe(Fixture.Check(), "lane", fixture.Result());
        var log = AsyncLocalConsoleRouter.Capture(() => Assert.Null(Record.Exception(shadow.Complete)));
        Assert.Contains("LANE_CARRY_SHADOW_UNAVAILABLE baseline-tree-missing", log);
        using var document = fixture.Read("current");
        Assert.Equal("baseline-tree-missing", OnlyLane(document).GetProperty("baseline_status").GetString());
        AssertTiers(OnlyLane(document), false, false);
    }

    [Fact]
    public void UnwritableDirectory_LogsWithoutThrowing()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".orchestrator"));
        File.WriteAllText(Path.Combine(fixture.Root, ".orchestrator", "lane-carry-shadow"), "directory blocked");
        var shadow = fixture.Shadow("current");
        shadow.Observe(Fixture.Check(), "lane", fixture.Result());
        var log = AsyncLocalConsoleRouter.Capture(() => Assert.Null(Record.Exception(shadow.Complete)));
        Assert.Contains("LANE_CARRY_SHADOW_UNAVAILABLE", log);
        Assert.False(File.Exists(fixture.RecordPath("current")));
    }

    [Fact]
    public void SharedBaselineTree_DiffIsResolvedOnceAndLastObservationWins()
    {
        using var fixture = new Fixture();
        var baseline = fixture.Shadow("baseline");
        baseline.Observe(Fixture.Check(), "a", fixture.Result());
        baseline.Observe(Fixture.Check(), "b", fixture.Result());
        baseline.Complete();
        var calls = 0;
        var shadow = fixture.Shadow("current", (_, _, _) => { calls++; return ["data/unrelated.bin"]; });
        shadow.Observe(Fixture.Check(), "a", fixture.Result());
        shadow.Observe(Fixture.Check(), "a", fixture.Result(false));
        shadow.Observe(Fixture.Check(), "b", fixture.Result());
        shadow.Complete();
        shadow.Complete();
        Assert.Equal(1, calls);
        using var document = fixture.Read("current");
        Assert.Equal(2, document.RootElement.GetProperty("lanes").GetArrayLength());
        Assert.Equal(1, document.RootElement.GetProperty("summary").GetProperty("mid").GetProperty("false_carry_count").GetInt32());
    }

    [Theory]
    [InlineData("Ns.CarryFixtureTests+Nested`1", false)]
    [InlineData("Ns.UnknownTests", true)]
    public void TrxClasses_NormalizeForLookupAndUnresolvedCannotCarry(string className, bool unresolved)
    {
        using var fixture = new Fixture();
        fixture.Seed("baseline");
        var shadow = fixture.Shadow("current");
        shadow.Observe(Fixture.Check(), "lane", fixture.Result(className: className));
        shadow.Complete();
        using var document = fixture.Read("current");
        var lane = OnlyLane(document);
        Assert.Equal(unresolved ? 1 : 0, lane.GetProperty("unresolved_classes").GetArrayLength());
        AssertTiers(lane, !unresolved, false);
    }

    [Fact]
    public void MalformedBaseline_IsCountedAndSkipped()
    {
        using var fixture = new Fixture();
        fixture.Seed("baseline");
        File.WriteAllText(fixture.RecordPath("malformed"), "{bad-json");
        var shadow = fixture.Shadow("current");
        shadow.Observe(Fixture.Check(), "lane", fixture.Result());
        shadow.Complete();
        using var document = fixture.Read("current");
        Assert.Equal(1, document.RootElement.GetProperty("skipped_baseline_files").GetInt32());
        AssertTiers(OnlyLane(document), true, false);
    }

    [Fact]
    public void BaselineSearch_ReadsOnlyNewestTwoHundredFiles()
    {
        using var fixture = new Fixture();
        fixture.Seed("old-passed");
        File.SetLastWriteTimeUtc(fixture.RecordPath("old-passed"), DateTime.UnixEpoch);
        // The only eligible record is older than the bounded read window.
        for (var index = 0; index < 200; index++)
        {
            var path = fixture.RecordPath($"recent-{index}");
            File.WriteAllText(path, "{malformed");
            File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddDays(1));
        }
        var shadow = fixture.Shadow("current", (_, _, _) => throw new InvalidOperationException("must not diff"));
        shadow.Observe(Fixture.Check(), "lane", fixture.Result());
        shadow.Complete();
        using var document = fixture.Read("current");
        Assert.Equal(200, document.RootElement.GetProperty("skipped_baseline_files").GetInt32());
        Assert.Equal("no-baseline", OnlyLane(document).GetProperty("baseline_status").GetString());
    }

    [Fact]
    public void Complete_ExcludesCurrentAttemptAndFutureCompletion()
    {
        using var fixture = new Fixture();
        fixture.Seed("current");
        fixture.Seed("future");
        fixture.SetCompletion("future", DateTimeOffset.MaxValue);
        var calls = 0;
        var shadow = fixture.Shadow("current", (_, _, _) => { calls++; return []; });
        shadow.Observe(Fixture.Check(), "lane", fixture.Result());
        shadow.Complete();
        Assert.Equal(0, calls);
        using var document = fixture.Read("current");
        Assert.Equal("no-baseline", OnlyLane(document).GetProperty("baseline_status").GetString());
    }

    [Fact]
    public void NoTrx_IsFailedAndNeverBecomesBaseline()
    {
        using var fixture = new Fixture();
        var shadow = fixture.Shadow("empty");
        shadow.Observe(Fixture.Check(), "lane", new("lane", true, 0, null));
        shadow.Complete();
        using var empty = fixture.Read("empty");
        Assert.Equal("failed", OnlyLane(empty).GetProperty("outcome").GetString());
        Assert.Empty(OnlyLane(empty).GetProperty("test_classes").EnumerateArray());
        fixture.Seed("current");
        using var current = fixture.Read("current");
        Assert.Equal("no-baseline", OnlyLane(current).GetProperty("baseline_status").GetString());
    }

    private static JsonElement OnlyLane(JsonDocument record) => Assert.Single(record.RootElement.GetProperty("lanes").EnumerateArray());
    private static void AssertTiers(JsonElement lane, bool carry, bool falseCarry)
    {
        var tiers = lane.GetProperty("tiers");
        Assert.Equal(3, tiers.EnumerateObject().Count());
        foreach (var tier in tiers.EnumerateObject())
        {
            Assert.Equal(carry, tier.Value.GetProperty("would_carry").GetBoolean());
            Assert.Equal(falseCarry, tier.Value.GetProperty("false_carry").GetBoolean());
            if (carry) Assert.False(tier.Value.GetProperty("reached").GetBoolean());
        }
    }

    internal sealed class Fixture : IDisposable
    {
        internal static readonly GoalId Goal = new("ed166ed190cd4c23ab620e95fffeddb7");
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-carry-tests", Guid.NewGuid().ToString("N"));
        internal Fixture()
        {
            var sources = Path.Combine(Root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(sources);
            File.WriteAllText(Path.Combine(sources, "CarryFixtureTests.cs"),
                "namespace Ns; public class CarryFixtureTests { [Fact] public void Case() {} }");
        }
        internal AcceptanceLaneCarryShadow Shadow(string attempt,
            Func<string, string, string, IReadOnlyList<string>>? resolver = null) =>
            new(Root, Goal.Value, attempt, $"tree-{attempt}", resolver ?? ((_, _, _) => ["data/unrelated.bin"]));
        internal void Seed(string attempt, bool passed = true, string filter = "lane")
        {
            var shadow = Shadow(attempt);
            shadow.Observe(Check(), filter, Result(passed));
            shadow.Complete();
            Assert.True(File.Exists(RecordPath(attempt)));
        }
        internal AcceptanceCheckResult Result(bool passed = true, string className = "Ns.CarryFixtureTests")
        {
            var path = Path.Combine(Root, $"{Guid.NewGuid():N}.trx");
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            new XDocument(new XElement(ns + "TestRun",
                new XElement(ns + "TestDefinitions", new XElement(ns + "UnitTest", new XAttribute("id", "test"),
                    new XElement(ns + "TestMethod", new XAttribute("className", className), new XAttribute("name", "Case")))),
                new XElement(ns + "Results", new XElement(ns + "UnitTestResult", new XAttribute("testId", "test"),
                    new XAttribute("testName", $"{className}.Case"), new XAttribute("outcome", passed ? "Passed" : "Failed")))))
                .Save(path);
            return new(Check().Name, passed, passed ? 0 : 1, passed ? null : "failed", TestResultPaths: [path], DurationMilliseconds: 100);
        }
        internal static AcceptanceManifestCheck Check() => new()
        {
            Name = "infrastructure tests: CarryFixture", Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~CarryFixtureTests"]
        };
        internal string RecordPath(string attempt) => Path.Combine(Root, ".orchestrator", "lane-carry-shadow", Goal.Value, $"{attempt}.json");
        internal JsonDocument Read(string attempt) => JsonDocument.Parse(File.ReadAllText(RecordPath(attempt)));
        internal void SetCompletion(string attempt, DateTimeOffset completed)
        {
            var record = JsonNode.Parse(File.ReadAllText(RecordPath(attempt)))!;
            record["completed_at"] = completed;
            File.WriteAllText(RecordPath(attempt), record.ToJsonString());
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
