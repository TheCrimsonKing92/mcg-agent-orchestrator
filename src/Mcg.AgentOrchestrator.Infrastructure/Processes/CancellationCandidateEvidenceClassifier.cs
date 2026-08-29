using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class CancellationCandidateEvidenceClassifier
{
    internal static CancellationCandidateEvidence Classify(
        TaskSpec task,
        TaskProcessRecord processRecord,
        GoalId goalId,
        DispatchWorktreeCommitter worktreeCommitter)
    {
        var dispatch = task.LastDispatch;
        if (task.RequiredRole != AgentRole.Developer ||
            task.LatestRetryAt is null ||
            dispatch is null ||
            string.IsNullOrWhiteSpace(dispatch.BaseCommit) ||
            string.IsNullOrWhiteSpace(dispatch.WorktreeHeadSha) ||
            string.IsNullOrWhiteSpace(dispatch.DirtyStateHash))
        {
            return CancellationCandidateEvidence.Indeterminate("missing retried Developer spawn generation evidence");
        }

        var inspection = worktreeCommitter.InspectGoalWorktree(
            processRecord.WorkingDirectory,
            goalId,
            dispatch.DispatchedAt,
            forceRefresh: true);
        if (inspection.IsUnsafe)
        {
            return CancellationCandidateEvidence.Unsafe(
                inspection.UnavailableReason ?? "unsafe-worktree",
                inspection.GitReceipt);
        }

        if (!inspection.IsAvailable)
        {
            return CancellationCandidateEvidence.Unavailable(
                inspection.UnavailableReason ?? "worktree-inspection-unavailable",
                inspection.GitReceipt);
        }

        var worktree = inspection.Evidence;
        var receipt = $"branch={worktree.Branch}; head={worktree.Head}; worktree={worktree.WorktreeStatus}; " +
            $"commits_after_dispatch={worktree.CommitsAfterDispatch}; status_short={worktree.StatusShort}; " +
            $"spawn_head={dispatch.WorktreeHeadSha}; base_commit={dispatch.BaseCommit}; " +
            $"spawn_dirty_state_hash={dispatch.DirtyStateHash}; post_reap_dirty_state_hash={worktree.DirtyStateHash}";
        if (!worktree.IsClean)
        {
            return CancellationCandidateEvidence.Dirty(worktree.Head, "post-reap worktree is dirty", receipt);
        }

        if (worktree.CommitsAfterDispatch != 0 ||
            !string.Equals(worktree.Head, dispatch.BaseCommit.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(worktree.Head, dispatch.WorktreeHeadSha.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(worktree.DirtyStateHash, dispatch.DirtyStateHash.Trim(), StringComparison.Ordinal))
        {
            return CancellationCandidateEvidence.Changed(
                worktree.Head,
                "post-reap candidate or dirty-state identity changed",
                receipt);
        }

        return CancellationCandidateEvidence.ConfirmedUnchanged(worktree.Head, receipt);
    }
}
