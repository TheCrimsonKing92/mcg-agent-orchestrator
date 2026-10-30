using System.Text;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its temporary Git repository and kernel; no shared seams or environment writes.
public sealed class OwnerConsoleSelectedPreviewWholeGoalTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] WholeFiles = ["A docs/round2.md", "A src/Round1.cs"];
    private const string WholeLine = "Changes 2 files in 2 areas: round2.md and 1 more";

    [Theory]
    [InlineData("main")]
    [InlineData("trunk")]
    public async Task TwoDeveloperRounds_CountUnionAgainstConfiguredIntegrationBranch(string branch)
    {
        using var repo = new Repository(branch);
        var goal = repo.Goal();
        // HEAD deliberately differs from the recorded candidate: the read must use the latter.
        repo.Git("checkout", branch);
        var summary = await repo.Query().LoadAsync(goal, Token);

        Assert.Equal(repo.Round2, summary.Candidate);
        Assert.Equal(WholeFiles, summary.Preview!.Changes.Files);
        Assert.Equal(2, summary.Preview.Changes.Total);
        Assert.Equal(WholeLine, Line2(goal, summary));
        Assert.Equal(new[] { "A docs/round2.md" }, summary.Changed.Items);
    }

    [Fact]
    public async Task IntegrateMainMerge_ExcludesFileIntroducedOnlyByMain()
    {
        using var repo = new Repository();
        repo.Git("checkout", repo.IntegrationBranch);
        repo.CommitFile("src/PostLandingCanaryTrigger.cs", "main only");
        repo.Git("checkout", "goal/preview");
        repo.Git("merge", "--no-ff", repo.IntegrationBranch, "-m", "integrate main");
        var merged = repo.Head;
        var goal = repo.Goal(new("conductor", "integrate-main", repo.Root, At.AddMinutes(2),
            BaseCommit: repo.Round2, ResultCommit: merged));

        var summary = await repo.Query().LoadAsync(goal, Token);

        Assert.Equal(merged, summary.Candidate);
        Assert.Equal(WholeFiles, summary.Preview!.Changes.Files);
        Assert.Equal(2, summary.Preview.Changes.Total);
        Assert.Equal(WholeLine, Line2(goal, summary));
        Assert.DoesNotContain("PostLandingCanaryTrigger.cs", Line2(goal, summary));
        // The latest dispatch really does contain the unwanted file, discriminating the old range.
        Assert.Equal(new[] { "A src/PostLandingCanaryTrigger.cs" }, summary.Changed.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LandedGoal_CountsExactlyRecordedPromotionRange(bool priorIncludesRound1)
    {
        using var repo = new Repository();
        var goal = repo.Goal();
        repo.Git("checkout", repo.IntegrationBranch);
        if (priorIncludesRound1) repo.Git("merge", "--ff-only", repo.Round1);
        var prior = repo.Head;
        repo.Git("merge", "--no-ff", "goal/preview", "-m", "promote goal");
        var promotion = new GoalPromotionFact(goal.Id.Value, repo.Round2, prior, repo.Head,
            At.AddMinutes(10), GoalPromotionFact.SoloProvenance);
        repo.CommitFile("later-main.txt", "after promotion");
        var mergeBaseReads = 0;
        var query = repo.Query(promotion, (_, _) =>
        {
            mergeBaseReads++;
            throw new InvalidOperationException("Landed preview must use promotion, not current main.");
        });

        var summary = await query.LoadAsync(goal, Token);

        Assert.Equal(repo.Round2, summary.Candidate);
        Assert.Equal(priorIncludesRound1 ? new[] { "A docs/round2.md" } : WholeFiles,
            summary.Preview!.Changes.Files);
        Assert.Equal(priorIncludesRound1 ? 1 : 2, summary.Preview.Changes.Total);
        Assert.Equal(priorIncludesRound1 ? "Changes 1 file in docs: round2.md" : WholeLine, Line2(goal, summary));
        Assert.Equal(0, mergeBaseReads);
    }

    [Fact]
    public async Task DetailChanged_PreservesExactDispatchSectionAndSourceBytes()
    {
        using var repo = new Repository();
        var goal = repo.Goal();
        var summary = await repo.Query().LoadAsync(goal, Token);

        // Pre-change literals from e234976eb's query and DetailLines; only fixture SHAs vary.
        var expected = "Changed: observed 1 files (file scope: docs)\n" +
            "  • A docs/round2.md\n" +
            $"  Source: Git {repo.Round1}..{repo.Round2}; latest contributing dispatch range, not whole-goal coverage";
        var actual = string.Join('\n', summary.DetailLines().SkipWhile(line => !line.StartsWith("Changed:", StringComparison.Ordinal))
            .TakeWhile(line => !line.StartsWith("Reported:", StringComparison.Ordinal)));

        Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
        Assert.Equal(WholeLine, Line2(goal, summary));
    }

    [Theory]
    [InlineData("missing-ref")]
    [InlineData("unrelated-history")]
    [InlineData("throws")]
    [InlineData("invalid-revision")]
    public async Task UnresolvedMergeBase_ShowsUnavailableWithoutDispatchFallback(string scenario)
    {
        using var repo = new Repository();
        var goal = repo.Goal();
        var branch = "missing";
        if (scenario == "unrelated-history")
        {
            branch = "unrelated";
            repo.Git("checkout", "--orphan", branch);
            repo.Git("commit", "-m", "unrelated root");
        }
        var reads = new List<(string, string)>();
        var query = new OwnerGoalChangeSummaryQuery(changes: (basis, candidate, _) =>
        {
            reads.Add((basis, candidate));
            return Task.FromResult(GoalChangesReader.ReadCommittedRange(repo.Root, basis, candidate));
        }, mergeBase: (candidate, _) => scenario switch
        {
            "throws" => throw new IOException("merge-base unavailable"),
            "invalid-revision" => Task.FromResult<string?>("not a revision"),
            _ => Task.FromResult(GoalChangesReader.ReadMergeBase(repo.Root, branch, candidate))
        });

        var summary = await query.LoadAsync(goal, Token);

        Assert.Equal(new[] { (repo.Round1, repo.Round2) }, reads);
        Assert.Equal(new[] { "A docs/round2.md" }, summary.Changed.Items);
        Assert.Equal(OwnerGoalPreviewChangeKind.Unavailable, summary.Preview!.Changes.Kind);
        Assert.Equal("Change list unavailable: could not read the commits", Line2(goal, summary));
    }

    [Fact]
    public async Task SeparatePreviewCache_ReusesReadsAndTracksMovedIntegrationRef()
    {
        using var repo = new Repository();
        var goal = repo.Goal(firstRoundOnly: true);
        var reads = new List<(string, string)>();
        var bases = 0;
        var query = new OwnerGoalChangeSummaryQuery(changes: (basis, candidate, _) =>
        {
            reads.Add((basis, candidate));
            return Task.FromResult(GoalChangesReader.ReadCommittedRange(repo.Root, basis, candidate));
        }, mergeBase: (candidate, _) =>
        {
            bases++;
            return Task.FromResult(GoalChangesReader.ReadMergeBase(repo.Root, repo.IntegrationBranch, candidate));
        });

        var first = await query.LoadAsync(goal, Token);
        var again = await query.LoadAsync(goal, Token);
        Assert.Equal("Changes 1 file in src: Round1.cs", Line2(goal, first));
        Assert.Equal(Line2(goal, first), Line2(goal, again));
        // Matching endpoints still require distinct detail and preview entries.
        Assert.Equal(new[] { (repo.Initial, repo.Round1), (repo.Initial, repo.Round1) }, reads);

        repo.Git("checkout", repo.IntegrationBranch);
        repo.Git("merge", "--ff-only", repo.Round1);
        var moved = await query.LoadAsync(goal, Token);
        Assert.Equal("No committed changes yet", Line2(goal, moved));
        Assert.Equal(first.Changed.State, moved.Changed.State);
        Assert.Equal(first.Changed.Items, moved.Changed.Items);
        Assert.Equal(first.Changed.Source, moved.Changed.Source);
        Assert.Equal((repo.Round1, repo.Round1), reads[2]);
        Assert.Equal(3, reads.Count);
        Assert.Equal(3, bases);
    }

    [Fact]
    public async Task ConcurrentLoads_ShareEachRangeReadIndependently()
    {
        using var repo = new Repository();
        var goal = repo.Goal(firstRoundOnly: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var query = new OwnerGoalChangeSummaryQuery(changes: async (basis, candidate, token) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return GoalChangesReader.ReadCommittedRange(repo.Root, basis, candidate);
        }, mergeBase: (candidate, _) => Task.FromResult(
            GoalChangesReader.ReadMergeBase(repo.Root, repo.IntegrationBranch, candidate)));
        var first = query.LoadAsync(goal, Token);
        await TestHangGuard.WaitAsync(entered.Task, "first committed range read entered");
        var second = query.LoadAsync(goal, Token);
        release.TrySetResult();
        var summaries = await TestHangGuard.WaitAsync(Task.WhenAll(first, second), "both selected summary loads completed");

        Assert.Equal(2, reads);
        Assert.All(summaries, summary => Assert.Equal("Changes 1 file in src: Round1.cs", Line2(goal, summary)));
    }

    private static string Line2(Goal goal, OwnerGoalChangeSummary summary) =>
        OwnerConsoleSelectedPreview.Lines(new(goal.Id.Value, summary), "waiting", TimeZoneInfo.Utc, 240)[1];

    private sealed class Repository : IDisposable
    {
        internal string Root { get; } = SharedTestSupport.CreateTempDirectory();
        internal string IntegrationBranch { get; }
        internal string Initial { get; }
        internal string Round1 { get; }
        internal string Round2 { get; }
        internal string Head => Git("rev-parse", "HEAD");

        internal Repository(string integrationBranch = "main")
        {
            IntegrationBranch = integrationBranch;
            try
            {
                Git("init", "-b", integrationBranch);
                Git("config", "user.name", "Preview fixture");
                Git("config", "user.email", "preview@example.invalid");
                Initial = CommitFile("seed.txt", "baseline");
                Git("checkout", "-b", "goal/preview");
                Round1 = CommitFile("src/Round1.cs", "round one");
                Round2 = CommitFile("docs/round2.md", "round two");
            }
            catch { Dispose(); throw; }
        }

        internal string Git(params string[] arguments) => InfrastructureTestSupport.RunGitWithCommitPostcondition(
            Root, ["-c", "commit.gpgsign=false", "-c", "core.hooksPath=", .. arguments]).StandardOutput.Trim();

        internal string CommitFile(string path, string text)
        {
            var fullPath = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, text);
            Git("add", "--", path);
            Git("commit", "-m", "add " + path);
            return Head;
        }

        internal Goal Goal(TaskDispatchSnapshot? additional = null, bool firstRoundOnly = false)
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(new GoalId("11111111-whole-goal"), "Whole goal preview",
                [new(TaskId.New(), "Develop", AgentRole.Developer)]);
            var snapshot = kernel.ExportGoalSnapshot(goal.Id);
            var rounds = new List<TaskDispatchSnapshot>
            {
                new("developer", "round 1", Root, At, BaseCommit: Initial, ResultCommit: Round1)
            };
            if (!firstRoundOnly)
                rounds.Add(new("developer", "round 2", Root, At.AddMinutes(1), BaseCommit: Round1, ResultCommit: Round2));
            if (additional is not null) rounds.Add(additional);
            return AgentOrchestratorKernel.FromSnapshot(new([snapshot with
            {
                Tasks = snapshot.Tasks.Select(task => task with
                {
                    LastDispatch = rounds[^1], DispatchHistory = rounds.ToArray()
                }).ToArray()
            }], [])).Goals.Single();
        }

        internal OwnerGoalChangeSummaryQuery Query(GoalPromotionFact? promotion = null,
            Func<string, CancellationToken, Task<string?>>? mergeBase = null) => new(
                promotion: (_, _) => Task.FromResult(promotion),
                changes: (basis, candidate, _) => Task.FromResult(GoalChangesReader.ReadCommittedRange(Root, basis, candidate)),
                mergeBase: mergeBase ?? ((candidate, _) => Task.FromResult(
                    GoalChangesReader.ReadMergeBase(Root, IntegrationBranch, candidate))));

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
