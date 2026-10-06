using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Uses the shared verifier override state; JobAccounting owns that resource.
[Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsLaneReuseShadowMiss : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    private const string Goal = "12345678123456781234567812345678";
    private const string ReferenceGoal = "87654321876543218765432187654321";
    private const string WidgetPath = "src/Fixture/ShadowWidget.cs";

    [Fact]
    public async Task UnaffectedRedLane_RecordsLandingRuleMissAndFailureClasses()
    {
        var root = CreateFixture();
        try
        {
            var run = await Gate(root);
            Assert.False(run.Result.Passed);
            using var document = ReadRecord(root, Goal);
            var record = document.RootElement;
            var beta = Lane(record, "beta");
            Assert.Equal("would-reuse", beta.GetProperty("decision").GetString());
            Assert.True(beta.GetProperty("executed").GetBoolean());
            Assert.True(beta.GetProperty("miss_evaluated").GetBoolean());
            Assert.Equal("RED", beta.GetProperty("verdict").GetString());
            Assert.True(beta.GetProperty("shadow_miss").GetBoolean());
            Assert.Equal("red-against-green", beta.GetProperty("miss_reason").GetString());
            Assert.Equal("GREEN", beta.GetProperty("reference_verdict").GetString());
            Assert.Equal("landing-rule", beta.GetProperty("reference_source").GetString());
            Assert.NotNull(beta.GetProperty("failed_predicate").GetString());
            Assert.Equal(["BetaPlainTests"], beta.GetProperty("failing_classes").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(JsonValueKind.Null, beta.GetProperty("flake_confirmed").ValueKind);
            var alpha = Lane(record, "alpha");
            Assert.Equal("must-run", alpha.GetProperty("decision").GetString());
            Assert.False(alpha.GetProperty("miss_evaluated").GetBoolean());
            Assert.False(alpha.GetProperty("shadow_miss").GetBoolean());
            Assert.Equal(JsonValueKind.Null, alpha.GetProperty("reference_source").ValueKind);
            Assert.Equal(JsonValueKind.Null, alpha.GetProperty("reference_verdict").ValueKind);
            Assert.Equal(JsonValueKind.Null, alpha.GetProperty("miss_reason").ValueKind);
            Assert.Equal(1, record.GetProperty("summary").GetProperty("shadow_miss_count").GetInt32());
            Assert.Equal(2, record.GetProperty("summary").GetProperty("would_reuse_executed_count").GetInt32());
            Assert.Equal(Git(root, "rev-parse", "main^{tree}"), record.GetProperty("main_tree_sha").GetString());
            Assert.Equal(0, record.GetProperty("unreadable_reference_records").GetInt32());
            Assert.Equal(JsonValueKind.String, record.GetProperty("recorded_at").ValueKind);
            Assert.True(beta.TryGetProperty("duration_ms", out _));
        }
        finally { DeleteDirectoryWithRetry(root); }
    }

    [Theory]
    [InlineData(true, "RED", false)]
    [InlineData(false, "GREEN", true)]
    public async Task MainTreeGate_ProvidesRecordedReference(bool firstFails, string reference, bool miss)
    {
        var root = CreateFixture();
        try
        {
            Git(root, "checkout", "-b", "goal/main-tree", "main");
            Assert.Equal("0", Git(root, "rev-list", "--count", "main..HEAD"));
            var first = await Gate(root, goal: ReferenceGoal, failBeta: firstFails);
            Assert.Equal(!firstFails, first.Result.Passed);
            using var firstDocument = ReadRecord(root, ReferenceGoal);
            var firstRecord = firstDocument.RootElement;
            var attempt = Path.GetFileNameWithoutExtension(RecordPath(root, ReferenceGoal));
            Assert.Equal(reference, Lane(firstRecord, "beta").GetProperty("verdict").GetString());
            Git(root, "checkout", "goal/shadow");
            var second = await Gate(root);
            Assert.False(second.Result.Passed);
            using var secondDocument = ReadRecord(root, Goal);
            var record = secondDocument.RootElement;
            Assert.Equal(firstRecord.GetProperty("candidate_tree_sha").GetString(), record.GetProperty("main_tree_sha").GetString());
            var beta = Lane(record, "beta");
            Assert.Equal("would-reuse", beta.GetProperty("decision").GetString());
            Assert.True(beta.GetProperty("miss_evaluated").GetBoolean());
            Assert.Equal(reference, beta.GetProperty("reference_verdict").GetString());
            Assert.Equal($"recorded:{ReferenceGoal}/{attempt}", beta.GetProperty("reference_source").GetString());
            Assert.Equal(miss, beta.GetProperty("shadow_miss").GetBoolean());
            Assert.Equal(miss ? "red-against-green" : null, beta.GetProperty("miss_reason").GetString());
            Assert.Equal(miss ? 1 : 0, record.GetProperty("summary").GetProperty("shadow_miss_count").GetInt32());
        }
        finally { DeleteDirectoryWithRetry(root); }
    }

    [Fact]
    public async Task PassingProbe_KeepsFirstRunRedMissAndMatchesDisabledGate()
    {
        var enabledRoot = CreateFixture();
        var disabledRoot = CreateFixture();
        try
        {
            var enabled = await Gate(enabledRoot, passingProbe: true);
            var disabled = await Gate(disabledRoot, passingProbe: true, disableShadow: true);
            AssertParity(enabled, disabled);
            Assert.False(enabled.Result.Passed);
            Assert.True(enabled.Result.Retried);
            Assert.False(Directory.Exists(Path.Combine(disabledRoot, ".orchestrator", "lane-reuse-shadow")));
            using var document = ReadRecord(enabledRoot, Goal);
            var beta = Lane(document.RootElement, "beta");
            Assert.Equal("would-reuse", beta.GetProperty("decision").GetString());
            Assert.Equal("RED", beta.GetProperty("verdict").GetString());
            Assert.True(beta.GetProperty("shadow_miss").GetBoolean());
            Assert.True(beta.GetProperty("flake_confirmed").GetBoolean());
            Assert.Equal("nonzero-exit", beta.GetProperty("failed_predicate").GetString());
        }
        finally { DeleteDirectoryWithRetry(enabledRoot); DeleteDirectoryWithRetry(disabledRoot); }
    }

    [Fact]
    public async Task MalformedReferences_AreCountedWithoutChangingGate()
    {
        var enabledRoot = CreateFixture();
        var disabledRoot = CreateFixture();
        try
        {
            var directory = Path.Combine(enabledRoot, ".orchestrator", "lane-reuse-shadow", ReferenceGoal);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "malformed.json"), "{broken");
            File.WriteAllText(Path.Combine(directory, "bad-lanes.json"), JsonSerializer.Serialize(new
            {
                candidate_tree_sha = Git(enabledRoot, "rev-parse", "main^{tree}"), lanes = "unreadable"
            }));
            var enabled = await Gate(enabledRoot);
            var disabled = await Gate(disabledRoot, disableShadow: true);
            AssertParity(enabled, disabled);
            Assert.False(enabled.Result.Passed);
            using var document = ReadRecord(enabledRoot, Goal);
            Assert.Equal(2, document.RootElement.GetProperty("unreadable_reference_records").GetInt32());
            Assert.Equal("landing-rule", Lane(document.RootElement, "beta").GetProperty("reference_source").GetString());
            Assert.True(Lane(document.RootElement, "beta").GetProperty("shadow_miss").GetBoolean());
        }
        finally { DeleteDirectoryWithRetry(enabledRoot); DeleteDirectoryWithRetry(disabledRoot); }
    }

    private async Task<GateRun> Gate(string root, string goal = Goal, bool failBeta = true,
        bool disableShadow = false, bool passingProbe = false)
    {
        ResetPartitionVerdictKeyHooks();
        var oldDisable = TestOverrides.DisableLaneReuseShadowForTests;
        var oldRerun = TestOverrides.PartitionVerdictWithinAttemptRerunEnabled;
        var oldCleanupObserver = TestOverrides.OnGateWorktreeCleanupLineForTests;
        TestOverrides.DisableLaneReuseShadowForTests = disableShadow;
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = passingProbe;
        var calls = new ConcurrentBag<string[]>();
        var cleanupLines = new ConcurrentBag<string>();
        TestOverrides.OnGateWorktreeCleanupLineForTests = cleanupLines.Add;
        var betaRuns = 0;
        var goalId = new GoalId(goal);
        var attemptId = "miss-attempt";
        var resultsPrefix = Path.Combine(root, ".orchestrator", attemptId);
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
                var betaRun = beta ? Interlocked.Increment(ref betaRuns) : 0;
                var passed = !beta || !failBeta || (passingProbe && betaRun == 2);
                // A passing TRX with exit 1 and no all-passed stdout selects the retryable nonzero-exit predicate.
                WriteTrx(args, className, passed || passingProbe);
                if (beta && passingProbe && betaRun == 1)
                    return Task.FromResult(CreateFailedResultWithHeartbeat(goalId, root, resultsPrefix));
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(passed ? 0 : 1,
                    passed ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1." :
                        "Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1."));
            });
            var result = await verifier.RunOwnedAsync(root, goalId, changedFiles: null, stableSlotIndex: null,
                stableSlotLease: null, cancellationToken: default,
                executionOptions: new AcceptanceRunExecutionOptions(RunId: attemptId, ResultsPrefix: resultsPrefix));
            Assert.DoesNotContain(cleanupLines, line =>
                line.StartsWith("GATE_WORKTREE_UNTRACKED_REMOVED", StringComparison.Ordinal) &&
                line.Contains(".orchestrator/lane-reuse-shadow/", StringComparison.Ordinal));
            var filters = calls.Where(IsInfrastructurePartitionTestCall)
                .Select(args => args[Array.IndexOf(args, "--filter") + 1]).Order(StringComparer.Ordinal).ToArray();
            var names = passingProbe ? new[] { "Alpha", "Beta", "Beta", "Remainder" } : ["Alpha", "Beta", "Remainder"];
            Assert.Equal(names.Select(name => ResolveLaneFilter(root, name)).Order(StringComparer.Ordinal), filters);
            Assert.Equal(passingProbe ? 2 : 1, betaRuns);
            return new(result, filters);
        }
        finally
        {
            TestOverrides.DisableLaneReuseShadowForTests = oldDisable;
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = oldRerun;
            TestOverrides.OnGateWorktreeCleanupLineForTests = oldCleanupObserver;
        }
    }

    private static GoalAcceptanceVerifier.CommandResult CreateFailedResultWithHeartbeat(
        GoalId goalId, string root, string resultsPrefix)
    {
        const string checkName = "infrastructure tests: Beta";
        const string stderr = "first Beta nonzero exit";
        var heartbeatPath = GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(
            checkName, root, invocationOrdinal: 0, attemptResultsPrefix: resultsPrefix);
        var now = DateTimeOffset.UtcNow;
        GateHeartbeatArtifacts.Write(heartbeatPath, new GateHeartbeatSnapshot(
            goalId.Value, "verification-check", checkName, null, Environment.ProcessId, null, "completed",
            now, now, now, 0, Encoding.UTF8.GetByteCount(stderr), Encoding.UTF8.GetByteCount(stderr),
            "test command", 1, StderrPath: null));
        return new GoalAcceptanceVerifier.CommandResult(1,
            "Beta test process exited with code 1.", Stderr: stderr);
    }

    private static void AssertParity(GateRun first, GateRun second)
    {
        Assert.Equal(first.Result.Passed, second.Result.Passed);
        Assert.Equal(first.Result.Checks.Select(check => (check.Name, check.Passed)),
            second.Result.Checks.Select(check => (check.Name, check.Passed)));
        Assert.Equal(first.Filters, second.Filters);
    }

    private static JsonElement Lane(JsonElement record, string partition) =>
        Assert.Single(record.GetProperty("lanes").EnumerateArray(), row => row.GetProperty("partition_id").GetString() == partition);
    private static string RecordPath(string root, string goal) => Assert.Single(Directory.GetFiles(
        Path.Combine(root, ".orchestrator", "lane-reuse-shadow", goal), "*.json"));
    private static JsonDocument ReadRecord(string root, string goal) => JsonDocument.Parse(File.ReadAllText(RecordPath(root, goal)));

    private static string CreateFixture()
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
            Write(root, ".gitignore", ".orchestrator/\n");
            Write(root, "src/Fixture/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write(root, WidgetPath, "public class ShadowWidget { public int Value => 1; }");
            Write(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../../src/Fixture/Fixture.csproj\" /></ItemGroup></Project>");
            Write(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AlphaWidgetConsumerTests.cs",
                "public class AlphaWidgetConsumerTests { private ShadowWidget widget; [Xunit.Fact] public void Example() { } }");
            Write(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/BetaPlainTests.cs",
                "public class BetaPlainTests { [Xunit.Fact] public void Example() { } }");
            Write(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GammaPlainTests.cs",
                "public class GammaPlainTests { [Xunit.Fact] public void Example() { } }");
            Git(root, "init", "--initial-branch=main");
            Git(root, "add", ".gitignore", "config", "src", "tests");
            Commit(root, "fixture main");
            Git(root, "checkout", "-b", "goal/shadow");
            Write(root, WidgetPath, "public class ShadowWidget { public int Value => 2; }");
            Git(root, "add", "src");
            Commit(root, "fixture goal");
            Assert.Equal("goal/shadow", Git(root, "branch", "--show-current"));
            Assert.Equal("1", Git(root, "rev-list", "--count", "main..HEAD"));
            Assert.Equal(WidgetPath, Git(root, "diff", "--name-only", "main", "HEAD"));
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

    private static void Commit(string root, string message) => Git(root,
        "-c", "user.name=Shadow Fixture", "-c", "user.email=shadow@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", message);
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
