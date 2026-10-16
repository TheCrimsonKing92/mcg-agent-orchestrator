using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its repository; the progress sink is scoped through AsyncLocal.
public sealed class GoalWorktreeTestsRebaseRatchetRetightenLowering : GoalWorktreeTestBase
{
    [Fact]
    public void IntegrationLowersAnUneditedRowAndCarriesPatchEquivalence()
    {
        var scenario = GoalWorktreeTestsRebaseRatchetRetighten.CreateScenario(addsLine: false, lowersRow: false, mainGrowth: 3);
        var progress = new List<string>();
        var previous = SourceSizeRatchetRetightener.ProgressSink;
        try
        {
            SourceSizeRatchetRetightener.ProgressSink = progress.Add;
            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, GoalWorktrees.TryRebaseOntoMain(scenario.Repo, scenario.Goal).Status);
            GoalWorktreeTestsRebaseRatchetRetighten.AssertRetightened(scenario.Worktree, 18);
            Assert.Contains("src/Guarded.cs: 20 -> 18", RunGitOutput(scenario.Worktree, "log", "-1", "--format=%b"));
            Assert.Equal(new[] { "RATCHET_RETIGHTEN goal=11111111 path=src/Guarded.cs old=20 new=18" }, progress);
            var head = RunGitOutput(scenario.Worktree, "rev-parse", "HEAD").Trim();
            Assert.True(GoalWorktrees.TryComputePatchEquivalence(scenario.Repo, scenario.OldHead, head, TrunkBranchName.Default,
                out var evidence, out var refusal), $"{refusal}: {evidence}");
            Assert.Equal("", RunGitOutput(scenario.Worktree, "status", "--porcelain").Trim());

            File.AppendAllLines(Path.Combine(scenario.Worktree, "src/Guarded.cs"), new[] { "// one more line" });
            RunGit(scenario.Worktree, "add", "src/Guarded.cs");
            RunGit(scenario.Worktree, "commit", "-m", "Exceed integrated ceiling");
            var preflight = SourceSizeRatchetPreflight.Evaluate(scenario.Worktree);
            Assert.True(preflight.HasBlockingViolation);
            Assert.Contains("recorded ceiling of 18", preflight.Message);
        }
        finally
        {
            SourceSizeRatchetRetightener.ProgressSink = previous;
            DeleteDirectory(scenario.Repo);
        }
    }

    [Fact]
    public void ClassLoweringCommitsMeasuredPairAndIsIdempotent()
    {
        var repo = CreateSeededRepository();
        var progress = new List<string>();
        var previous = SourceSizeRatchetRetightener.ProgressSink;
        try
        {
            RunGit(repo, "branch", "-M", "main");
            var authority = Path.Combine(repo, SourceSizeRatchet.SourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(authority)!);
            File.WriteAllText(authority, "new SourceClassCeiling(\"Widget\", 12, 3)\n");
            File.WriteAllLines(Path.Combine(repo, "src/Widget.A.cs"), new[] { "partial class Widget", "{", "// a", "// b", "}" });
            File.WriteAllLines(Path.Combine(repo, "src/Widget.B.cs"), new[] { "partial class Widget", "{", "// c", "}" });
            RunGit(repo, "add", "src");
            RunGit(repo, "commit", "-m", "Seed class authority");
            SourceSizeRatchetRetightener.ProgressSink = progress.Add;

            var rows = SourceSizeRatchetRetightener.RetightenAndCommit(repo, "main", "11111111");

            Assert.Equal(new SourceSizeRetightenedRow("class:Widget", 12, 9, 3, 2), Assert.Single(rows));
            Assert.Equal("new SourceClassCeiling(\"Widget\", 9, 2)\n", File.ReadAllText(authority));
            Assert.Equal(SourceSizeRatchetRetightener.CommitSubject, RunGitOutput(repo, "log", "-1", "--format=%s").Trim());
            Assert.Contains("class:Widget: 12 -> 9 lines, 3 -> 2 files", RunGitOutput(repo, "log", "-1", "--format=%b"));
            Assert.Equal(new[] { "RATCHET_RETIGHTEN goal=11111111 path=class:Widget old=12 new=9 oldFiles=3 newFiles=2" }, progress);
            var head = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
            Assert.Empty(SourceSizeRatchetRetightener.RetightenAndCommit(repo, "main", "11111111"));
            Assert.Equal(head, RunGitOutput(repo, "rev-parse", "HEAD").Trim());
            Assert.Single(progress);
            Assert.Equal("", RunGitOutput(repo, "status", "--porcelain").Trim());
        }
        finally
        {
            SourceSizeRatchetRetightener.ProgressSink = previous;
            DeleteDirectory(repo);
        }
    }
}
