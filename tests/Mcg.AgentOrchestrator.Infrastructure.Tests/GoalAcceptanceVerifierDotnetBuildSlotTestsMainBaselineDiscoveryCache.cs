using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static GoalAcceptanceVerifierDotnetBuildSlotTestsTrustedBaselineDiscovery;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsMainBaselineDiscoveryCache : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public async Task SameMainContentSkipsBothBaselineCommandsAcrossGates()
    {
        using var fixture = new Fixture(TestOverrides);
        var first = await fixture.Run();
        Assert.True(first.Passed);
        fixture.AssertBaselineCommands(1, 1);
        Assert.Single(Directory.GetFiles(fixture.CacheRoot, "*.json"));
        fixture.Calls.Clear();

        var second = await fixture.Run();
        fixture.AssertBaselineCommands(0, 0);
        Assert.Equal(1, fixture.BuildStarts);
        Assert.Equal(first.Passed, second.Passed);
        Assert.Equal(first.ResultSummary, second.ResultSummary);
        Assert.Contains(fixture.Calls, call => call.Worktree == fixture.CandidateRoot && call.Args.Contains("--list-tests"));
        Assert.False(Directory.Exists(Path.Combine(fixture.LastArtifactsPath!, "main-coverage-baseline")));
    }

    [Xunit.Fact]
    public async Task CommittedMainChangeRebuildsAndChangesCoverageVerdict()
    {
        using var fixture = new Fixture(TestOverrides);
        var first = await fixture.Run();
        Assert.True(first.Passed);
        Assert.Contains("cross-generation-count:candidate=1,minimum=1,main=1,deleted=0", first.ResultSummary);
        Assert.Single(Directory.GetFiles(fixture.CacheRoot, "*.json"));
        fixture.Calls.Clear();
        Assert.True((await fixture.Run()).Passed);
        fixture.AssertBaselineCommands(0, 0);
        fixture.Calls.Clear();
        fixture.AddMainTests("AdditionalTests.cs", "Sample.Tests.Added", "Sample.Tests.AlsoAdded");
        fixture.CommitMain();

        var second = await fixture.Run();
        fixture.AssertBaselineCommands(1, 1);
        Assert.False(second.Passed);
        Assert.Equal(AcceptanceFailureClassifications.StructuralCoverageFailed, second.FailureClassification);
        Assert.Contains("cross-generation-count:candidate=1,minimum=3,main=3,deleted=0", second.ResultSummary);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task CorruptEntryRebuildsAndIsReplaced(bool mismatchKey)
    {
        using var fixture = new Fixture(TestOverrides);
        var first = await fixture.Run();
        var path = Assert.Single(Directory.GetFiles(fixture.CacheRoot, "*.json"));
        if (mismatchKey)
        {
            var entry = JsonNode.Parse(File.ReadAllText(path))!;
            entry["Payload"]!["Key"]!["MainWorktreePath"] = "wrong-main";
            File.WriteAllText(path, entry.ToJsonString());
        }
        else File.WriteAllText(path, "{");
        fixture.Calls.Clear();

        var second = await fixture.Run();
        fixture.AssertBaselineCommands(1, 1);
        Assert.Equal(first.ResultSummary, second.ResultSummary);
        fixture.Calls.Clear();
        // A new verifier and cache instance must read the replacement from disk.
        Assert.Equal(first.ResultSummary, (await fixture.Run()).ResultSummary);
        fixture.AssertBaselineCommands(0, 0);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, "trusted-main-discovery-failed")]
    [Xunit.InlineData(true, "trusted-main-discovery-timeout")]
    public async Task FailedDiscoveryDefersAndNeverCaches(bool timedOut, string reason)
    {
        using var fixture = new Fixture(TestOverrides);
        fixture.DiscoveryFailure = new(1, "discovery failed", TimedOut: timedOut);
        var error = await Assert.ThrowsAsync<AcceptanceInfrastructureDeferredException>(() => fixture.Run());
        Assert.Equal(reason, error.ReasonCode);
        fixture.AssertBaselineCommands(1, 1);
        Assert.Empty(Directory.Exists(fixture.CacheRoot) ? Directory.GetFiles(fixture.CacheRoot, "*.json") : []);
        fixture.DiscoveryFailure = null;
        fixture.Calls.Clear();
        Assert.True((await fixture.Run()).Passed);
        fixture.AssertBaselineCommands(1, 1);
        Assert.Single(Directory.GetFiles(fixture.CacheRoot, "*.json"));
        fixture.Calls.Clear();
        Assert.True((await fixture.Run()).Passed);
        fixture.AssertBaselineCommands(0, 0);
    }

    [Xunit.Fact]
    public async Task FaultedDiscoveryDefersAndNeverCaches()
    {
        using var fixture = new Fixture(TestOverrides);
        fixture.DiscoveryIoFailure = true;
        var error = await Assert.ThrowsAsync<AcceptanceInfrastructureDeferredException>(() => fixture.Run());
        Assert.Equal("trusted-main-discovery-io", error.ReasonCode);
        fixture.AssertBaselineCommands(1, 1);
        Assert.False(Directory.Exists(fixture.CacheRoot));
        fixture.DiscoveryIoFailure = false;
        fixture.Calls.Clear();
        Assert.True((await fixture.Run()).Passed);
        fixture.AssertBaselineCommands(1, 1);
        Assert.Single(Directory.GetFiles(fixture.CacheRoot, "*.json"));
    }

    [Xunit.Fact]
    public async Task NoCacheSeamAlwaysBuildsEvenWithGitMain()
    {
        using var fixture = new Fixture(TestOverrides, enableCache: false);
        Assert.True((await fixture.Run()).Passed);
        fixture.Calls.Clear();
        Assert.True((await fixture.Run()).Passed);
        fixture.AssertBaselineCommands(1, 1);
        Assert.Equal(2, fixture.BuildStarts);
        Assert.False(Directory.Exists(fixture.CacheRoot));
    }

    [Xunit.Fact]
    public async Task UncomputableDigestBypassesCache()
    {
        using var fixture = new Fixture(TestOverrides, initializeGit: false);
        Assert.True((await fixture.Run()).Passed);
        fixture.Calls.Clear();
        Assert.True((await fixture.Run()).Passed);
        fixture.AssertBaselineCommands(1, 1);
        Assert.False(Directory.Exists(fixture.CacheRoot));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly GoalAcceptanceVerifierTestOverrides _overrides;
        private readonly bool _enableCache;
        private readonly List<GoalId> _goals = [];
        internal readonly ConcurrentQueue<(string[] Args, string Worktree)> Calls = new();
        internal string CandidateRoot { get; }
        internal string MainRoot { get; }
        internal string CacheRoot { get; } = Path.Combine(Path.GetTempPath(), "mcg-main-discovery-cache", Guid.NewGuid().ToString("N"));
        internal string? LastArtifactsPath { get; private set; }
        internal GoalAcceptanceVerifier.CommandResult? DiscoveryFailure { get; set; }
        internal bool DiscoveryIoFailure { get; set; }
        internal int BuildStarts;

        internal Fixture(GoalAcceptanceVerifierTestOverrides overrides, bool enableCache = true, bool initializeGit = true)
        {
            _overrides = overrides;
            _enableCache = enableCache;
            (CandidateRoot, MainRoot) = CreateTrustedBaselineWorkspace();
            AddMainTests("OriginalTests.cs", "Sample.Tests.Passes");
            if (initializeGit)
            {
                Git("init");
                CommitMain();
                Assert.True(WorktreeTreeDigest.TryCompute(MainRoot, out _, out _), "Fixture main tree must be digestible.");
            }
            _overrides.ResolveMainWorktreePathForTests = _ => MainRoot;
            _overrides.ResolveDeletedTestFilesForTests = _ => [];
            _overrides.OnTrustedMainBaselineBuildStartingForTests = _ => Interlocked.Increment(ref BuildStarts);
        }

        internal async Task<AcceptanceCheckResult> Run()
        {
            _overrides.MainBaselineDiscoveryCacheForTests = _enableCache ? new MainBaselineDiscoveryCache(CacheRoot) : null;
            var goal = GoalId.New();
            _goals.Add(goal);
            var verifier = new GoalAcceptanceVerifier(_overrides, (args, worktree, _) =>
            {
                Calls.Enqueue((args, worktree));
                if (IsVstestExecution(args))
                {
                    WriteVstestTrx(args, "Sample.Tests.Passes");
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                        "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }
                if (args.Contains("--list-tests"))
                {
                    if (worktree == MainRoot)
                    {
                        if (DiscoveryIoFailure) throw new IOException("fixture discovery fault");
                        if (DiscoveryFailure is { } failure) return Task.FromResult(failure);
                    }
                    var tests = worktree == MainRoot
                        ? Directory.GetFiles(Path.Combine(MainRoot, "tests", "Sample.Tests"), "*Tests.cs")
                            .Order(StringComparer.Ordinal).SelectMany(File.ReadAllLines).ToArray()
                        : ["Sample.Tests.Passes"];
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                        "The following Tests are available:\n    " + string.Join("\n    ", tests)));
                }
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });
            var result = await verifier.RunAsync(CandidateRoot, goal, changedFiles: ["src/Sample.cs"]);
            LastArtifactsPath = Calls.Where(call => call.Worktree == CandidateRoot && call.Args.Contains("--artifacts-path"))
                .Select(call => GetArtifactsPath(call.Args)).First();
            return Assert.Single(result.Checks!, check => check.Name.StartsWith("structural test coverage", StringComparison.Ordinal));
        }

        internal void AssertBaselineCommands(int builds, int discoveries)
        {
            var mainCalls = Calls.Where(call => call.Worktree == MainRoot).ToArray();
            Assert.Equal(builds, mainCalls.Count(call => call.Args.Length > 1 && call.Args[0] == "dotnet" && call.Args[1] == "build"));
            Assert.Equal(discoveries, mainCalls.Count(call => call.Args.Contains("--list-tests")));
        }

        internal void AddMainTests(string file, params string[] tests) =>
            File.WriteAllLines(Path.Combine(MainRoot, "tests", "Sample.Tests", file), tests);

        internal void CommitMain()
        {
            Git("add", "-A");
            Git("-c", "user.name=Cache fixture", "-c", "user.email=cache-fixture@example.invalid",
                "-c", "commit.gpgsign=false", "-c", "core.hooksPath=NUL", "commit", "-m", "fixture main content");
        }

        private void Git(params string[] args)
        {
            var result = GitCli.Run(MainRoot, args);
            Assert.True(result.Succeeded, result.Error);
        }

        public void Dispose()
        {
            _overrides.MainBaselineDiscoveryCacheForTests = null;
            _overrides.ResolveMainWorktreePathForTests = null;
            _overrides.ResolveDeletedTestFilesForTests = null;
            _overrides.OnTrustedMainBaselineBuildStartingForTests = null;
            foreach (var goal in _goals) DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal);
            DeleteDirectoryWithRetry(CandidateRoot);
            DeleteDirectoryWithRetry(MainRoot);
            DeleteDirectoryWithRetry(CacheRoot);
        }
    }
}
