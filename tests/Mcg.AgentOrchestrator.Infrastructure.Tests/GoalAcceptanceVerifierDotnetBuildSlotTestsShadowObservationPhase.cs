using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsShadowObservationPhase : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    private const string Goal = "12345678123456781234567812345678";
    private const string WidgetPath = "src/Fixture/ShadowWidget.cs";

    [Fact]
    public async Task ShadowWork_RunsInOwnPhaseAfterVerdictAndReconcilesAccounting()
    {
        var root = CreateFixture();
        try
        {
            var run = await Gate(root);
            Assert.True(run.Result.Passed, DescribeFailures(run.Result));
            Assert.Equal(AcceptanceGatePhaseNames.ShadowObservation, Assert.Single(run.ResolverPhases));
            var emitted = Assert.Single(run.Progress, item => item.Phase == "gate-phase-breakdown");
            var breakdown = Assert.IsType<AcceptanceGatePhaseBreakdown>(emitted.PhaseBreakdown);
            var phases = breakdown.Phases.Select(phase => phase.Name).ToList();
            Assert.True(phases.IndexOf(AcceptanceGatePhaseNames.ShadowObservation) > phases.IndexOf(AcceptanceGatePhaseNames.Finalize));
            Assert.True(phases.IndexOf(AcceptanceGatePhaseNames.ShadowObservation) > phases.IndexOf(AcceptanceGatePhaseNames.PolicySynthesis));
            Assert.Contains(AcceptanceGatePhaseNames.Finalize, phases);
            Assert.Contains(AcceptanceGatePhaseNames.PolicySynthesis, phases);
            Assert.Contains("shadow-observation_ms=", emitted.CurrentTarget, StringComparison.Ordinal);
            Assert.Single(Directory.GetFiles(RecordDirectory(root), "*.json"));
            Assert.Equal(breakdown.TotalDuration,
                breakdown.AttributedPhaseDuration + breakdown.LaneExecutionDuration + breakdown.UnattributedDuration);
            Assert.True(breakdown.UnattributedDuration >= TimeSpan.Zero, breakdown.ToString());
            Assert.True(Assert.Single(breakdown.Phases, phase => phase.Name == AcceptanceGatePhaseNames.ShadowObservation).Duration >= TimeSpan.Zero);
            Assert.Equal(breakdown.AttributedPhaseDuration, breakdown.Phases
                .Where(phase => phase.Name != AcceptanceGatePhaseNames.LaneExecution)
                .Aggregate(TimeSpan.Zero, (total, phase) => total + phase.Duration));
        }
        finally { DeleteDirectoryWithRetry(root); }
    }

    [Fact]
    public async Task ThrowingResolver_LeavesVerdictUnchanged()
    {
        var controlRoot = CreateFixture();
        var faultRoot = CreateFixture();
        try
        {
            var control = await Gate(controlRoot);
            var fault = await Gate(faultRoot, (_, _, _) => throw new InvalidOperationException("changed files unavailable"));
            AssertParity(control, fault);
            Assert.Equal(AcceptanceGatePhaseNames.ShadowObservation, Assert.Single(fault.ResolverPhases));
        }
        finally { DeleteDirectoryWithRetry(controlRoot); DeleteDirectoryWithRetry(faultRoot); }
    }

    [Fact]
    public async Task BlockedRecordDirectory_LeavesVerdictUnchanged()
    {
        var controlRoot = CreateFixture();
        var blockedRoot = CreateFixture();
        var blockedPath = Path.Combine(blockedRoot, ".orchestrator", "test-reuse-shadow");
        Directory.CreateDirectory(Path.GetDirectoryName(blockedPath)!);
        File.WriteAllText(blockedPath, "block directory creation");
        try
        {
            AssertParity(await Gate(controlRoot), await Gate(blockedRoot));
            Assert.Equal("block directory creation", File.ReadAllText(blockedPath));
        }
        finally { DeleteDirectoryWithRetry(controlRoot); DeleteDirectoryWithRetry(blockedRoot); }
    }

    [Fact]
    public async Task NoInfrastructurePartitions_DoesNotEnterShadowPhaseOrResolvePlan()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": { "enforceStructuralCoverage": false },
              "checks": [
                { "name": "phase seam", "type": "command", "command": "git", "arguments": ["diff", "--check"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var run = await Gate(root, (_, _, _) => throw new InvalidOperationException("must not resolve"), expectPartitions: false);
            Assert.True(run.Result.Passed, DescribeFailures(run.Result));
            Assert.Empty(run.ResolverPhases);
            var emitted = Assert.Single(run.Progress, item => item.Phase == "gate-phase-breakdown");
            var breakdown = Assert.IsType<AcceptanceGatePhaseBreakdown>(emitted.PhaseBreakdown);
            Assert.DoesNotContain(breakdown.Phases, phase => phase.Name == AcceptanceGatePhaseNames.ShadowObservation);
            Assert.DoesNotContain("shadow-observation_ms=", emitted.CurrentTarget, StringComparison.Ordinal);
        }
        finally { DeleteDirectoryWithRetry(root); }
    }

    private async Task<GateRun> Gate(string root,
        Func<string, string, string, IReadOnlyList<string>>? resolver = null, bool expectPartitions = true)
    {
        ResetPartitionVerdictKeyHooks();
        var oldResolver = TestOverrides.ResolvePartitionVerdictChangedFilesForTests;
        var oldRerun = TestOverrides.PartitionVerdictWithinAttemptRerunEnabled;
        var resolverPhases = new ConcurrentQueue<string?>();
        var progress = new ConcurrentQueue<AcceptanceGateProgress>();
        var calls = new ConcurrentBag<string[]>();
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.ResolvePartitionVerdictChangedFilesForTests = (path, main, commit) =>
        {
            resolverPhases.Enqueue(AcceptanceGatePhaseAccountant.CurrentSnapshot.Phase);
            return (resolver ?? AcceptanceTestReuseShadow.ResolveChangedFilesFromGit)(path, main, commit);
        };
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
            {
                calls.Add(args);
                if (args.Length > 1 && args[0] == "dotnet" && args[1] == "test")
                {
                    var className = args.Contains(ResolveLaneFilter(root, "Beta")) ? "BetaPlainTests" :
                        args.Contains(ResolveLaneFilter(root, "Alpha")) ? "AlphaWidgetConsumerTests" : "GammaPlainTests";
                    WriteTrx(args, className, true);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                        "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
            });
            var result = await verifier.RunOwnedAsync(root, new GoalId(Goal), changedFiles: null,
                stableSlotIndex: null, stableSlotLease: null, CancellationToken.None,
                new AcceptanceRunExecutionOptions(ProgressSink: progress.Enqueue));
            var filters = calls.Where(IsInfrastructurePartitionTestCall)
                .Select(args => args[Array.IndexOf(args, "--filter") + 1]).Order(StringComparer.Ordinal).ToArray();
            if (expectPartitions)
                Assert.Equal(new[] { "Alpha", "Beta", "Remainder" }.Select(name => ResolveLaneFilter(root, name)).Order(StringComparer.Ordinal), filters);
            else
                Assert.Empty(filters);
            return new(result, filters, resolverPhases.ToArray(), progress.ToArray());
        }
        finally
        {
            TestOverrides.ResolvePartitionVerdictChangedFilesForTests = oldResolver;
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = oldRerun;
        }
    }

    private static void AssertParity(GateRun first, GateRun second)
    {
        Assert.True(first.Result.Passed, DescribeFailures(first.Result));
        Assert.Equal(first.Result.Passed, second.Result.Passed);
        Assert.Equal(first.Result.Checks.Select(check => (check.Name, check.Passed)),
            second.Result.Checks.Select(check => (check.Name, check.Passed)));
        Assert.Equal(first.Filters, second.Filters);
        var firstReceipt = Assert.Single(first.Result.Checks, check => check.Name == "infrastructure partition verdict cache");
        var secondReceipt = Assert.Single(second.Result.Checks, check => check.Name == "infrastructure partition verdict cache");
        Assert.Equal(firstReceipt.Advisory, secondReceipt.Advisory);
        Assert.Equal(firstReceipt.Passed, secondReceipt.Passed);
        foreach (var token in new[] { "executed_partitions", "reused_partitions", "aggregate_verdict" })
        {
            string ReadToken(AcceptanceCheckResult receipt)
            {
                var match = Regex.Match(receipt.ResultSummary!, $@"{token}=([^ ]+)");
                Assert.True(match.Success, receipt.ResultSummary);
                return match.Groups[1].Value;
            }
            Assert.Equal(ReadToken(firstReceipt), ReadToken(secondReceipt));
        }
    }

    private static string RecordDirectory(string root) => Path.Combine(root, ".orchestrator", "test-reuse-shadow", Goal);
    private static string DescribeFailures(AcceptanceVerificationResult result) =>
        string.Join("\n", result.Checks.Where(check => !check.Passed).Select(check => $"{check.Name}: {check.OutputTail}"));

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
            // Match the repository's state-directory ignore so gate cleanup retains its records.
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

    private sealed record GateRun(AcceptanceVerificationResult Result, string[] Filters,
        string?[] ResolverPhases, AcceptanceGateProgress[] Progress);
}
