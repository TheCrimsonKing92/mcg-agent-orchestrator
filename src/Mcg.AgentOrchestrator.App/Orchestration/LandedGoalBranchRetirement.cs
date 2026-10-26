using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class LandedGoalBranchRetirement
{
    public const string RepairKind = "completed-branch-landed-retired";

    internal sealed record Proof(string ReceiptKind, string ReceiptId, string CandidateRevision, string LandingCommit)
    {
        public string Describe(Goal goal, string cleanup) =>
            $"completed goal branch {GoalWorktrees.BranchName(goal.Id)} is not merged by ancestry; landed receipt={ReceiptKind}:{ReceiptId}; candidateRevision={CandidateRevision}; landingCommit={LandingCommit}; action=retire; cleanup={cleanup}";
    }

    public static string? TryGetLandedBranchTip(
        Goal goal,
        GoalBranchFacts branchFacts,
        string? branchTip,
        string? mainSha,
        GoalOperationJournalSummary journal,
        MergeTrainAcceptanceStore mergeTrainStore,
        CohortAcceptanceStore cohortStore,
        string executionDirectory,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner,
        out Proof? proof,
        GoalAncestryCache? ancestryCache = null)
    {
        proof = null;
        if (goal.Status != GoalStatus.Completed || !branchFacts.HasGoalBranch ||
            branchFacts.BranchAlreadyLanded || branchFacts.ContentState == GoalBranchContentState.EquivalentToMain ||
            string.IsNullOrWhiteSpace(branchTip) || string.IsNullOrWhiteSpace(mainSha) ||
            GoalOperationJournal.TryGetLatestLandingIntent(journal) is not { } intent)
            return null;

        var train = mergeTrainStore.ReadPassedReceiptsForGoal(goal.Id)
            .OrderBy(receipt => receipt.ReceiptId, StringComparer.Ordinal)
            .FirstOrDefault(receipt => receipt.Identity.Members.Any(member =>
                member.GoalId == goal.Id && string.Equals(member.CandidateRevision, branchTip, StringComparison.OrdinalIgnoreCase)));
        if (train is not null)
            proof = new Proof("merge-train-receipt", train.ReceiptId, branchTip, intent.MergeCommitSha);
        else
        {
            var cohort = cohortStore.ReadPassedReceiptsForGoal(goal.Id)
                .OrderBy(receipt => receipt.ReceiptId, StringComparer.Ordinal)
                .FirstOrDefault(receipt => receipt.Identity.Members.Any(member =>
                    member.GoalId == goal.Id && string.Equals(member.CandidateRevision, branchTip, StringComparison.OrdinalIgnoreCase)));
            if (cohort is not null)
                proof = new Proof("cohort-receipt", cohort.ReceiptId, branchTip, intent.MergeCommitSha);
        }

        if (proof is null)
            return null;
        if (AcceptanceCriterionEvidenceRecovery.IsAncestor(
            executionDirectory, intent.MergeCommitSha, mainSha, gitRunner, ancestryCache))
            return branchTip;

        proof = null;
        return null;
    }
}
