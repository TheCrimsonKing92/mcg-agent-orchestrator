using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fact owns its repository; no process-global state is changed.
public sealed class GoalWorktreeTestsFollowerWorkspace : GoalWorktreeTestBase
{
    internal static readonly GoalId Leader = new("22222222222222222222222222222222");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Materialize_ExactLanding_ReproducesCarryTreeWithoutMovingBranch(bool mergeBearing)
    {
        var scenario = CreateLeaderScenario(mergeBearing);
        try
        {
            var originalWorktrees = RegisteredWorktrees(scenario.Repo);
            var result = CreateFollower(scenario);
            Assert.False(result.IsConflict);
            Assert.Empty(result.ConflictPaths);
            using var workspace = Assert.IsType<FollowerGateWorkspace>(result.Workspace);
            var testedTree = workspace.TestedTreeRevision;
            var receipt = workspace.ToReceipt("plan-b");
            Assert.Equal(scenario.Main, workspace.BaseMainRevision);
            Assert.Equal(Leader, workspace.LeaderGoalId);
            Assert.Equal(scenario.Candidate, workspace.LeaderCandidateRevision);
            Assert.Equal(scenario.CandidateTree, workspace.LeaderCandidateTree);
            Assert.Equal(scenario.Goal, workspace.FollowerGoalId);
            Assert.Equal(scenario.OldHead, workspace.FollowerBranchHead);
            Assert.Equal(testedTree, RunGitOutput(workspace.Path, "rev-parse", "HEAD^{tree}"));
            Assert.Equal(workspace.TestedCommitRevision, RunGitOutput(workspace.Path, "rev-parse", "HEAD"));
            GoalWorktreeTestsRebaseRatchetRetighten.AssertRetightened(workspace.Path);
            if (mergeBearing)
            {
                Assert.NotEqual("0", RunGitOutput(scenario.Repo, "rev-list", "--count", "--merges",
                    $"{scenario.Candidate}..{scenario.OldHead}"));
                // The ratchet repair is on top of a squash with C_A as its sole parent.
                var squash = RunGitOutput(workspace.Path, "rev-parse", "HEAD^1");
                Assert.Equal($"{squash} {scenario.Candidate}",
                    RunGitOutput(workspace.Path, "rev-list", "--parents", "-n", "1", squash));
            }
            workspace.AssertFollowerBranchUnchanged();
            AssertFollowerUnchanged(scenario);
            Assert.NotEqual(originalWorktrees, RegisteredWorktrees(scenario.Repo));
            var workspacePath = workspace.Path;
            workspace.Dispose();
            workspace.Dispose();
            AssertFollowerUnchanged(scenario);
            Assert.False(Directory.Exists(workspacePath));
            Assert.Equal(originalWorktrees, RegisteredWorktrees(scenario.Repo));

            LandLeader(scenario);
            var parent = RunGitOutput(scenario.Repo, "rev-parse", "HEAD^1");
            var landedTree = RunGitOutput(scenario.Repo, "rev-parse", "HEAD^{tree}");
            Assert.Equal(GoalWorktreeRebaseStatus.Rebased,
                GoalWorktrees.TryRebaseOntoMain(scenario.Repo, scenario.Goal).Status);
            var carriedTree = RunGitOutput(scenario.Worktree, "rev-parse", "HEAD^{tree}");
            Assert.Equal(testedTree, carriedTree);
            var verdict = FollowerGateBindingRule.Evaluate(receipt,
                new(FollowerLeaderOutcome.Landed, parent, landedTree, scenario.OldHead, carriedTree, "plan-b"));
            Assert.True(verdict.IsValid);
        }
        finally { DeleteDirectory(scenario.Repo); }
    }

