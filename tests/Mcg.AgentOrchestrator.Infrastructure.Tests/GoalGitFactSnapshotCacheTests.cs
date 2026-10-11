using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using MetadataFixture = GoalGitRefFingerprintReaderTests.MetadataFixture;

// Parallel-safe: each case owns its metadata, cache and counting runner.
public sealed class GoalGitFactSnapshotCacheTests
{
    [Xunit.Theory]
    [Xunit.InlineData("goal-text")]
    [Xunit.InlineData("goal-add")]
    [Xunit.InlineData("goal-remove")]
    [Xunit.InlineData("packed")]
    [Xunit.InlineData("integration")]
    [Xunit.InlineData("HEAD")]
    [Xunit.InlineData("worktree-add")]
    [Xunit.InlineData("worktree-gitdir")]
    [Xunit.InlineData("HEAD-target")]
    public void ChangedMetadataRebuildsAndReportsNewFact(string input)
    {
        using var fixture = new MetadataFixture();
        var cache = new GoalGitFactSnapshotCache();
        var first = fixture.Build(cache);
        var reused = fixture.Build(cache);
        Assert.NotSame(first, reused);
        Assert.Equal(MetadataFixture.IndexCommands, fixture.Commands.ToArray());

        fixture.Change(input);
        var refreshed = fixture.Build(cache);

        Assert.Equal(MetadataFixture.IndexCommands.Concat(MetadataFixture.IndexCommands).ToArray(), fixture.Commands.ToArray());
        switch (input)
        {
            case "goal-text":
                Assert.NotEqual(first.TryGetGoalBranchTip(fixture.GoalId), refreshed.TryGetGoalBranchTip(fixture.GoalId));
                Assert.Equal(fixture.TipSha, refreshed.TryGetGoalBranchTip(fixture.GoalId));
                break;
            case "goal-add":
                Assert.False(first.HasGoalBranch("goal/nested/added"));
                Assert.True(refreshed.HasGoalBranch("goal/nested/added"));
                break;
            case "goal-remove":
                Assert.True(first.HasGoalBranch(fixture.GoalId));
                Assert.False(refreshed.HasGoalBranch(fixture.GoalId));
                break;
            case "packed":
            case "integration":
                Assert.NotEqual(first.MainSha, refreshed.MainSha);
                Assert.Equal(fixture.MainSha, refreshed.MainSha);
                break;
            case "HEAD":
            case "HEAD-target":
                Assert.Contains("merged=false", first.BuildGoalEvidenceKey(fixture.Goal));
                Assert.Contains("merged=true", refreshed.BuildGoalEvidenceKey(fixture.Goal));
                break;
            default:
                Assert.Contains("worktree=absent", first.BuildGoalEvidenceKey(fixture.Goal));
                Assert.Contains("worktree=present", refreshed.BuildGoalEvidenceKey(fixture.Goal));
                break;
        }
        fixture.Build(cache);
        Assert.Equal(8, fixture.Commands.Count);
    }

    [Xunit.Fact]
    public void NullCachePreservesCommandOrderOnEveryBuild()
    {
        using var fixture = new MetadataFixture();
        for (var build = 0; build < 3; build++)
            fixture.Build(cache: null);
        Assert.Equal(Enumerable.Range(0, 3).SelectMany(_ => MetadataFixture.IndexCommands).ToArray(), fixture.Commands.ToArray());
    }

    [Xunit.Fact]
    public void TwentyHitsAreFollowedByRealBuildAndNewHitBudget()
    {
        using var fixture = new MetadataFixture();
        var cache = new GoalGitFactSnapshotCache();
        fixture.Build(cache);
        for (var hit = 0; hit < 20; hit++)
        {
            fixture.Build(cache);
            Assert.Equal(4, fixture.Commands.Count);
        }
        fixture.Build(cache);
        Assert.Equal(8, fixture.Commands.Count);
        fixture.Build(cache);
        Assert.Equal(8, fixture.Commands.Count);
    }

    [Xunit.Fact]
    public void ForcedMissAndKeyMissDoNotResetHitBudget()
    {
        using var fixture = new MetadataFixture();
        var cache = new GoalGitFactSnapshotCache();
        var snapshot = fixture.Build(cache: null).Snapshot;
        cache.Store(fixture.Root, "main", "fingerprint", snapshot);
        for (var hit = 0; hit < 20; hit++)
            Assert.True(cache.TryGet(fixture.Root, "main", "fingerprint", out _));
        Assert.False(cache.TryGet(fixture.Root, "main", "fingerprint", out _));
        Assert.False(cache.TryGet(fixture.Root, "other", "fingerprint", out _));
        Assert.False(cache.TryGet(fixture.Root, "main", "fingerprint", out _));
        cache.Store(fixture.Root, "main", "fingerprint", snapshot);
        Assert.True(cache.TryGet(fixture.Root, "main", "fingerprint", out var restored));
        Assert.Same(snapshot, restored);
    }

    [Xunit.Theory]
    [Xunit.InlineData(MetadataFixture.TipsCommand, 1)]
    [Xunit.InlineData(MetadataFixture.MergedCommand, 4)]
    [Xunit.InlineData(MetadataFixture.WorktreeCommand, 4)]
    [Xunit.InlineData(MetadataFixture.MainCommand, 4)]
    public void FailedEvidenceIsNeverStored(string failedCommand, int commandsPerBuild)
    {
        using var fixture = new MetadataFixture { FailedCommand = failedCommand };
        var cache = new GoalGitFactSnapshotCache();
        Assert.False(fixture.Build(cache).IsAvailable);
        Assert.False(fixture.Build(cache).IsAvailable);
        Assert.Equal(2 * commandsPerBuild, fixture.Commands.Count);
        fixture.FailedCommand = null;
        Assert.True(fixture.Build(cache).IsAvailable);
        Assert.Equal(2 * commandsPerBuild + 4, fixture.Commands.Count);
        fixture.Build(cache);
        Assert.Equal(2 * commandsPerBuild + 4, fixture.Commands.Count);
    }

