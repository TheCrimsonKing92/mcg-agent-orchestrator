using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsLaneReuseShadow : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    private const string Goal = "12345678123456781234567812345678";
    private const string WidgetPath = "src/Fixture/ShadowWidget.cs";

    [Fact]
    public async Task SourceChangeRecordsConsumerAndUnaffectedLanesWithActualVerdicts()
    {
        var root = CreateFixture();
        try
        {
            var run = await Gate(root, failBeta: true);
            Assert.False(run.Result.Passed);
            using var document = ReadRecord(root);
            var record = document.RootElement;
            Assert.Equal(Git(root, "rev-parse", "main"), record.GetProperty("main_sha").GetString());
            Assert.Equal(Git(root, "rev-parse", "HEAD^{tree}"), record.GetProperty("candidate_tree_sha").GetString());
            Assert.Equal(Git(root, "rev-parse", "HEAD"), record.GetProperty("verifying_commit_sha").GetString());
            Assert.Equal("resolved", record.GetProperty("status").GetString());
            Assert.Equal(1, record.GetProperty("changed_file_count").GetInt32());
            AssertLane(record, "alpha", "must-run", "references-changed-source:AlphaWidgetConsumerTests", "GREEN");
            AssertLane(record, "beta", "would-reuse", "unaffected", "RED");
            AssertLane(record, "remainder", "would-reuse", "unaffected", "GREEN");
            var rows = record.GetProperty("lanes").EnumerateArray().ToArray();
            Assert.Equal(3, rows.Length);
            Assert.All(rows, row =>
            {
                Assert.True(row.GetProperty("executed").GetBoolean());
                Assert.Equal(JsonValueKind.Number, row.GetProperty("duration_ms").ValueKind);
                var check = Assert.Single(run.Result.Checks, check => check.Name == row.GetProperty("lane").GetString());
                Assert.Equal(check.Passed ? "GREEN" : "RED", row.GetProperty("verdict").GetString());
            });
            Assert.Equal(3, run.Filters.Length);
            Assert.Equal(3, record.GetProperty("summary").GetProperty("lane_count").GetInt32());
            Assert.Equal(2, record.GetProperty("summary").GetProperty("would_reuse_count").GetInt32());
            Assert.Equal(1, record.GetProperty("summary").GetProperty("must_run_count").GetInt32());
        }
        finally { DeleteDirectoryWithRetry(root); }
    }

    [Fact]
    public async Task OutsideSourceAndTestsRequiresEveryLane()
    {
        var root = CreateFixture(outsideChange: true);
        try
        {
            var run = await Gate(root);
            Assert.True(run.Result.Passed, DescribeFailures(run.Result));
            using var document = ReadRecord(root);
            Assert.Equal(3, document.RootElement.GetProperty("lanes").GetArrayLength());
            Assert.All(document.RootElement.GetProperty("lanes").EnumerateArray(), row =>
            {
                Assert.Equal("must-run", row.GetProperty("decision").GetString());
                Assert.Equal("change-outside-src-tests:docs/shadow-note.md", row.GetProperty("reason").GetString());
            });
        }
        finally { DeleteDirectoryWithRetry(root); }
    }

    [Fact]
    public async Task ProcessMarkerRequiresBetaAlongsideSourceConsumer()
    {
        var root = CreateFixture(betaMarker: true);
        try
        {
            var run = await Gate(root);
            Assert.True(run.Result.Passed, DescribeFailures(run.Result));
            using var document = ReadRecord(root);
            AssertLane(document.RootElement, "alpha", "must-run", "references-changed-source:AlphaWidgetConsumerTests", "GREEN");
            AssertLane(document.RootElement, "beta", "must-run", "always-affected:process-spawning:BetaPlainTests", "GREEN");
            AssertLane(document.RootElement, "remainder", "would-reuse", "unaffected", "GREEN");
        }
        finally { DeleteDirectoryWithRetry(root); }
    }

    [Fact]
    public async Task DisablingShadowPreservesGateChecksFiltersAndCacheReceipt()
    {
        var disabledRoot = CreateFixture();
        var enabledRoot = CreateFixture();
        try
        {
            var disabled = await Gate(disabledRoot, disableShadow: true);
            var enabled = await Gate(enabledRoot);
            AssertParity(disabled, enabled);
            Assert.False(Directory.Exists(RecordDirectory(disabledRoot)));
            using var record = ReadRecord(enabledRoot);
            Assert.Equal(3, record.RootElement.GetProperty("lanes").GetArrayLength());
        }
        finally { DeleteDirectoryWithRetry(disabledRoot); DeleteDirectoryWithRetry(enabledRoot); }
    }

    [Fact]
    public async Task BlockedRecordDirectoryDoesNotChangeTheGateOrThrow()
    {
        var controlRoot = CreateFixture();
        var blockedRoot = CreateFixture();
        var blockedPath = Path.Combine(blockedRoot, ".orchestrator", "lane-reuse-shadow");
        Directory.CreateDirectory(Path.GetDirectoryName(blockedPath)!);
        File.WriteAllText(blockedPath, "blocking file");
        try
        {
            var control = await Gate(controlRoot);
            var blocked = await Gate(blockedRoot);
            AssertParity(control, blocked);
            Assert.Equal("blocking file", File.ReadAllText(blockedPath));
            Assert.False(Directory.Exists(RecordDirectory(blockedRoot)));
        }
        finally { DeleteDirectoryWithRetry(controlRoot); DeleteDirectoryWithRetry(blockedRoot); }
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
        foreach (var token in new[] { "executed_partitions", "reused_partitions" })
        {
            string ReadToken(GateRun run)
            {
                var receipt = Assert.Single(run.Result.Checks, check => check.Name == "infrastructure partition verdict cache");
                var match = Regex.Match(receipt.ResultSummary!, $@"{token}=([^ ]+)");
                Assert.True(match.Success, receipt.ResultSummary);
                return match.Groups[1].Value;
            }
            Assert.Equal(ReadToken(first), ReadToken(second));
        }
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
        var result = InfrastructureTestSupport.RunGitProbe(root, args);
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
