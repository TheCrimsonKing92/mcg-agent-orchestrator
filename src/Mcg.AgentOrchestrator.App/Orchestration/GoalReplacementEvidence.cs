using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalReplacementEvidence
{
    internal static Action<GoalId>? AfterFinalTransferValidation { get; set; }

    public static GoalReplacementEligibilityFacts Capture(
        OrchestratorWorkspace workspace,
        Goal predecessor,
        Func<string, IReadOnlyList<string>, GitCli.GitResult>? gitRunner = null)
    {
        var gitFacts = GoalGitFactIndex.Build(workspace.ExecutionDirectory, workspace.IntegrationBranch, gitRunner);
        var branchFacts = gitFacts.BuildReplacementFacts(predecessor);
        var journal = GoalOperationJournal.Read(workspace.ExecutionDirectory, predecessor.Id);
        var hasDispatch = predecessor.Tasks.Any(task =>
            task.LastDispatch is not null || task.LastProcess is not null || task.LastExecution is not null);
        var isRunning = predecessor.Tasks.Any(task =>
            task.Status == WorkTaskStatus.Running || task.LastProcess is { IsRunning: true });
        var isRecorded = journal.LatestByOperation.Any(entry =>
            entry.Status == GoalOperationStatus.Completed &&
            entry.Operation.Equals("conductor:record", StringComparison.OrdinalIgnoreCase));
        var hasLandingIntent = GoalOperationJournal.HasDurableLandingIntent(journal);
        var hasMergeEvidence = branchFacts.BranchAlreadyLanded ||
            GoalOperationJournal.HasMergeEvidenceTerminalDisposition(journal);

        var evidenceMaterial = JsonSerializer.Serialize(new
        {
            Repository = Path.GetFullPath(workspace.ExecutionDirectory),
            predecessor.Status,
            Tasks = predecessor.Tasks.Select(task => new
            {
                task.Id.Value,
                task.Status,
                HasDispatch = task.LastDispatch is not null,
                HasProcess = task.LastProcess is not null,
                HasExecution = task.LastExecution is not null
            }),
            HasDispatch = hasDispatch,
            IsRunning = isRunning,
            IsRecorded = isRecorded,
            HasLandingIntent = hasLandingIntent,
            HasMergeEvidence = hasMergeEvidence,
            Git = new
            {
                gitFacts.IsAvailable,
                gitFacts.MainSha,
                EvidenceKey = gitFacts.BuildGoalEvidenceKey(predecessor),
                branchFacts.HasRegisteredWorktree,
                branchFacts.HasGoalBranch,
                branchFacts.BranchAlreadyLanded,
                branchFacts.ContentState
            },
            Journal = journal.Entries
        });
        var evidenceToken = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidenceMaterial)))
            .ToLowerInvariant();

        return new GoalReplacementEligibilityFacts(
            predecessor.Status,
            branchFacts.HasRegisteredWorktree,
            branchFacts.HasGoalBranch,
            hasDispatch,
            branchFacts.ContentState == GoalBranchContentState.AbsentFromMain,
            isRunning,
            hasLandingIntent,
            hasMergeEvidence,
            isRecorded,
            gitFacts.IsAvailable && branchFacts.ContentState != GoalBranchContentState.Inconclusive,
            evidenceToken);
    }

    public static bool MatchesStateFacts(Goal predecessor, GoalReplacementEligibilityFacts facts)
    {
        var hasDispatch = predecessor.Tasks.Any(task =>
            task.LastDispatch is not null || task.LastProcess is not null || task.LastExecution is not null);
        var isRunning = predecessor.Tasks.Any(task =>
            task.Status == WorkTaskStatus.Running || task.LastProcess is { IsRunning: true });
        return predecessor.Status == facts.Status &&
            hasDispatch == facts.HasDispatch &&
            isRunning == facts.IsRunning;
    }

    public static IGoalReplacementTransferAuthority CreateTransferAuthority(
        OrchestratorWorkspace workspace,
        Goal predecessor,
        string backlogItemId,
        long expectedClaimVersion,
        GoalReplacementEligibilityFacts authorizedFacts,
        TimeProvider? timeProvider = null)
    {
        timeProvider ??= TimeProvider.System;
        var issuedAt = timeProvider.GetUtcNow();
        return new TransferAuthority(
            workspace,
            predecessor,
            authorizedFacts,
            timeProvider,
            new GoalReplacementTransferAuthoritySnapshot(
                Guid.NewGuid(),
                backlogItemId,
                predecessor.Id.Value,
                expectedClaimVersion,
                authorizedFacts.EvidenceToken,
                issuedAt,
                issuedAt.AddSeconds(30)));
    }

    private sealed class TransferAuthority(
        OrchestratorWorkspace workspace,
        Goal predecessor,
        GoalReplacementEligibilityFacts authorizedFacts,
        TimeProvider timeProvider,
        GoalReplacementTransferAuthoritySnapshot snapshot)
        : IGoalReplacementTransferAuthority
    {
        public GoalReplacementTransferAuthoritySnapshot Snapshot { get; } = snapshot;

        public GoalReplacementEligibilityFacts RevalidateForTransfer()
        {
            if (timeProvider.GetUtcNow() >= Snapshot.ExpiresAt)
                throw new GoalReplacementTransferAuthorityException("eligibility-authority-expired");
            if (string.IsNullOrWhiteSpace(Snapshot.EvidenceToken))
                throw new GoalReplacementTransferAuthorityException("eligibility-evidence-missing");

            GoalReplacementEligibilityFacts observedFacts;
            try
            {
                observedFacts = Capture(workspace, predecessor);
            }
            catch
            {
                throw new GoalReplacementTransferAuthorityException("eligibility-evidence-indeterminate");
            }

            if (!observedFacts.IsGitEvidenceAvailable)
                throw new GoalReplacementTransferAuthorityException("eligibility-evidence-indeterminate", observedFacts);
            if (string.IsNullOrWhiteSpace(observedFacts.EvidenceToken))
                throw new GoalReplacementTransferAuthorityException("eligibility-evidence-missing", observedFacts);
            if (!string.Equals(observedFacts.EvidenceToken, authorizedFacts.EvidenceToken, StringComparison.Ordinal) ||
                !string.Equals(observedFacts.EvidenceToken, Snapshot.EvidenceToken, StringComparison.Ordinal))
            {
                throw new GoalReplacementTransferAuthorityException("eligibility-evidence-changed", observedFacts);
            }

            AfterFinalTransferValidation?.Invoke(predecessor.Id);
            GoalReplacementEligibilityFacts transferFacts;
            try
            {
                transferFacts = Capture(workspace, predecessor);
            }
            catch
            {
                throw new GoalReplacementTransferAuthorityException("eligibility-evidence-indeterminate");
            }

            if (!transferFacts.IsGitEvidenceAvailable)
                throw new GoalReplacementTransferAuthorityException("eligibility-evidence-indeterminate", transferFacts);
            if (!string.Equals(transferFacts.EvidenceToken, observedFacts.EvidenceToken, StringComparison.Ordinal))
                throw new GoalReplacementTransferAuthorityException("eligibility-evidence-changed", transferFacts);
            return transferFacts;
        }
    }
}
