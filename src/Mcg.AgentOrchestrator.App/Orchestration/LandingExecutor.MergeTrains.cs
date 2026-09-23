using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class LandingExecutor
{
    internal static AcceptanceCohortLandingResult ExecuteMergeTrain(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<Goal> goals,
        OrchestratorWorkspace workspace,
        MergeTrainReceipt receipt,
        string trainCommitRevision,
        MergeTrainAcceptanceStore store,
        ConductorAutonomyPolicy policy,
        IGoalLifecycleEventWriter? eventWriter = null,
        Func<string?>? mutationBlocker = null)
    {
        ArgumentNullException.ThrowIfNull(goals);
        ArgumentNullException.ThrowIfNull(receipt);
        if (goals.Count is < 2 or > 3 ||
            goals.Select(goal => goal.Id).Distinct().Count() != goals.Count ||
            goals.Count != receipt.Identity.Members.Count)
        {
            throw new ArgumentException("Merge train landing requires the exact two or three distinct tested goals.", nameof(goals));
        }
        if (!receipt.HasAuthoritativeLandingEvidence)
        {
            throw new InvalidOperationException("Only an exact passing merge train receipt can authorize landing.");
        }

        var executionDirectory = workspace.ExecutionDirectory;
        var blockReason = mutationBlocker?.Invoke();
        if (!string.IsNullOrWhiteSpace(blockReason))
        {
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.RetryableHold,
                $"Merge train landing held at mutation boundary: {blockReason}");
        }
        var liveMain = GoalWorktrees.ResolveRequiredRef(executionDirectory, "refs/heads/main");
        if (!liveMain.Equals(receipt.Identity.ObservedMainRevision, StringComparison.Ordinal))
        {
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.StateInvalidated,
                "Merge train receipt invalidated because main changed after the gate.");
        }
        var commit = MergeTrainMemberBinding.NormalizeRevision(trainCommitRevision, nameof(trainCommitRevision));
        var tree = GoalWorktrees.ResolveRequiredRef(executionDirectory, $"{commit}^{{tree}}");
        if (!tree.Equals(receipt.Identity.TrainTreeRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Merge train landing commit does not contain the exact tested tree.");
        }

        for (var index = 0; index < goals.Count; index++)
        {
            var member = receipt.Identity.Members[index];
            if (goals[index].Id != member.GoalId)
            {
                throw new InvalidOperationException("Merge train goal order differs from the tested receipt.");
            }
            var liveBranch = GoalWorktrees.ResolveRequiredRef(
                executionDirectory,
                $"refs/heads/{GoalWorktrees.BranchName(member.GoalId)}");
            if (!liveBranch.Equals(member.BranchRevision, StringComparison.Ordinal) ||
                !liveBranch.Equals(member.CandidateRevision, StringComparison.Ordinal))
            {
                return new AcceptanceCohortLandingResult(
                    AcceptanceCohortLandingOutcome.StateInvalidated,
                    $"Merge train receipt invalidated because goal {member.GoalId.Value[..8]} changed after the gate.");
            }
        }

        var evidenceDiagnostic = AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(
            goals,
            goalId => receipt.Identity.Members.Single(member => member.GoalId == goalId).CandidateRevision,
            kernel,
            $"merge-train-receipt:{receipt.ReceiptId}");
        if (evidenceDiagnostic is not null)
        {
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.RetryableHold,
                $"Merge train landing held before mutation because {evidenceDiagnostic}");
        }

        var changedFiles = receipt.Identity.Members
            .SelectMany(member => member.LandingPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ownership = RepositoryOwnershipMap.GuardWriteSet(changedFiles);
        if (ownership.RequiresOperatorApproval && !policy.AllowsAutonomousHighRiskOwnership)
        {
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.RetryableHold,
                "Merge train landing requires operator approval for ownership-protected paths.");
        }

        var currentIntegration = RunGit(
            executionDirectory,
            "rev-parse", "--verify", "--quiet", $"refs/heads/{IntegrationBranchName}");
        string? priorIntegrationRevision = null;
        if (currentIntegration.ExitCode == 0 && !string.IsNullOrWhiteSpace(currentIntegration.Output))
        {
            priorIntegrationRevision = currentIntegration.Output.Trim();
            if (RunGit(executionDirectory, "merge-base", "--is-ancestor", priorIntegrationRevision, liveMain).ExitCode != 0)
            {
                return new AcceptanceCohortLandingResult(
                    AcceptanceCohortLandingOutcome.RetryableHold,
                    "Merge train landing held because integration contains state not present on bound main.");
            }
        }

        store.PrepareLanding(receipt, commit, priorIntegrationRevision);
        foreach (var goal in goals)
        {
            GoalOperationJournal.RecordLandingIntent(
                executionDirectory,
                goal,
                GoalWorktrees.BranchName(goal.Id),
                $"train/{receipt.Identity.Value}",
                commit,
                "LandingExecutor.ExecuteMergeTrain",
                boundMainRevision: liveMain,
                previousIntegrationRevision: priorIntegrationRevision);
        }

        blockReason = mutationBlocker?.Invoke();
        if (!string.IsNullOrWhiteSpace(blockReason))
        {
            foreach (var goal in goals)
            {
                GoalOperationJournal.TombstoneLandingIntent(
                    executionDirectory,
                    goal,
                    $"merge train landing mutation blocked after intent write: {blockReason}");
            }
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.RetryableHold,
                $"Merge train landing held at mutation boundary: {blockReason}");
        }

        var integrationUpdate = priorIntegrationRevision is null
            ? RunGit(executionDirectory, "update-ref", $"refs/heads/{IntegrationBranchName}", commit)
            : RunGit(executionDirectory, "update-ref", $"refs/heads/{IntegrationBranchName}", commit, priorIntegrationRevision);
        if (integrationUpdate.ExitCode != 0)
        {
            foreach (var goal in goals)
            {
                GoalOperationJournal.TombstoneLandingIntent(
                    executionDirectory,
                    goal,
                    $"merge train integration ref update failed: {integrationUpdate.Error}");
            }
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.RetryableHold,
                $"Merge train integration ref update failed: {integrationUpdate.Error}");
        }

        var mainUpdate = RunGit(executionDirectory, "update-ref", "refs/heads/main", commit, receipt.Identity.ObservedMainRevision);
        if (mainUpdate.ExitCode != 0)
        {
            _ = priorIntegrationRevision is null
                ? RunGit(executionDirectory, "update-ref", "-d", $"refs/heads/{IntegrationBranchName}", commit)
                : RunGit(executionDirectory, "update-ref", $"refs/heads/{IntegrationBranchName}", priorIntegrationRevision, commit);
            foreach (var goal in goals)
            {
                GoalOperationJournal.TombstoneLandingIntent(
                    executionDirectory,
                    goal,
                    $"merge train main compare-and-swap failed: {mainUpdate.Error}");
            }
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.StateInvalidated,
                $"Merge train receipt invalidated at the main compare-and-swap boundary: {mainUpdate.Error}");
        }
        var checkout = RunGit(executionDirectory, "read-tree", "-m", "-u", receipt.Identity.ObservedMainRevision, commit);
        if (checkout.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Main advanced to the exact merge train commit, but checkout refresh failed: {checkout.Error}");
        }

        var coverage = store.FinalizeLanding(receipt.Identity.Value, receipt.ReceiptId);
        foreach (var goal in goals)
        {
            GoalOperationJournal.Completed(
                executionDirectory,
                goal,
                "conductor:land",
                $"Merge train receipt {receipt.ReceiptId} landed exact tree {receipt.Identity.TrainTreeRevision}.");
            eventWriter?.AppendGoalLanded(
                goal.Id,
                $"train/{receipt.Identity.Value}",
                GoalWorktrees.BranchName(goal.Id));
            StateEffectProposalApplier.ApplyLandedProposals(kernel, goal, workspace, changedFiles, Console.WriteLine);
        }
        return new AcceptanceCohortLandingResult(
            AcceptanceCohortLandingOutcome.Advanced,
            $"Landed exact tested merge train tree for {string.Join(',', goals.Select(goal => goal.Id.Value[..8]))}.",
            commit,
            coverage.Select(item => new AcceptanceCohortCoverage(item.GoalId, item.TrainId, item.ReceiptId, item.Landed)).ToArray());
    }
}
