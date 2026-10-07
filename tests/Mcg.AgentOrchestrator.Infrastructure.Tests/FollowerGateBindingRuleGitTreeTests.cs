using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every fact owns and cleans up its own repository and worktree.
public sealed class FollowerGateBindingRuleGitTreeTests : GoalWorktreeTestBase
{
    [Fact]
    public void ExactLeaderLandingReproducesFollowerTestedTree()
    {
        var scenario = CreateLeaderScenario();
        try
        {
            var receipt = TestFollowerOnCandidate(scenario);
            var observation = LandLeaderAndCarryFollower(scenario);

            Assert.Equal(scenario.Main, observation.LandedFirstParent);
            Assert.Equal(scenario.CandidateTree, observation.LandedTree);
            Assert.Equal(receipt.FollowerBranchHead, observation.CurrentFollowerBranchHead);
            Assert.Equal(receipt.FollowerTestedTree, observation.RebasedFollowerTree);
            var verdict = FollowerGateBindingRule.Evaluate(receipt, observation);
            Assert.True(verdict.IsValid);
            Assert.Null(verdict.Reason);
        }
        finally { DeleteDirectory(scenario.Repo); }
    }

    [Fact]
    public void ChangedLeaderTreeInvalidatesFollowerDespiteUnchangedFirstParent()
    {
        var scenario = CreateLeaderScenario();
        try
        {
            var receipt = TestFollowerOnCandidate(scenario);
            File.WriteAllText(Path.Combine(scenario.Repo, "leader-extra.txt"), "Leader changed after the follower test.\n");
            RunGit(scenario.Repo, "add", "leader-extra.txt");
            RunGit(scenario.Repo, "commit", "-m", "Advance leader beyond tested candidate");
            var observation = LandLeaderAndCarryFollower(scenario);

            Assert.Equal(scenario.Main, observation.LandedFirstParent);
            Assert.NotEqual(scenario.CandidateTree, observation.LandedTree);
            Assert.NotEqual(receipt.FollowerTestedTree, observation.RebasedFollowerTree);
            var verdict = FollowerGateBindingRule.Evaluate(receipt, observation);
            Assert.False(verdict.IsValid);
            Assert.Equal(FollowerGateInvalidReason.LeaderTreeDiffers, verdict.Reason);
        }
        finally { DeleteDirectory(scenario.Repo); }
    }

    [Fact]
    public void RetighteningAgainstMainInsteadOfCandidateInvalidatesRebaseTree()
    {
        var scenario = CreateLeaderScenario();
        try
        {
            RunGit(scenario.Worktree, "rebase", "--merge", scenario.Candidate);
            Assert.Empty(SourceSizeRatchetRetightener.RetightenAndCommit(scenario.Worktree, scenario.Main, "train-test"));
            Assert.Equal(15, Assert.Single(SourceSizeRatchetPreflight.TryReadAuthority(scenario.Worktree)!).MaximumLineCount);
            Assert.True(SourceSizeRatchetPreflight.Evaluate(scenario.Worktree).HasBlockingViolation);
            var receipt = Receipt(scenario, Tree(scenario.Worktree));
            RunGit(scenario.Worktree, "reset", "--hard", scenario.OldHead);
            var observation = LandLeaderAndCarryFollower(scenario);

            Assert.Equal(scenario.Main, observation.LandedFirstParent);
            Assert.Equal(scenario.CandidateTree, observation.LandedTree);
            Assert.NotEqual(receipt.FollowerTestedTree, observation.RebasedFollowerTree);
            var verdict = FollowerGateBindingRule.Evaluate(receipt, observation);
            Assert.False(verdict.IsValid);
            Assert.Equal(FollowerGateInvalidReason.RebaseTreeDiffers, verdict.Reason);
        }
        finally { DeleteDirectory(scenario.Repo); }
    }

    private sealed record Scenario(string Repo, GoalId Goal, string Worktree, string Main,
        string OldHead, string Candidate, string CandidateTree);

    private static Scenario CreateLeaderScenario()
    {
        var seed = GoalWorktreeTestsRebaseRatchetRetighten.CreateScenario();
        try
        {
            // The fixture's main-growth commit becomes A's candidate; its parent is M.
            RunGit(seed.Repo, "branch", "leader", "HEAD");
            var candidateTree = Tree(seed.Repo);
            RunGit(seed.Repo, "reset", "--hard", "HEAD~1");
            var main = RunGitOutput(seed.Repo, "rev-parse", "HEAD").Trim();
            RunGit(seed.Repo, "checkout", "leader");
            return new(seed.Repo, seed.Goal, seed.Worktree, main, seed.OldHead, seed.Main, candidateTree);
        }
        catch { DeleteDirectory(seed.Repo); throw; }
    }

    private static FollowerGateReceipt TestFollowerOnCandidate(Scenario scenario)
    {
        Assert.Equal(scenario.Candidate, RunGitOutput(scenario.Repo, "rev-parse", "HEAD").Trim());
        Assert.Equal(GoalWorktreeRebaseStatus.Rebased, GoalWorktrees.TryRebaseOntoMain(scenario.Repo, scenario.Goal).Status);
        GoalWorktreeTestsRebaseRatchetRetighten.AssertRetightened(scenario.Worktree);
        var receipt = Receipt(scenario, Tree(scenario.Worktree));
        RunGit(scenario.Worktree, "reset", "--hard", scenario.OldHead);
        return receipt;
    }

    private static FollowerGateLandingObservation LandLeaderAndCarryFollower(Scenario scenario)
    {
        RunGit(scenario.Repo, "checkout", "main");
        RunGit(scenario.Repo, "merge", "--no-ff", "--no-edit", "leader");
        var parent = RunGitOutput(scenario.Repo, "rev-parse", "HEAD^1").Trim();
        var landedTree = Tree(scenario.Repo);
        // Observe the original branch before the temporary carry rebase moves it.
        var branchHead = RunGitOutput(scenario.Worktree, "rev-parse", "HEAD").Trim();
        Assert.Equal(GoalWorktreeRebaseStatus.Rebased, GoalWorktrees.TryRebaseOntoMain(scenario.Repo, scenario.Goal).Status);
        GoalWorktreeTestsRebaseRatchetRetighten.AssertRetightened(scenario.Worktree);
        return new(FollowerLeaderOutcome.Landed, parent, landedTree, branchHead, Tree(scenario.Worktree), "plan-b");
    }

    private static FollowerGateReceipt Receipt(Scenario scenario, string testedTree) =>
        new(new GoalId("22222222222222222222222222222222"), scenario.Candidate, scenario.CandidateTree,
            scenario.Main, scenario.Goal, scenario.OldHead, testedTree, "plan-b");

    private static string Tree(string path) => RunGitOutput(path, "rev-parse", "HEAD^{tree}").Trim();
}
