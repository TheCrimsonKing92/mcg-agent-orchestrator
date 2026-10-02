using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;
using static LandingExecutorTests;

public sealed class AcceptanceVerdictCarryForwardTestsSquashRebind : HostCapacityBoundTestBase
{
    [Xunit.Fact]
    public void PendingObligationRebindsToExactSquashedMergeTree()
    {
        var repo = CreateGitRepository();
        try
        {
            var (kernel, goal) = SimpleGoal("Carry acceptance evidence across merge squashing");
            var path = CreateMergeBearingBranch(repo, goal.Id);
            var oldHead = ReadGit(path, "rev-parse", "HEAD");
            BindAcceptance(kernel, goal, oldHead);
            Assert.Equal(CriterionEvidenceState.Pending, Assert.Single(goal.CriterionEvidenceObligations).State);
            var mainHead = ReadGit(repo, "rev-parse", "main");
            var tree = ReadGit(repo, "merge-tree", "--write-tree", mainHead, oldHead);

            var result = GoalWorktrees.TryRebaseOntoMain(repo, goal.Id);
            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, result.Status);
            Assert.Equal("squashed-merge-commits", result.Detail);
            var newHead = ReadGit(path, "rev-parse", "HEAD");
            Assert.Equal(tree, ReadGit(path, "rev-parse", "HEAD^{tree}"));
            Assert.True(GoalWorktrees.TryComputePatchEquivalence(repo, oldHead, newHead, out var evidence, out var refusal), refusal);
            Assert.StartsWith("squashed-merge-commits:", evidence);

            var diagnostic = AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(
                goal, newHead, kernel, "test", repo);

            Assert.Null(diagnostic);
            var obligation = Assert.Single(goal.CriterionEvidenceObligations);
            Assert.Equal(CriterionEvidenceState.Satisfied, obligation.State);
            Assert.Equal(newHead, obligation.ExpectedCandidateSha);
            Assert.Equal(newHead, obligation.CandidateSha);
            Assert.Equal($"full-acceptance:{newHead}", obligation.ReceiptId);
            var carry = Assert.Single(GoalOperationJournal.Read(repo, goal.Id).Entries.Where(entry =>
                entry.Operation == "conductor:criterion-evidence-landing-carry"));
            Assert.Contains(oldHead, carry.Detail, StringComparison.Ordinal);
            Assert.Contains("squashed-merge-commits:", carry.Detail, StringComparison.Ordinal);
        }
        finally { TryDeleteDirectory(repo); }
    }

    [Xunit.Fact]
    public void ChangedSquashedTreeKeepsBindingAndOriginalRefusal()
    {
        var repo = CreateGitRepository();
        try
        {
            var (kernel, goal) = SimpleGoal("Reject changed squash evidence");
            var path = CreateMergeBearingBranch(repo, goal.Id);
            var oldHead = ReadGit(path, "rev-parse", "HEAD");
            BindAcceptance(kernel, goal, oldHead);
            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, GoalWorktrees.TryRebaseOntoMain(repo, goal.Id).Status);
            File.WriteAllText(Path.Combine(path, "feature-one.txt"), "changed after squash");
            ReadGit(path, "add", "feature-one.txt");
            ReadGit(path, "commit", "--amend", "--no-edit");
            var changedHead = ReadGit(path, "rev-parse", "HEAD");
            Assert.False(GoalWorktrees.TryComputePatchEquivalence(repo, oldHead, changedHead, out _, out var refusal));
            Assert.Equal("commit-count-mismatch", refusal);

            var diagnostic = AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(
                goal, changedHead, kernel, "test", repo);

            Assert.Contains("reason=commit-count-mismatch", diagnostic, StringComparison.Ordinal);
            var obligation = Assert.Single(goal.CriterionEvidenceObligations);
            Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
            Assert.Equal(oldHead, obligation.ExpectedCandidateSha);
            Assert.Null(obligation.ReceiptId);
            Assert.DoesNotContain(GoalOperationJournal.Read(repo, goal.Id).Entries,
                entry => entry.Operation == "conductor:criterion-evidence-landing-carry");
        }
        finally { TryDeleteDirectory(repo); }
    }

    private static string CreateMergeBearingBranch(string repo, GoalId goalId)
    {
        ReadGit(repo, "config", "core.autocrlf", "false");
        var path = GoalWorktrees.Ensure(repo, goalId);
        AppendCommit(path, "feature-one.txt", "goal one");
        AppendCommit(path, "feature-two.txt", "goal two");
        AppendCommit(repo, "main-first.txt", "main before merge");
        ReadGit(path, "merge", "--no-edit", "main");
        AppendCommit(repo, "main-later.txt", "main after merge");
        return path;
    }

    private static void BindAcceptance(AgentOrchestratorKernel kernel, Goal goal, string head)
    {
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            goal.Objective, ["Full acceptance passes"], VerificationClass.TestVerifiable, [], []));
        kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
            "reviewer", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: head);
    }
}
