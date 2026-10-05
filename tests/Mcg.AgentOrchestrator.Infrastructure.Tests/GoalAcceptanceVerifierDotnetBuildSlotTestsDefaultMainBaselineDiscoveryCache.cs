using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static GoalAcceptanceVerifierDotnetBuildSlotTestsTrustedBaselineDiscovery;

// JobAccounting owns the build-slot environment; each fact owns all repositories and storage.
[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsDefaultMainBaselineDiscoveryCache : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public void ConductorConstructorPersistsDiscoveryUnderExplicitStorageRoot()
    {
        using var fixture = new Fixture();
        var first = new GoalAcceptanceVerifier(fixture.Storage, Path.Combine(fixture.CandidateRoot, ".orchestrator"));
        var cache = Assert.IsType<MainBaselineDiscoveryCache>(first.ResolvedMainBaselineDiscoveryCache);
        var key = Assert.IsType<MainBaselineDiscoveryCacheKey>(MainBaselineDiscoveryCache.TryCreateKey(
            fixture.MainRoot, "tests/Sample.Tests/Sample.Tests.csproj", "Debug", ["--list-tests"], fixture.Storage.RootPath));
        var discovery = new GoalAcceptanceVerifier.CommandResult(0, "Sample.Tests.Passes");
        cache.TryWrite(key, discovery);
        Assert.Single(Directory.GetFiles(fixture.CacheRoot, "*.json"));

        var second = new GoalAcceptanceVerifier(fixture.Storage, Path.Combine(fixture.CandidateRoot, ".orchestrator"));
        var secondCache = Assert.IsType<MainBaselineDiscoveryCache>(second.ResolvedMainBaselineDiscoveryCache);
        Assert.NotSame(cache, secondCache);
        Assert.Equal(discovery, secondCache.TryRead(key));
    }

    [Xunit.Fact]
    public async Task SeparateVerifiersReuseDefaultDiskCacheAndKeepCoverageVerdict()
    {
        using var fixture = new Fixture();
        var first = await fixture.Run();
        Assert.True(first.Gate.Passed);
        Assert.True(first.Check.Passed);
        Assert.Contains("MAIN_BASELINE_DISCOVERY_CACHE status=miss project=tests/Sample.Tests/Sample.Tests.csproj", first.Log);
        Assert.Equal(1, fixture.BuildStarts);
        fixture.AssertBaselineCommands(1, 1);
        Assert.Single(Directory.GetFiles(fixture.CacheRoot, "*.json"));
        fixture.Calls.Clear();

        var second = await fixture.Run();
        Assert.Contains("MAIN_BASELINE_DISCOVERY_CACHE status=hit project=tests/Sample.Tests/Sample.Tests.csproj", second.Log);
        Assert.Equal(1, fixture.BuildStarts);
        fixture.AssertBaselineCommands(0, 0);
        Assert.Equal(first.Gate.Passed, second.Gate.Passed);
        Assert.Equal(first.Check.Passed, second.Check.Passed);
        Assert.Equal(first.Check.ResultSummary, second.Check.ResultSummary);
    }

    [Xunit.Fact]
    public async Task CommittedMainContentChangeMissesDefaultCacheAndChangesVerdict()
    {
        using var fixture = new Fixture();
        var first = await fixture.Run();
        Assert.True(first.Check.Passed);
        Assert.Contains("MAIN_BASELINE_DISCOVERY_CACHE status=miss", first.Log);
        Assert.Equal(1, fixture.BuildStarts);
        Assert.Single(Directory.GetFiles(fixture.CacheRoot, "*.json"));
        fixture.AddMainTests("AdditionalTests.cs", "Sample.Tests.Added", "Sample.Tests.AlsoAdded");
        fixture.CommitMain();
        fixture.IntegrateMainIntoCandidate();
        fixture.Calls.Clear();

        var second = await fixture.Run();
        Assert.Contains("MAIN_BASELINE_DISCOVERY_CACHE status=miss", second.Log);
        Assert.Equal(2, fixture.BuildStarts);
        fixture.AssertBaselineCommands(1, 1);
        Assert.False(second.Gate.Passed);
        Assert.False(second.Check.Passed);
        Assert.Equal(AcceptanceFailureClassifications.StructuralCoverageFailed, second.Check.FailureClassification);
        Assert.Contains("cross-generation-count:candidate=1,minimum=3,main=3,deleted=0", second.Check.ResultSummary);
        Assert.Contains("disposition=coverage-shortfall", second.Check.ResultSummary);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task CorruptDefaultEntryRebuildsAndPreservesVerdict(bool changedHash)
    {
        using var fixture = new Fixture();
        var first = await fixture.Run();
        Assert.True(first.Check.Passed);
        Assert.Contains("MAIN_BASELINE_DISCOVERY_CACHE status=miss", first.Log);
        Assert.Equal(1, fixture.BuildStarts);
        var path = Assert.Single(Directory.GetFiles(fixture.CacheRoot, "*.json"));
        if (changedHash)
        {
            var entry = JsonNode.Parse(File.ReadAllText(path))!;
            var hash = entry["ContentHash"]!.GetValue<string>();
            entry["ContentHash"] = (hash[0] == '0' ? "1" : "0") + hash[1..];
            File.WriteAllText(path, entry.ToJsonString());
        }
        else File.WriteAllText(path, "{");
        fixture.Calls.Clear();

        var second = await fixture.Run();
        Assert.Contains("MAIN_BASELINE_DISCOVERY_CACHE status=miss", second.Log);
        Assert.Equal(2, fixture.BuildStarts);
        fixture.AssertBaselineCommands(1, 1);
        Assert.Equal(first.Gate.Passed, second.Gate.Passed);
        Assert.Equal(first.Check.Passed, second.Check.Passed);
        Assert.Equal(first.Check.ResultSummary, second.Check.ResultSummary);
        fixture.Calls.Clear();
        var third = await fixture.Run();
        Assert.Contains("MAIN_BASELINE_DISCOVERY_CACHE status=hit", third.Log);
        Assert.Equal(2, fixture.BuildStarts);
        fixture.AssertBaselineCommands(0, 0);
        Assert.Equal(first.Check.ResultSummary, third.Check.ResultSummary);
    }

    [Xunit.Fact]
    public void ExplicitOverrideWinsWhenDefaultCacheIsDisabled()
    {
        using var fixture = new Fixture();
        var overrides = new GoalAcceptanceVerifierTestOverrides
        {
            BuildStorageRootForTests = fixture.Storage,
            MainBaselineDiscoveryCacheEnabled = false
        };
        Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string worktree, CancellationToken token) =>
            throw new InvalidOperationException("Construction must not invoke commands.");
        Assert.Null(new GoalAcceptanceVerifier(overrides, Runner).ResolvedMainBaselineDiscoveryCache);
        var cache = new MainBaselineDiscoveryCache(fixture.CacheRoot);
        overrides.MainBaselineDiscoveryCacheForTests = cache;
        Assert.Same(cache, new GoalAcceptanceVerifier(overrides, Runner).ResolvedMainBaselineDiscoveryCache);
        Assert.Equal(overrides, overrides.Snapshot());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<GoalId> _goals = [];
        internal readonly ConcurrentQueue<(string[] Args, string Worktree)> Calls = new();
        internal string CandidateRoot { get; }
        internal string MainRoot { get; }
        internal DotnetBuildStorageRoot Storage { get; } = new(Path.Combine(
            Path.GetTempPath(), "mcg-default-main-discovery", Guid.NewGuid().ToString("N")));
        internal string CacheRoot => MainBaselineDiscoveryCache.DefaultRootPath(Storage.RootPath);
        internal int BuildStarts;

        internal Fixture()
        {
            (CandidateRoot, MainRoot) = CreateTrustedBaselineWorkspace();
            var mainConfig = Path.Combine(MainRoot, "config");
            Directory.CreateDirectory(mainConfig);
            File.Copy(Path.Combine(CandidateRoot, "config", "acceptance-manifest.json"),
                Path.Combine(mainConfig, "acceptance-manifest.json"));
            AddMainTests("OriginalTests.cs", "Sample.Tests.Passes");
            Git("init", "--initial-branch=main");
            CommitMain();
            DeleteDirectoryWithRetry(CandidateRoot);
            Git("worktree", "add", "-b", "candidate", CandidateRoot, "main");
            var resolvedMain = GoalAcceptanceVerifier.ResolveMainWorktreePathWithGitForTests(
                CandidateRoot, AcceptanceGitTextResolver.Resolve);
            Assert.NotNull(resolvedMain);
            Assert.Equal(Path.GetFullPath(MainRoot), Path.GetFullPath(resolvedMain));
            MainRoot = resolvedMain;
            Assert.True(WorktreeTreeDigest.TryCompute(MainRoot, out _, out _), "Fixture main tree must be digestible.");
        }

        internal async Task<(AcceptanceVerificationResult Gate, AcceptanceCheckResult Check, string Log)> Run()
        {
            // Resolve the disk cache exactly as a live gate does, using only the two
            // operator-approved overrides and a real linked main worktree.
            var overrides = new GoalAcceptanceVerifierTestOverrides
            {
                BuildStorageRootForTests = Storage,
                OnTrustedMainBaselineBuildStartingForTests = _ => Interlocked.Increment(ref BuildStarts)
            };
            var goal = GoalId.New();
            _goals.Add(goal);
            var verifier = new GoalAcceptanceVerifier(overrides, (args, worktree, _) =>
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
                    var tests = worktree == MainRoot
                        ? Directory.GetFiles(Path.Combine(MainRoot, "tests", "Sample.Tests"), "*Tests.cs")
                            .Order(StringComparer.Ordinal).SelectMany(File.ReadAllLines).ToArray()
                        : ["Sample.Tests.Passes"];
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                        "The following Tests are available:\n    " + string.Join("\n    ", tests)));
                }
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });
            AcceptanceVerificationResult? result = null;
            var log = await AsyncLocalConsoleRouter.Out.CaptureLocalAsync(async () =>
                result = await verifier.RunAsync(CandidateRoot, goal, changedFiles: ["src/Sample.cs"]));
            var gate = Assert.IsType<AcceptanceVerificationResult>(result);
            var check = Assert.Single(gate.Checks!,
                item => item.Name.StartsWith("structural test coverage", StringComparison.Ordinal));
            return (gate, check, log);
        }

        internal void AssertBaselineCommands(int builds, int discoveries)
        {
            var calls = Calls.Where(call => call.Worktree == MainRoot).ToArray();
            Assert.Equal(builds, calls.Count(call => call.Args.Length > 1 && call.Args[0] == "dotnet" && call.Args[1] == "build"));
            Assert.Equal(discoveries, calls.Count(call => call.Args.Contains("--list-tests")));
        }

        internal void AddMainTests(string file, params string[] tests) =>
            File.WriteAllLines(Path.Combine(MainRoot, "tests", "Sample.Tests", file), tests);

        internal void CommitMain()
        {
            Git("add", "-A");
            Git("-c", "user.name=Default cache fixture", "-c", "user.email=cache-fixture@example.invalid",
                "-c", "commit.gpgsign=false", "-c", "core.hooksPath=NUL", "commit", "-m", "fixture main content");
        }

        internal void IntegrateMainIntoCandidate()
        {
            // Pin the new main generation so the simulated candidate discovery shortfall
            // cannot be reclassified as a stale candidate against its older merge base.
            var merge = GitCli.Run(CandidateRoot, ["merge", "--ff-only", "main"]);
            Assert.True(merge.Succeeded, merge.Error);
            var contained = AcceptanceGitTextResolver.Resolve(CandidateRoot, ["merge-base", "HEAD", "main"]);
            var observed = AcceptanceGitTextResolver.Resolve(CandidateRoot, ["rev-parse", "main"]);
            Assert.False(string.IsNullOrWhiteSpace(observed), "Fixture main commit must resolve.");
            Assert.Equal(observed, contained);
        }

        private void Git(params string[] args)
        {
            var result = GitCli.Run(MainRoot, args);
            Assert.True(result.Succeeded, result.Error);
        }

        public void Dispose()
        {
            foreach (var goal in _goals) DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal, Storage);
            Git("worktree", "remove", "--force", CandidateRoot);
            DeleteDirectoryWithRetry(CandidateRoot);
            DeleteDirectoryWithRetry(MainRoot);
            DeleteDirectoryWithRetry(Storage.RootPath);
        }
    }
}