    [Xunit.Fact]
    public void NullFingerprintAndUnavailableSnapshotAreIgnored()
    {
        using var fixture = new MetadataFixture();
        var snapshot = fixture.Build(cache: null).Snapshot;
        var cache = new GoalGitFactSnapshotCache();
        cache.Store(fixture.Root, "main", null, snapshot);
        Assert.False(cache.TryGet(fixture.Root, "main", null, out _));
        Assert.False(cache.TryGet(fixture.Root, "main", "fingerprint", out _));
        cache.Store(fixture.Root, "main", "fingerprint", snapshot with { EvidenceAvailable = false });
        Assert.False(cache.TryGet(fixture.Root, "main", "fingerprint", out _));
    }

    [Xunit.Fact]
    public void UnreadableFingerprintBypassesWarmCache()
    {
        using var fixture = new MetadataFixture();
        var cache = new GoalGitFactSnapshotCache();
        fixture.Build(cache);
        File.Delete(fixture.PathFor("HEAD"));
        fixture.Build(cache);
        fixture.Build(cache);
        Assert.Equal(12, fixture.Commands.Count);
    }

    [Xunit.Fact]
    public void CacheKeysUseOrdinalEqualityOnAllThreeInputs()
    {
        using var fixture = new MetadataFixture();
        var cache = new GoalGitFactSnapshotCache();
        var snapshot = fixture.Build(cache: null).Snapshot;
        cache.Store("directory", "main", "fingerprint", snapshot);
        Assert.False(cache.TryGet("other-directory", "main", "fingerprint", out _));
        Assert.False(cache.TryGet("Directory", "main", "fingerprint", out _));
        Assert.False(cache.TryGet("directory", "Main", "fingerprint", out _));
        Assert.False(cache.TryGet("directory", "main", "Fingerprint", out _));
        Assert.True(cache.TryGet("directory", "main", "fingerprint", out var actual));
        Assert.Same(snapshot, actual);
    }

    [Xunit.Fact]
    public void DifferentDirectoryOrIntegrationBranchForcesBuild()
    {
        using var first = new MetadataFixture();
        using var second = new MetadataFixture();
        // Make both roots' metadata byte-identical so the directory key alone discriminates.
        Directory.Delete(second.PathFor(""), recursive: true);
        foreach (var path in Directory.EnumerateFiles(first.PathFor(""), "*", SearchOption.AllDirectories))
            second.Write(Path.GetRelativePath(first.PathFor(""), path), File.ReadAllText(path));
        var cache = new GoalGitFactSnapshotCache();
        first.Build(cache);
        Assert.Equal(first.Read(), second.Read());
        second.Build(cache);
        Assert.Equal(4, second.Commands.Count);
        GoalGitFactIndex.Build(second.Root, "alternate", second.RunGit, snapshotCache: cache);
        Assert.Equal(8, second.Commands.Count);
        Assert.Equal("rev-parse --verify refs/heads/alternate", second.Commands[^1]);
    }

    [Xunit.Fact]
    public void ReusedIndexesRetryInconclusiveCherryWithCurrentRunner()
    {
        using var fixture = new MetadataFixture();
        var cache = new GoalGitFactSnapshotCache();
        var cherryCache = new GoalBranchContentStateCache();
        fixture.Build(cache, cherryCache);
        var firstReuse = fixture.Build(cache, cherryCache);
        Assert.Equal(GoalBranchContentState.Inconclusive, firstReuse.BuildGoalBranchFacts(fixture.Goal).ContentState);
        Assert.Equal(GoalBranchContentState.Inconclusive, firstReuse.BuildGoalBranchFacts(fixture.Goal).ContentState);
        Assert.Equal(1, fixture.Commands.Count(command => command.StartsWith("cherry ", StringComparison.Ordinal)));

        var currentRunnerCalls = 0;
        GitCli.GitResult CurrentRunner(string directory, IReadOnlyList<string> args)
        {
            currentRunnerCalls++;
            return fixture.RunGit(directory, args);
        }
        var secondReuse = GoalGitFactIndex.Build(fixture.Root, "main", CurrentRunner, cherryCache, cache);
        Assert.NotSame(firstReuse, secondReuse);
        Assert.Equal(0, currentRunnerCalls);
        Assert.Equal(GoalBranchContentState.Inconclusive, secondReuse.BuildGoalBranchFacts(fixture.Goal).ContentState);
        Assert.Equal(1, currentRunnerCalls);
        Assert.Equal(2, fixture.Commands.Count(command => command.StartsWith("cherry ", StringComparison.Ordinal)));
        Assert.Equal(4, fixture.Commands.Count(command => MetadataFixture.IndexCommands.Contains(command)));
    }

    [Xunit.Fact]
    public void SnapshotPreservesCaseInsensitiveWorktreeFacts()
    {
        using var fixture = new MetadataFixture();
        fixture.Change("worktree-add");
        var cache = new GoalGitFactSnapshotCache();
        var first = fixture.Build(cache);
        var reused = fixture.Build(cache);
        Assert.True(first.BuildGoalBranchFacts(fixture.Goal).HasRegisteredWorktree);
        Assert.True(reused.BuildGoalBranchFacts(fixture.Goal).HasRegisteredWorktree);
        Assert.Equal(first.BuildGoalEvidenceKey(fixture.Goal), reused.BuildGoalEvidenceKey(fixture.Goal));
        Assert.Equal(4, fixture.Commands.Count(command => MetadataFixture.IndexCommands.Contains(command)));
    }
}
