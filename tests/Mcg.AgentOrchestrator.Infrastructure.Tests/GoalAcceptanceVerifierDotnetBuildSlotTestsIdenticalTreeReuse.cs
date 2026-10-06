using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

[Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsIdenticalTreeReuse : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    private static readonly GoalId Goal = new("12345678123456781234567812345678");
    private const string CoreProject = "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj";
    private const string CliProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj";
    private const string OwnerProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Acceptance/Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests.csproj";
    private const string LaneProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    private const string CoreSource = "tests/Mcg.AgentOrchestrator.Core.Tests/Fixture.cs";
    private static readonly string[] DeterministicProjects = [CoreProject, CliProject, OwnerProject];
    private static readonly string[] DeterministicNames = ["core tests", "cli tests", "acceptance execution owner tests"];
    private static readonly string[] DeterministicIds = ["core-tests", "cli-tests", "acceptance-execution-owner-tests"];

    [Fact]
    public async Task IdenticalCandidate_ReusesThreeGreensWithOriginatingAttemptReceipts()
    {
        var root = CreateFixture();
        try
        {
            var first = await Gate(root, "attempt-one");
            AssertPassed(first);
            AssertFreshGreens(root, first, "attempt-one");
            var second = await Gate(root, "attempt-two");
            AssertPassed(second);
            AssertReuse(root, second, "attempt-two", "attempt-one", DeterministicProjects);
            Assert.Equal(Git(root, "rev-parse", "HEAD^{tree}"), Summary(root, "attempt-two").BranchHeadSha);
            Assert.Equal(Git(root, "rev-parse", "main"), Summary(root, "attempt-two").MainHeadSha);
            AssertNoIdenticalTreeIndexEntries(root);
        }
        finally { DeleteDirectoryWithRetry(root); }
    }

    [Fact]
    public async Task DifferentTree_RerunsEvenWhenEarlierClosureContentReturns()
    {
        var root = CreateFixture();
        try
        {
            var first = await Gate(root, "attempt-one");
            AssertPassed(first);
            AssertFreshGreens(root, first, "attempt-one");
            var originalTree = Git(root, "rev-parse", "HEAD^{tree}");
            var originalClosure = AcceptanceLaneClosureHasher.TryCompute(root, TestCheck("core tests", CoreProject));
            Assert.NotNull(originalClosure);
            Write(root, CoreSource, "public class Fixture { public int Value => 2; }");
            Write(root, "candidate-marker.txt", "outside every check closure");
            Commit(root, "changed closure");
            Assert.NotEqual(originalTree, Git(root, "rev-parse", "HEAD^{tree}"));
            Assert.NotEqual(originalClosure, AcceptanceLaneClosureHasher.TryCompute(root, TestCheck("core tests", CoreProject)));
            var changed = await Gate(root, "attempt-changed");
            AssertPassed(changed);
            AssertFreshGreens(root, changed, "attempt-changed");
            Assert.DoesNotContain("reuse_rule=identical-tree", changed.Detail, StringComparison.Ordinal);
            Write(root, CoreSource, "public class Fixture { public int Value => 1; }");
            Commit(root, "restored closure on different tree");
            Assert.NotEqual(originalTree, Git(root, "rev-parse", "HEAD^{tree}"));
            Assert.Equal(originalClosure, AcceptanceLaneClosureHasher.TryCompute(root, TestCheck("core tests", CoreProject)));
            var restored = await Gate(root, "attempt-restored");
            AssertPassed(restored);
            AssertFreshGreens(root, restored, "attempt-restored");
            Assert.DoesNotContain("reuse_rule=identical-tree", restored.Detail, StringComparison.Ordinal);
            Assert.Contains("missed_lane=core-tests:no-green-verdict-for-identical-tree", restored.Detail, StringComparison.Ordinal);
            AssertNoIdenticalTreeIndexEntries(root);
        }
        finally { DeleteDirectoryWithRetry(root); }
    }

    [Fact]
    public async Task FailedAttempt_ReusesGreensButRedAndDirtyChecksRunAgain()
    {
        var root = CreateFixture();
        var redRoot = CreateFixture();
        var dirtyRoot = CreateFixture();
        try
        {
            var failed = await Gate(root, "attempt-failed", failProject: LaneProject);
            Assert.False(failed.Result.Passed);
            AssertFreshGreens(root, failed, "attempt-failed");
            Assert.Equal(GoalOperationStatus.Failed, Summary(root, "attempt-failed").Status);
            var recovered = await Gate(root, "attempt-recovered");
            AssertPassed(recovered);
            AssertReuse(root, recovered, "attempt-recovered", "attempt-failed", DeterministicProjects);

            var red = await Gate(redRoot, "attempt-red", failProject: CoreProject);
            Assert.False(red.Result.Passed);
            var redRecord = Assert.Single(Verdicts(redRoot, "attempt-red"), row => row.PartitionId == "core-tests");
            Assert.Equal("RED", redRecord.PartitionVerdict);
            var afterRed = await Gate(redRoot, "attempt-after-red");
            AssertPassed(afterRed);
            AssertLaunch(afterRed, CoreProject, 1);
            Assert.DoesNotContain("reuse_rule=identical-tree", afterRed.Detail, StringComparison.Ordinal);

            var clean = await Gate(dirtyRoot, "attempt-clean");
            AssertPassed(clean);
            AssertFreshGreens(dirtyRoot, clean, "attempt-clean");
            Write(dirtyRoot, CoreSource, "public class Fixture { public int Value => 3; }");
            Assert.Null(AcceptanceLaneClosureHasher.TryCompute(dirtyRoot, TestCheck("core tests", CoreProject)));
            var dirty = await Gate(dirtyRoot, "attempt-dirty");
            AssertPassed(dirty);
            AssertLaunch(dirty, CoreProject, 1);
            Assert.Contains("missed_lane=core-tests:closure-hash-unavailable", dirty.Detail, StringComparison.Ordinal);
            Assert.False(Assert.Single(dirty.Result.Checks, check => check.Name == "core tests").TestResultIsExplicitCrossAttemptReuse);
            AssertReuse(dirtyRoot, dirty, "attempt-dirty", "attempt-clean", [CliProject, OwnerProject]);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(redRoot);
            DeleteDirectoryWithRetry(dirtyRoot);
        }
    }

    [Fact]
    public async Task HostChecks_RerunWithReasonCodesAndOtherKeylessTextIsUnchanged()
    {
        var root = CreateFixture(includeKeyless: true);
        try
        {
            var first = await Gate(root, "attempt-one");
            var second = await Gate(root, "attempt-two");
            AssertPassed(first);
            AssertPassed(second);
            foreach (var run in new[] { first, second })
            {
                AssertLaunch(run, "tests/Provider/Provider.csproj", 1);
                AssertLaunch(run, "tests/ShardProbe/ShardProbe.csproj", 1);
                Assert.Single(run.Launches, args => args.SequenceEqual(new[] { "git", "diff", "--check" }));
                Assert.Single(run.Launches, args => args.SequenceEqual(new[] { "git", "diff", "--no-key-fixture" }));
                var tokens = run.Detail.Split(' ');
                Assert.Contains("missed_lane=provider%20environment%20tests:cache-key-unavailable:host-state-dependent", tokens);
                Assert.Contains("missed_lane=real%20process%20shard%20probe%20tests:cache-key-unavailable:gate-apparatus-probe", tokens);
                Assert.Contains("missed_lane=git%20diff%20whitespace:cache-key-unavailable:worktree-diff-dependent", tokens);
                Assert.Equal("missed_lane=check%20with%20spaces%3A%20no%20key:cache-key-unavailable",
                    Assert.Single(tokens, token => token.StartsWith("missed_lane=check%20", StringComparison.Ordinal)));
            }
            AssertReuse(root, second, "attempt-two", "attempt-one", DeterministicProjects);
        }
        finally { DeleteDirectoryWithRetry(root); }
    }

    private async Task<GateRun> Gate(string root, string attemptId, string? failProject = null)
    {
        ResetPartitionVerdictKeyHooks();
        var launches = new ConcurrentBag<string[]>();
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
        {
            launches.Add(args);
            if (args.Length < 3 || args[0] != "dotnet" || args[1] != "test")
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
            var passed = args[2] != failProject;
            WriteTrx(args, passed);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(passed ? 0 : 1,
                passed ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1." :
                    "Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1."));
        });
        var result = await verifier.RunOwnedAsync(root, Goal, changedFiles: null, stableSlotIndex: null,
            stableSlotLease: null, cancellationToken: default,
            executionOptions: new AcceptanceRunExecutionOptions(RunId: attemptId,
                ResultsPrefix: Path.Combine(root, ".orchestrator", attemptId)));
        var detail = Assert.Single(result.Checks, check => check.Name == "infrastructure partition verdict cache").ResultSummary!;
        Assert.Equal(detail, Summary(root, attemptId).Detail);
        return new(result, launches.ToArray(), detail);
    }

    private static void AssertFreshGreens(string root, GateRun run, string attemptId)
    {
        foreach (var project in DeterministicProjects)
        {
            AssertLaunch(run, project, 1);
            var check = TestCheck(DeterministicNames[Array.IndexOf(DeterministicProjects, project)], project);
            var id = DeterministicIds[Array.IndexOf(DeterministicProjects, project)];
            var row = Assert.Single(Verdicts(root, attemptId), row => row.PartitionId == id);
            Assert.StartsWith("check-identity-", row.PartitionFilterHash, StringComparison.Ordinal);
            Assert.Equal("GREEN", row.PartitionVerdict);
            Assert.Equal(Git(root, "rev-parse", "HEAD^{tree}"), row.BranchHeadSha);
            var rawRow = Assert.Single(RawVerdicts(root, attemptId), row => row.GetProperty("partitionId").GetString() == id);
            Assert.Equal(AcceptanceLaneClosureHasher.TryCompute(root, check), rawRow.GetProperty("partitionClosureHash").GetString());
            Assert.False(string.IsNullOrWhiteSpace(rawRow.GetProperty("partitionClosureHash").GetString()));
        }
    }

    private static void AssertReuse(string root, GateRun run, string attemptId, string sourceAttempt, string[] projects)
    {
        var detail = Summary(root, attemptId).Detail!;
        foreach (var project in projects)
        {
            AssertLaunch(run, project, 0);
            var name = DeterministicNames[Array.IndexOf(DeterministicProjects, project)];
            var check = TestCheck(name, project);
            var id = DeterministicIds[Array.IndexOf(DeterministicProjects, project)];
            var source = Assert.Single(Verdicts(root, sourceAttempt), row => row.PartitionId == id);
            var closureHash = AcceptanceLaneClosureHasher.TryCompute(root, check);
            Assert.False(string.IsNullOrWhiteSpace(closureHash));
            Assert.Contains($"{{partition_id={id},source_attempt_id={sourceAttempt},cache_key={source.PartitionVerdictCacheKey}," +
                $"reuse_rule=identical-tree,closure_hash={closureHash}}}", detail, StringComparison.Ordinal);
            var result = Assert.Single(run.Result.Checks, result => result.Name == name);
            Assert.True(result.TestResultIsExplicitCrossAttemptReuse);
            Assert.Equal(sourceAttempt, result.TestResultAttemptId);
            Assert.Contains("reuse_rule=identical-tree", result.ResultSummary, StringComparison.Ordinal);
            Assert.Contains($"closure_hash={closureHash}", result.ResultSummary, StringComparison.Ordinal);
            Assert.DoesNotContain(Verdicts(root, attemptId), row => row.PartitionId == id);
        }
    }

    private static void AssertLaunch(GateRun run, string project, int count) =>
        Assert.Equal(count, run.Launches.Count(args => args.Length > 2 && args[0] == "dotnet" && args[1] == "test" && args[2] == project));

    private static void AssertPassed(GateRun run) => Assert.True(run.Result.Passed,
        string.Join('\n', run.Result.Checks.Where(check => !check.Passed).Select(check => $"{check.Name}: {check.OutputTail} {check.ResultSummary}")));

    private static GoalOperationJournalEntry Summary(string root, string attemptId) =>
        Assert.Single(GoalOperationJournal.Read(root, Goal).Entries, row => row.Operation == "acceptance:partition-verdict-cache" && row.PartitionAttemptId == attemptId);

    private static GoalOperationJournalEntry[] Verdicts(string root, string attemptId) =>
        GoalOperationJournal.Read(root, Goal).Entries.Where(row => row.Operation == "acceptance:partition-verdict" && row.PartitionAttemptId == attemptId).ToArray();

    private static JsonElement[] RawVerdicts(string root, string attemptId) =>
        File.ReadAllLines(Path.Combine(root, ".orchestrator", "goal-operations", $"{Goal.Value}.jsonl"))
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
            .Where(row => row.GetProperty("operation").GetString() == "acceptance:partition-verdict" &&
                row.GetProperty("partitionAttemptId").GetString() == attemptId).ToArray();

    private static void AssertNoIdenticalTreeIndexEntries(string root)
    {
        var path = Path.Combine(root, ".orchestrator", "acceptance-closure-verdicts.jsonl");
        if (File.Exists(path)) Assert.DoesNotContain("check-identity-", File.ReadAllText(path), StringComparison.Ordinal);
    }

    private static Check TestCheck(string name, string project) => new()
    {
        Name = name, Type = "dotnet-test", Project = project, Arguments = ["--verbosity", "minimal"]
    };

    private static string CreateFixture(bool includeKeyless = false)
    {
        var checks = DeterministicNames.Zip(DeterministicProjects, TestCheck).ToList();
        checks.Add(new Check { Name = "infrastructure tests: Fixture", Type = "dotnet-test", Project = LaneProject,
            Arguments = ["--filter", "FullyQualifiedName~FixtureLaneTests", "--verbosity", "minimal"] });
        if (includeKeyless)
        {
            checks.Add(TestCheck("provider environment tests", "tests/Provider/Provider.csproj"));
            checks.Add(TestCheck("real process shard probe tests", "tests/ShardProbe/ShardProbe.csproj"));
            checks.Add(new Check { Name = "git diff whitespace", Type = "command", Command = "git", Arguments = ["diff", "--check"] });
            checks.Add(new Check { Name = "check with spaces: no key", Type = "command", Command = "git", Arguments = ["diff", "--no-key-fixture"] });
        }
        var manifest = JsonSerializer.Serialize(new
        {
            version = 1, engine = new { enforceStructuralCoverage = false, maxConcurrentShards = 1 },
            checks, forbiddenChangedPathGlobs = Array.Empty<string>()
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var root = CreateManifestWorkspace(manifest);
        try
        {
            Write(root, ".gitignore", ".orchestrator/\nTestResults/\n");
            foreach (var check in checks.Where(check => check.Project is not null))
                Write(root, check.Project!, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write(root, CoreSource, "public class Fixture { public int Value => 1; }");
            Write(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FixtureLaneTests.cs", "public class FixtureLaneTests { [Xunit.Fact] public void Passes() { } }");
            Git(root, "init", "--initial-branch=main");
            Commit(root, "fixture main");
            Git(root, "checkout", "-b", "goal/identical-tree");
            Write(root, "candidate.txt", "candidate outside check closures");
            Commit(root, "fixture candidate");
            Assert.Equal("goal/identical-tree", Git(root, "branch", "--show-current"));
            Assert.Equal(string.Empty, Git(root, "status", "--porcelain"));
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

    private static void Commit(string root, string message)
    {
        Git(root, "add", ".");
        Git(root, "-c", "user.name=Identical Tree Fixture", "-c", "user.email=fixture@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "-m", message);
    }

    private static string Git(string root, params string[] args)
    {
        var result = InfrastructureTestSupport.RunGitProbe(root, args);
        Assert.True(result.Succeeded, result.ToString());
        return result.StandardOutput.Trim();
    }

    private static void WriteTrx(string[] args, bool passed)
    {
        var directoryIndex = Array.IndexOf(args, "--results-directory");
        var loggerIndex = Array.IndexOf(args, "--logger");
        Assert.True(directoryIndex >= 0 && loggerIndex >= 0);
        var directory = args[directoryIndex + 1];
        const string prefix = "trx;LogFileName=";
        Assert.StartsWith(prefix, args[loggerIndex + 1], StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(directory);
        var document = new XDocument(new XElement("TestRun",
            new XElement("TestDefinitions", new XElement("UnitTest", new XAttribute("id", "1"),
                new XElement("TestMethod", new XAttribute("className", "FixtureLaneTests"), new XAttribute("name", "Passes")))),
            new XElement("Results", new XElement("UnitTestResult", new XAttribute("testId", "1"),
                new XAttribute("testName", "FixtureLaneTests.Passes"), new XAttribute("outcome", passed ? "Passed" : "Failed"))),
            new XElement("ResultSummary", new XAttribute("outcome", "Completed"), new XElement("Counters",
                new XAttribute("total", 1), new XAttribute("executed", 1), new XAttribute("passed", passed ? 1 : 0),
                new XAttribute("failed", passed ? 0 : 1), new XAttribute("notExecuted", 0)))));
        document.Save(Path.Combine(directory, args[loggerIndex + 1][prefix.Length..]));
    }

    private sealed record GateRun(AcceptanceVerificationResult Result, string[][] Launches, string Detail);
}
