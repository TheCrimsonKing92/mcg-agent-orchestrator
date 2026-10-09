using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalWorktreeTestsRebaseRatchetRetighten : GoalWorktreeTestBase
{
    internal const string GuardedPath = "src/Guarded.cs";

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 6)]
    [InlineData(true, 6)]
    public void PureMoveRetightensAndPreservesPatchEquivalence(bool mergeBearing, int mainGrowth)
    {
        var scenario = CreateScenario(mainGrowth: mainGrowth, mergeBearing: mergeBearing);
        var expectedCeiling = 15 + mainGrowth;
        var progress = new List<string>();
        var previous = SourceSizeRatchetRetightener.ProgressSink;
        try
        {
            SourceSizeRatchetRetightener.ProgressSink = progress.Add;
            var result = GoalWorktrees.TryRebaseOntoMain(scenario.Repo, scenario.Goal);
            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, result.Status);
            AssertRetightened(scenario.Worktree, expectedCeiling);
            Assert.Contains($"{GuardedPath}: 15 -> {expectedCeiling}", RunGitOutput(scenario.Worktree, "log", "-1", "--format=%b"));
            Assert.Equal(new[] { $"RATCHET_RETIGHTEN goal={scenario.Goal.Value[..8]} path={GuardedPath} old=15 new={expectedCeiling}" }, progress);
            var head = RunGitOutput(scenario.Worktree, "rev-parse", "HEAD").Trim();
            Assert.True(GoalWorktrees.TryComputePatchEquivalence(scenario.Repo, scenario.OldHead, head, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                out var evidence, out var refusal), $"{refusal}: {evidence}");
            Assert.Equal(string.Empty, RunGitOutput(scenario.Worktree, "status", "--porcelain").Trim());
        }
        finally
        {
            SourceSizeRatchetRetightener.ProgressSink = previous;
            DeleteDirectory(scenario.Repo);
        }
    }

    [Theory]
    [InlineData(true, true, 3, 15)]
    [InlineData(false, false, 6, 20)]
    public void AddedLinesOrUnloweredRowsKeepBlocking(bool addsLine, bool lowersRow, int growth, int expectedCeiling)
    {
        var scenario = CreateScenario(addsLine, lowersRow, growth);
        var authority = File.ReadAllBytes(AuthorityPath(scenario.Worktree));
        var progress = new List<string>();
        var previous = SourceSizeRatchetRetightener.ProgressSink;
        try
        {
            SourceSizeRatchetRetightener.ProgressSink = progress.Add;
            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, GoalWorktrees.TryRebaseOntoMain(scenario.Repo, scenario.Goal).Status);
            Assert.Equal(authority, File.ReadAllBytes(AuthorityPath(scenario.Worktree)));
            Assert.Equal(expectedCeiling, Assert.Single(SourceSizeRatchetPreflight.TryReadAuthority(scenario.Worktree)!).MaximumLineCount);
            var preflight = SourceSizeRatchetPreflight.Evaluate(scenario.Worktree);
            Assert.True(preflight.HasBlockingViolation);
            Assert.Contains($"recorded ceiling of {expectedCeiling}", preflight.Message);
            Assert.NotEqual(SourceSizeRatchetRetightener.CommitSubject, RunGitOutput(scenario.Worktree, "log", "-1", "--format=%s").Trim());
            Assert.Empty(progress);
        }
        finally
        {
            SourceSizeRatchetRetightener.ProgressSink = previous;
            DeleteDirectory(scenario.Repo);
        }
    }

    [Fact]
    public void RepeatedIntegrationRetightensAgainAndCarriesForwardTheAcceptedPatch()
    {
        var scenario = CreateScenario();
        try
        {
            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, GoalWorktrees.TryRebaseOntoMain(scenario.Repo, scenario.Goal).Status);
            var accepted = RunGitOutput(scenario.Worktree, "rev-parse", "HEAD").Trim();
            File.AppendAllLines(Path.Combine(scenario.Repo, GuardedPath), ["// main added 4", "// main added 5"]);
            RunGit(scenario.Repo, "add", GuardedPath);
            RunGit(scenario.Repo, "commit", "-m", "Main grows again");
            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, GoalWorktrees.TryRebaseOntoMain(scenario.Repo, scenario.Goal).Status);
            Assert.Equal(20, Assert.Single(SourceSizeRatchetPreflight.TryReadAuthority(scenario.Worktree)!).MaximumLineCount);
            Assert.False(SourceSizeRatchetPreflight.Evaluate(scenario.Worktree).HasBlockingViolation);
            Assert.Contains($"{GuardedPath}: 18 -> 20", RunGitOutput(scenario.Worktree, "log", "-1", "--format=%b"));
            var current = RunGitOutput(scenario.Worktree, "rev-parse", "HEAD").Trim();
            Assert.True(GoalWorktrees.TryComputePatchEquivalence(scenario.Repo, accepted, current, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                out var evidence, out var refusal), $"{refusal}: {evidence}");
        }
        finally { DeleteDirectory(scenario.Repo); }
    }

    [Fact]
    public void CommitFailureRestoresAuthorityAndLeavesBlockingPreflight()
    {
        var scenario = CreateScenario(addsLine: true);
        try
        {
            // First materialize without an eligible repair, then remove the goal-added line.
            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, GoalWorktrees.TryRebaseOntoMain(scenario.Repo, scenario.Goal).Status);
            var guarded = Path.Combine(scenario.Worktree, GuardedPath);
            File.WriteAllLines(guarded, File.ReadAllLines(guarded).Where(line => line != "// goal added"));
            RunGit(scenario.Worktree, "add", GuardedPath);
            RunGit(scenario.Worktree, "commit", "-m", "Remove goal addition");
            var before = File.ReadAllBytes(AuthorityPath(scenario.Worktree));
            var head = RunGitOutput(scenario.Worktree, "rev-parse", "HEAD").Trim();
            // core.hooksPath points at a hermetic, deliberately rejecting hook.
            var hooks = Path.Combine(scenario.Repo, ".git", "reject-retighten");
            Directory.CreateDirectory(hooks);
            File.WriteAllText(Path.Combine(hooks, "pre-commit"), "#!/bin/sh\nexit 1\n", new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path.Combine(hooks, "pre-commit"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            RunGit(scenario.Worktree, "config", "core.hooksPath", hooks.Replace('\\', '/'));
            Assert.Empty(SourceSizeRatchetRetightener.RetightenAndCommit(scenario.Worktree, scenario.Main, "test"));
            Assert.Equal(before, File.ReadAllBytes(AuthorityPath(scenario.Worktree)));
            Assert.Equal(head, RunGitOutput(scenario.Worktree, "rev-parse", "HEAD").Trim());
            Assert.Equal(string.Empty, RunGitOutput(scenario.Worktree, "status", "--porcelain").Trim());
            Assert.True(SourceSizeRatchetPreflight.Evaluate(scenario.Worktree).HasBlockingViolation);
        }
        finally { DeleteDirectory(scenario.Repo); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetightenSubjectDoesNotHideUnrelatedChanges(bool modifiesOtherFile)
    {
        var scenario = CreateScenario();
        try
        {
            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, GoalWorktrees.TryRebaseOntoMain(scenario.Repo, scenario.Goal).Status);
            var before = RunGitOutput(scenario.Worktree, "rev-parse", "HEAD").Trim();
            File.AppendAllText(modifiesOtherFile ? Path.Combine(scenario.Worktree, "seed.txt") : AuthorityPath(scenario.Worktree), "extra text\n");
            RunGit(scenario.Worktree, "add", modifiesOtherFile ? "seed.txt" : SourceSizeRatchet.SourcePath);
            RunGit(scenario.Worktree, "commit", "-m", SourceSizeRatchetRetightener.CommitSubject);
            var after = RunGitOutput(scenario.Worktree, "rev-parse", "HEAD").Trim();
            Assert.False(GoalWorktrees.TryComputePatchEquivalence(scenario.Repo, before, after, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, out _));
        }
        finally { DeleteDirectory(scenario.Repo); }
    }

    internal static (string Repo, GoalId Goal, string Worktree, string Main, string OldHead) CreateScenario(
        bool addsLine = false, bool lowersRow = true, int mainGrowth = 3, bool mergeBearing = false)
    {
        var repo = CreateSeededRepository();
        try
        {
            RunGit(repo, "branch", "-M", "main");
            Directory.CreateDirectory(Path.Combine(repo, "src"));
            File.WriteAllLines(Path.Combine(repo, GuardedPath), Enumerable.Range(1, 20).Select(index => $"// line {index}"));
            Directory.CreateDirectory(Path.GetDirectoryName(AuthorityPath(repo))!);
            WriteAuthority(repo, 20);
            RunGit(repo, "add", GuardedPath, SourceSizeRatchet.SourcePath);
            RunGit(repo, "commit", "-m", "Seed guarded authority");
            var goal = new GoalId("11111111111111111111111111111111");
            var worktree = GoalWorktrees.Ensure(repo, goal);
            var remaining = Enumerable.Range(6, 15).Select(index => $"// line {index}").ToList();
            if (addsLine) remaining.Insert(5, "// goal added");
            File.WriteAllLines(Path.Combine(worktree, GuardedPath), remaining);
            if (lowersRow) WriteAuthority(worktree, 15);
            RunGit(worktree, "add", GuardedPath, SourceSizeRatchet.SourcePath);
            RunGit(worktree, "commit", "-m", "Move guarded lines out");
            if (mergeBearing)
            {
                File.WriteAllText(Path.Combine(repo, "main-only.txt"), "unrelated main change");
                RunGit(repo, "add", "main-only.txt");
                RunGit(repo, "commit", "-m", "Earlier main advance");
                RunGit(worktree, "merge", "--no-ff", "--no-edit", "main");
            }
            var oldHead = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
            File.AppendAllLines(Path.Combine(repo, GuardedPath), Enumerable.Range(1, mainGrowth).Select(index => $"// main added {index}"));
            RunGit(repo, "add", GuardedPath);
            RunGit(repo, "commit", "-m", "Grow guarded file on main");
            return (repo, goal, worktree, RunGitOutput(repo, "rev-parse", "HEAD").Trim(), oldHead);
        }
        catch { DeleteDirectory(repo); throw; }
    }

    internal static void AssertRetightened(string path, int expectedCeiling = 18)
    {
        Assert.Equal(expectedCeiling, File.ReadLines(Path.Combine(path, GuardedPath)).Count());
        Assert.Equal(expectedCeiling, Assert.Single(SourceSizeRatchetPreflight.TryReadAuthority(path)!).MaximumLineCount);
        Assert.False(SourceSizeRatchetPreflight.Evaluate(path).HasBlockingViolation);
        Assert.Equal(SourceSizeRatchetRetightener.CommitSubject, RunGitOutput(path, "log", "-1", "--format=%s").Trim());
    }

    private static string AuthorityPath(string root) => Path.Combine(root, SourceSizeRatchet.SourcePath);
    private static void WriteAuthority(string root, int ceiling) => File.WriteAllText(AuthorityPath(root),
        $"// authority comment\r\nnew SourceSizeCeiling(\"{GuardedPath}\", {ceiling}), // row comment\r\n", new UTF8Encoding(true));
}
