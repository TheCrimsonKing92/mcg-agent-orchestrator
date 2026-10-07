using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Serial collection: verifier override hooks; repository and command fixtures are privately owned.
[Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsLaneReuseShadowRuleV2 : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    private const string Goal = "12345678123456781234567812345678";
    private const string WidgetPath = "src/Fixture/ShadowWidget.cs";

    [Fact]
    public async Task DualRuleRecording_ExecutesAllLanesAndPreservesGateParity()
    {
        var disabledRoot = CreateFixture(betaMarker: true);
        var enabledRoot = CreateFixture(betaMarker: true);
        try
        {
            var disabled = await Gate(disabledRoot, disableShadow: true);
            var enabled = await Gate(enabledRoot);
            AssertParity(disabled, enabled);
            Assert.False(Directory.Exists(RecordDirectory(disabledRoot)));
            using var document = ReadRecord(enabledRoot);
            var record = document.RootElement;
            Assert.Equal(new[] { "marker-v1", "launch-contract-v2" },
                record.GetProperty("rule_versions").EnumerateArray().Select(value => value.GetString()));
            var rows = record.GetProperty("lanes").EnumerateArray().ToArray();
            Assert.Equal(3, rows.Length);
            Assert.All(rows, row =>
            {
                Assert.True(row.GetProperty("executed").GetBoolean());
                Assert.Equal(JsonValueKind.Object, row.GetProperty("rule_v2").ValueKind);
            });
            var beta = Assert.Single(rows, row => row.GetProperty("partition_id").GetString() == "beta");
            Assert.Equal("must-run", beta.GetProperty("decision").GetString());
            Assert.Equal("would-reuse", beta.GetProperty("rule_v2").GetProperty("decision").GetString());
            Assert.Equal("unaffected", beta.GetProperty("rule_v2").GetProperty("reason").GetString());
            Assert.Empty(beta.GetProperty("rule_v2").GetProperty("provenance").GetProperty("launch_targets").EnumerateArray());
            var summary = record.GetProperty("summary_v2");
            Assert.Equal(3, summary.GetProperty("lane_count").GetInt32());
            Assert.Equal(2, summary.GetProperty("would_reuse_count").GetInt32());
            Assert.Equal(1, summary.GetProperty("must_run_count").GetInt32());
            Assert.Equal(2, summary.GetProperty("would_reuse_executed_count").GetInt32());
            Assert.Equal(0, summary.GetProperty("shadow_miss_count").GetInt32());

            var checks = rows.Select(row => new AcceptanceManifestCheck
            {
                Name = row.GetProperty("lane").GetString()!, Type = "dotnet-test",
                Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                Arguments = ["--filter", ResolveLaneFilter(enabledRoot, row.GetProperty("partition_id").GetString() switch
                { "alpha" => "Alpha", "beta" => "Beta", _ => "Remainder" })]
            }).ToArray();
            var inventory = AcceptanceTestClassSourceScanner.ScanSources(enabledRoot);
            var expected = AcceptanceLaneReuseShadowClassifier.Classify([WidgetPath], checks, inventory,
                new(true, ["AlphaWidgetConsumerTests"], null, null));
            Assert.Equal(3, expected.Lanes.Count);
            foreach (var lane in expected.Lanes)
            {
                var row = Assert.Single(rows, row => row.GetProperty("lane").GetString() == lane.Lane);
                Assert.Equal(lane.Decision, row.GetProperty("decision").GetString());
                Assert.Equal(lane.Reason, row.GetProperty("reason").GetString());
            }
        }
        finally { DeleteDirectoryWithRetry(disabledRoot); DeleteDirectoryWithRetry(enabledRoot); }
    }

    private async Task<GateRun> Gate(string root, bool disableShadow = false, bool failBeta = false)
    {
        ResetPartitionVerdictKeyHooks();
        var oldDisable = TestOverrides.DisableLaneReuseShadowForTests;
        var oldRerun = TestOverrides.PartitionVerdictWithinAttemptRerunEnabled;
        TestOverrides.DisableLaneReuseShadowForTests = disableShadow;
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        var calls = new ConcurrentBag<string[]>();
        var cleanupLines = new ConcurrentBag<string>();
        var oldCleanupObserver = TestOverrides.OnGateWorktreeCleanupLineForTests;
        TestOverrides.OnGateWorktreeCleanupLineForTests = cleanupLines.Add;
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
            {
                calls.Add(args);
                var testCall = args.Length > 1 && args[0] == "dotnet" && args[1] == "test";
                if (!testCall) return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                var beta = args.Contains(ResolveLaneFilter(root, "Beta"));
                var alpha = args.Contains(ResolveLaneFilter(root, "Alpha"));
                var className = beta ? "BetaPlainTests" : alpha ? "AlphaWidgetConsumerTests" : "GammaPlainTests";
                var passed = !beta || !failBeta;
                WriteTrx(args, className, passed);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(passed ? 0 : 1,
                    passed ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1." :
                        "Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1."));
            });
            var result = await verifier.RunAsync(root, new GoalId(Goal));
            Assert.DoesNotContain(cleanupLines, line =>
                line.StartsWith("GATE_WORKTREE_UNTRACKED_REMOVED", StringComparison.Ordinal) &&
                line.Contains(".orchestrator/lane-reuse-shadow/", StringComparison.Ordinal));
            var filters = calls.Where(IsInfrastructurePartitionTestCall)
                .Select(args => args[Array.IndexOf(args, "--filter") + 1]).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "Alpha", "Beta", "Remainder" }.Select(name => ResolveLaneFilter(root, name)).Order(StringComparer.Ordinal), filters);
            return new(result, filters);
        }
        finally
        {
            TestOverrides.DisableLaneReuseShadowForTests = oldDisable;
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = oldRerun;
            TestOverrides.OnGateWorktreeCleanupLineForTests = oldCleanupObserver;
        }
    }

    private static void AssertParity(GateRun first, GateRun second)
    {
        Assert.True(first.Result.Passed, DescribeFailures(first.Result));
        Assert.Equal(first.Result.Passed, second.Result.Passed);
        Assert.Equal(first.Result.Checks.Select(check => (check.Name, check.Passed)), second.Result.Checks.Select(check => (check.Name, check.Passed)));
        Assert.Equal(first.Filters, second.Filters);
        string Receipt(GateRun run)
        {
            var check = Assert.Single(run.Result.Checks, check => check.Name == "infrastructure partition verdict cache");
            Assert.NotNull(check.ResultSummary);
            Assert.Contains("executed_partitions=", check.ResultSummary, StringComparison.Ordinal);
            // Compare the complete decision receipt; measured durations cannot decide test correctness.
            return Regex.Replace(check.ResultSummary!,
                @"(executed_lane_duration_ms|shared_prebuild_duration_ms)=[^ ]+", "$1=observed");
        }
        Assert.Equal(Receipt(first), Receipt(second));
    }

    private static void AssertLane(JsonElement record, string id, string decision, string reason, string verdict)
    {
        var row = Assert.Single(record.GetProperty("lanes").EnumerateArray(), row => row.GetProperty("partition_id").GetString() == id);
        Assert.Equal(decision, row.GetProperty("decision").GetString());
        Assert.Equal(reason, row.GetProperty("reason").GetString());
        Assert.Equal(verdict, row.GetProperty("verdict").GetString());
    }

    private static string RecordDirectory(string root) => Path.Combine(root, ".orchestrator", "lane-reuse-shadow", Goal);
    private static JsonDocument ReadRecord(string root) => JsonDocument.Parse(File.ReadAllText(
        Assert.Single(Directory.GetFiles(RecordDirectory(root), "*.json"))));
    private static string DescribeFailures(AcceptanceVerificationResult result) =>
        string.Join("\n", result.Checks.Where(check => !check.Passed).Select(check => $"{check.Name}: {check.OutputTail}"));

    private static string CreateFixture(bool outsideChange = false, bool betaMarker = false)
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "enforceStructuralCoverage": false,
                "maxConcurrentShards": 1,
                "infrastructureTestLanes": [
                  { "name": "Alpha", "filter": "FullyQualifiedName~AlphaWidgetConsumerTests" },
                  { "name": "Beta", "filter": "FullyQualifiedName~BetaPlainTests" },
                  { "name": "Remainder", "filter": "FullyQualifiedName!~AlphaWidgetConsumerTests&FullyQualifiedName!~BetaPlainTests&Category!=HostIntegration" }
                ]
              },
              "checks": [
                { "name": "infrastructure tests", "type": "dotnet-test",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            // Match the repository's state-directory ignore so gate cleanup retains its records.
            Write(root, ".gitignore", ".orchestrator/\n");
            Write(root, "src/Fixture/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write(root, WidgetPath, "public class ShadowWidget { public int Value => 1; }");
            Write(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../../src/Fixture/Fixture.csproj\" /></ItemGroup></Project>");
            Write(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AlphaWidgetConsumerTests.cs",
                "public class AlphaWidgetConsumerTests { private ShadowWidget widget; [Xunit.Fact] public void Example() { } }");
            Write(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/BetaPlainTests.cs",
                "public class BetaPlainTests { [Xunit.Fact] public void Example() { } }" + (betaMarker ? " // ProcessStartInfo" : ""));
            Write(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GammaPlainTests.cs",
                "public class GammaPlainTests { [Xunit.Fact] public void Example() { } }");
            Git(root, "init", "--initial-branch=main");
            Git(root, "add", ".gitignore", "config", "src", "tests");
            Commit(root, "fixture main");
            Git(root, "checkout", "-b", "goal/shadow");
            Write(root, WidgetPath, "public class ShadowWidget { public int Value => 2; }");
            if (outsideChange) Write(root, "docs/shadow-note.md", "shadow fixture note");
            Git(root, "add", "src");
            if (outsideChange) Git(root, "add", "docs");
            Commit(root, "fixture goal");
            Assert.Equal("goal/shadow", Git(root, "branch", "--show-current"));
            Assert.Equal("1", Git(root, "rev-list", "--count", "main..HEAD"));
            return root;
        }
        catch { DeleteDirectoryWithRetry(root); throw; }
    }

    private static void Write(string root, string path, string text)
    {
        var absolute = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, text);
    }
    private static void Commit(string root, string message) =>
        Git(root, "-c", "user.name=Shadow Fixture", "-c", "user.email=shadow@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", message);

    private static string Git(string root, params string[] args)
    {
        var result = InfrastructureTestSupport.RunGitProbe(root, args, commandEnvironment: new Dictionary<string, string>
        {
            ["GIT_AUTHOR_DATE"] = "2026-10-06T00:00:00Z",
            ["GIT_COMMITTER_DATE"] = "2026-10-06T00:00:00Z"
        });
        Assert.True(result.Succeeded, result.ToString());
        return result.StandardOutput.Trim();
    }

    private static void WriteTrx(string[] args, string className, bool passed)
    {
        var directory = args[Array.IndexOf(args, "--results-directory") + 1];
        var logger = args[Array.IndexOf(args, "--logger") + 1];
        const string prefix = "trx;LogFileName=";
        Assert.StartsWith(prefix, logger, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(directory);
        var outcome = passed ? "Passed" : "Failed";
        var document = new XDocument(new XElement("TestRun",
            new XElement("TestDefinitions", new XElement("UnitTest", new XAttribute("id", "1"),
                new XElement("TestMethod", new XAttribute("className", className), new XAttribute("name", "Example")))),
            new XElement("Results", new XElement("UnitTestResult", new XAttribute("testId", "1"),
                new XAttribute("testName", $"{className}.Example"), new XAttribute("outcome", outcome))),
            new XElement("ResultSummary", new XAttribute("outcome", "Completed"), new XElement("Counters",
                new XAttribute("total", 1), new XAttribute("executed", 1), new XAttribute("passed", passed ? 1 : 0),
                new XAttribute("failed", passed ? 0 : 1), new XAttribute("notExecuted", 0)))));
        document.Save(Path.Combine(directory, logger[prefix.Length..]));
    }

    private sealed record GateRun(AcceptanceVerificationResult Result, string[] Filters);
}

