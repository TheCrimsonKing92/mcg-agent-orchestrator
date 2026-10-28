using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Selects a unique upstream Developer whose committed output owns the reviewed candidate.</summary>
internal static class TesterFindingDeveloperTargetPolicy
{
    public static TaskSpec? Select(Goal goal, TaskSpec verifyingTask, string? repositoryPath = null)
    {
        var reviewedCommit = verifyingTask.LastVerification?.ReviewedCommit;
        // Resolve immutable Git objects in the explicitly supplied repository. Integration merges
        // preserve the Developer output on their first-parent spine; an older ordinary commit
        // (or a commit brought in from main) does not establish ownership of this candidate.
        string? outputCommit = null;
        if (PreDispatchIntegrationReceipt.IsCommitSha(reviewedCommit) && repositoryPath is not null)
        {
            var result = GitCli.RunForRepository(repositoryPath,
                "rev-list", "--first-parent", "--no-merges", "--max-count=1", reviewedCommit!);
            if (result.Succeeded && result.ProcessStarted && !result.DrainTimedOut)
                outputCommit = result.Output.Trim();
        }
        var upstreamDevelopers = goal.Tasks
            .TakeWhile(candidate => candidate.Id != verifyingTask.Id)
            .Where(candidate =>
                candidate.RequiredRole == AgentRole.Developer &&
                HasCandidateOutput(goal, candidate, verifyingTask, reviewedCommit, outputCommit))
            .Take(2)
            .ToArray();
        return upstreamDevelopers.Length == 1 ? upstreamDevelopers[0] : null;
    }

    private static bool HasCandidateOutput(
        Goal goal, TaskSpec task, TaskSpec verifyingTask, string? reviewedCommit, string? outputCommit)
    {
        // Preserve the existing legacy, candidate-less finding contract. Once a reviewed SHA is
        // present, a historical HasCommittedChanges flag cannot supply candidate authority.
        if (string.IsNullOrWhiteSpace(reviewedCommit))
            return task.VerificationHistory.Any(verification => verification.HasCommittedChanges) ||
                task.DispatchHistory.Any(HasChangedResult);

        if (task.LastVerification?.Succeeded != true || task.LastDispatch is not { } currentDispatch ||
            currentDispatch.DispatchedAt < task.LatestRetryAt.GetValueOrDefault() ||
            task.LastVerification.CompletedAt < currentDispatch.DispatchedAt ||
            task.LastVerification.CompletedAt > verifyingTask.LastVerification!.CompletedAt ||
            !string.Equals(currentDispatch.ResultCommit, reviewedCommit, StringComparison.OrdinalIgnoreCase))
            return false;

        bool OwnsCandidate(string? commit) => !string.IsNullOrWhiteSpace(commit) &&
            (string.Equals(commit, reviewedCommit, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(commit, outputCommit, StringComparison.OrdinalIgnoreCase));

        return task.DispatchHistory.Any(dispatch =>
            dispatch.DispatchedAt <= currentDispatch.DispatchedAt &&
            HasChangedResult(dispatch) && OwnsCandidate(dispatch.ResultCommit)) ||
            // A failed round can be committed at the pre-dispatch checkpoint boundary. The
            // decision must identify this dispatch AND the output underlying this candidate.
            task.DispatchHistory.Any(dispatch =>
                dispatch.FailedRoundCheckpointDecision is
                    { Outcome: FailedRoundCheckpointOutcome.Committed, CommitSha: { } commitSha } decision &&
                OwnsCandidate(commitSha) &&
                !string.Equals(dispatch.BaseCommit, commitSha, StringComparison.OrdinalIgnoreCase) &&
                dispatch.DispatchedAt <= currentDispatch.DispatchedAt &&
                decision.DispatchId == DispatchIdentity.BuildId(goal.Id, task.Id, dispatch));
    }

    private static bool HasChangedResult(TaskDispatchRecord dispatch) =>
            !string.IsNullOrWhiteSpace(dispatch.ResultCommit) &&
            (string.IsNullOrWhiteSpace(dispatch.BaseCommit) ||
             !string.Equals(dispatch.BaseCommit, dispatch.ResultCommit, StringComparison.OrdinalIgnoreCase));
}