    [Fact]
    public void SameLineConflict_ReturnsPathAndRemovesWorkspaceWithoutMovingBranch()
    {
        var repo = CreateSeededRepository();
        try
        {
            RunGit(repo, "branch", "-M", "main");
            var main = RunGitOutput(repo, "rev-parse", "HEAD");
            var goal = new GoalId("11111111111111111111111111111111");
            var worktree = GoalWorktrees.Ensure(repo, goal);
            RunGit(repo, "checkout", "-b", "leader");
            WriteAndCommit(repo, "seed.txt", "leader\n");
            WriteAndCommit(worktree, "seed.txt", "follower\n");
            var candidate = RunGitOutput(repo, "rev-parse", "HEAD");
            var head = RunGitOutput(worktree, "rev-parse", "HEAD");
            var registered = RegisteredWorktrees(repo);
            var result = GoalWorktrees.CreateFollowerWorkspace(repo, main, Leader, candidate, goal, head, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default);
            Assert.True(result.IsConflict);
            Assert.Null(result.Workspace);
            Assert.Equal(new[] { "seed.txt" }, result.ConflictPaths);
            Assert.False(string.IsNullOrWhiteSpace(result.ConflictDetail));
            Assert.Equal(head, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(goal)}"));
            Assert.Equal(head, RunGitOutput(worktree, "rev-parse", "HEAD"));
            Assert.Equal(registered, RegisteredWorktrees(repo));
        }
        finally { DeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StaleMainOrFollower_ThrowsBeforeAddingWorkspace(bool mainMoved)
    {
        var scenario = CreateLeaderScenario();
        try
        {
            if (mainMoved)
            {
                RunGit(scenario.Repo, "checkout", "main");
                WriteAndCommit(scenario.Repo, "extra.txt", "advance main\n");
            }
            else WriteAndCommit(scenario.Worktree, "extra.txt", "advance follower\n");
            var registered = RegisteredWorktrees(scenario.Repo);
            Assert.Throws<InvalidOperationException>(() => CreateFollower(scenario));
            Assert.Equal(registered, RegisteredWorktrees(scenario.Repo));
        }
        finally { DeleteDirectory(scenario.Repo); }
    }

    [Fact]
    public void CandidateOutsideMainAncestry_ThrowsBeforeAddingWorkspace()
    {
        var scenario = CreateLeaderScenario();
        try
        {
            var unrelatedCandidate = RunGitOutput(scenario.Repo, "rev-parse", $"{scenario.Main}^1");
            var registered = RegisteredWorktrees(scenario.Repo);
            Assert.Throws<InvalidOperationException>(() => GoalWorktrees.CreateFollowerWorkspace(
                scenario.Repo, scenario.Main, Leader, unrelatedCandidate, scenario.Goal, scenario.OldHead, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default));
            AssertFollowerUnchanged(scenario);
            Assert.Equal(registered, RegisteredWorktrees(scenario.Repo));
        }
        finally { DeleteDirectory(scenario.Repo); }
    }

    internal sealed record Scenario(string Repo, GoalId Goal, string Worktree, string Main,
        string OldHead, string Candidate, string CandidateTree);

    internal static Scenario CreateLeaderScenario(bool mergeBearing = false)
    {
        var seed = GoalWorktreeTestsRebaseRatchetRetighten.CreateScenario(mergeBearing: mergeBearing);
        try
        {
            RunGit(seed.Repo, "branch", "leader", "HEAD");
            var tree = RunGitOutput(seed.Repo, "rev-parse", "HEAD^{tree}");
            RunGit(seed.Repo, "reset", "--hard", "HEAD~1");
            var main = RunGitOutput(seed.Repo, "rev-parse", "HEAD");
            RunGit(seed.Repo, "checkout", "leader");
            return new(seed.Repo, seed.Goal, seed.Worktree, main, seed.OldHead, seed.Main, tree);
        }
        catch { DeleteDirectory(seed.Repo); throw; }
    }

    internal static FollowerWorkspaceResult CreateFollower(Scenario scenario) =>
        GoalWorktrees.CreateFollowerWorkspace(scenario.Repo, scenario.Main, Leader,
            scenario.Candidate, scenario.Goal, scenario.OldHead, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default);

    internal static void LandLeader(Scenario scenario)
    {
        RunGit(scenario.Repo, "checkout", "main");
        RunGit(scenario.Repo, "merge", "--no-ff", "--no-edit", "leader");
    }

    private static void AssertFollowerUnchanged(Scenario scenario)
    {
        Assert.Equal(scenario.OldHead, RunGitOutput(scenario.Repo, "rev-parse",
            $"refs/heads/{GoalWorktrees.BranchName(scenario.Goal)}"));
        Assert.Equal(scenario.OldHead, RunGitOutput(scenario.Worktree, "rev-parse", "HEAD"));
        Assert.Equal(scenario.Main, RunGitOutput(scenario.Repo, "rev-parse", "refs/heads/main"));
        Assert.Equal(scenario.Candidate, RunGitOutput(scenario.Repo, "rev-parse", "refs/heads/leader"));
        Assert.Equal(scenario.Candidate, RunGitOutput(scenario.Repo, "rev-parse", "HEAD"));
    }

    private static string RegisteredWorktrees(string repo) => RunGitOutput(repo, "worktree", "list", "--porcelain");

    internal static void WriteAndCommit(string path, string file, string contents)
    {
        File.WriteAllText(Path.Combine(path, file), contents);
        RunGit(path, "add", file);
        RunGit(path, "commit", "-m", $"Change {file}");
    }
}
